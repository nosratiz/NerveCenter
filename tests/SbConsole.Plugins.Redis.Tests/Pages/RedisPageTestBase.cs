using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Redis.Acl;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Keys;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Plugins.Redis.PubSub;
using SbConsole.Plugins.Redis.Streams;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Pages;

// Base class is BunitContext (not TestContext) and rendering goes through Render<T>, not
// RenderComponent<T> -- this bUnit version (2.10.3) marks both older names [Obsolete], which
// TreatWarningsAsErrors turns into a build failure. Same convention already used by every other
// plugin's page test base (see RabbitPageTestBase, and the Kafka/AWS page tests).
public abstract class RedisPageTestBase : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    protected readonly IRedisOperations Operations = Substitute.For<IRedisOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();
    protected readonly IConfirmationService Confirmation = Substitute.For<IConfirmationService>();
    protected readonly Guid ConnectionId = Guid.NewGuid();
    protected const string Secret = "standalone|0|localhost:6379";
    protected const string ConnectionName = "redis-uk-prod";

    protected RedisPageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync("redis", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "redis", ["prod"], true, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, null),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        Confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);

        Services.AddSingleton(Operations);
        Services.AddSingleton(Connections);
        Services.AddSingleton(Audit);
        Services.AddSingleton(Confirmation);
        Services.AddSingleton(new GetOverviewQueryHandler(Operations, Connections, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new GetMemoryByPrefixQueryHandler(Operations, Connections, NullLogger<GetMemoryByPrefixQueryHandler>.Instance));
        Services.AddSingleton(new GetSlowLogQueryHandler(Operations, Connections, NullLogger<GetSlowLogQueryHandler>.Instance));

        Services.AddSingleton(new KeysQueryHandler(Operations, Connections, NullLogger<KeysQueryHandler>.Instance));
        Services.AddSingleton(new DeleteKeyCommandHandler(Operations, Connections, Audit, NullLogger<DeleteKeyCommandHandler>.Instance));
        Services.AddSingleton(new RenameKeyCommandHandler(Operations, Connections, Audit, NullLogger<RenameKeyCommandHandler>.Instance));
        Services.AddSingleton(new SetTtlCommandHandler(Operations, Connections, Audit, NullLogger<SetTtlCommandHandler>.Instance));
        Services.AddSingleton(new DeleteByPatternCommandHandler(Operations, Connections, Audit, NullLogger<DeleteByPatternCommandHandler>.Instance));
        Services.AddSingleton(new SetStringCommandHandler(Operations, Connections, Audit, NullLogger<SetStringCommandHandler>.Instance));
        Services.AddSingleton(new SetHashFieldCommandHandler(Operations, Connections, Audit, NullLogger<SetHashFieldCommandHandler>.Instance));
        Services.AddSingleton(new DeleteHashFieldCommandHandler(Operations, Connections, Audit, NullLogger<DeleteHashFieldCommandHandler>.Instance));
        Services.AddSingleton(new PushListCommandHandler(Operations, Connections, Audit, NullLogger<PushListCommandHandler>.Instance));
        Services.AddSingleton(new TrimListCommandHandler(Operations, Connections, Audit, NullLogger<TrimListCommandHandler>.Instance));
        Services.AddSingleton(new AddSortedSetMemberCommandHandler(Operations, Connections, Audit, NullLogger<AddSortedSetMemberCommandHandler>.Instance));
        Services.AddSingleton(new IncrementSortedSetScoreCommandHandler(Operations, Connections, Audit, NullLogger<IncrementSortedSetScoreCommandHandler>.Instance));
        Services.AddSingleton(new AddSetMemberCommandHandler(Operations, Connections, Audit, NullLogger<AddSetMemberCommandHandler>.Instance));
        Services.AddSingleton(new RemoveSetMemberCommandHandler(Operations, Connections, Audit, NullLogger<RemoveSetMemberCommandHandler>.Instance));
        Services.AddSingleton(new RevealHashFieldCommandHandler(Audit, NullLogger<RevealHashFieldCommandHandler>.Instance));

        Services.AddSingleton(new StreamsQueryHandler(Operations, Connections, NullLogger<StreamsQueryHandler>.Instance));
        Services.AddSingleton(new AddStreamEntryCommandHandler(Operations, Connections, Audit, NullLogger<AddStreamEntryCommandHandler>.Instance));
        Services.AddSingleton(new TrimStreamCommandHandler(Operations, Connections, Audit, NullLogger<TrimStreamCommandHandler>.Instance));
        Services.AddSingleton(new AcknowledgeStreamEntriesCommandHandler(Operations, Connections, Audit, NullLogger<AcknowledgeStreamEntriesCommandHandler>.Instance));
        Services.AddSingleton(new ClaimStreamEntriesCommandHandler(Operations, Connections, Audit, NullLogger<ClaimStreamEntriesCommandHandler>.Instance));
        Services.AddSingleton(new DeleteStreamEntriesCommandHandler(Operations, Connections, Audit, NullLogger<DeleteStreamEntriesCommandHandler>.Instance));

        Services.AddSingleton(new FlushDatabaseCommandHandler(Operations, Connections, Audit, NullLogger<FlushDatabaseCommandHandler>.Instance));
        Services.AddSingleton(new AclQueryHandler(Operations, Connections, NullLogger<AclQueryHandler>.Instance));
        Services.AddSingleton(new PubSubQueryHandler(Operations, Connections, NullLogger<PubSubQueryHandler>.Instance));
        Services.AddSingleton(new SubscribeToChannelCommandHandler(Operations, Connections, Audit, NullLogger<SubscribeToChannelCommandHandler>.Instance));
        Services.AddSingleton(new PublishMessageCommandHandler(Operations, Connections, Audit, NullLogger<PublishMessageCommandHandler>.Instance));

        // ACL checks fail open by default in tests too (matches RedisOperations' own fail-open
        // default) -- individual tests override this via Operations.HasCommandPermissionAsync(...)
        // when they need to exercise the disabled-button state.
        Operations.HasCommandPermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    /// <summary>MudMenu/MudSelect items render into a MudPopoverProvider (hosted by the real layout), so menu/select tests render one alongside the component under test.</summary>
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

    /// <summary>Renders a dialog component inside the cascading dialog instance, with a popover provider for its selects (mirrors RabbitPageTestBase).</summary>
    protected IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog<TDialog>(IReadOnlyDictionary<string, object?> parameters)
        where TDialog : IComponent
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(DialogInstance.GetType());
        RenderFragment dialog = builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent(1, cascadingValueType);
            builder.AddComponentParameter(2, "Value", DialogInstance);
            builder.AddComponentParameter(3, "IsFixed", true);
            builder.AddComponentParameter(4, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<TDialog>(0);
                inner.AddMultipleAttributes(1, parameters.Select(p => new KeyValuePair<string, object>(p.Key, p.Value!)));
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(dialog);
    }

    // MudDialog only renders when it receives MudBlazor's internal IMudDialogInstanceInternal as a
    // cascading value (same trick as RabbitPageTestBase/AWS dialog tests).
    private static readonly Type MudDialogInstanceInternalType =
        typeof(MudBlazor.IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    protected readonly MudBlazor.IMudDialogInstance DialogInstance =
        (MudBlazor.IMudDialogInstance)Substitute.For([typeof(MudBlazor.IMudDialogInstance), MudDialogInstanceInternalType], []);

    protected static async Task SettleAsync<TComponent>(IRenderedComponent<TComponent> cut) where TComponent : IComponent
    {
        await Task.Delay(30);
        cut.Render();
    }
}
