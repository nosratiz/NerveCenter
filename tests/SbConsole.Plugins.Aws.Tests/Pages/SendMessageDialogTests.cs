using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class SendMessageDialogTests : BunitContext, IAsyncLifetime
{
    // Same xunit v2 / MudBlazor async-disposable-interop teardown ordering issue as
    // CreateQueueDialogTests.cs / PurgeQueueDialogTests.cs -- IAsyncLifetime.DisposeAsync must run
    // before the synchronous Dispose() xunit would otherwise call.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter -- without it, <MudDialog> treats itself as "inline and not yet shown" and renders
    // nothing. That type isn't public, so a substitute implementing both it and the public
    // IMudDialogInstance is built via reflection and cascaded as IMudDialogInstance. Same pattern
    // as CreateQueueDialogTests.cs / PurgeQueueDialogTests.cs.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public SendMessageDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_audit);
        Services.AddLogging();
        Services.AddSingleton<SendMessageCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(bool isFifo, bool contentBasedDeduplication = false)
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
                inner.OpenComponent<SbConsole.Plugins.Aws.Pages.SendMessageDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Aws.Pages.SendMessageDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Aws.Pages.SendMessageDialog.ConnectionName), "aws-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Aws.Pages.SendMessageDialog.QueueUrl), "https://sqs/orders");
                inner.AddComponentParameter(4, nameof(SbConsole.Plugins.Aws.Pages.SendMessageDialog.QueueName), "orders");
                inner.AddComponentParameter(5, nameof(SbConsole.Plugins.Aws.Pages.SendMessageDialog.IsFifo), isFifo);
                inner.AddComponentParameter(6, nameof(SbConsole.Plugins.Aws.Pages.SendMessageDialog.ContentBasedDeduplication), contentBasedDeduplication);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void FIFO_queue_shows_message_group_and_deduplication_fields()
    {
        var cut = RenderDialog(isFifo: true);

        cut.FindAll(".message-group-id").Should().ContainSingle();
        cut.FindAll(".message-dedup-id").Should().ContainSingle();
    }

    [Fact]
    public void Standard_queue_hides_FIFO_only_fields()
    {
        var cut = RenderDialog(isFifo: false);

        cut.FindAll(".message-group-id").Should().BeEmpty();
        cut.FindAll(".message-dedup-id").Should().BeEmpty();
    }

    [Fact]
    public void FIFO_queue_with_content_based_dedup_hides_deduplication_id_only()
    {
        var cut = RenderDialog(isFifo: true, contentBasedDeduplication: true);

        cut.FindAll(".message-group-id").Should().ContainSingle();
        cut.FindAll(".message-dedup-id").Should().BeEmpty();
    }

    [Fact]
    public void Send_is_disabled_for_a_FIFO_queue_with_a_blank_deduplication_id_and_no_content_based_dedup()
    {
        // Deduplication ID is Required="true" and rendered whenever IsFifo && !ContentBasedDeduplication,
        // but CanSend didn't actually require it to be filled -- a FIFO send with a blank dedup ID
        // was submittable and would fail at AWS (SendMessage requires MessageDeduplicationId when
        // the queue lacks content-based dedup).
        var cut = RenderDialog(isFifo: true, contentBasedDeduplication: false);
        cut.Find("#message-body").Input("hello world");
        cut.Find(".message-group-id input").Input("group-1");

        cut.Find("button.send-message").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Send_is_enabled_once_a_deduplication_id_is_filled_in_for_a_FIFO_queue()
    {
        var cut = RenderDialog(isFifo: true, contentBasedDeduplication: false);
        cut.Find("#message-body").Input("hello world");
        cut.Find(".message-group-id input").Input("group-1");
        cut.Find(".message-dedup-id input").Input("dedup-1");

        cut.Find("button.send-message").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Send_calls_the_handler_and_closes_the_dialog_when_it_succeeds()
    {
        var cut = RenderDialog(isFifo: false);
        cut.Find("#message-body").Input("hello world");

        cut.Find("button.send-message").Click();
        await Task.Delay(30);

        await _operations.Received(1).SendMessageAsync(
            "mode=default-chain;region=eu-west-1", "https://sqs/orders",
            Arg.Is<SendMessageRequest>(r => r.Body == "hello world"), Arg.Any<CancellationToken>());
        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Send_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.SendMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("message too large")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog(isFifo: false);
        cut.Find("#message-body").Input("hello world");

        cut.Find("button.send-message").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("message too large"));
    }
}
