using Bunit;
using FluentAssertions;
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
    private static ServerOverview SampleOverview() => new(
        RedisVersion: "7.2.4", Mode: "standalone", ConnectedReplicas: 2,
        UsedMemoryBytes: 7_387_423_669, MaxMemoryBytes: 8L * 1024 * 1024 * 1024,
        MaxMemoryPolicy: "allkeys-lru", MemoryFragmentationRatio: 1.12,
        HitRatioPercent: 94.2, OpsPerSecond: 41800, EvictedKeysTotal: 1240000, ExpiredKeysTotal: 98213,
        ConnectedClients: 412, BlockedClients: 3,
        Databases: [new KeyspaceDatabaseInfo(0, 2_410_000, 1_820_000)]);

    [Fact]
    public async Task Renders_the_memory_and_hit_ratio_tiles()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(SampleOverview());
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MemoryByPrefixEntry>)[]);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = Render<SbConsole.Plugins.Redis.Pages.Overview>();
        await SettleAsync(cut);

        cut.Find(".redis-overview-memory-used").TextContent.Should().Contain("6.9");
        cut.Find(".redis-overview-hit-ratio").TextContent.Should().Contain("94.2");
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

        var cut = Render<SbConsole.Plugins.Redis.Pages.Overview>();
        await SettleAsync(cut);

        cut.Find(".redis-overview-error").Should().NotBeNull();
    }
}
