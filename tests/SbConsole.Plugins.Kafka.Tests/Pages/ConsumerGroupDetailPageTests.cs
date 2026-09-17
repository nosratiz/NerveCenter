using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ConsumerGroupDetailPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();

    public ConsumerGroupDetailPageTests()
    {
        var connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<GetConsumerGroupDetailQueryHandler>();
        Services.AddSingleton<ResetConsumerGroupOffsetCommandHandler>();
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
    }

    // ConnectionId is [SupplyParameterFromQuery] on ConsumerGroupDetail.razor -- bUnit's
    // parameter-builder .Add() only works for plain [Parameter]s and throws for
    // [SupplyParameterFromQuery] ones, so it has to arrive via a real NavigationManager
    // navigation instead. Same split PeekPageTests.cs (this project) and
    // SubscriptionPeekPageTests.cs (SbConsole.Plugins.ServiceBus.Tests) use for their own
    // route-vs-query parameters.
    private void NavigateWithConnectionId()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(
            new Dictionary<string, object?> { ["ConnectionId"] = _connectionId }));
    }

    private Bunit.IRenderedComponent<SbConsole.Plugins.Kafka.Pages.ConsumerGroupDetail> RenderDetailPage()
    {
        NavigateWithConnectionId();
        return Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroupDetail>(parameters => parameters
            .Add(p => p.GroupId, "order-processors"));
    }

    [Fact]
    public async Task Renders_partitions_and_members_for_an_Empty_group()
    {
        _operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(new ConsumerGroupDetail(
                "order-processors", "Empty",
                [new ConsumerGroupPartitionLag("orders", 0, 90, 100, 10, null, null)],
                []));

        var cut = RenderDetailPage();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("idle");
    }

    [Fact]
    public async Task Reset_button_is_enabled_when_the_group_is_Empty()
    {
        _operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(new ConsumerGroupDetail(
                "order-processors", "Empty",
                [new ConsumerGroupPartitionLag("orders", 0, 90, 100, 10, null, null)],
                []));

        var cut = RenderDetailPage();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".reset-offset").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Reset_button_is_disabled_when_the_group_is_not_Empty()
    {
        _operations.GetConsumerGroupDetailAsync("bootstrap.servers=real:9092", "order-processors", Arg.Any<CancellationToken>())
            .Returns(new ConsumerGroupDetail(
                "order-processors", "Stable",
                [new ConsumerGroupPartitionLag("orders", 0, 90, 100, 10, "consumer-1", "10.0.0.5")],
                [new ConsumerGroupMember("consumer-1", "10.0.0.5", [new TopicPartitionRef("orders", 0)])]));

        var cut = RenderDetailPage();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".reset-offset").HasAttribute("disabled").Should().BeTrue();
    }
}
