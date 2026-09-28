using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Pages;

// Fully-qualifying the page type below (rather than `using SbConsole.Plugins.Redis.Pages;` +
// `Overview`) sidesteps a real name collision: this file's namespace,
// SbConsole.Plugins.Redis.Tests.Pages, has SbConsole.Plugins.Redis as an ancestor, and that
// namespace has its own nested "Overview" namespace (the query handlers). C# resolves the simple
// name "Overview" to that nested namespace before consulting any using directive, so
// `Render<Overview>()` fails with CS0118 ("Overview is a namespace but is used like a type").
public class OverviewPageTests : RedisPageTestBase
{
    // The page auto-refreshes on the injected TimeProvider, so tests drive the 5s cadence by
    // advancing this fake clock instead of waiting on real time.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));

    public OverviewPageTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
    }

    private static ServerOverview SampleOverview(long evicted = 1240000, long used = 5_000_000_000, string role = "master", int replicas = 2) => new(
        RedisVersion: "7.2.4", Mode: "standalone", ConnectedReplicas: replicas,
        UsedMemoryBytes: used, MaxMemoryBytes: 8L * 1024 * 1024 * 1024,
        MaxMemoryPolicy: "allkeys-lru", MemoryFragmentationRatio: 1.12,
        HitRatioPercent: 94.2, OpsPerSecond: 41800, EvictedKeysTotal: evicted, ExpiredKeysTotal: 98213,
        ConnectedClients: 412, BlockedClients: 3,
        Databases: [new KeyspaceDatabaseInfo(0, 2_410_000, 1_820_000), new KeyspaceDatabaseInfo(1, 18_200, 0)],
        Role: role);

    private static readonly IReadOnlyList<MemoryByPrefixEntry> SamplePrefixes =
    [
        new("session:", 1_181_116_006, 100, 38),
        new("cart:", 400_000_000, 50, 0),
        new("other", 100_000_000, 10, 1),
    ];

    private void Arrange(
        ServerOverview? overview = null,
        IReadOnlyList<MemoryByPrefixEntry>? prefixes = null,
        IReadOnlyList<SlowLogEntry>? slowLog = null)
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(overview ?? SampleOverview());
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>()).Returns(prefixes ?? []);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>()).Returns(slowLog ?? []);
    }

    private async Task<IRenderedComponent<SbConsole.Plugins.Redis.Pages.Overview>> RenderPageAsync()
    {
        var cut = Render<SbConsole.Plugins.Redis.Pages.Overview>();
        await SettleAsync(cut);
        return cut;
    }

    private async Task AdvanceAsync(IRenderedComponent<SbConsole.Plugins.Redis.Pages.Overview> cut, TimeSpan by)
    {
        _time.Advance(by);
        await SettleAsync(cut);
    }

    [Fact]
    public async Task Renders_the_memory_and_hit_ratio_tiles()
    {
        Arrange(SampleOverview(used: 7_387_423_669));

        var cut = await RenderPageAsync();

        cut.Find(".redis-overview-memory-used").TextContent.Should().Contain("6.9 GB").And.Contain("86%");
        cut.Find(".redis-overview-memory-used small").ClassList.Should().Contain("rds-warn");
        cut.Find(".redis-overview-memory .rds-bar").ClassList.Should().Contain("rds-bar-warn");
        cut.Find(".redis-overview-memory").TextContent.Should().Contain("maxmemory 8.0 GB")
            .And.Contain("policy allkeys-lru").And.Contain("warns at 90%").And.Contain("frag 1.12");
        cut.Find(".redis-overview-hit-ratio").TextContent.Should().Contain("94.2%");
        cut.Find(".redis-overview-ops").TextContent.Should().Contain("41.8k");
    }

    [Fact]
    public async Task Memory_tile_without_maxmemory_shows_the_value_only_with_no_bar()
    {
        Arrange(SampleOverview() with { MaxMemoryBytes = null });

        var cut = await RenderPageAsync();

        cut.FindAll(".redis-overview-memory .rds-bar").Should().BeEmpty();
        cut.Find(".redis-overview-memory").TextContent.Should().Contain("no maxmemory set · policy allkeys-lru");
    }

    [Fact]
    public async Task Header_meta_names_version_mode_role_and_replicas()
    {
        Arrange(SampleOverview(role: "master", replicas: 2));

        var cut = await RenderPageAsync();

        cut.Find(".redis-header-meta").TextContent.Should().Be("7.2.4 · standalone · primary + 2 replicas");
    }

    [Fact]
    public async Task Header_meta_calls_a_slave_a_replica_and_omits_zero_replicas()
    {
        Arrange(SampleOverview(role: "slave", replicas: 0));

        var cut = await RenderPageAsync();

        cut.Find(".redis-header-meta").TextContent.Should().Be("7.2.4 · standalone · replica");
    }

    [Fact]
    public async Task Header_shows_how_long_ago_INFO_was_read()
    {
        Arrange();

        var cut = await RenderPageAsync();
        cut.Find(".redis-overview-info-age").TextContent.Should().Be("INFO 0s ago");

        await AdvanceAsync(cut, TimeSpan.FromSeconds(3));
        cut.Find(".redis-overview-info-age").TextContent.Should().Be("INFO 3s ago");
    }

    [Fact]
    public async Task Evicted_per_minute_is_sampling_until_a_second_refresh_then_shows_the_delta_rate()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns(SampleOverview(evicted: 1000), SampleOverview(evicted: 1100));
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>()).Returns(SamplePrefixes);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = await RenderPageAsync();
        cut.Find(".redis-overview-evicted").TextContent.Should().Contain("—").And.Contain("sampling…");

        await AdvanceAsync(cut, TimeSpan.FromSeconds(5));

        // 100 evictions over 5 s = 1,200 / min.
        var tile = cut.Find(".redis-overview-evicted");
        tile.QuerySelector(".rds-tile-value")!.TextContent.Should().Contain("1,200");
        tile.QuerySelector(".rds-tile-value")!.ClassList.Should().Contain("rds-danger");
        tile.TextContent.Should().NotContain("sampling");
    }

    [Fact]
    public async Task Evicted_per_minute_is_zero_and_not_danger_when_the_counter_does_not_move()
    {
        Arrange(SampleOverview(evicted: 500));

        var cut = await RenderPageAsync();
        await AdvanceAsync(cut, TimeSpan.FromSeconds(5));

        var value = cut.Find(".redis-overview-evicted .rds-tile-value");
        value.TextContent.Trim().Should().Be("0");
        value.ClassList.Should().NotContain("rds-danger");
    }

    [Fact]
    public async Task Banner_is_hidden_when_nothing_is_evicting_and_memory_is_below_the_warning_threshold()
    {
        Arrange(SampleOverview(evicted: 500), SamplePrefixes);

        var cut = await RenderPageAsync();
        await AdvanceAsync(cut, TimeSpan.FromSeconds(5));

        cut.FindAll(".redis-overview-banner").Should().BeEmpty();
    }

    [Fact]
    public async Task Banner_explains_running_evictions_and_links_the_largest_prefix_into_Keys()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns(SampleOverview(evicted: 1000), SampleOverview(evicted: 1100));
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>()).Returns(SamplePrefixes);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = await RenderPageAsync();
        cut.FindAll(".redis-overview-banner").Should().BeEmpty();

        await AdvanceAsync(cut, TimeSpan.FromSeconds(5));

        var banner = cut.Find(".redis-overview-banner");
        banner.TextContent.Should().Contain("Evicting 1,200 keys/min")
            .And.Contain("largest share is under session:* (1.1 GB, no TTL on 38% of sampled keys)");
        var href = banner.QuerySelector("a.redis-overview-open-in-keys")!.GetAttribute("href")!;
        Uri.UnescapeDataString(href).Should().Be($"/p/redis/keys?connectionId={ConnectionId}&database=0&pattern=session:*");
    }

    [Fact]
    public async Task Banner_warns_when_memory_is_at_or_above_ninety_percent_even_without_evictions()
    {
        Arrange(SampleOverview(used: (long)(8L * 1024 * 1024 * 1024 * 0.92)), SamplePrefixes);

        var cut = await RenderPageAsync();

        cut.Find(".redis-overview-banner").TextContent.Should().Contain("Memory is at 92% of maxmemory");
    }

    [Fact]
    public async Task Memory_by_prefix_rows_show_globbed_prefixes_sizes_and_ttl_share()
    {
        Arrange(prefixes: SamplePrefixes);

        var cut = await RenderPageAsync();

        var rows = cut.FindAll(".redis-overview-memory-by-prefix .redis-overview-prefix-row");
        rows.Should().HaveCount(3);
        rows[0].TextContent.Should().Contain("session:*").And.Contain("1.1 GB").And.Contain("38% no TTL");
        rows[0].QuerySelector(".rds-bar")!.ClassList.Should().Contain("rds-bar-warn");
        rows[1].TextContent.Should().Contain("cart:*").And.Contain("all TTL");
        rows[1].QuerySelector(".rds-bar")!.ClassList.Should().NotContain("rds-bar-warn");
        rows[2].TextContent.Should().Contain("other").And.NotContain("other*");

        var footer = cut.Find(".redis-overview-keyspace").TextContent;
        footer.Should().Contain("db0").And.Contain("2.41M keys").And.Contain("db1").And.Contain("18.2k");
        footer.Should().Contain("clients 412").And.Contain("blocked 3");
    }

    [Fact]
    public async Task Slow_log_colors_durations_and_shows_sub_millisecond_ones_with_two_decimals()
    {
        var at = new DateTimeOffset(2026, 9, 28, 9, 40, 12, TimeSpan.Zero);
        Arrange(slowLog:
        [
            new SlowLogEntry(at, TimeSpan.FromMilliseconds(412), "KEYS session:*", "10.4.8.31"),
            new SlowLogEntry(at, TimeSpan.FromMilliseconds(41), "ZRANGE lb:weekly 0 -1", "lb-worker"),
            new SlowLogEntry(at, TimeSpan.FromMilliseconds(12), "SMEMBERS flags:beta", "flags-svc"),
            new SlowLogEntry(at, TimeSpan.FromMicroseconds(350), "HGETALL cart:u:1", null),
        ]);

        var cut = await RenderPageAsync();

        var ms = cut.FindAll(".redis-overview-slowlog tbody .redis-overview-slowlog-ms");
        ms.Select(m => m.TextContent.Trim()).Should().Equal("412", "41", "12", "0.35");
        ms[0].ClassList.Should().Contain("rds-danger");
        ms[1].ClassList.Should().Contain("rds-warn");
        ms[2].ClassList.Should().NotContain("rds-warn").And.NotContain("rds-danger");
        cut.Find(".redis-overview-slowlog").TextContent.Should().Contain("last 4");
    }

    [Fact]
    public async Task Slow_log_footnote_calls_out_a_KEYS_command_as_coming_from_outside_SbConsole()
    {
        var at = DateTimeOffset.UtcNow;
        Arrange(slowLog:
        [
            new SlowLogEntry(at, TimeSpan.FromMilliseconds(88), "HGETALL cart:u:77120", "cart-api"),
            new SlowLogEntry(at, TimeSpan.FromMilliseconds(412), "KEYS session:*", "10.4.8.31"),
        ]);

        var cut = await RenderPageAsync();

        cut.Find(".redis-overview-keys-footnote").TextContent
            .Should().Be("The 412 ms KEYS came from outside SbConsole — the plugin only ever issues SCAN.");
    }

    [Fact]
    public async Task Slow_log_has_no_KEYS_footnote_without_a_KEYS_command()
    {
        Arrange(slowLog: [new SlowLogEntry(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(88), "HGETALL cart:u:77120", "cart-api")]);

        var cut = await RenderPageAsync();

        cut.FindAll(".redis-overview-keys-footnote").Should().BeEmpty();
    }

    [Fact]
    public async Task Auto_refresh_stops_once_the_page_is_disposed()
    {
        Arrange();

        var cut = await RenderPageAsync();
        await AdvanceAsync(cut, TimeSpan.FromSeconds(5));
        await Operations.Received(2).GetOverviewAsync(Secret, Arg.Any<CancellationToken>());

        await cut.Instance.DisposeAsync();
        _time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(30);

        await Operations.Received(2).GetOverviewAsync(Secret, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Shows_the_broker_error_when_the_overview_call_fails()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<ServerOverview>>(_ => throw new InvalidOperationException("down"));
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MemoryByPrefixEntry>)[]);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = await RenderPageAsync();

        cut.Find(".redis-overview-error").Should().NotBeNull();
    }

    [Fact]
    public async Task Shows_an_inline_warning_when_memory_by_prefix_fails_without_blocking_the_rest_of_the_page()
    {
        // The design spec explicitly wants ACL/NOPERM errors surfaced, not hidden -- LoadAsync
        // used to map a failed sub-result to [] silently, so a NOPERM on MEMORY USAGE looked
        // identical to "no keys". The rest of the page (memory tiles, slow log) must still render.
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(SampleOverview());
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<MemoryByPrefixEntry>>>(_ =>
                throw new InvalidOperationException("Command refused by ACL (NOPERM): no permission for 'memory'"));
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = await RenderPageAsync();

        cut.Find(".redis-overview-memory-by-prefix-error").TextContent.Should().Contain("NOPERM");
        cut.Find(".redis-overview-memory-used").Should().NotBeNull();
    }

    [Fact]
    public async Task Shows_an_inline_warning_when_the_slow_log_fails_without_blocking_the_rest_of_the_page()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(SampleOverview());
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MemoryByPrefixEntry>)[]);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<SlowLogEntry>>>(_ =>
                throw new InvalidOperationException("Command refused by ACL (NOPERM): no permission for 'slowlog'"));

        var cut = await RenderPageAsync();

        cut.Find(".redis-overview-slowlog-error").TextContent.Should().Contain("NOPERM");
        cut.Find(".redis-overview-memory-used").Should().NotBeNull();
    }

    [Fact]
    public async Task Shows_the_no_connections_message_when_none_are_saved()
    {
        Connections.ListAsync("redis", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<SbConsole.Sdk.ConnectionInfo>)[]);

        var cut = await RenderPageAsync();

        cut.Find(".no-connections").Should().NotBeNull();
        await Operations.DidNotReceive().GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
