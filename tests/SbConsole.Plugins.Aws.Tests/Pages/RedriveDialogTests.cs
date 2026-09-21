using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class RedriveDialogTests : BunitContext, IAsyncLifetime
{
    // Same xunit v2 / MudBlazor async-disposable-interop teardown ordering issue as
    // CreateQueueDialogTests.cs / PurgeQueueDialogTests.cs / SendMessageDialogTests.cs --
    // IAsyncLifetime.DisposeAsync must run before the synchronous Dispose() xunit would otherwise
    // call.
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
    // as CreateQueueDialogTests.cs / PurgeQueueDialogTests.cs / SendMessageDialogTests.cs -- the
    // brief's own suggested plain Render<RedriveDialog>() approach doesn't reliably surface
    // DialogContent markup in this codebase's bUnit setup.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public RedriveDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_audit);
        Services.AddLogging();
        Services.AddSingleton<StartRedriveCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(string sourceQueueName = "orders-dlq")
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
                inner.OpenComponent<SbConsole.Plugins.Aws.Pages.RedriveDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Aws.Pages.RedriveDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Aws.Pages.RedriveDialog.ConnectionName), "aws-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Aws.Pages.RedriveDialog.SourceQueueArn), "arn:aws:sqs:eu-west-1:123456789012:orders-dlq");
                inner.AddComponentParameter(4, nameof(SbConsole.Plugins.Aws.Pages.RedriveDialog.SourceQueueName), sourceQueueName);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void Shows_the_receive_count_reset_warning()
    {
        var cut = RenderDialog("orders-dlq");

        cut.Markup.Should().Contain("orders-dlq").And.Contain("receive count resets");
    }

    [Fact]
    public void Start_button_disabled_until_a_destination_ARN_is_entered()
    {
        var cut = RenderDialog();

        cut.Find("button.start-redrive").HasAttribute("disabled").Should().BeTrue();

        cut.Find(".destination-queue-arn input").Input("arn:aws:sqs:eu-west-1:123456789012:orders");

        cut.Find("button.start-redrive").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Start_calls_the_handler_with_the_entered_destination_and_rate_and_closes_the_dialog_when_it_succeeds()
    {
        var cut = RenderDialog("orders-dlq");
        cut.Find(".destination-queue-arn input").Input("arn:aws:sqs:eu-west-1:123456789012:orders");
        cut.Find(".rate-limit input").Change("10");

        cut.Find("button.start-redrive").Click();
        await Task.Delay(30);

        await _operations.Received(1).StartRedriveTaskAsync(
            "mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq",
            "arn:aws:sqs:eu-west-1:123456789012:orders", 10, Arg.Any<CancellationToken>());
        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Start_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.StartRedriveTaskAsync(
                "mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq",
                "arn:aws:sqs:eu-west-1:123456789012:orders", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("a move task is already running for this queue")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog("orders-dlq");
        cut.Find(".destination-queue-arn input").Input("arn:aws:sqs:eu-west-1:123456789012:orders");

        cut.Find("button.start-redrive").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("a move task is already running for this queue"));
    }
}
