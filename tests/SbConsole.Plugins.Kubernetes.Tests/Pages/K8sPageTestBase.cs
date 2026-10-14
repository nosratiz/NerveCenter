using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kubernetes.Access;
using SbConsole.Plugins.Kubernetes.Client;
using SbConsole.Plugins.Kubernetes.Namespaces;
using SbConsole.Plugins.Kubernetes.Overview;
using SbConsole.Plugins.Kubernetes.Workloads;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kubernetes.Tests.Pages;

// BunitContext + Render<T> (not TestContext/RenderComponent): bUnit 2.10.3 marks the old names
// [Obsolete], which TreatWarningsAsErrors turns into a build failure. Later page tasks extend this
// base by registering their handlers in RegisterHandlers().
public abstract class K8sPageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected const string ConnectionName = "aks-prod-eu";
    protected const string UserName = "admin";
    protected static readonly string Secret = TestData.Kubeconfig();

    protected readonly IKubernetesOperations Operations = Substitute.For<IKubernetesOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();
    protected readonly IConfirmationService Confirmation = Substitute.For<IConfirmationService>();
    protected readonly InMemoryPluginStore Store = new();
    protected readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    protected readonly AccessCache AccessCache;
    protected readonly Guid ConnectionId = Guid.NewGuid();

    protected K8sPageTestBase()
    {
        AccessCache = new AccessCache(Time);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync(KubernetesPlugin.Kind, Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, KubernetesPlugin.Kind, ["prod"], true, Time.GetUtcNow(), null, Time.GetUtcNow(),
                new Dictionary<string, string>
                {
                    ["Server"] = "aks-prod-eu.example", ["Context"] = "aks-prod-eu-admin", ["TLS"] = "verified",
                }),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        Operations.GetAccessAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.AdminAccess());
        Operations.ListNamespacesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => TestData.Namespaces());

        Services.AddKeyedSingleton<IPluginStore>(KubernetesPlugin.PluginId, Store);
        Services.AddSingleton<TimeProvider>(Time);
        Services.AddSingleton(Connections);
        Services.AddSingleton(Audit);
        Services.AddSingleton(Confirmation);
        Services.AddSingleton(Operations);
        Services.AddSingleton(AccessCache);
        Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
        Confirmation.ConfirmAsync(Arg.Any<ConfirmationRequest>(), Arg.Any<CancellationToken>()).Returns(true);
        RegisterHandlers();
    }

    /// <summary>Constructs every handler that exists so far. Later tasks append theirs here.</summary>
    private void RegisterHandlers()
    {
        Services.AddSingleton(new GetPermissionsQueryHandler(
            Operations, Connections, AccessCache, NullLogger<GetPermissionsQueryHandler>.Instance));
        Services.AddSingleton(new ListNamespacesQueryHandler(
            Operations, Connections, NullLogger<ListNamespacesQueryHandler>.Instance));
        Services.AddSingleton(new GetOverviewQueryHandler(
            Operations, Connections, Store, Time, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new ListWorkloadsQueryHandler(
            Operations, Connections, NullLogger<ListWorkloadsQueryHandler>.Instance));
        Services.AddSingleton(new CountWorkloadsQueryHandler(
            Operations, Connections, NullLogger<CountWorkloadsQueryHandler>.Instance));
        Services.AddSingleton(new GetScaleInfoQueryHandler(
            Operations, Connections, NullLogger<GetScaleInfoQueryHandler>.Instance));
        Services.AddSingleton(new GetResourceYamlQueryHandler(
            Operations, Connections, NullLogger<GetResourceYamlQueryHandler>.Instance));
        Services.AddSingleton(new ScaleWorkloadCommandHandler(
            Operations, Connections, Audit, NullLogger<ScaleWorkloadCommandHandler>.Instance));
        Services.AddSingleton(new RestartWorkloadCommandHandler(
            Operations, Connections, Audit, Time, NullLogger<RestartWorkloadCommandHandler>.Instance));
        Services.AddSingleton(new DeleteWorkloadCommandHandler(
            Operations, Connections, Audit, NullLogger<DeleteWorkloadCommandHandler>.Instance));
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, UserName)], "test"))));
    }

    /// <summary>MudMenu/MudSelect items render into a MudPopoverProvider (hosted by the real layout), so such tests render one alongside.</summary>
    protected IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderWithPopovers(RenderFragment content)
    {
        RenderFragment fragment = builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.AddContent(1, content);
        };

        return Render(fragment);
    }
}
