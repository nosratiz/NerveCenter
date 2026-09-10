using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Tests;
using SbConsole.Sdk;
using SbConsole.Web.Components.Pages;

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

    public HomeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDbContextFactory<SbcDbContext>>(_testDb);
        Services.AddSingleton<ListConnectionsQueryHandler>();
        Services.AddSingleton<ListAuditEntriesQueryHandler>();
    }

    [Fact]
    public void Shows_all_clear_when_nothing_needs_attention()
    {
        var cut = Render<Home>();

        cut.Markup.Should().Contain("All clear");
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
}
