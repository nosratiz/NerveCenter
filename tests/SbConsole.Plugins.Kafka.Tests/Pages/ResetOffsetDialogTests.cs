using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ResetOffsetDialogTests : BunitContext, IAsyncLifetime
{
    // See CreateTopicDialogTests.cs for why this class implements IAsyncLifetime and builds its
    // own IMudDialogInstance substitute rather than relying on bUnit's default dialog wiring.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly IMudDialogInstance _dialogInstance;

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter (distinct from the public `IMudDialogInstance` our component's code-behind uses to
    // call Close/Cancel) -- without it, <MudDialog> treats itself as "inline and not yet shown" and
    // renders nothing. That type isn't public, so it can't be named directly; instead we build one
    // substitute that implements both interfaces via reflection, and cascade it as
    // `IMudDialogInstance`. See CreateTopicDialogTests.cs/ProduceMessageDialogTests.cs for the same
    // pattern.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public ResetOffsetDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<ResetConsumerGroupOffsetCommandHandler>();
        _connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        _confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(Guid connectionId)
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog.ConnectionId), connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog.ConnectionName), "kafka-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog.IsProd), false);
                inner.AddComponentParameter(4, nameof(SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog.GroupId), "order-processors");
                inner.AddComponentParameter(5, nameof(SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog.TopicName), "orders");
                inner.AddComponentParameter(6, nameof(SbConsole.Plugins.Kafka.Pages.ResetOffsetDialog.Partition), 2);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Confirms_before_calling_the_reset_handler()
    {
        var connectionId = Guid.NewGuid();

        var cut = RenderDialog(connectionId);
        cut.Find(".confirm-reset").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Reset offset for", "orders-2", false, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).ResetConsumerGroupOffsetAsync(
            "bootstrap.servers=real:9092", "order-processors", "orders", 2, OffsetResetMode.Latest, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Declining_confirmation_never_calls_the_reset_handler()
    {
        _confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(false);
        var connectionId = Guid.NewGuid();

        var cut = RenderDialog(connectionId);
        cut.Find(".confirm-reset").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().ResetConsumerGroupOffsetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<OffsetResetMode>(), Arg.Any<long?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }
}
