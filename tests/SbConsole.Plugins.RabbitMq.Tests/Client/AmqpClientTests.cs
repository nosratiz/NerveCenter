using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;
using SbConsole.Plugins.RabbitMq.Client;

namespace SbConsole.Plugins.RabbitMq.Tests.Client;

/// <summary>
/// The transport itself needs a broker; what is tested here is the post-ack shutdown seam: once
/// the ack/nack has gone out, a failing close must not throw away the messages it returns.
/// </summary>
public class AmqpClientTests
{
    private readonly IChannel _channel = Substitute.For<IChannel>();
    private readonly IConnection _connection = Substitute.For<IConnection>();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Fact]
    public async Task Shutdown_after_the_ack_swallows_and_logs_close_and_dispose_failures()
    {
        _channel.CloseAsync(default, default!, default, default).ThrowsAsyncForAnyArgs(new IOException("socket reset"));
        _channel.DisposeAsync().Returns(_ => throw new IOException("dispose"));
        _connection.CloseAsync(default, default!, default, default, default).ThrowsAsyncForAnyArgs(new IOException("socket reset"));
        _connection.DisposeAsync().Returns(_ => throw new IOException("dispose"));

        var act = () => AmqpClient.ShutdownQuietlyAsync(_channel, _connection, _logger);

        await act.Should().NotThrowAsync();
        _logger.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Shutdown_still_closes_the_connection_when_the_channel_close_fails()
    {
        _channel.CloseAsync(default, default!, default, default).ThrowsAsyncForAnyArgs(new IOException("socket reset"));

        await AmqpClient.ShutdownQuietlyAsync(_channel, _connection, _logger);

        await _connection.ReceivedWithAnyArgs(1).CloseAsync(default, default!, default, default, default);
        await _connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task Shutdown_without_a_logger_does_not_throw()
    {
        _connection.CloseAsync(default, default!, default, default, default).ThrowsAsyncForAnyArgs(new IOException("socket reset"));

        var act = () => AmqpClient.ShutdownQuietlyAsync(_channel, _connection, logger: null);

        await act.Should().NotThrowAsync();
    }
}
