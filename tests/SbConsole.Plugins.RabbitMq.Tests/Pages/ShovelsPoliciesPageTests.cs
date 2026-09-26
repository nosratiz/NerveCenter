using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Plugins.RabbitMq.Tests.Components;
using ShovelsPoliciesPage = SbConsole.Plugins.RabbitMq.Pages.ShovelsPolicies;

namespace SbConsole.Plugins.RabbitMq.Tests.Pages;

public class ShovelsPoliciesPageTests : RabbitPageTestBase
{
    private const string Vhost = SeededTopology.Vhost;

    private DateTimeOffset T0 => Clock.Now;

    private ShovelInfo Shovel(string name, string state, string? sourceQueue, string? destQueue = null, string? destExchange = null,
        string? destKey = null, string? reason = null, DateTimeOffset? timestamp = null, string sourceUri = "amqp://",
        string destUri = "amqp://", string ackMode = "on-confirm") =>
        new(name, state, reason, sourceQueue, null, sourceUri, destQueue, destExchange, destKey, destUri, ackMode, timestamp);

    private List<ShovelInfo> LiveShovels() =>
    [
        Shovel("legacy-migrate", "running", "legacy.import.q", destExchange: "order-events", destKey: "order.legacy.created",
            timestamp: T0.AddHours(-2)),
        Shovel("billing-retry-loop", "terminated", "billing.retry", destExchange: "billing.direct", reason: "econnrefused",
            timestamp: T0.AddMinutes(-4), ackMode: "on-publish"),
        Shovel("audit-offsite", "starting", "audit.sink", destQueue: "audit.sink", timestamp: T0.AddMinutes(-5),
            destUri: "amqp://shovel:hunter2@rabbit-eu-dr.example.com:5672/%2Faudit"),
    ];

    private static List<QueueSummary> Queues() =>
    [
        SeededTopology.Queue("legacy.import.q") with { Ready = 12 },
        SeededTopology.Queue("billing.retry") with { Ready = 3 },
        SeededTopology.Queue("audit.sink"),
        SeededTopology.Queue("order-events.q"),
        SeededTopology.Queue("payments-dlq"),
    ];

    private static PolicyInfo Policy(string name, string pattern, int priority, Dictionary<string, object?> definition, string applyTo = "queues") =>
        new(name, pattern, applyTo, priority, definition);

    private static List<PolicyInfo> Policies() =>
    [
        Policy("retry-shortttl", @"\.retry$", 5, new() { ["message-ttl"] = 30000L, ["dead-letter-exchange"] = "billing.direct" }),
        Policy("dlq-ttl", "-dlq$", 5, new() { ["message-ttl"] = 604800000L }),
        Policy("orders-limits", "^order-events", 10, new() { ["max-length"] = 100000L, ["overflow"] = "reject-publish" }),
    ];

