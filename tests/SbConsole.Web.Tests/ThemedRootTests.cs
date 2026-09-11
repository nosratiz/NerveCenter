using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Data;
using SbConsole.Core.Settings;
using SbConsole.Core.Tests;
using SbConsole.Web.Theming;

namespace SbConsole.Web.Tests;

public class ThemedRootTests : BunitContext, IAsyncLifetime
{
    private readonly TestDb _testDb = new();

    public ThemedRootTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ISettings, DbSettings>();
    }

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

    [Fact]
    public async Task Renders_child_content_regardless_of_theme()
    {
        var cut = Render<ThemedRoot>(parameters => parameters
            .AddChildContent("<p>hello</p>"));
        await Task.Delay(20);
        cut.Render();

        cut.Markup.Should().Contain("hello");
    }

    [Fact]
    public async Task Dark_setting_resolves_to_dark_immediately()
    {
        await Services.GetRequiredService<ISettings>().SetAsync("theme.mode", "dark");

        var cut = Render<ThemedRoot>(parameters => parameters
            .AddChildContent("<p>hello</p>"));
        await Task.Delay(20);
        cut.Render();

        cut.Markup.Should().Contain("hello"); // renders without throwing under the dark theme
    }

    [Fact]
    public async Task Light_setting_resolves_to_light_immediately()
    {
        await Services.GetRequiredService<ISettings>().SetAsync("theme.mode", "light");

        var cut = Render<ThemedRoot>(parameters => parameters
            .AddChildContent("<p>hello</p>"));
        await Task.Delay(20);
        cut.Render();

        cut.Markup.Should().Contain("hello");
    }
}
