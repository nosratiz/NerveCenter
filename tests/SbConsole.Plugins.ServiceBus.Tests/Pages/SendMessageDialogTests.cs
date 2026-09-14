using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class SendMessageDialogTests : BunitContext, IAsyncLifetime
{
    // See CreateQueueDialogTests.cs for why this class implements IAsyncLifetime and builds its
    // own IMudDialogInstance substitute rather than relying on bUnit's default dialog wiring.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public SendMessageDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging(); // handlers take an ILogger<T> so they can log the full exception behind a truncated UI message
        Services.AddSingleton<SendMessageCommandHandler>();

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
                inner.OpenComponent<SendMessageDialog>(0);
                inner.AddComponentParameter(1, nameof(SendMessageDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SendMessageDialog.ConnectionName), "sb-dev");
                inner.AddComponentParameter(3, nameof(SendMessageDialog.QueueName), "orders-inbound");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Send_closes_the_dialog_and_calls_the_operations_seam_when_the_handler_succeeds()
    {
        var cut = RenderDialog();
        cut.Find("#message-body").Input("""{"a":1}""");

        cut.Find("button.send-message").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).SendMessageAsync(
            "Endpoint=sb://real", "orders-inbound",
            Arg.Is<SendMessageRequest>(r => r.Body == """{"a":1}"""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.SendMessageAsync("Endpoint=sb://real", "orders-inbound", Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("queue not found")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("#message-body").Input("""{"a":1}""");

        cut.Find("button.send-message").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        _dialogInstance.DidNotReceive().Cancel();
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("queue not found"));
    }
}
