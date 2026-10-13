using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;
using SbConsole.Web.Plugins;
using SbConsole.Web.Wallboard;

namespace SbConsole.Web.Tests;

public class HomeTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first -- disposing those services
    // the async-safe way before the base (synchronous) Dispose() runs as a no-op afterwards.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _testDb.Dispose();
    }

    private readonly TestDb _testDb = new();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IConnectionProvider _connectionProvider = Substitute.For<IConnectionProvider>();
    private static readonly byte[] Key = new byte[32];

    private IPlugin[] _plugins = [];
    private readonly IPluginStoreFactory _pluginStoreFactory = Substitute.For<IPluginStoreFactory>();

    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
        Services.AddSingleton<IEnumerable<IPlugin>>(_ => _plugins);
        Services.AddSingleton<PluginRegistry>();
        Services.AddSingleton(_connectionProvider);
        Services.AddSingleton(_audit);
        Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(Key));
        Services.AddSingleton(new FakeTimeProvider());
        Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FakeTimeProvider>());
        Services.AddLogging();
        Services.AddSingleton<TestConnectionCommandHandler>();
        _pluginStoreFactory.For(Arg.Any<string>()).Returns(Substitute.For<IPluginStore>());
        Services.AddSingleton(_pluginStoreFactory);
        Services.AddSingleton<WallboardSnapshotLoader>();
    }

    [Fact]
    public void Shows_one_quiet_all_clear_row_when_nothing_needs_attention()
    {
        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".triage-all-clear").Count > 0);

        cut.Find(".triage-all-clear").TextContent.Should().Contain("nothing needs attention");
    }

    [Fact]
    public async Task Shows_recent_activity_from_real_audit_entries()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "admin", Action = "auth.login",
                Target = "-", Risk = ActionRisk.Safe, Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("auth.login"));

        cut.Markup.Should().Contain("auth.login");
    }

    [Fact]
    public async Task Header_summary_counts_connections_plugins_and_problems()
    {
        var ids = await SeedConnections(("sb-dev", "azure-servicebus"), ("sb-uk-prod", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(ids[1], "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem> { new("Warning", "payments-dlq", "5 dead-lettered", null) });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Find(".dash-summary").TextContent.Should().Be("2 connections · 1 plugin · 1 needs attention");
    }

    [Fact]
    public async Task Fleet_shows_one_card_per_plugin_with_its_own_metrics_instead_of_summing_same_labels_across_plugins()
    {
        await SeedConnections(("sb-dev", "azure-servicebus"), ("kafka-dev", "kafka"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var serviceBus = FakePlugin("azure-servicebus", "Service Bus", "SB");
        serviceBus.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Queues", 14), new("Topics", 6) });
        var kafka = FakePlugin("kafka", "Kafka", "KF");
        kafka.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Topics", 38), new("Partitions", 1214) });
        _plugins = [serviceBus, kafka];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("38 topics"));

        var sbCard = cut.Find(".fleet-card[data-plugin='azure-servicebus']");
        sbCard.TextContent.Should().Contain("SB").And.Contain("Service Bus")
            .And.Contain("14 queues").And.Contain("6 topics");
        var kafkaCard = cut.Find(".fleet-card[data-plugin='kafka']");
        kafkaCard.TextContent.Should().Contain("38 topics").And.Contain("1,214 partitions");
        cut.Markup.Should().NotContain("44 topics");
    }

    [Fact]
    public async Task Fleet_card_shows_a_dot_per_connection_and_a_problem_count_colored_by_worst_severity()
    {
        var ids = await SeedConnections(("sb-uk-prod", "azure-servicebus"), ("sb-dev", "azure-servicebus"));
        await MarkUnreachable("sb-eu-stage", "azure-servicebus");
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(ids[0], "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem> { new("Warning", "payments-dlq", "5 dead-lettered", null) });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        var card = cut.Find(".fleet-card[data-plugin='azure-servicebus']");
        var count = card.QuerySelector(".fleet-count")!;
        count.TextContent.Should().Be("2");
        count.ClassList.Should().Contain("bad");
        card.ClassList.Should().Contain("has-error");
        card.QuerySelector(".fleet-dot[title='sb-uk-prod']")!.ClassList.Should().Contain("warn");
        card.QuerySelector(".fleet-dot[title='sb-dev']")!.ClassList.Should().Contain("ok");
        card.QuerySelector(".fleet-dot[title='sb-eu-stage']")!.ClassList.Should().Contain("bad");
        card.QuerySelector(".fleet-conn")!.TextContent.Should().Contain("3 conn");
    }

    [Fact]
    public async Task Unreachable_connection_shows_a_triage_row_with_a_working_retry_button()
    {
        await MarkUnreachable("sb-eu-prod", "azure-servicebus");

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("sb-eu-prod"));

        var row = cut.Find(".triage-row.sev-error");
        row.TextContent.Should().Contain("sb-eu-prod").And.Contain("Unauthorized (401)");

        // No plugin is registered in this test host, so TestConnectionCommandHandler fails fast
        // with "no plugin registered" rather than touching the connection row. The button round-
        // tripping through the real handler and re-rendering afterwards (busy flag cleared, no
        // unhandled exception, the connection still listed) is the observable proof it's wired up.
        cut.Find("button.retry-connection").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("sb-eu-prod"));
    }

    [Fact]
    public async Task Plugin_reported_problems_render_as_triage_rows_carrying_the_plugin_monogram_and_connection()
    {
        await SeedConnections(("sb-uk-prod", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>
            {
                new("Warning", "payments-dlq", "214 dead-lettered", "/p/azure-servicebus/dead-letter"),
            });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        var row = cut.Find(".triage-row.sev-warning");
        row.QuerySelector(".triage-mono")!.TextContent.Should().Be("SB");
        row.QuerySelector(".triage-conn")!.TextContent.Should().Be("sb-uk-prod");
        row.TextContent.Should().Contain("payments-dlq").And.Contain("214 dead-lettered");
        row.QuerySelector("a")!.GetAttribute("href").Should().Be("/p/azure-servicebus/dead-letter");
    }

    [Fact]
    public async Task All_clear_requires_both_zero_unreachable_connections_and_zero_plugin_problems()
    {
        await SeedConnections(("sb-dev", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        _plugins = [FakePlugin("azure-servicebus", "Service Bus", "SB")];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".triage-all-clear").Count > 0);

        cut.Find(".triage-all-clear").TextContent.Should().Contain("All 1 connection checked");
    }

    [Fact]
    public async Task A_plugin_that_throws_gets_an_unchecked_row_naming_its_connection_instead_of_All_clear()
    {
        await SeedConnections(("sb-uk-prod", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PluginDashboardProblem>>(new InvalidOperationException("boom")));
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".triage-row.sev-unchecked").Count > 0);

        cut.FindAll(".triage-all-clear").Should().BeEmpty();
        var row = cut.Find(".triage-row.sev-unchecked");
        row.TextContent.Should().Contain("sb-uk-prod").And.Contain("Couldn't check");
        row.TextContent.Should().NotContain("boom");
        cut.Find(".fleet-dot[title='sb-uk-prod']").ClassList.Should().Contain("unchecked");
    }

    [Fact]
    public async Task An_unreachable_connection_whose_plugin_check_also_fails_is_listed_once_not_twice()
    {
        await MarkUnreachable("sb-eu-stage", "azure-servicebus");
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PluginDashboardMetric>>(new InvalidOperationException("down")));
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".triage-row").Count > 0 && cut.FindAll(".triage-row.sev-pending").Count == 0);

        cut.FindAll(".triage-row").Should().ContainSingle().Which.ClassList.Should().Contain("sev-error");
    }

    [Fact]
    public async Task Results_render_per_connection_as_they_land_while_slower_checks_show_as_pending()
    {
        var ids = await SeedConnections(("sb-fast", "azure-servicebus"), ("sb-slow", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(ids[0], Arg.Any<CancellationToken>()).Returns("fast");
        _connectionProvider.GetSecretAsync(ids[1], Arg.Any<CancellationToken>()).Returns("slow");
        var release = new TaskCompletionSource<IReadOnlyList<PluginDashboardProblem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(ids[0], "fast", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem> { new("Warning", "payments-dlq", "5 dead-lettered", null) });
        plugin.GetDashboardProblemsAsync(ids[1], "slow", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Find(".triage-row.sev-pending .triage-conn").TextContent.Should().Be("sb-slow");
        cut.Find(".triage-progress").TextContent.Should().Be("checking 1 of 2…");
        cut.Find(".fleet-dot[title='sb-slow']").ClassList.Should().Contain("pending");

        release.SetResult([]);
        cut.WaitForState(() => cut.FindAll(".triage-row.sev-pending").Count == 0);
        cut.FindAll(".triage-progress").Should().BeEmpty();
    }

    [Fact]
    public async Task Connections_are_checked_concurrently_so_one_slow_broker_does_not_serialize_the_rest()
    {
        // Each connection's metrics call only completes once the OTHER connection's call has
        // started -- a sequential loop deadlocks on the first one, a concurrent one finishes. Stands
        // in for a stopped broker: before this, every down broker added its full connect timeout to
        // the Dashboard's load time, one after another.
        var (plugin, _) = await SeedTwoConnectionsThatWaitForEachOther();
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("3 queues"), TimeSpan.FromSeconds(5));

        cut.Find(".fleet-card[data-plugin='azure-servicebus']").TextContent.Should().Contain("3 queues");
    }

    // Two connections of one plugin, with secrets "secret-a"/"secret-b" and one Queues metric each
    // (1 and 2), whose metrics calls each wait for the other's to start.
    private async Task<(IPlugin Plugin, (Guid A, Guid B) Ids)> SeedTwoConnectionsThatWaitForEachOther()
    {
        var ids = await SeedConnections(("sb-a", "azure-servicebus"), ("sb-b", "azure-servicebus"));
        var (a, b) = (ids[0], ids[1]);

        _connectionProvider.GetSecretAsync(a, Arg.Any<CancellationToken>()).Returns("secret-a");
        _connectionProvider.GetSecretAsync(b, Arg.Any<CancellationToken>()).Returns("secret-b");

        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardMetricsAsync("secret-a", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            aStarted.TrySetResult();
            await bStarted.Task;
            return (IReadOnlyList<PluginDashboardMetric>)[new("Queues", 1)];
        });
        plugin.GetDashboardMetricsAsync("secret-b", Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            bStarted.TrySetResult();
            await aStarted.Task;
            return (IReadOnlyList<PluginDashboardMetric>)[new("Queues", 2)];
        });
        return (plugin, (a, b));
    }

    [Fact]
    public async Task Needs_attention_sorts_unreachable_before_plugin_errors_before_warnings_before_unchecked()
    {
        await MarkUnreachable("sb-eu-prod", "azure-servicebus");
        var ids = await SeedConnections(("sb-uk-prod", "azure-servicebus"), ("sb-broken", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(ids[0], "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>
            {
                new("Warning", "payments-dlq", "5 dead-lettered", null),
                new("Error", "namespace-outage", "connectivity lost", null),
            });
        plugin.GetDashboardProblemsAsync(ids[1], "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PluginDashboardProblem>>(new InvalidOperationException("boom")));
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq") && cut.FindAll(".triage-row.sev-pending").Count == 0);

        var rows = cut.FindAll(".triage-row").Select(r => r.TextContent).ToList();
        rows.Should().HaveCount(4);
        rows[0].Should().Contain("sb-eu-prod");
        rows[1].Should().Contain("namespace-outage");
        rows[2].Should().Contain("payments-dlq");
        rows[3].Should().Contain("sb-broken");
    }

    [Fact]
    public async Task Severity_filter_chips_narrow_the_triage_list()
    {
        var ids = await SeedConnections(("sb-uk-prod", "azure-servicebus"));
        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");
        var plugin = FakePlugin("azure-servicebus", "Service Bus", "SB");
        plugin.GetDashboardProblemsAsync(ids[0], "secret", Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>
            {
                new("Error", "namespace-outage", "connectivity lost", null),
                new("Warning", "payments-dlq", "5 dead-lettered", null),
            });
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("payments-dlq"));

        cut.Find(".triage-filter[data-filter='all']").TextContent.Should().Be("All 2");
        cut.Find(".triage-filter[data-filter='error']").TextContent.Should().Be("Errors 1");

        cut.Find(".triage-filter[data-filter='error']").Click();

        cut.FindAll(".triage-row").Should().ContainSingle().Which.TextContent.Should().Contain("namespace-outage");
        cut.Find(".triage-filter[data-filter='error']").ClassList.Should().Contain("active");
    }

    private async Task<Guid[]> SeedConnections(params (string Name, string Kind)[] connections)
    {
        await using var db = _testDb.CreateDbContext();
        var entities = connections.Select(c => new Connection { Name = c.Name, Kind = c.Kind, SecretCiphertext = [1] }).ToList();
        db.Connections.AddRange(entities);
        await db.SaveChangesAsync();
        return [.. entities.Select(e => e.Id)];
    }

    private async Task MarkUnreachable(string name, string kind)
    {
        await using var db = _testDb.CreateDbContext();
        db.Connections.Add(new Connection
        {
            Name = name, Kind = kind, SecretCiphertext = [1],
            LastTestedAt = DateTimeOffset.UtcNow, LastTestSucceeded = false, LastTestError = "Unauthorized (401)",
        });
        await db.SaveChangesAsync();
    }

    private static IPlugin FakePlugin(string id, string displayName, string monogram)
    {
        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns(id);
        plugin.ConnectionKind.Returns(id);
        plugin.DisplayName.Returns(displayName);
        plugin.Monogram.Returns(monogram);
        plugin.GetDashboardMetricsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric>());
        plugin.GetDashboardProblemsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IPluginStore>(), Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardProblem>());
        return plugin;
    }

    [Fact]
    public async Task Activity_panel_still_shows_real_audit_entries_and_links_to_the_audit_log()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTimeOffset.UtcNow, Actor = "admin", Action = "queue.purge",
                Target = "sb-uk-prod / payments-dlq", Risk = ActionRisk.Destructive, Succeeded = true,
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("queue.purge"));

        cut.Markup.Should().Contain("queue.purge");
        cut.Markup.Should().Contain("sb-uk-prod / payments-dlq");
        cut.Find("a[href='/audit']").Should().NotBeNull();
    }

    [Fact]
    public void Header_links_to_the_wallboard()
    {
        var cut = Render<Home>();

        cut.Find("a.open-wallboard").GetAttribute("href").Should().Be("/wallboard");
    }

    [Fact]
    public async Task Dashboard_embeds_a_wallboard_widget_with_a_link_to_the_full_page()
    {
        await using (var db = _testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Name = "sb-uk-prod", Kind = "azure-servicebus", SecretCiphertext = [1] });
            await db.SaveChangesAsync();
        }

        _connectionProvider.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("secret");

        var plugin = Substitute.For<IPlugin>();
        plugin.Id.Returns("azure-servicebus");
        plugin.ConnectionKind.Returns("azure-servicebus");
        plugin.GetDashboardMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginDashboardMetric> { new("Dead-lettered", 42) });
        plugin.GetResourceMetricsAsync("secret", Arg.Any<CancellationToken>())
            .Returns(new List<PluginResourceMetric>());
        plugin.GetOldestDeadLetterAsync(Arg.Any<Guid>(), "secret", Arg.Any<CancellationToken>())
            .Returns((OldestDeadLetterEntry?)null);
        _plugins = [plugin];

        var cut = Render<Home>();
        cut.WaitForState(() => cut.FindAll(".wallboard-widget .tile-dead-lettered").Count > 0);

        cut.Find(".wallboard-widget .tile-dead-lettered").TextContent.Should().Contain("42");
        cut.Find("a.open-full-wallboard").GetAttribute("href").Should().Be("/wallboard");
    }
}
