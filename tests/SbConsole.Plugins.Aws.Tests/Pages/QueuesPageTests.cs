using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class QueuesPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly IPluginStore _store = Substitute.For<IPluginStore>();
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 9, 24, 14, 3, 7, TimeSpan.Zero));
    private const string Secret = "mode=default-chain;region=eu-west-1";

    public QueuesPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "aws-dev", "aws", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<ListQueuesQueryHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
        Services.AddSingleton<DeleteQueueCommandHandler>();
        Services.AddSingleton<CreateQueueCommandHandler>();
        Services.AddSingleton<PurgeQueueCommandHandler>();
        Services.AddSingleton<SbConsole.Plugins.Aws.Messages.SendMessageCommandHandler>();
        Services.AddSingleton<SbConsole.Plugins.Aws.Redrive.StartRedriveCommandHandler>();
        Services.AddKeyedSingleton<IPluginStore>("aws", _store);
        Services.AddSingleton<TimeProvider>(_clock);
    }

    // Deterministic "now" in a UTC local zone, so the "counts read HH:mm:ss" caption is stable.
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    // The row overflow MudMenu's items render into <MudPopoverProvider/> (hosted once by the real
    // app's layout), so menu tests render one alongside the page -- same pattern as
    // SbConsole.Web.Tests/ConnectionsPageTests.RenderPage.
    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPopovers()
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<SbConsole.Plugins.Aws.Pages.Queues>(1);
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    private async Task OpenRowMenuAsync(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        await cut.Find(".queue-row-menu button").ClickAsync(new());
        await Task.Delay(30);
        cut.Render();
    }

    private static QueueSummary Queue(string name, int deadLetterSourceCount = 0, bool isFifo = false) =>
        new(name, $"https://sqs/{name}", $"arn:aws:sqs:eu-west-1:123456789012:{name}", isFifo, 10, 2, 0, false, false, DateTimeOffset.UtcNow, deadLetterSourceCount);

    [Fact]
    public async Task Lists_queues_for_the_first_available_connection()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("order-events");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Prefix_filter_is_labelled_Starts_with_and_re_queries_on_change()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", "events-", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("Starts with");
        cut.Find(".prefix-filter input").Input("events-");
        // The field's DebounceInterval="300" delays ValueChanged by real wall-clock time; the
        // 30ms delay used elsewhere in this file isn't enough to let it fire, so this assertion
        // needs a longer wait.
        await Task.Delay(400);
        cut.Render();

        await _operations.Received(1).ListQueuesAsync("mode=default-chain;region=eu-west-1", "events-", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_call_count_caption_reflects_1_plus_n_not_1_plus_3n()
    {
        // Correction from the mockup's "1 + 3n" caption -- GetQueueAttributes with
        // AttributeNames=[All] returns every attribute in one call per queue (design spec §5).
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("a"), Queue("b"), Queue("c") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".refresh-cost-caption").TextContent.Should().Contain("1 + 3 = 4");
    }

    [Fact]
    public async Task Refresh_call_count_caption_reflects_the_extra_dead_letter_lookup_under_a_prefix()
    {
        // With a prefix, each returned queue also gets a ListDeadLetterSourceQueues call so a DLQ
        // whose sources the prefix excluded is still detected (design doc §6.7.2).
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("a") });
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("orders"), Queue("orders-dlq", deadLetterSourceCount: 1), Queue("orders-x") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();
        cut.Find(".prefix-filter input").Input("orders");
        await Task.Delay(400);
        cut.Render();

        cut.Find(".refresh-cost-caption").TextContent.Should().Contain("1 + 2 × 3 = 7");
    }

    [Theory]
    [InlineData(0, false, "1 + 0 = 1")]
    [InlineData(3, false, "1 + 3 = 4")]
    [InlineData(3, true, "1 + 2 × 3 = 7")]
    public void RefreshCostFormula_matches_the_calls_ListQueuesAsync_makes(int queueCount, bool prefixApplied, string expected)
    {
        SbConsole.Plugins.Aws.Pages.Queues.RefreshCostFormula(queueCount, prefixApplied).Should().Be(expected);
    }

    [Fact]
    public async Task Connection_echo_renders_safe_fields_and_never_leaks_credentials()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3tKEY");
        _operations.ListQueuesAsync("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3tKEY", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        var echo = cut.Find(".connection-echo");
        echo.TextContent.Should().Contain("region=eu-west-1");
        cut.Markup.Should().NotContain("s3cr3tKEY");
    }

    [Fact]
    public async Task A_queue_with_a_dead_letter_target_shows_the_DLQ_flag()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events-dlq", deadLetterSourceCount: 1) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task A_source_queue_with_a_configured_redrive_policy_but_no_incoming_redrives_does_NOT_show_the_DLQ_chip_or_Redrive_button()
    {
        // "order-events" has its own RedrivePolicy (it dead-letters TO order-events-dlq), which
        // made it look like a DLQ under the old (inverted) HasDeadLetterTarget-based logic. It is
        // not itself a redrive target -- DeadLetterSourceCount is 0 -- so neither the chip nor the
        // Redrive button should render for it.
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events", deadLetterSourceCount: 0) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().BeEmpty();
        cut.FindAll(".redrive-action").Should().BeEmpty();
    }

    [Fact]
    public async Task A_queue_that_is_a_redrive_target_for_other_queues_shows_the_DLQ_chip_and_Redrive_button()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events-dlq", deadLetterSourceCount: 2) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().ContainSingle();
        cut.Find(".dlq-flag").TextContent.Should().Contain("2");
        cut.FindAll(".redrive-action").Should().ContainSingle();
    }

    [Fact]
    public async Task A_FIFO_queue_shows_the_FIFO_flag()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("billing-retry.fifo", isFifo: true) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".fifo-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task One_queues_unavailable_attributes_dont_blank_the_other_rows()
    {
        // SqsOperations.ListQueuesAsync degrades a single queue to QueueSummary.Unavailable rather
        // than throwing when its GetQueueAttributes call fails (design spec §5's partial-failure
        // resilience requirement) -- this test exercises the handler/page side of that contract via
        // a substituted ISqsOperations, since SqsOperations itself can't be unit-tested without a
        // real AWS account.
        var degraded = SbConsole.Plugins.Aws.Client.QueueSummary.Unavailable("throttled-queue", "https://sqs/throttled-queue");
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("healthy-queue"), degraded });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("healthy-queue");
        cut.Markup.Should().Contain("throttled-queue");
        cut.FindAll(".approx-visible").Select(e => e.TextContent).Should().Contain("—");
        cut.FindAll(".approx-visible").Select(e => e.TextContent).Should().Contain("~10");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "order-events", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = RenderWithPopovers();
        await Task.Delay(30);
        cut.Render();
        cut.FindAll(".delete-queue").Should().BeEmpty("Delete lives in the row overflow menu, not inline");
        await OpenRowMenuAsync(cut);
        await cut.Find(".delete-queue").ClickAsync(new());
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Delete", "order-events", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/order-events", Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task The_queue_name_links_to_the_queue_detail_page_for_the_selected_connection()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        var link = cut.Find("a.queue-detail-link");
        link.TextContent.Should().Contain("order-events");
        link.GetAttribute("href").Should().Be(
            $"/p/aws/queues/{Uri.EscapeDataString("https://sqs/order-events")}?connectionId={_connectionId}");
    }

    [Fact]
    public async Task Purge_lives_in_the_row_overflow_menu_and_goes_through_confirmation()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var confirmation = Services.GetRequiredService<IConfirmationService>();

        var cut = RenderWithPopovers();
        await Task.Delay(30);
        cut.Render();
        cut.FindAll(".purge-queue-action").Should().BeEmpty("Purge lives in the row overflow menu, not inline");
        await OpenRowMenuAsync(cut);
        await cut.Find(".purge-queue-action").ClickAsync(new());
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Purge", "order-events", _connectionInfo.IsProd, 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Created_column_shows_the_creation_date_and_a_dash_for_unavailable_rows()
    {
        var created = new DateTimeOffset(2025, 3, 14, 9, 30, 0, TimeSpan.Zero);
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>
            {
                Queue("healthy-queue") with { CreatedAt = created },
                QueueSummary.Unavailable("throttled-queue", "https://sqs/throttled-queue"),
            });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("Created");
        cut.FindAll(".queue-created").Select(e => e.TextContent.Trim()).Should().Equal("2025-03-14", "—");
    }

    [Fact]
    public async Task A_counts_read_caption_shows_the_local_time_of_the_last_load()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".counts-read-caption").TextContent.Trim().Should().Be("counts read 14:03:07 UTC");
    }

    [Fact]
    public async Task Auto_refresh_defaults_to_Off_when_nothing_is_stored()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);

        cut.Instance.AutoRefreshSeconds.Should().Be(0);
        cut.Instance.IsAutoRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task Choosing_an_auto_refresh_interval_persists_it_and_starts_the_timer()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        await cut.Find("button.auto-refresh-30").ClickAsync(new());

        await _store.Received(1).SetAsync("queues.autoRefreshSeconds", "30", Arg.Any<CancellationToken>());
        cut.Instance.AutoRefreshSeconds.Should().Be(30);
        cut.Instance.IsAutoRefreshing.Should().BeTrue();

        await cut.Find("button.auto-refresh-off").ClickAsync(new());

        await _store.Received(1).SetAsync("queues.autoRefreshSeconds", "0", Arg.Any<CancellationToken>());
        cut.Instance.IsAutoRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task A_stored_auto_refresh_interval_is_restored_on_load()
    {
        _store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>()).Returns("15");
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);

        cut.Instance.AutoRefreshSeconds.Should().Be(15);
        cut.Instance.IsAutoRefreshing.Should().BeTrue();
    }

    [Theory]
    [InlineData("7")]
    [InlineData("not-a-number")]
    public async Task An_unrecognised_stored_interval_falls_back_to_Off(string stored)
    {
        _store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>()).Returns(stored);
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);

        cut.Instance.AutoRefreshSeconds.Should().Be(0);
        cut.Instance.IsAutoRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task A_store_read_failure_falls_back_to_Off_and_still_lists_queues()
    {
        _store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new InvalidOperationException("db locked"));
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Instance.AutoRefreshSeconds.Should().Be(0);
        cut.Markup.Should().Contain("order-events");
    }

    [Fact]
    public async Task A_refresh_tick_reloads_the_queues_and_updates_the_caption()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        _operations.ClearReceivedCalls();
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events"), Queue("billing") });
        _clock.Now = _clock.Now.AddSeconds(30);

        var refreshed = await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());

        refreshed.Should().BeTrue();
        await _operations.Received(1).ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>());
        cut.Markup.Should().Contain("billing");
        cut.Find(".counts-read-caption").TextContent.Trim().Should().Be("counts read 14:03:37 UTC");
    }

    [Fact]
    public async Task A_refresh_tick_never_overlaps_an_in_flight_load()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());
        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        var pending = new TaskCompletionSource<IReadOnlyList<QueueSummary>>();
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(pending.Task);
        _operations.ClearReceivedCalls();

        var first = cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());
        var second = await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());

        second.Should().BeFalse("a tick that lands mid-load is skipped");
        await _operations.Received(1).ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>());
        pending.SetResult([]);
        (await first).Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_the_page_stops_the_auto_refresh_timer()
    {
        _store.GetAsync("queues.autoRefreshSeconds", Arg.Any<CancellationToken>()).Returns("60");
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(new List<QueueSummary>());
        var page = Render<SbConsole.Plugins.Aws.Pages.Queues>().Instance;
        await Task.Delay(30);
        page.IsAutoRefreshing.Should().BeTrue();

        await DisposeComponentsAsync();

        page.IsAutoRefreshing.Should().BeFalse();
    }

    [Fact]
    public async Task The_counts_read_caption_labels_a_non_UTC_zone_with_its_offset()
    {
        _clock.Zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".counts-read-caption").TextContent.Trim().Should().Be("counts read 16:03:07 UTC+02:00");
    }

    [Fact]
    public async Task A_failed_refresh_clears_the_counts_read_caption_and_shows_an_inline_error()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.FindAll(".counts-read-caption").Should().ContainSingle();
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<QueueSummary>>(new InvalidOperationException("throttled")));

        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());

        cut.FindAll(".counts-read-caption").Should().BeEmpty("a failed read must not keep advertising stale counts");
        cut.FindAll(".queues-load-error").Should().ContainSingle();
    }

    [Fact]
    public async Task A_failing_auto_refresh_streak_raises_one_snackbar_not_one_per_tick()
    {
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        var snackbar = Services.GetRequiredService<ISnackbar>();
        // A distinct message per tick, so MudBlazor's duplicate-snackbar suppression can't mask a
        // snackbar-per-tick regression.
        var tick = 0;
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<QueueSummary>>(new InvalidOperationException($"throttled #{++tick}")));

        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());
        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());
        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());

        snackbar.ShownSnackbars.Should().ContainSingle("the streak's first failure is announced once");
        cut.FindAll(".queues-load-error").Should().ContainSingle();

        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        await cut.InvokeAsync(() => cut.Instance.AutoRefreshTickAsync());
        cut.FindAll(".queues-load-error").Should().BeEmpty();
    }

    [Fact]
    public async Task A_slow_load_for_the_previous_connection_never_overwrites_the_newly_selected_one()
    {
        const string SecretB = "mode=default-chain;region=us-east-1";
        var connectionB = new ConnectionInfo(Guid.NewGuid(), "aws-prod", "aws", ["prod"]);
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { _connectionInfo, connectionB });
        _connections.GetSecretAsync(connectionB.Id, Arg.Any<CancellationToken>()).Returns(SecretB);
        var loadA = new TaskCompletionSource<IReadOnlyList<QueueSummary>>();
        var loadB = new TaskCompletionSource<IReadOnlyList<QueueSummary>>();
        _operations.ListQueuesAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(loadA.Task);
        _operations.ListQueuesAsync(SecretB, null, Arg.Any<CancellationToken>()).Returns(loadB.Task);

        var cut = RenderWithPopovers();
        await Task.Delay(30);
        await cut.Find(".connection-menu .mud-menu-activator").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        await Task.Delay(30);
        cut.Render();
        var switchTask = cut.FindAll(".connection-option").Single(e => e.TextContent.Contains("aws-prod")).ClickAsync(new());

        loadB.SetResult([Queue("prod-orders")]);
        await switchTask;
        loadA.SetResult([Queue("dev-orders")]);
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("prod-orders");
        cut.Markup.Should().NotContain("dev-orders", "connection A's late result must be discarded");
        cut.FindAll(".queues-busy").Should().BeEmpty();
    }
}
