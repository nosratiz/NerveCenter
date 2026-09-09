using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Plugins;
using SbConsole.Core.Security;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components;
using SbConsole.Web.Plugins;

var builder = WebApplication.CreateBuilder(args);

var bootstrap = BootstrapOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.WebHost.UseUrls(bootstrap.Bind);
builder.Services.AddSingleton(bootstrap);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddDbContextFactory<SbcDbContext>(o => o.UseSqlite($"Data Source={bootstrap.DbPath}"));
builder.Services.AddSingleton<ISecretProtector>(new AesGcmSecretProtector(bootstrap.DataKey));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISettings, DbSettings>();
builder.Services.AddSingleton<IAuditWriter, EfAuditWriter>();
builder.Services.AddSingleton<IPluginStoreFactory, EfPluginStoreFactory>();
builder.Services.AddSingleton<IConnectionProvider, EfConnectionProvider>();
builder.Services.AddScoped<CreateConnectionCommandHandler>();
builder.Services.AddScoped<ListConnectionsQueryHandler>();
builder.Services.AddScoped<DeleteConnectionCommandHandler>();

// Plugins (compile-time registration; see docs/design.md §2)
builder.Services.AddSingleton(sp => new PluginRegistry(sp.GetServices<IPlugin>()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<SbcDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
