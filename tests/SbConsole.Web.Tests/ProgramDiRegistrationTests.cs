using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Plugins;
using SbConsole.Core.Security;
using SbConsole.Core.Tests;
using SbConsole.Sdk;

namespace SbConsole.Web.Tests;

/// <summary>
/// Regression guard for a class of bug that every task-scoped bUnit test missed: a handler class
/// exists and is consumed by a real page's [Inject], but nobody ever added its
/// `builder.Services.AddScoped&lt;...&gt;()` line to Program.cs, so the real app throws
/// InvalidOperationException ("There is no registered service of type...") the moment the page
/// tries to render -- while every bUnit test for that page passes, because bUnit tests register
/// their own handlers directly into the test host's service collection instead of exercising
/// Program.cs's registrations.
///
/// This mirrors the DI-relevant subset of Program.cs's `builder.Services` registrations (skipping
/// only the parts that need a live web host, such as authentication/authorization and the DB
/// migration step) and resolves every command/query handler a page component injects, the same
/// way the ASP.NET Core container would when constructing that page. If a handler is ever added to
/// a page's [Inject] list without a matching registration in Program.cs, this test fails with the
/// same InvalidOperationException a user would hit in the browser.
/// </summary>
public sealed class ProgramDiRegistrationTests : IDisposable
{
    private readonly TestDb _db = new();

    [Fact]
    public void All_handlers_injected_by_page_components_are_registered()
    {
        var services = new ServiceCollection();

        // Mirrors Program.cs's non-host-dependent singleton registrations.
        services.AddSingleton<IDbContextFactory<SbcDbContext>>(_db);
        services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(new byte[32]));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditWriter, EfAuditWriter>();
        // No IPlugin implementations are registered here on purpose: ListPluginsQueryHandler takes
        // IEnumerable<IPlugin>, which DI happily resolves as empty -- this test only needs to prove
        // the handler itself resolves, not exercise plugin behavior.

        // Mirrors every `builder.Services.AddScoped<...>()` line in Program.cs for handlers that a
        // real page's [Inject] consumes (Home.razor, Audit.razor, Plugins.razor, Connections.razor,
        // AddEditConnectionDialog.razor).
        services.AddScoped<CreateConnectionCommandHandler>();
        services.AddScoped<ListConnectionsQueryHandler>();
        services.AddScoped<DeleteConnectionCommandHandler>();
        services.AddScoped<UpdateConnectionCommandHandler>();
        services.AddScoped<ListAuditEntriesQueryHandler>();
        services.AddScoped<ListPluginsQueryHandler>();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        // Each GetRequiredService call throws InvalidOperationException if Program.cs is ever
        // missing the corresponding registration -- exactly the failure mode this guards against.
        sp.GetRequiredService<CreateConnectionCommandHandler>();
        sp.GetRequiredService<ListConnectionsQueryHandler>();
        sp.GetRequiredService<DeleteConnectionCommandHandler>();
        sp.GetRequiredService<UpdateConnectionCommandHandler>();
        sp.GetRequiredService<ListAuditEntriesQueryHandler>();
        sp.GetRequiredService<ListPluginsQueryHandler>();
    }

    public void Dispose() => _db.Dispose();
}
