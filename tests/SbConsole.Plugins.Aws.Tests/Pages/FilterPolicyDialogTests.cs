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

public class FilterPolicyDialogTests : BunitContext, IAsyncLifetime
{
    // Same xunit v2 / MudBlazor async-disposable-interop teardown ordering issue as
    // CreateQueueDialogTests.cs / PublishDialogTests.cs.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();
    private const string Secret = "mode=default-chain;region=eu-west-1";
    private const string SubscriptionArn = "arn:aws:sns:eu-west-1:1:orders-topic:sub-1";

    // Same direct-render pattern as CreateQueueDialogTests.cs: MudDialog only renders under the
    // internal IMudDialogInstanceInternal cascading parameter, so a substitute implementing both
    // it and IMudDialogInstance is built via reflection.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public FilterPolicyDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton(_audit);
        Services.AddLogging();
        Services.AddSingleton<SetFilterPolicyCommandHandler>();

        _dialogInstance = (IMudDialogInstance)Substitute.For(
            [typeof(IMudDialogInstance), MudDialogInstanceInternalType], []);
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderDialog(string? initialPolicy, string? initialScope = null)
    {
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<FilterPolicyDialog>(0);
                inner.AddComponentParameter(1, nameof(FilterPolicyDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(FilterPolicyDialog.ConnectionName), "aws-dev");
                inner.AddComponentParameter(3, nameof(FilterPolicyDialog.TopicName), "orders-topic");
                inner.AddComponentParameter(4, nameof(FilterPolicyDialog.SubscriptionArn), SubscriptionArn);
                inner.AddComponentParameter(5, nameof(FilterPolicyDialog.Endpoint), "orders-queue");
                inner.AddComponentParameter(6, nameof(FilterPolicyDialog.InitialPolicyJson), initialPolicy);
                inner.AddComponentParameter(7, nameof(FilterPolicyDialog.InitialScope), initialScope);
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public void Pre_fills_the_existing_policy_pretty_printed()
    {
        var cut = RenderDialog("""{"region":["uk"]}""");

        var text = cut.Find("#filter-policy-json").GetAttribute("value") ?? cut.Find("#filter-policy-json").TextContent;
        text.Should().Be(TopicDetail.FormatJson("""{"region":["uk"]}"""));
        text.Should().Contain("\n");
    }

    [Fact]
    public async Task Save_sets_the_edited_policy_with_the_existing_scope_and_closes()
    {
        var cut = RenderDialog("""{"region":["uk"]}""", "MessageBody");
        cut.Find("#filter-policy-json").Input("""{"region":["us"]}""");

        cut.Find("button.save-filter-policy").Click();
        await Task.Delay(30);

        await _snsOperations.Received(1).SetSubscriptionFilterPolicyAsync(Secret, SubscriptionArn, """{"region":["us"]}""", "MessageBody", Arg.Any<CancellationToken>());
        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Emptying_the_editor_clears_the_policy()
    {
        var cut = RenderDialog("""{"region":["uk"]}""");
        cut.Find("#filter-policy-json").Input("");

        cut.Find("button.save-filter-policy").Click();
        await Task.Delay(30);

        await _snsOperations.Received(1).SetSubscriptionFilterPolicyAsync(Secret, SubscriptionArn, null, "MessageAttributes", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Invalid_json_shows_an_inline_error_and_disables_save()
    {
        var cut = RenderDialog(null);

        cut.FindAll(".filter-policy-error").Should().BeEmpty();
        cut.Find("#filter-policy-json").Input("""["uk"]""");

        cut.Find(".filter-policy-error").TextContent.Should().Contain("JSON object");
        cut.Find("button.save-filter-policy").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task A_failure_shows_the_error_and_keeps_the_dialog_open()
    {
        _snsOperations.SetSubscriptionFilterPolicyAsync(Secret, SubscriptionArn, Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Invalid parameter: FilterPolicy")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog("""{"region":["uk"]}""");
        cut.Find("button.save-filter-policy").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("Invalid parameter: FilterPolicy"));
    }
}
