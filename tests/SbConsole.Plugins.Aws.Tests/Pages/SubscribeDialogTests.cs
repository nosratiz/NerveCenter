using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Pages;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class SubscribeDialogTests : BunitContext, IAsyncLifetime
{
    // Same xunit v2 / MudBlazor async-disposable-interop teardown ordering issue as
    // CreateQueueDialogTests.cs / PublishDialogTests.cs.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();
    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string TopicArn = "arn:aws:sns:eu-west-1:1:orders-topic";

    // Same direct-render pattern as CreateQueueDialogTests.cs.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public SubscribeDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<SubscribeCommandHandler>();
        _snsOperations.SubscribeAsync(Secret, Arg.Any<SubscribeRequest>(), Arg.Any<CancellationToken>()).Returns("arn:sub-1");

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
                inner.OpenComponent<SubscribeDialog>(0);
                inner.AddComponentParameter(1, nameof(SubscribeDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SubscribeDialog.ConnectionName), "aws-dev");
                inner.AddComponentParameter(3, nameof(SubscribeDialog.TopicArn), TopicArn);
                inner.AddComponentParameter(4, nameof(SubscribeDialog.TopicName), "orders-topic");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void The_filter_policy_editor_is_hidden_until_toggled_on()
    {
        var cut = RenderDialog();

        cut.FindAll("#subscribe-filter-policy").Should().BeEmpty();

        cut.Find(".filter-policy-toggle input").Change(true);

        cut.FindAll("#subscribe-filter-policy").Should().ContainSingle();
        cut.FindAll(".filter-scope-select").Should().ContainSingle();
    }

    [Fact]
    public async Task Subscribing_without_a_filter_policy_sends_none()
    {
        var cut = RenderDialog();
        cut.Find("#subscribe-endpoint").Input("arn:aws:sqs:eu-west-1:1:orders-queue");

        cut.Find("button.confirm-subscribe").Click();
        await Task.Delay(30);

        await _snsOperations.Received(1).SubscribeAsync(Secret,
            Arg.Is<SubscribeRequest>(r => r.FilterPolicy == null && r.FilterPolicyScope == null && r.Endpoint == "arn:aws:sqs:eu-west-1:1:orders-queue"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Subscribing_with_a_filter_policy_sends_it_with_the_default_scope()
    {
        var cut = RenderDialog();
        cut.Find("#subscribe-endpoint").Input("arn:aws:sqs:eu-west-1:1:orders-queue");
        cut.Find(".filter-policy-toggle input").Change(true);
        cut.Find("#subscribe-filter-policy").Input("""{"region":["uk"]}""");

        cut.Find("button.confirm-subscribe").Click();
        await Task.Delay(30);

        await _snsOperations.Received(1).SubscribeAsync(Secret,
            Arg.Is<SubscribeRequest>(r => r.FilterPolicy == """{"region":["uk"]}""" && r.FilterPolicyScope == "MessageAttributes"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void An_invalid_filter_policy_shows_an_inline_error_and_disables_subscribe()
    {
        var cut = RenderDialog();
        cut.Find("#subscribe-endpoint").Input("arn:aws:sqs:eu-west-1:1:orders-queue");
        cut.Find(".filter-policy-toggle input").Change(true);
        cut.Find("#subscribe-filter-policy").Input("not json");

        cut.Find(".filter-policy-error").TextContent.Should().Contain("valid JSON");
        cut.Find("button.confirm-subscribe").HasAttribute("disabled").Should().BeTrue();
    }
}
