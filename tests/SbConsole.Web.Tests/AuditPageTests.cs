using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class AuditPageTests : BunitContext, IAsyncLifetime
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

    public AuditPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<SbConsole.Core.Data.SbcDbContext>>(_testDb);
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
    }

    private async Task SeedAsync(params AuditEntry[] entries)
    {
        await using var db = _testDb.CreateDbContext();
        db.AuditEntries.AddRange(entries);
        await db.SaveChangesAsync();
    }

    private static AuditEntry Entry(string action, ActionRisk risk, DateTimeOffset at) => new()
    {
        At = at, Actor = "admin", Action = action, Target = "t", Risk = risk, Succeeded = true,
    };

    // MudSelect's dropdown content is rendered by <MudPopoverProvider/>, a separate component that
    // the real app hosts once in its layout and which portals every open popover's markup into
    // itself. bUnit only renders what's given to Render(...), so a lone <Audit/> never gets the
    // popover's <div class="mud-list-item"> markup in its subtree -- render a provider alongside it
    // so cut.Find/FindAll can see the opened popover's items (same pattern as
    // ConnectionEditorTests.RenderEditor).
    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage()
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Audit>(1);
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Renders_seeded_entries()
    {
        await SeedAsync(Entry("queue.purge", ActionRisk.Destructive, DateTimeOffset.UtcNow));

        var cut = RenderPage();
        cut.WaitForState(() => cut.Markup.Contains("queue.purge"));

        cut.Markup.Should().Contain("queue.purge");
    }

    [Fact]
    public async Task Risk_filter_narrows_the_visible_rows()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(
            Entry("queue.purge", ActionRisk.Destructive, now),
            Entry("message.peek", ActionRisk.Safe, now));

        var cut = RenderPage();
        cut.WaitForState(() => cut.Markup.Contains("message.peek"));

        // MudSelect opens its popover on mousedown of the inner input-control div, not on click of
        // the outer "mud-select" wrapper (which only carries an onclick:stopPropagation modifier
        // with no click handler of its own) -- confirmed working pattern from
        // ConnectionEditorTests.
        cut.Find("div.mud-input-control.mud-select").MouseDown(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.FindAll("div.mud-list-item").First(e => e.TextContent.Contains("Destructive")).Click();
        cut.WaitForState(() => !cut.Markup.Contains("message.peek"));

        cut.Markup.Should().NotContain("message.peek");
        cut.Markup.Should().Contain("queue.purge");
    }

    [Fact]
    public async Task From_date_filter_excludes_entries_before_it()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-10);
        var newer = DateTimeOffset.UtcNow;
        await SeedAsync(
            Entry("queue.purge", ActionRisk.Destructive, older),
            Entry("message.peek", ActionRisk.Safe, newer));

        var cut = RenderPage();
        cut.WaitForState(() => cut.Markup.Contains("queue.purge"));

        // MudDatePicker's own calendar popover is a separate concern from this page's filter
        // wiring -- rather than driving the popup UI, set the bound "From" date directly via
        // bUnit's parameter-setting API on the rendered MudDatePicker and let its @bind-Date:after
        // callback (ReloadAsync) run, which is the actual behavior under test here.
        var fromPicker = cut.FindComponents<MudDatePicker>()[0];
        await cut.InvokeAsync(() => fromPicker.Instance.DateChanged.InvokeAsync(newer.Date));
        cut.WaitForState(() => !cut.Markup.Contains("queue.purge"));

        cut.Markup.Should().NotContain("queue.purge");
        cut.Markup.Should().Contain("message.peek");
    }
}
