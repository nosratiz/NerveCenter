using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class CreateQueueDialogTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers, pointer-
    // events routing) that implement only IAsyncDisposable. xunit v2 tears down a test class via
    // its synchronous IDisposable.Dispose() unless the class also implements Xunit.IAsyncLifetime,
    // in which case DisposeAsync() runs first. Same pattern as CreateTopicDialogTests.cs (Kafka).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter (distinct from the public `IMudDialogInstance` our component's code-behind uses to
    // call Close/Cancel) -- without it, <MudDialog> treats itself as "inline and not yet shown" and
    // renders nothing. That type isn't public, so it can't be named directly; instead we build one
    // substitute that implements both interfaces via reflection, and cascade it as
    // `IMudDialogInstance`. Same reasoning as CreateTopicDialogTests.cs / the brief's own
    // RenderComponent<MudDialogProvider>() + IDialogService.ShowAsync suggestion did not surface
    // the dialog's DialogContent markup reliably, so this direct-render pattern (already proven in
    // this codebase) is used instead.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public CreateQueueDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateQueueCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog()
    {
        // Must cascade as CascadingValue<TRuntimeProxyType>, not CascadingValue<IMudDialogInstance>
        // -- MudDialog's internal cascading parameter is typed IMudDialogInstanceInternal, and
        // Blazor only matches a CascadingValue<T> to consumers whose parameter type T is
        // assignable from.
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<SbConsole.Plugins.Aws.Pages.CreateQueueDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Aws.Pages.CreateQueueDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Aws.Pages.CreateQueueDialog.ConnectionName), "aws-dev");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void FIFO_toggle_reveals_the_dedup_and_high_throughput_switches()
    {
        var cut = RenderDialog();

        cut.FindAll(".content-based-dedup-toggle").Should().BeEmpty();
        cut.FindAll(".high-throughput-toggle").Should().BeEmpty();

        cut.Find(".fifo-toggle input").Change(true);

        cut.FindAll(".content-based-dedup-toggle").Should().ContainSingle();
        cut.FindAll(".high-throughput-toggle").Should().ContainSingle();
    }

    [Fact]
    public void Dead_letter_toggle_reveals_target_arn_and_max_receives()
    {
        var cut = RenderDialog();

        cut.FindAll(".dlq-target-arn").Should().BeEmpty();
        cut.FindAll(".max-receive-count").Should().BeEmpty();

        cut.Find(".dlq-toggle input").Change(true);

        cut.FindAll(".dlq-target-arn").Should().ContainSingle();
        cut.FindAll(".max-receive-count").Should().ContainSingle();
    }

    [Fact]
    public async Task Save_creates_a_standard_queue_and_closes_the_dialog_when_the_handler_succeeds()
    {
        _operations.CreateQueueAsync("mode=default-chain;region=eu-west-1", Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns("https://sqs.eu-west-1.amazonaws.com/123456789012/orders");

        var cut = RenderDialog();
        cut.Find("input#queue-name").Input("orders");

        cut.Find("button.save-queue").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateQueueAsync(
            "mode=default-chain;region=eu-west-1",
            Arg.Is<CreateQueueRequest>(r => r.Name == "orders" && !r.IsFifo),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateQueueAsync("mode=default-chain;region=eu-west-1", Arg.Any<CreateQueueRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("queue already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#queue-name").Input("orders");

        cut.Find("button.save-queue").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("queue already exists"));
    }
}
