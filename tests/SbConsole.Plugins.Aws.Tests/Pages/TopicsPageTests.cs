using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class TopicsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public TopicsPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "aws-dev", "aws", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns([_connectionInfo]);
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton<ListTopicsQueryHandler>();
        Services.AddSingleton<CreateTopicCommandHandler>();
        Services.AddSingleton<DeleteTopicCommandHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
        Services.AddSingleton<GetTopicDeliveryFailureCountQueryHandler>();
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddLogging();
    }

    [Fact]
    public void Renders_topics_from_the_handler()
    {
        _snsOperations.ListTopicsAsync("mode=access-keys;region=us-east-1", Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("order-events-topic", "arn:aws:sns:us-east-1:1:order-events-topic", false, 3, 0, false)]);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Topics>();

        cut.Markup.Should().Contain("order-events-topic");
        cut.Find("td.sub-count").TextContent.Should().Be("3");
    }

    [Fact]
    public void Shows_a_flag_for_a_topic_with_no_subscriptions()
    {
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("invoice-issued-topic", "arn:aws:sns:us-east-1:1:invoice-issued-topic", false, 0, 0, false)]);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Topics>();

        cut.FindAll(".no-subs-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_calls_the_handler_only_when_confirmation_service_returns_true()
    {
        var confirmation = Substitute.For<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "order-events-topic", false, null, Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(confirmation);
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("order-events-topic", "arn:aws:sns:us-east-1:1:order-events-topic", false, 0, 0, false)]);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Topics>();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(50);

        await _snsOperations.Received(1).DeleteTopicAsync("mode=access-keys;region=us-east-1", "arn:aws:sns:us-east-1:1:order-events-topic", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Shows_the_failure_count_once_it_loads()
    {
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("shipment-updates-topic", "arn:aws:sns:us-east-1:1:shipment-updates-topic", false, 5, 2, false)]);
        _snsOperations.GetDeliveryFailureCountAsync("mode=access-keys;region=us-east-1", "shipment-updates-topic", Arg.Any<CancellationToken>())
            .Returns(1204L);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Topics>();
        await Task.Delay(50);
        cut.Render();

        cut.Find("td.failed-24h").TextContent.Should().Be("1204");
    }

    [Fact]
    public async Task Shows_unavailable_when_the_metrics_call_is_denied()
    {
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("shipment-updates-topic", "arn:aws:sns:us-east-1:1:shipment-updates-topic", false, 5, 2, false)]);
        _snsOperations.GetDeliveryFailureCountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("AccessDenied")));

        var cut = Render<SbConsole.Plugins.Aws.Pages.Topics>();
        await Task.Delay(50);
        cut.Render();

        cut.Find("td.failed-24h").TextContent.Should().Be("—");
    }

    [Fact]
    public async Task A_superseded_failure_count_load_does_not_overwrite_the_newer_result()
    {
        // The initial load's failure-count fetch is slow (call #1); the reload triggered by the
        // delete below is fast (call #2) and finishes first. Once the slow call #1 finally
        // resolves, it must not clobber the fresher value call #2 already wrote.
        var topic = new TopicSummary("topic-a", "arn:aws:sns:us-east-1:1:topic-a", false, 0, 0, false);
        Services.AddSingleton<ISnsOperations>(new SequencedFailureCountSnsOperations(topic));

        var confirmation = Substitute.For<IConfirmationService>();
        confirmation.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        Services.AddSingleton(confirmation);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Topics>();

        // The delete's own reload (generation 2) resolves synchronously via the fast call #2,
        // well before the initial load's slow call #1 (generation 1) finishes.
        cut.Find("button.delete-topic").Click();
        await Task.Delay(50);
        cut.Render();
        cut.Find("td.failed-24h").TextContent.Should().Be("42");

        // Let the slow call #1 resolve. A generation check must stop it from writing stale data.
        await Task.Delay(300);
        cut.Render();

        cut.Find("td.failed-24h").TextContent.Should().Be("42");
    }

    private sealed class SequencedFailureCountSnsOperations(TopicSummary topic) : ISnsOperations
    {
        private int _failureCountCalls;

        public Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string secret, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TopicSummary>>([topic]);

        public Task<string> CreateTopicAsync(string secret, CreateTopicRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteTopicAsync(string secret, string topicArn, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsForEndpointAsync(string secret, string endpoint, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _failureCountCalls) == 1)
            {
                await Task.Delay(150, ct);
                return 999L;
            }

            return 42L;
        }
    }
}
