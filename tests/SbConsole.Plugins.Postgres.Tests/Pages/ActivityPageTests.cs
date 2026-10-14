using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NSubstitute;
using SbConsole.Plugins.Postgres.Activity;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

public class ActivityPageTests : PgPageTestBase
{
    private const int Blocker = 48213;
    private const int OwnPid = 5;

    private PgBackend B(int pid, string state = "active", string user = "app_rw", string app = "api", int[]? blockedBy = null,
        int xactMinutesAgo = 1, TimeSpan? duration = null, string? relation = null, string query = "select 1") =>
        new(pid, user, app, "10.0.4.18", state, null, query, Time.GetUtcNow().AddMinutes(-xactMinutesAgo), null,
            state == "idle" ? null : duration ?? TimeSpan.FromMinutes(xactMinutesAgo), blockedBy ?? [], relation, "app");

    private IReadOnlyList<PgBackend> Chain() =>
    [
        B(Blocker, "idle in transaction", xactMinutesAgo: 14, duration: TimeSpan.FromSeconds(842)),
        B(48230, blockedBy: [Blocker], xactMinutesAgo: 9, relation: "orders", query: "update orders set status = 'x'"),
        B(48231, blockedBy: [Blocker], xactMinutesAgo: 8, relation: "orders"),
        B(48232, blockedBy: [48230], xactMinutesAgo: 7),
        B(OwnPid, app: "sbconsole", user: "svc", xactMinutesAgo: 0, duration: TimeSpan.FromSeconds(1)),
        B(60, "idle", xactMinutesAgo: 30),
        B(61, "idle", xactMinutesAgo: 31),
    ];

