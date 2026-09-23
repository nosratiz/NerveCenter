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
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
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
}