    private void Seed(List<ShovelInfo>? shovels = null, List<PolicyInfo>? policies = null)
    {
        Operations.ListShovelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(shovels ?? LiveShovels());
        Operations.ListQueuesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(Queues());
        Operations.ListExchangesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(SeededTopology.Exchanges());
        Operations.ListPoliciesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>()).Returns(policies ?? Policies());
    }

    private async Task<IRenderedComponent<Bunit.Rendering.ContainerFragment>> RenderPageAsync(string? query = null)
    {
        if (query is not null)
        {
            Navigation.NavigateTo("/p/rabbitmq/shovels?" + query);
        }

        RenderFragment page = builder =>
        {
            builder.OpenComponent<ShovelsPoliciesPage>(0);
            builder.CloseComponent();
        };
        var cut = RenderWithPopovers(page);
        await SettleAsync(cut);
        return cut;
    }

    private static IElement ShovelRow(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".shovel-row").Single(r => r.QuerySelector(".shovel-name")!.TextContent.Trim() == name);

    private static IElement PolicyRow(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, string name) =>
        cut.FindAll(".policy-row").Single(r => r.QuerySelector(".policy-name")!.TextContent.Trim() == name);

    private static string Text(IElement element, string selector) => element.QuerySelector(selector)!.TextContent.Trim();

    [Fact]
    public async Task Lists_shovels_as_source_to_destination_with_ack_mode_and_state_chips()
    {
        Seed();

        var cut = await RenderPageAsync();

        cut.Find(".shovels-heading").TextContent.Should().Contain("Dynamic shovels — 3");
        cut.FindAll(".shovel-row").Should().HaveCount(3);

        var legacy = ShovelRow(cut, "legacy-migrate");
        Text(legacy, ".shovel-route").Should().Be("legacy.import.q → order-events · key order.legacy.created");
        Text(legacy, ".shovel-ack").Should().Be("on-confirm");
        legacy.QuerySelector(".shovel-state")!.TextContent.Should().Contain("running");
        legacy.QuerySelector(".shovel-state")!.ClassList.Should().Contain("mud-chip-color-success");
        legacy.QuerySelector(".shovel-remote").Should().BeNull("amqp:// is this broker");

        var billing = ShovelRow(cut, "billing-retry-loop");
        billing.QuerySelector(".shovel-state")!.ClassList.Should().Contain("mud-chip-color-error");
        Text(billing, ".shovel-ack").Should().Be("on-publish");

        ShovelRow(cut, "audit-offsite").QuerySelector(".shovel-state")!.ClassList.Should().Contain("mud-chip-color-info");

        cut.Find(".shovel-deviation").TextContent.Should().Contain(
            "Transfer rate and pause/resume aren't shown — the shovel status API reports no rate, and pausing is rabbitmqctl-only.");
        cut.Find(".new-shovel").Should().NotBeNull();
    }

    [Fact]
    public async Task A_terminated_shovel_gets_an_explanation_row_with_the_messages_holding_in_its_source()
    {
        Seed();

        var cut = await RenderPageAsync();

        cut.FindAll(".shovel-explanation").Select(e => e.TextContent.Trim()).Should().Contain(
            "billing-retry-loop failed 4m ago — econnrefused. 3 messages holding in billing.retry.");
    }

    [Fact]
    public async Task A_shovel_starting_for_over_a_minute_gets_a_softer_explanation()
    {
        Seed();

        var cut = await RenderPageAsync();

        cut.FindAll(".shovel-explanation").Select(e => e.TextContent.Trim()).Should().Contain(
            "audit-offsite has been starting since 13:58 — it's likely retrying an unreachable endpoint.");
        cut.FindAll(".shovel-explanation").Should().HaveCount(2, "the running shovel needs no explanation");
    }

    [Fact]
    public async Task A_recently_started_shovel_gets_no_explanation()
    {
        Seed([Shovel("fresh", "starting", "audit.sink", destQueue: "x", timestamp: T0.AddSeconds(-20))]);

        var cut = await RenderPageAsync();

        cut.FindAll(".shovel-explanation").Should().BeEmpty();
    }

    [Fact]
    public async Task A_remote_uri_shows_only_its_host_never_the_credentials()
    {
        Seed();

        var cut = await RenderPageAsync();

        var audit = ShovelRow(cut, "audit-offsite");
        Text(audit, ".shovel-route").Should().Be("audit.sink → audit.sink");
        Text(audit, ".shovel-remote").Should().Be("via rabbit-eu-dr.example.com");
        cut.Markup.Should().NotContain("hunter2").And.NotContain("shovel:");
    }

    [Fact]
    public async Task A_uri_naming_this_brokers_vhost_is_not_remote()
    {
        Seed([Shovel("local", "running", "audit.sink", destQueue: "x", sourceUri: "amqp:///%2Forders", destUri: "amqp://")]);

        var cut = await RenderPageAsync();

        ShovelRow(cut, "local").QuerySelector(".shovel-remote").Should().BeNull();
    }

    [Fact]
    public async Task Exchange_sourced_shovel_names_the_exchange()
    {
        Seed([new ShovelInfo("from-ex", "running", null, null, "order-events", "amqp://", "sink.q", null, null, "amqp://", "on-confirm", null)]);

        var cut = await RenderPageAsync();

        Text(ShovelRow(cut, "from-ex"), ".shovel-route").Should().Be("exchange order-events → sink.q");
    }

    [Fact]
    public async Task No_shovels_shows_the_empty_state()
    {
        Seed([]);

        var cut = await RenderPageAsync();

        cut.Find(".shovels-empty").TextContent.Should().Contain("No dynamic shovels in /orders.");
        cut.Find(".shovels-heading").TextContent.Should().Contain("Dynamic shovels — 0");
    }

    [Fact]
    public async Task Missing_shovel_plugin_is_an_info_alert_and_hides_new_shovel()
    {
        Seed();
        Operations.ListShovelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(404, "GET", "/api/shovels/%2Forders", "Object Not Found"));

        var cut = await RenderPageAsync();

        var alert = cut.Find(".shovel-plugin-missing");
        alert.TextContent.Should().Contain(
            "The shovel management plugin isn't enabled on this broker (rabbitmq-plugins enable rabbitmq_shovel_management).");
        alert.ClassList.Should().Contain("mud-alert-text-info");
        cut.FindAll(".new-shovel").Should().BeEmpty();
        cut.FindAll(".shovels-panel .management-error").Should().BeEmpty();
    }

    [Fact]
    public async Task Restart_confirms_then_calls_the_handler_and_reloads()
    {
        Seed();
        Confirmation.ConfirmAsync("Restart", "billing-retry-loop", false, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await ShovelRow(cut, "billing-retry-loop").QuerySelector(".restart-shovel")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).RestartShovelAsync(DevSecret, Vhost, "billing-retry-loop", Arg.Any<CancellationToken>());
        await Operations.Received(2).ListShovelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_confirms_then_calls_the_handler()
    {
        Seed();
        Confirmation.ConfirmAsync("Delete", "legacy-migrate", false, null, Arg.Any<CancellationToken>()).Returns(true);
        var cut = await RenderPageAsync();

        await ShovelRow(cut, "legacy-migrate").QuerySelector(".delete-shovel")!.ClickAsync(new());
        await SettleAsync(cut);

        await Operations.Received(1).DeleteShovelAsync(DevSecret, Vhost, "legacy-migrate", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelled_restart_and_delete_call_nothing()
    {
        Seed();
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(false);
        var cut = await RenderPageAsync();

        await ShovelRow(cut, "legacy-migrate").QuerySelector(".restart-shovel")!.ClickAsync(new());
        await ShovelRow(cut, "legacy-migrate").QuerySelector(".delete-shovel")!.ClickAsync(new());
        await SettleAsync(cut);

        await Confirmation.Received(1).ConfirmAsync("Restart", "legacy-migrate", false, null, Arg.Any<CancellationToken>());
        await Confirmation.Received(1).ConfirmAsync("Delete", "legacy-migrate", false, null, Arg.Any<CancellationToken>());
        await Operations.DidNotReceiveWithAnyArgs().RestartShovelAsync(default!, default!, default!, default);
        await Operations.DidNotReceiveWithAnyArgs().DeleteShovelAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task A_failed_restart_shows_a_snackbar()
    {
        Seed();
        Confirmation.ConfirmAsync(default!, default!, default).ReturnsForAnyArgs(true);
        Operations.RestartShovelAsync(DevSecret, Vhost, "legacy-migrate", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "DELETE", "/api/shovels/vhost/%2Forders/legacy-migrate/restart", "access refused"));
        var cut = await RenderPageAsync();

        await ShovelRow(cut, "legacy-migrate").QuerySelector(".restart-shovel")!.ClickAsync(new());
        await SettleAsync(cut);

        var snackbar = Services.GetService(typeof(ISnackbar)) as ISnackbar;
        snackbar!.ShownSnackbars.Should().ContainSingle(s => s.Severity == Severity.Error);
    }

    [Fact]
    public async Task Policies_are_sorted_by_priority_then_name_with_definition_and_match_counts()
    {
        Seed();

        var cut = await RenderPageAsync("tab=policies");

        cut.Find(".policies-heading").TextContent.Should().Contain("Policies — 3, applied by pattern");
        cut.FindAll(".policy-row .policy-name").Select(e => e.TextContent.Trim())
            .Should().Equal("orders-limits", "dlq-ttl", "retry-shortttl");

        var orders = PolicyRow(cut, "orders-limits");
        Text(orders, ".policy-pattern").Should().Be("^order-events");
        Text(orders, ".policy-applies").Should().Be("queues");
        Text(orders, ".policy-definition").Should().Be("max-length: 100,000 · overflow: reject-publish");
        Text(orders, ".policy-matches").Should().Be("1");
        Text(orders, ".policy-priority").Should().Be("10");

        Text(PolicyRow(cut, "dlq-ttl"), ".policy-matches").Should().Be("1");
        Text(PolicyRow(cut, "dlq-ttl"), ".policy-definition").Should().Be("message-ttl: 604,800,000");
        Text(PolicyRow(cut, "retry-shortttl"), ".policy-matches").Should().Be("1");
        Text(PolicyRow(cut, "retry-shortttl"), ".policy-definition").Should().Be("message-ttl: 30,000 · dead-letter-exchange: billing.direct");

        cut.Find(".policies-caption").TextContent.Should().Contain(
            "A policy overrides queue arguments silently — queue detail names the winning policy beside the arguments.");
    }

    [Fact]
    public async Task Invalid_regex_shows_a_question_mark_with_an_explanation()
    {
        Seed(policies:
        [
            Policy("broken", "([", 1, new() { ["federation-upstream-set"] = new List<object?> { "a", "b" } }),
        ]);

        var cut = await RenderPageAsync("tab=policies");

        var broken = PolicyRow(cut, "broken");
        Text(broken, ".policy-matches").Should().Be("?");
        broken.QuerySelector(".policy-matches")!.GetAttribute("title").Should().Be("The pattern isn't a valid .NET regex or timed out");
        Text(broken, ".policy-definition").Should().Be("federation-upstream-set: a, b");
    }

    [Fact]
    public async Task No_policies_shows_the_empty_state()
    {
        Seed(policies: []);

        var cut = await RenderPageAsync("tab=policies");

        cut.Find(".policies-empty").TextContent.Should().Contain("No policies in /orders.");
    }

    [Fact]
    public async Task Tab_query_selects_policies_and_keeps_the_other_parameters()
    {
        Seed();

        var cut = await RenderPageAsync($"tab=policies&connectionId={Dev.Id}&vhost=%2Forders");

        cut.Find(".mud-tab.mud-tab-active").TextContent.Should().Contain("Policies");
        Navigation.Uri.Should().Contain("tab=policies").And.Contain($"connectionId={Dev.Id}");
    }

    [Fact]
    public async Task Default_tab_is_shovels_and_switching_writes_the_tab_into_the_url()
    {
        Seed();
        var cut = await RenderPageAsync();

        var tabs = cut.FindComponent<MudTabs>();
        cut.Find(".mud-tab.mud-tab-active").TextContent.Should().Contain("Shovels");

        await cut.InvokeAsync(() => tabs.Instance.ActivePanelIndexChanged.InvokeAsync(1));
        Navigation.Uri.Should().Contain("tab=policies").And.Contain("connectionId=");
    }

    [Fact]
    public async Task A_policies_failure_shows_its_own_error_while_shovels_render()
    {
        Seed();
        Operations.ListPoliciesAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(403, "GET", "/api/policies/%2Forders", "Access refused."));

        var cut = await RenderPageAsync();

        cut.Find(".policies-panel .management-error").TextContent.Should().Contain("403");
        cut.FindAll(".shovels-panel .management-error").Should().BeEmpty();
        cut.FindAll(".shovel-row").Should().HaveCount(3);
    }

    [Fact]
    public async Task A_shovels_failure_shows_its_own_error_while_policies_render()
    {
        Seed();
        Operations.ListShovelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(500, "GET", "/api/shovels/%2Forders", "boom"));

        var cut = await RenderPageAsync("tab=policies");

        cut.Find(".shovels-panel .management-error").TextContent.Should().Contain("500");
        cut.FindAll(".policy-row").Should().HaveCount(3);
    }

    [Fact]
    public async Task Refresh_failure_keeps_rows_under_a_stale_banner()
    {
        Seed();
        var cut = await RenderPageAsync();

        Clock.Now = Clock.Now.AddMinutes(2);
        Operations.ListShovelsAsync(DevSecret, Vhost, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ManagementApiException(503, "GET", "/api/shovels/%2Forders", "Service Unavailable"));
        await cut.Find(".refresh-shovels").ClickAsync(new());
        await SettleAsync(cut);

        cut.Find(".shovels-panel .stale-banner").TextContent.Should().Contain("Showing values from 14:03, 2m ago.");
        cut.FindAll(".shovels-panel .stale-data .shovel-row").Should().HaveCount(3);
        cut.FindAll(".policies-panel .stale-banner").Should().BeEmpty();
    }
}