    private void Arrange(IReadOnlyList<PgBackend>? backends = null) =>
        Operations.GetActivityAsync(Secret, Arg.Any<CancellationToken>())
            .Returns(_ => new PgActivity(OwnPid, Time.GetUtcNow(), backends ?? Chain()));

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage()
    {
        Nav.NavigateTo($"/p/postgres/activity?connectionId={ConnectionId}");
        var cut = RenderWithPopovers(b =>
        {
            b.OpenComponent<SbConsole.Plugins.Postgres.Pages.Activity>(0);
            b.CloseComponent();
        });
        cut.WaitForElement(".pg-activity-table");
        cut.WaitForState(() => cut.FindAll(".pg-activity-row").Count > 0 || cut.FindAll(".pg-activity-empty").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static AngleSharp.Dom.IElement RowOf(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int pid) =>
        cut.FindAll(".pg-activity-row").First(r => r.QuerySelector($"#pg-backend-{pid}") is not null);

    private int Calls(string name) => Operations.ReceivedCalls().Count(c => c.GetMethodInfo().Name == name);

    [Fact]
    public void Summary_counts_backends_blocked_and_idle_in_transaction()
    {
        Arrange();
        var cut = RenderPage();

        cut.Find(".pg-activity-summary").TextContent.Should().Be("7 backends · 3 blocked · 1 idle in txn");
    }

    [Fact]
    public void Victims_are_indented_under_their_blocker_with_chips()
    {
        Arrange();
        var cut = RenderPage();

        var rows = cut.FindAll(".pg-activity-row");
        rows.Select(r => r.QuerySelector(".pg-pid")!.TextContent.Replace("↳", "").Trim()).Take(5)
            .Should().Equal("48213", "48230", "48232", "48231", "5");
        rows[0].ClassList.Should().NotContain(c => c.StartsWith("pg-depth-"));
        rows[1].ClassList.Should().Contain("pg-depth-1");
        rows[2].ClassList.Should().Contain("pg-depth-2");
        rows[1].TextContent.Should().Contain("↳");
        rows[0].QuerySelector(".pg-blocks-chip")!.TextContent.Should().Contain("blocks 2");
        RowOf(cut, OwnPid).QuerySelector(".pg-own-chip")!.TextContent.Should().Contain("this console");
        RowOf(cut, Blocker).QuerySelector(".pg-own-chip").Should().BeNull();
    }

    [Fact]
    public void Banner_names_the_idle_in_transaction_blocker_and_its_relation()
    {
        Arrange();
        var cut = RenderPage();

        var text = cut.Find(".pg-blocker-banner").TextContent;
        text.Should().Contain("pid 48213 has been idle in transaction for 14m 02s and is blocking 2 sessions on orders.")
            .And.Contain("Cancelling does nothing to an idle backend — terminating it will.")
            .And.Contain("Jump to blocker");
    }

    [Fact]
    public void Banner_omits_the_relation_when_no_victim_has_one()
    {
        Arrange(
        [
            B(Blocker, "idle in transaction", xactMinutesAgo: 14, duration: TimeSpan.FromSeconds(842)),
            B(48230, blockedBy: [Blocker]),
        ]);
        var cut = RenderPage();

        cut.Find(".pg-blocker-banner").TextContent.Should().Contain("is blocking 1 session.").And.NotContain(" on ");
    }

    [Fact]
    public void No_banner_when_the_worst_blocker_is_active()
    {
        Arrange([B(10, "active", xactMinutesAgo: 5), B(11, blockedBy: [10])]);
        var cut = RenderPage();

        cut.FindAll(".pg-blocker-banner").Should().BeEmpty();
    }

    [Fact]
    public void Jump_to_blocker_scrolls_to_the_blocker_row()
    {
        Arrange();
        var module = JSInterop.SetupModule("./_content/SbConsole.Plugins.Postgres/postgres.js");
        module.SetupVoid("scrollToId", _ => true).SetVoidResult();
        var cut = RenderPage();

        cut.Find(".pg-jump-blocker").Click();

        module.VerifyInvoke("scrollToId").Arguments[0].Should().Be($"pg-backend-{Blocker}");
    }

    [Fact]
    public void Cancel_is_disabled_for_idle_backends_and_the_console_s_own_row()
    {
        Arrange();
        var cut = RenderPage();

        RowOf(cut, Blocker).QuerySelector(".pg-cancel")!.HasAttribute("disabled").Should().BeTrue();
        RowOf(cut, OwnPid).QuerySelector(".pg-cancel")!.HasAttribute("disabled").Should().BeTrue();
        RowOf(cut, 48230).QuerySelector(".pg-cancel")!.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Cancel_confirms_plainly_calls_the_handler_and_reloads()
    {
        Arrange();
        Confirmation.ConfirmAsync("Cancel query of", "pid 48230", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        Operations.GetBackendAsync(Secret, 48230, Arg.Any<CancellationToken>()).Returns(B(48230));
        Operations.CancelBackendAsync(Secret, 48230, Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();
        var before = Calls(nameof(IPostgresOperations.GetActivityAsync));

        RowOf(cut, 48230).QuerySelector(".pg-cancel")!.Click();

        cut.WaitForAssertion(() => Calls(nameof(IPostgresOperations.GetActivityAsync)).Should().Be(before + 1));
        Operations.Received(1).CancelBackendAsync(Secret, 48230, Arg.Any<CancellationToken>());
        Services.GetRequiredService<ISnackbar>().ShownSnackbars.Should().Contain(s => s.Message!.Contains("48230"));
    }

    [Fact]
    public void Declining_the_cancel_confirmation_calls_nothing()
    {
        Arrange();
        Confirmation.ConfirmAsync("Cancel query of", "pid 48230", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        RowOf(cut, 48230).QuerySelector(".pg-cancel")!.Click();

        cut.WaitForAssertion(() => Confirmation.Received(1).ConfirmAsync("Cancel query of", "pid 48230", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()));
        Operations.DidNotReceive().CancelBackendAsync(Secret, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_failed_cancel_shows_the_error_in_a_snackbar()
    {
        Arrange();
        Operations.GetBackendAsync(Secret, 48230, Arg.Any<CancellationToken>()).Returns((PgBackend?)null);
        Confirmation.ConfirmAsync("Cancel query of", "pid 48230", false, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        RowOf(cut, 48230).QuerySelector(".pg-cancel")!.Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<ISnackbar>().ShownSnackbars
            .Should().Contain(s => s.Message!.Contains("had already ended") && s.Severity == Severity.Error));
    }

    private void OpenRowMenu(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut, int pid)
    {
        RowOf(cut, pid).QuerySelector(".pg-row-menu button")!.Click();
        cut.WaitForElement(".mud-popover-open .pg-terminate, .mud-popover-open .pg-filter-user");
    }

    [Fact]
    public void Terminate_sends_a_destructive_detailed_request_with_the_typed_target()
    {
        Arrange();
        ConfirmationRequest? seen = null;
        Confirmation.ConfirmAsync(Arg.Do<ConfirmationRequest>(r => seen = r), Arg.Any<CancellationToken>()).Returns(true);
        Operations.GetBackendAsync(Secret, Blocker, Arg.Any<CancellationToken>()).Returns(B(Blocker, "idle in transaction"));
        Operations.TerminateBackendAsync(Secret, Blocker, Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderPage();

        OpenRowMenu(cut, Blocker);
        cut.Find(".mud-popover-open .pg-terminate").Click();

        cut.WaitForAssertion(() => seen.Should().NotBeNull());
        seen!.Verb.Should().Be("Terminate");
        seen.Target.Should().Be("pid 48213");
        seen.IsProd.Should().BeTrue();
        seen.Destructive.Should().BeTrue();
        seen.AuditAction.Should().Be("pg.backend.terminate");
        seen.Consequence.Should().Contain("pg_terminate_backend").And.Contain("14m 02s").And.Contain("api").And.Contain("The 2 blocked sessions will proceed.");
        seen.Details.Should().Contain(d => d.Label == "pid" && d.Value == "48213");
        seen.Details.Should().Contain(d => d.Label == "user" && d.Value == "app_rw · api · 10.0.4.18");
        seen.Details.Should().Contain(d => d.Label == "state" && d.Value == "idle in transaction · 14m 02s · blocks 2");
        seen.Details.Should().Contain(d => d.Label == "query");
        cut.WaitForAssertion(() => Operations.Received(1).TerminateBackendAsync(Secret, Blocker, Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Declining_terminate_calls_nothing()
    {
        Arrange();
        Confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(false);
        var cut = RenderPage();

        OpenRowMenu(cut, Blocker);
        cut.Find(".mud-popover-open .pg-terminate").Click();

        cut.WaitForAssertion(() => Confirmation.Received(1).ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()));
        Operations.DidNotReceive().TerminateBackendAsync(Secret, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Filter_by_user_filters_rows_and_the_chip_clears_it()
    {
        Arrange(
        [
            B(10, user: "alice", xactMinutesAgo: 5), B(11, user: "bob", xactMinutesAgo: 4), B(12, user: "alice", xactMinutesAgo: 3),
        ]);
        var cut = RenderPage();

        OpenRowMenu(cut, 11);
        cut.Find(".mud-popover-open .pg-filter-user").Click();

        cut.WaitForAssertion(() => cut.FindAll(".pg-activity-row").Should().HaveCount(1));
        cut.Find(".pg-user-filter").TextContent.Should().Contain("bob");
        cut.Find(".pg-user-filter .mud-chip-close-button").Click();
        cut.WaitForAssertion(() => cut.FindAll(".pg-activity-row").Should().HaveCount(3));
    }

    [Fact]
    public void Idle_backends_are_hidden_behind_a_footer_that_toggles()
    {
        Arrange();
        var cut = RenderPage();

        cut.Find(".pg-idle-hidden").TextContent.Should().Contain("2 idle backends hidden").And.Contain("show all");
        cut.FindAll(".pg-activity-row").Should().HaveCount(5);

        cut.Find(".pg-show-idle").Click();
        cut.WaitForAssertion(() => cut.FindAll(".pg-activity-row").Should().HaveCount(7));
        cut.Find(".pg-idle-hidden").TextContent.Should().Contain("hide idle");
    }

    [Fact]
    public void Active_only_shows_active_rows_and_drops_the_idle_footer_but_keeps_the_banner()
    {
        Arrange();
        var cut = RenderPage();

        cut.Find(".pg-active-only input").Change(true);

        cut.WaitForAssertion(() => cut.FindAll(".pg-activity-row").Should().HaveCount(4));
        cut.FindAll(".pg-idle-hidden").Should().BeEmpty();
        cut.FindAll(".pg-blocker-banner").Should().HaveCount(1);
    }

    [Fact]
    public void Auto_refresh_reloads_after_five_seconds_and_counts_down()
    {
        Arrange();
        var cut = RenderPage();
        cut.Find(".pg-next-refresh").TextContent.Should().Contain("next in 5 s");
        var before = Calls(nameof(IPostgresOperations.GetActivityAsync));

        cut.InvokeAsync(() => Time.Advance(TimeSpan.FromSeconds(2)));
        cut.WaitForAssertion(() => cut.Find(".pg-next-refresh").TextContent.Should().Contain("next in 3 s"));
        Calls(nameof(IPostgresOperations.GetActivityAsync)).Should().Be(before);

        cut.InvokeAsync(() => Time.Advance(TimeSpan.FromSeconds(3)));
        cut.WaitForAssertion(() => Calls(nameof(IPostgresOperations.GetActivityAsync)).Should().Be(before + 1));
        cut.WaitForAssertion(() => cut.Find(".pg-next-refresh").TextContent.Should().Contain("next in 5 s"));
    }

    [Fact]
    public void Auto_refresh_off_never_reloads()
    {
        Arrange();
        var cut = RenderPage();
        cut.Find(".pg-autorefresh").MouseDown();
        cut.WaitForElement(".mud-popover-open .mud-list-item");
        cut.FindAll(".mud-popover-open .mud-list-item").First(i => i.TextContent.Trim() == "Off").Click();
        cut.WaitForAssertion(() => cut.FindAll(".pg-next-refresh").Should().BeEmpty());
        var before = Calls(nameof(IPostgresOperations.GetActivityAsync));

        cut.InvokeAsync(() => Time.Advance(TimeSpan.FromSeconds(30)));

        Calls(nameof(IPostgresOperations.GetActivityAsync)).Should().Be(before);
    }

    [Fact]
    public void A_load_error_shows_an_alert()
    {
        Operations.GetActivityAsync(Secret, Arg.Any<CancellationToken>()).Returns<PgActivity>(_ => throw new TimeoutException("connect timed out"));
        var cut = RenderPage();

        cut.WaitForElement(".pg-activity-error");
    }

    [Fact]
    public void Footnote_links_to_the_audit_log()
    {
        Arrange();
        var cut = RenderPage();

        var note = cut.Find(".pg-footnote");
        note.TextContent.Should().Contain("Blocked rows are indented under their blocker").And.Contain("Cancel and Terminate are audited");
        note.QuerySelector("a")!.GetAttribute("href").Should().Be("/audit");
    }
}
