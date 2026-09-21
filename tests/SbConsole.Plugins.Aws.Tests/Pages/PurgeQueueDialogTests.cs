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

public class PurgeQueueDialogTests : BunitContext, IAsyncLifetime
{
    // Same xunit v2 / MudBlazor async-disposable-interop teardown ordering issue as
    // CreateQueueDialogTests.cs -- IAsyncLifetime.DisposeAsync must run before the synchronous
    // Dispose() xunit would otherwise call.
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
    // as CreateQueueDialogTests.cs / CreateTopicDialogTests.cs (Kafka) / CreateQueueDialogTests.cs
    // (Service Bus) -- the brief's own suggested RenderComponent<MudDialogProvider>() + ShowAsync
    // approach doesn't reliably surface DialogContent markup in this codebase's bUnit setup.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public PurgeQueueDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_audit);
        Services.AddLogging();
        Services.AddSingleton<PurgeQueueCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(string queueName, long approxVisibleCount)
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
                inner.OpenComponent<SbConsole.Plugins.Aws.Pages.PurgeQueueDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Aws.Pages.PurgeQueueDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Aws.Pages.PurgeQueueDialog.ConnectionName), "aws-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Aws.Pages.PurgeQueueDialog.QueueUrl), "https://sqs/orders");
                inner.AddComponentParameter(4, nameof(SbConsole.Plugins.Aws.Pages.PurgeQueueDialog.QueueName), queueName);
                inner.AddComponentParameter(5, nameof(SbConsole.Plugins.Aws.Pages.PurgeQueueDialog.ApproxVisibleCount), approxVisibleCount);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void Shows_the_approximate_count_and_no_undo_warning()
    {
        var cut = RenderDialog("orders", 1204);

        cut.Markup.Should().Contain("1204").And.Contain("orders").And.ContainEquivalentOf("no undo");
    }

    [Fact]
    public async Task Purge_calls_the_handler_and_closes_the_dialog_when_it_succeeds()
    {
        var cut = RenderDialog("orders", 1204);

        cut.Find("button.confirm-purge").Click();
        await Task.Delay(30);

        await _operations.Received(1).PurgeQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("aws.queue.purge", "aws-dev/orders", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Purge_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.PurgeQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("purge already in progress")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog("orders", 1204);

        cut.Find("button.confirm-purge").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("purge already in progress"));
    }
}
