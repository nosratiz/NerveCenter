using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateSubscriptionDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public CreateSubscriptionDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateSubscriptionCommandHandler>();

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
                inner.OpenComponent<CreateSubscriptionDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateSubscriptionDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateSubscriptionDialog.ConnectionName), "sb-dev");
                inner.AddComponentParameter(3, nameof(CreateSubscriptionDialog.TopicName), "orders");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Save_closes_the_dialog_when_the_handler_succeeds()
    {
        var cut = RenderDialog();
        cut.Find("input#subscription-name").Input("uk-team");

        cut.Find("button.save-subscription").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateSubscriptionAsync("Endpoint=sb://real", "orders", Arg.Any<CreateSubscriptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("subscription already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#subscription-name").Input("uk-team");

        cut.Find("button.save-subscription").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("subscription already exists"));
    }
}
