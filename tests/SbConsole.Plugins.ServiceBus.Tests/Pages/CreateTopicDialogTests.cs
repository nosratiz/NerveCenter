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
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateTopicDialogTests : BunitContext, IAsyncLifetime
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

    public CreateTopicDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateTopicCommandHandler>();

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
                inner.OpenComponent<CreateTopicDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateTopicDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateTopicDialog.ConnectionName), "sb-dev");
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
        cut.Find("input#topic-name").Input("orders");

        cut.Find("button.save-topic").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateTopicAsync("Endpoint=sb://real", Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("topic already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#topic-name").Input("orders");

        cut.Find("button.save-topic").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("topic already exists"));
    }
}
