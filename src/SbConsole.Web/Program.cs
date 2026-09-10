using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Plugins;
using SbConsole.Core.Security;
using SbConsole.Core.Settings;
using SbConsole.Sdk;
using SbConsole.Web.Components;
using SbConsole.Web.Confirmation;
using SbConsole.Web.Plugins;

var builder = WebApplication.CreateBuilder(args);

var bootstrap = BootstrapOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.WebHost.UseUrls(bootstrap.Bind);
builder.Services.AddSingleton(bootstrap);

var keysDirectory = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(bootstrap.DbPath) ?? ".", "dataprotection-keys"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(keysDirectory)
    .SetApplicationName("SbConsole");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "sbc.auth";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());
builder.Services.AddCascadingAuthenticationState();

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
builder.Services.AddScoped<UpdateConnectionCommandHandler>();
builder.Services.AddScoped<ListAuditEntriesQueryHandler>();
builder.Services.AddScoped<ListPluginsQueryHandler>();
builder.Services.AddScoped<IConfirmationService, MudConfirmationService>();

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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapPost("/auth/login", async (HttpContext http, BootstrapOptions options, IAuditWriter audit, TimeProvider clock) =>
{
    var form = await http.Request.ReadFormAsync();
    var password = form["password"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(options.AdminPassword)))
    {
        await audit.WriteAsync(new AuditEntry
        {
            At = clock.GetUtcNow(),
            Actor = "admin",
            Action = "auth.login",
            Target = "-",
            Risk = ActionRisk.Safe,
            Succeeded = false,
        });
        return Results.Redirect("/login?failed=true");
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    await audit.WriteAsync(new AuditEntry
    {
        At = clock.GetUtcNow(),
        Actor = "admin",
        Action = "auth.login",
        Target = "-",
        Risk = ActionRisk.Safe,
        Succeeded = true,
    });
    return Results.Redirect("/");
}).AllowAnonymous().DisableAntiforgery(); // pre-auth form; password is the only field, no session to ride

app.MapPost("/auth/logout", async (HttpContext http, IAuditWriter audit, TimeProvider clock) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await audit.WriteAsync(new AuditEntry
    {
        At = clock.GetUtcNow(),
        Actor = "admin",
        Action = "auth.logout",
        Target = "-",
        Risk = ActionRisk.Safe,
        Succeeded = true,
    });
    return Results.Redirect("/login");
});

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Exposes the top-level-statement Program class so WebApplicationFactory<Program> can host it
// in-process for integration tests (see tests/SbConsole.Web.Tests/AuthEndpointsTests.cs).
public partial class Program;
