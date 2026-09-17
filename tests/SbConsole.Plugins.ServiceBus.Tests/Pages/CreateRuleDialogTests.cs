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
using SbConsole.Plugins.ServiceBus.Rules;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateRuleDialogTests : BunitContext, IAsyncLifetime
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

    public CreateRuleDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateRuleCommandHandler>();

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
                inner.OpenComponent<CreateRuleDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateRuleDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateRuleDialog.ConnectionName), "sb-dev");
                inner.AddComponentParameter(3, nameof(CreateRuleDialog.TopicName), "orders");
                inner.AddComponentParameter(4, nameof(CreateRuleDialog.SubscriptionName), "uk-team");
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
        cut.Find("input#rule-name").Input("HighPriority");
        cut.Find("input#rule-sql-expression").Input("Priority = 'High'");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Is<CreateRuleRequest>(r => r is CreateSqlRuleRequest && ((CreateSqlRuleRequest)r).Name == "HighPriority" && ((CreateSqlRuleRequest)r).SqlExpression == "Priority = 'High'"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateRuleAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CreateRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("rule already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("HighPriority");
        cut.Find("input#rule-sql-expression").Input("Priority = 'High'");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("rule already exists"));
    }

    [Fact]
    public async Task Switching_to_correlation_mode_hides_the_sql_field_and_shows_correlation_fields()
    {
        var cut = RenderDialog();

        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();

        cut.FindAll("input#rule-sql-expression").Should().BeEmpty();
        cut.Find("input#rule-correlation-id").Should().NotBeNull();
        cut.Find("input#rule-label").Should().NotBeNull();
    }

    [Fact]
    public async Task Save_is_disabled_in_correlation_mode_until_at_least_one_match_field_is_set()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();

        cut.Find("button.save-rule").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input#rule-correlation-id").Input("vip-123");
        cut.Render();

        cut.Find("button.save-rule").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Saving_a_correlation_rule_with_custom_properties_calls_the_handler_and_closes()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();
        cut.Find("input#rule-correlation-id").Input("vip-123");
        cut.Find("input#rule-label").Input("Orders");
        cut.Find("button.add-property-row").Click();
        cut.Render();
        cut.Find(".property-key input").Input("tier");
        cut.Find(".property-value input").Input("gold");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                && ((CreateCorrelationRuleRequest)r).Name == "VipCustomers"
                && ((CreateCorrelationRuleRequest)r).Match.CorrelationId == "vip-123"
                && ((CreateCorrelationRuleRequest)r).Match.Label == "Orders"
                && ((CreateCorrelationRuleRequest)r).Properties["tier"] == "gold"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_a_correlation_rule_with_two_distinct_property_rows_sends_both()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();
        cut.Find("input#rule-correlation-id").Input("vip-123");

        cut.Find("button.add-property-row").Click();
        cut.Render();
        cut.Find("button.add-property-row").Click();
        cut.Render();

        var rows = cut.FindAll(".property-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".property-key input")!.Input("tier");
        rows[0].QuerySelector(".property-value input")!.Input("gold");
        rows[1].QuerySelector(".property-key input")!.Input("region");
        rows[1].QuerySelector(".property-value input")!.Input("uk");

        cut.Find("button.save-rule").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                && ((CreateCorrelationRuleRequest)r).Properties.Count == 2
                && ((CreateCorrelationRuleRequest)r).Properties["tier"] == "gold"
                && ((CreateCorrelationRuleRequest)r).Properties["region"] == "uk"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_a_correlation_rule_with_duplicate_property_keys_keeps_the_last_value_and_does_not_crash()
    {
        var cut = RenderDialog();
        cut.Find("input#rule-name").Input("VipCustomers");
        cut.Find("button.rule-mode-correlation").Click();
        cut.Render();
        cut.Find("input#rule-correlation-id").Input("vip-123");

        cut.Find("button.add-property-row").Click();
        cut.Render();
        cut.Find("button.add-property-row").Click();
        cut.Render();

        var rows = cut.FindAll(".property-row");
        rows.Should().HaveCount(2);
        rows[0].QuerySelector(".property-key input")!.Input("tier");
        rows[0].QuerySelector(".property-value input")!.Input("gold");
        rows[1].QuerySelector(".property-key input")!.Input("tier");
        rows[1].QuerySelector(".property-value input")!.Input("platinum");

        var act = () => cut.Find("button.save-rule").Click();
        act.Should().NotThrow();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).CreateRuleAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<CreateRuleRequest>(r => r is CreateCorrelationRuleRequest
                && ((CreateCorrelationRuleRequest)r).Properties.Count == 1
                && ((CreateCorrelationRuleRequest)r).Properties["tier"] == "platinum"),
            Arg.Any<CancellationToken>());
    }
}
