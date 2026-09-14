using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Data;
using SbConsole.Core.Settings;
using SbConsole.Core.Tests;
using SbConsole.Web.Components.Pages;

namespace SbConsole.Web.Tests;

public class SettingsPageTests : BunitContext, IAsyncLifetime
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

    public SettingsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ISettings, DbSettings>();
    }

    [Fact]
    public async Task Saving_persists_instance_name_and_theme()
    {
        var cut = Render<Settings>();

        cut.Find("input#instance-name").Input("Platform Ops");
        cut.Find("button.save-settings").Click();

        var settings = Services.GetRequiredService<ISettings>();
        // SaveAsync's DB write happens inside an async click handler that bUnit's Click() does not
        // block on -- poll via bUnit's own WaitForAssertion (same intent as the AuditPageTests /
        // ConnectionsPageTests pattern of avoiding a fixed Task.Delay) until the write lands.
        cut.WaitForAssertion(() =>
            settings.GetAsync("instance.name").GetAwaiter().GetResult().Should().Be("Platform Ops"));
    }

    [Fact]
    public void Form_fields_are_width_capped_for_visual_consistency()
    {
        var cut = Render<Settings>();

        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "max-width:320px").Count.Should().Be(4);
    }
}
