using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class TopicsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();

    public TopicsPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "sb-dev", "azure-servicebus", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(_confirmation);
        // Overrides MudServices' real IDialogService so tests that drive a dialog flow (e.g.
        // create-subscription) can stub the dialog's result without rendering a MudDialogProvider.
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<ListTopicsQueryHandler>();
        Services.AddSingleton<CreateTopicCommandHandler>();
        Services.AddSingleton<DeleteTopicCommandHandler>();
        Services.AddSingleton<CreateSubscriptionCommandHandler>();
        Services.AddSingleton<DeleteSubscriptionCommandHandler>();
        Services.AddSingleton<SendMessageCommandHandler>();
        Services.AddSingleton<ListSubscriptionRulesQueryHandler>();
        Services.AddSingleton<DeleteRuleCommandHandler>();
    }

    [Fact]
    public async Task Collapsed_topic_row_shows_aggregated_counts_without_expanding()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096, 3) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active"), new("eu-team", 2, 0, 2, "Active") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("7"); // aggregated Active (5 + 2)
        cut.Markup.Should().Contain("1"); // aggregated Dead-letter (1 + 0)
        cut.FindAll(".subscription-row").Should().BeEmpty("collapsed by default");
    }

    [Fact]
    public async Task Expanding_a_topic_reveals_its_subscription_rows()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        cut.Render();

        cut.FindAll(".subscription-row").Should().HaveCount(1);
        cut.Markup.Should().Contain("uk-team");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Dead_letter_link_carries_connectionId_deadLetter_and_the_rows_live_count()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        cut.Render();

        var link = cut.Find("a.dead-letter-action");
        link.GetAttribute("href").Should().Be(
            $"/p/azure-servicebus/topics/orders/subscriptions/uk-team/peek?connectionId={_connectionId}&deadLetter=true&deadLetterCount=1");
    }

    [Fact]
    public async Task Delete_topic_goes_through_confirmation_with_the_subscription_count()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0, "Active"), new("eu-team", 0, 0, 0, "Active") });
        _confirmation.ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, 2, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, 2, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteTopicAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_subscription_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0, "Active") });
        _confirmation.ConfirmAsync("Delete", "uk-team", false, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        cut.Render();
        cut.Find("button.delete-subscription").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteSubscriptionAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rules_chip_shows_the_live_count_after_the_topic_expands()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'"), new SqlRuleSummary("LowPriority", "Priority = 'Low'") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".rule-count-chip").TextContent.Should().Contain("2");
    }

    [Fact]
    public async Task Expanding_a_subscriptions_rules_panel_reveals_its_rules_without_a_second_fetch()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("HighPriority");
        cut.Markup.Should().Contain("Priority = 'High'");
        await _operations.Received(1).ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_correlation_rules_panel_row_renders_its_id_label_and_properties()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new CorrelationRuleSummary("VipCustomers", new CorrelationMatch(CorrelationId: "vip-123", Label: "Orders"), new Dictionary<string, string> { ["tier"] = "gold" }) });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("VipCustomers");
        cut.Markup.Should().Contain("CorrelationId: vip-123");
        cut.Markup.Should().Contain("Label: Orders");
        cut.Markup.Should().Contain("tier: gold");
    }

    [Fact]
    public async Task A_correlation_rule_with_additional_match_fields_renders_all_of_them()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new CorrelationRuleSummary(
                "VipCustomers",
                new CorrelationMatch(MessageId: "msg-1", To: "sales", ReplyTo: "support", SessionId: "sess-1", ReplyToSessionId: "sess-2", ContentType: "application/json"),
                new Dictionary<string, string>()) });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("MessageId: msg-1");
        cut.Markup.Should().Contain("To: sales");
        cut.Markup.Should().Contain("ReplyTo: support");
        cut.Markup.Should().Contain("SessionId: sess-1");
        cut.Markup.Should().Contain("ReplyToSessionId: sess-2");
        cut.Markup.Should().Contain("ContentType: application/json");
    }

    [Fact]
    public async Task An_other_kind_rules_panel_row_renders_its_raw_filter_text()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new OtherRuleSummary("$Default", "TrueFilter") });

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("$Default");
        cut.Markup.Should().Contain("TrueFilter");
    }

    [Fact]
    public async Task A_subscription_with_no_rules_shows_an_honest_empty_state_in_its_panel()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary>());

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();

        cut.Markup.Should().Contain("No rules.");
    }

    [Fact]
    public async Task Delete_rule_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });
        _confirmation.ConfirmAsync("Delete", "HighPriority", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();
        cut.Find("button.delete-rule").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteRuleAsync("Endpoint=sb://real", "orders", "uk-team", "HighPriority", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_rule_opens_the_dialog_pre_filled_with_the_rules_current_values()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Cancel()));
        _dialogService.ShowAsync<CreateRuleDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();
        cut.Find("button.edit-rule").Click();
        await Task.Delay(30);

        await _dialogService.Received(1).ShowAsync<CreateRuleDialog>("Edit rule", Arg.Is<DialogParameters>(p =>
            p.Get<RuleSummary>(nameof(CreateRuleDialog.ExistingRule)) is SqlRuleSummary
            && ((SqlRuleSummary)p.Get<RuleSummary>(nameof(CreateRuleDialog.ExistingRule))!).Name == "HighPriority"
            && ((SqlRuleSummary)p.Get<RuleSummary>(nameof(CreateRuleDialog.ExistingRule))!).SqlExpression == "Priority = 'High'"
            && p.Get<bool>(nameof(CreateRuleDialog.IsProd)) == _connectionInfo.IsProd));
    }

    [Fact]
    public async Task A_non_canceled_edit_refreshes_the_rules_panel()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary> { new SqlRuleSummary("HighPriority", "Priority = 'High'") });
        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        _dialogService.ShowAsync<CreateRuleDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-subscription-rules").Click();
        cut.Render();
        cut.Find("button.edit-rule").Click();
        await Task.Delay(30);

        await _operations.Received(2).ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Creating_a_subscription_fetches_its_rules_even_when_the_topic_starts_collapsed()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(new List<RuleSummary>());

        var dialogReference = Substitute.For<IDialogReference>();
        dialogReference.Result.Returns(Task.FromResult<DialogResult?>(DialogResult.Ok(true)));
        _dialogService.ShowAsync<CreateSubscriptionDialog>(Arg.Any<string>(), Arg.Any<DialogParameters>())
            .Returns(Task.FromResult(dialogReference));

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        // Topic is still collapsed here - the row and its "+ Subscription" action are always visible.
        cut.FindAll(".subscription-row").Should().BeEmpty("collapsed by default");
        cut.Find("button.add-subscription-action").Click();
        await Task.Delay(30);
        cut.Render();

        await _operations.Received(1).ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_rules_fetch_shows_the_error_instead_of_caching_it_as_empty()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6, "Active") });
        _operations.ListRulesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<RuleSummary>>(new InvalidOperationException("rules listing throttled")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = Render<SbConsole.Plugins.ServiceBus.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        await Task.Delay(30);
        cut.Render();

        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("rules listing throttled"));
    }
}
