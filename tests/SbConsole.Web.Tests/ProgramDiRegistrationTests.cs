using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Plugins;

namespace SbConsole.Web.Tests;

/// <summary>
/// Regression guard for a Critical bug: three handlers (<see cref="UpdateConnectionCommandHandler"/>,
/// <see cref="ListAuditEntriesQueryHandler"/>, <see cref="ListPluginsQueryHandler"/>) were consumed by
/// real page/dialog components' [Inject] properties but were never added to Program.cs's
/// `builder.Services.AddScoped&lt;...&gt;()` registrations, so the real app threw
/// InvalidOperationException ("There is no registered service of type...") -- an HTTP 500 -- the
/// moment a user hit "/", "/audit" or "/plugins".
///
/// The original version of this test built its own separate ServiceCollection and re-registered the
/// same handlers itself, so it never actually exercised Program.cs -- deleting the registrations from
/// Program.cs left this test (and the whole suite) green while the real app still 500'd. This version
/// hosts the REAL Program.cs via WebApplicationFactory&lt;Program&gt; (the same pattern as
/// AuthEndpointsTests) and proves two independent things against the real container:
///
///   1. Authenticated GETs to every page route that injects one of these handlers return 200 OK --
///      the exact reproduction of the original bug (500s on "/", "/audit", "/plugins").
///   2. Every command/query handler a page OR dialog component injects resolves without throwing
///      from the app's own root service provider -- this also covers
///      AddEditConnectionDialog.razor's UpdateConnectionCommandHandler, which a plain GET never
///      renders (it's opened on demand from an active Blazor circuit, so prerendering alone can't
///      reach it).
///
/// Shares the "ProgramFactory" xunit collection with AuthEndpointsTests: both mutate process-wide
/// environment variables (SBC_DB_PATH et al.) that BootstrapOptions reads for the in-process
/// WebApplicationFactory host, so they must not run concurrently with each other.
/// </summary>
[Collection("ProgramFactory")]
public sealed class ProgramDiRegistrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sbc-di-test-{Guid.NewGuid()}.db");
    private readonly string _originalDbPath;
    private readonly string? _originalDataKey;
    private readonly string? _originalAdminPassword;
    private readonly string? _originalApiKey;
    private const string AdminPassword = "correct-horse-battery-staple";

    public ProgramDiRegistrationTests()
    {
        // BootstrapOptions reads these directly via Environment.GetEnvironmentVariable, so the
        // in-process WebApplicationFactory host needs them set on the test process itself (same
        // mechanism as AuthEndpointsTests).
        _originalDbPath = Environment.GetEnvironmentVariable("SBC_DB_PATH") ?? "";
        _originalDataKey = Environment.GetEnvironmentVariable("SBC_DATA_KEY");
        _originalAdminPassword = Environment.GetEnvironmentVariable("SBC_ADMIN_PASSWORD");
        _originalApiKey = Environment.GetEnvironmentVariable("SBC_API_KEY");

        Environment.SetEnvironmentVariable("SBC_DB_PATH", _dbPath);
        Environment.SetEnvironmentVariable("SBC_DATA_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("SBC_ADMIN_PASSWORD", AdminPassword);
        Environment.SetEnvironmentVariable("SBC_API_KEY", "test-api-key");
    }

    [Fact]
    public async Task Authenticated_requests_to_every_page_route_succeed_against_the_real_container()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var loginResponse = await client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = AdminPassword }));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect, "login must succeed for the page requests below to be authenticated");
        var authCookie = loginResponse.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("sbc.auth", StringComparison.Ordinal)).Split(';')[0];

        foreach (var route in new[] { "/", "/audit", "/plugins", "/connections", "/settings", "/p/azure-servicebus/queues", "/p/azure-servicebus/topics", "/p/azure-servicebus/dead-letter", "/p/kafka/topics" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Add("Cookie", authCookie);
            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                $"GET {route} must render via the real Program.cs DI container, not a test-local one that " +
                "re-registers the same handlers and would pass even if Program.cs's registration were deleted");
        }
    }

    [Fact]
    public void Every_handler_injected_by_a_page_or_dialog_component_resolves_from_the_real_root_service_provider()
    {
        // Resolves against factory.Services -- the actual root IServiceProvider built from Program.cs's
        // builder.Services -- not a hand-rolled ServiceCollection. This is what catches a missing
        // AddScoped<...>() line in Program.cs itself, including UpdateConnectionCommandHandler, which
        // AddEditConnectionDialog.razor only injects when opened from a live Blazor circuit and which the
        // page-route GETs above therefore cannot reach during prerendering.
        using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<CreateConnectionCommandHandler>();
        sp.GetRequiredService<ListConnectionsQueryHandler>();
        sp.GetRequiredService<DeleteConnectionCommandHandler>();
        sp.GetRequiredService<UpdateConnectionCommandHandler>();
        sp.GetRequiredService<ListAuditEntriesQueryHandler>();
        sp.GetRequiredService<ListPluginsQueryHandler>();
    }

    /// <summary>
    /// Pins [Authorize] directly on every compiled, routable component type in the plugin
    /// assembly, rather than one named type -- broadened from the Queues plan's original version
    /// (which pinned only the Queues component) because that plan's own final review flagged that a
    /// future page could escape the check silently. This is that future page: Topics (this task)
    /// and the subscription-peek / Dead-letter-overview pages (later tasks) are all covered
    /// automatically, as would any later page, with no test change required.
    /// </summary>
    [Fact]
    public void Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component()
    {
        var routableTypes = typeof(SbConsole.Plugins.ServiceBus.Pages.Queues).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false).Length > 0)
            .ToList();

        routableTypes.Should().NotBeEmpty("this assembly is expected to contain at least one @page component");
        foreach (var type in routableTypes)
        {
            type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Should().NotBeEmpty($"{type.Name} must declare [Authorize] directly (or inherit it from " +
                    "Pages/_Imports.razor), not merely rely on the app's fallback authorization policy to " +
                    "redirect anonymous requests");
        }
    }

    /// <summary>
    /// Same guard as Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component,
    /// for the Kafka plugin assembly added alongside it.
    /// </summary>
    [Fact]
    public void Every_kafka_plugin_page_declares_Authorize_directly_on_the_component()
    {
        var routableTypes = typeof(SbConsole.Plugins.Kafka.Pages.Topics).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false).Length > 0)
            .ToList();

        routableTypes.Should().NotBeEmpty("this assembly is expected to contain at least one @page component");
        foreach (var type in routableTypes)
        {
            type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Should().NotBeEmpty($"{type.Name} must declare [Authorize] directly (or inherit it from " +
                    "Pages/_Imports.razor), not merely rely on the app's fallback authorization policy to " +
                    "redirect anonymous requests");
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        Environment.SetEnvironmentVariable("SBC_DB_PATH", string.IsNullOrEmpty(_originalDbPath) ? null : _originalDbPath);
        Environment.SetEnvironmentVariable("SBC_DATA_KEY", _originalDataKey);
        Environment.SetEnvironmentVariable("SBC_ADMIN_PASSWORD", _originalAdminPassword);
        Environment.SetEnvironmentVariable("SBC_API_KEY", _originalApiKey);
    }
}

/// <summary>
/// Groups every test class that hosts the real Program.cs via WebApplicationFactory and mutates
/// process-wide environment variables for BootstrapOptions (SBC_DB_PATH, SBC_DATA_KEY, etc.) into one
/// xunit collection so they run sequentially with each other instead of racing on shared process state.
/// </summary>
[CollectionDefinition("ProgramFactory")]
public sealed class ProgramFactoryCollection;
