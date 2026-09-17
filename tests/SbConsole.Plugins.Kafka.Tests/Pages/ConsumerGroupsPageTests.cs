using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.ConsumerGroups;
using SbConsole.Sdk;
// KafkaPlugin lives in the parent namespace SbConsole.Plugins.Kafka -- this test file's own
// namespace (SbConsole.Plugins.Kafka.Tests.Pages) does not see it implicitly, same reason the page
// itself needs an explicit @using (see ConsumerGroups.razor below).
using SbConsole.Plugins.Kafka;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ConsumerGroupsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public ConsumerGroupsPageTests()
    {
        var connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<ListConsumerGroupsQueryHandler>();
    }

    [Fact]
    public async Task Lists_groups_for_the_first_available_connection()
    {
        _operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<ConsumerGroupSummary> { new("order-processors", "Stable", 3, 120) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("order-processors");
        cut.Markup.Should().Contain("Stable");
        cut.Markup.Should().Contain("120");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task A_group_over_the_lag_threshold_shows_the_high_lag_chip()
    {
        _operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<ConsumerGroupSummary> { new("laggy-group", "Stable", 1, KafkaPlugin.LagProblemThreshold + 1) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("high-lag-badge");
    }

    [Fact]
    public async Task A_group_at_or_under_the_lag_threshold_shows_no_high_lag_chip()
    {
        _operations.ListConsumerGroupsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<ConsumerGroupSummary> { new("healthy-group", "Stable", 1, KafkaPlugin.LagProblemThreshold) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.ConsumerGroups>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().NotContain("high-lag-badge");
    }
}
