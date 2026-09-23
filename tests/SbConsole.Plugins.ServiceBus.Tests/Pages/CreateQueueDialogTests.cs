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
using SbConsole.Plugins.ServiceBus.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateQueueDialogTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first -- disposing those services
    // the async-safe way before the base (synchronous) Dispose() runs as a no-op afterwards. See
    // ConnectionsPageTests.cs / ConnectionEditorTests.cs for the same pattern.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter (distinct from the public `IMudDialogInstance` our component's code-behind uses to
    // call Close/Cancel) -- without it, <MudDialog> treats itself as "inline and not yet shown" and
    // renders nothing. That type isn't public, so it can't be named directly; instead we build one
    // substitute that implements both interfaces via reflection, and cascade it as `IMudDialogInstance`.
    // See ConfirmDialogTests.cs for the same pattern.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public CreateQueueDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging(); // handlers take an ILogger<T> so they can log the full exception behind a truncated UI message
        Services.AddSingleton<CreateQueueCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog()
    {
        // Must cascade as CascadingValue<TRuntimeProxyType>, not CascadingValue<IMudDialogInstance> --
        // MudDialog's internal cascading parameter is typed IMudDialogInstanceInternal, and Blazor
        // only matches a CascadingValue<T> to consumers whose parameter type T is assignable from.
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<CreateQueueDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateQueueDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateQueueDialog.ConnectionName), "sb-dev");
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
        cut.Find("input#queue-name").Input("orders-inbound");

        cut.Find("button.save-queue").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateQueueAsync("Endpoint=sb://real", Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("queue already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#queue-name").Input("orders-inbound");

        cut.Find("button.save-queue").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        _dialogInstance.DidNotReceive().Cancel();
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("queue already exists"));
    }

    [Fact]
    public async Task Create_is_disabled_and_shows_a_spinner_while_the_azure_call_is_in_flight()
    {
        // Representative coverage for the busy-state convention documented on Queues.razor: hold the
        // substituted operation open with a TaskCompletionSource so the awaited call is genuinely
        // pending, then assert the triggering button is disabled and an inline progress indicator is
        // shown -- and that both revert once the call completes. Without this a slow (or hung)
        // operation looked identical to a frozen page, and a second click could fire a duplicate.
        var pending = new TaskCompletionSource();
        _operations.CreateQueueAsync("Endpoint=sb://real", Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);

        var cut = RenderDialog();
        cut.Find("input#queue-name").Input("orders-inbound");
        cut.Find("button.save-queue").Should().NotBeNull();
        cut.Find("button.save-queue").HasAttribute("disabled").Should().BeFalse();

        cut.Find("button.save-queue").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.save-queue").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".save-queue-busy").Should().NotBeEmpty();

        pending.SetResult();
        await Task.Delay(30);

        // The dialog closes on success, so the button's re-enable is observed on the failure path
        // instead -- what matters is that the `finally` clears the flag either way.
        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Create_is_re_enabled_after_a_failed_call()
    {
        var pending = new TaskCompletionSource();
        _operations.CreateQueueAsync("Endpoint=sb://real", Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(pending.Task);

        var cut = RenderDialog();
        cut.Find("input#queue-name").Input("orders-inbound");
        cut.Find("button.save-queue").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.save-queue").HasAttribute("disabled").Should().BeTrue();

        pending.SetException(new InvalidOperationException("queue already exists"));
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.save-queue").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll(".save-queue-busy").Should().BeEmpty();
    }
}
