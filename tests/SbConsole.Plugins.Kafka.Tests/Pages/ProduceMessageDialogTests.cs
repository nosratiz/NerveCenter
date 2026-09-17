using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ProduceMessageDialogTests : BunitContext, IAsyncLifetime
{
    // See CreateTopicDialogTests.cs for why this class implements IAsyncLifetime and builds its
    // own IMudDialogInstance substitute rather than relying on bUnit's default dialog wiring.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public ProduceMessageDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<ProduceMessageCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog()
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog.ConnectionName), "kafka-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog.TopicName), "orders");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Produce_closes_the_dialog_and_calls_the_operations_seam_when_the_handler_succeeds()
    {
        var cut = RenderDialog();
        // #message-value (no "input#" prefix) -- the field is multiline (Lines="8") and renders as
        // a <textarea>, not <input>, same reason SendMessageDialogTests.cs finds "#message-body"
        // without an element-tag prefix.
        cut.Find("#message-value").Input("hello world");

        cut.Find("button.produce-message").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).ProduceMessageAsync("bootstrap.servers=real:9092", "orders", null, "hello world", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Produce_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.ProduceMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("unknown topic")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("#message-value").Input("hello world");

        cut.Find("button.produce-message").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("unknown topic"));
    }
}
