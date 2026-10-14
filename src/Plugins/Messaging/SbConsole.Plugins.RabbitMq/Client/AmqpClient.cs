using System.Net.Security;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// RabbitMQ.Client 7.x transport (design spec §4): one connection per operation (open, do, close),
/// no automatic recovery -- a console call that fails should fail, not silently reconnect. Not
/// unit-tested (the transport needs a broker); the pure parts live in <see cref="AmqpMessageMapper"/>.
/// </summary>
internal sealed class AmqpClient(ILogger? logger = null)
{
    public const int MaxGetCount = 500;

    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Opens a connection and a channel on the connection's configured vhost, then closes both.</summary>
    public async Task ProbeAsync(RabbitConnectionSettings settings, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(settings, settings.Vhost, ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.CloseAsync(CancellationToken.None);
        await connection.CloseAsync(CancellationToken.None);
    }

    /// <summary>
    /// basic.get (manual ack) up to <paramref name="count"/> messages (clamped 1..500), stopping
    /// early on an empty queue; then one multiple nack-requeue (Peek) or one multiple ack (Consume)
    /// on the last delivery tag. Any exception or cancellation before that skips the ack entirely:
    /// disposing the connection closes the channel, which requeues everything held. Once the
    /// ack/nack has been sent the messages are returned no matter what the close does -- after a
    /// Consume they exist nowhere else.
    /// </summary>
    public async Task<IReadOnlyList<RabbitMessage>> GetMessagesAsync(
        RabbitConnectionSettings settings, string vhost, string queue, int count, GetMode mode, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, MaxGetCount);

        var connection = await OpenAsync(settings, vhost, ct);
        IChannel? channel = null;
        var messages = new List<RabbitMessage>(count);
        try
        {
            channel = await connection.CreateChannelAsync(cancellationToken: ct);

            ulong lastTag = 0;
            for (var i = 0; i < count; i++)
            {
                var result = await channel.BasicGetAsync(queue, autoAck: false, ct);
                if (result is null)
                {
                    break;
                }

                lastTag = result.DeliveryTag;
                messages.Add(AmqpMessageMapper.Map(
                    i, result.Exchange, result.RoutingKey, result.Redelivered, result.BasicProperties, result.Body));
            }

            if (messages.Count > 0)
            {
                // Last chance to back out: a cancelled Consume must not ack.
                ct.ThrowIfCancellationRequested();
                if (mode == GetMode.Consume)
                {
                    await channel.BasicAckAsync(lastTag, multiple: true, CancellationToken.None);
                }
                else
                {
                    await channel.BasicNackAsync(lastTag, multiple: true, requeue: true, CancellationToken.None);
                }
            }
        }
        catch
        {
            // Nothing was acked: dropping the channel requeues whatever was held. Keep the
            // original exception -- a dispose failure here must not replace it.
            await DisposeQuietlyAsync(channel, connection, logger);
            throw;
        }

        await ShutdownQuietlyAsync(channel, connection, logger);
        return messages;
    }

    /// <summary>Post-ack close: closes then disposes channel and connection, logging (never throwing) any failure.</summary>
    internal static async Task ShutdownQuietlyAsync(IChannel? channel, IConnection connection, ILogger? logger)
    {
        if (channel is not null)
        {
            await QuietlyAsync(() => channel.CloseAsync(CancellationToken.None), "closing the channel", logger);
        }

        await QuietlyAsync(() => connection.CloseAsync(CancellationToken.None), "closing the connection", logger);
        await DisposeQuietlyAsync(channel, connection, logger);
    }

    private static async Task DisposeQuietlyAsync(IChannel? channel, IConnection connection, ILogger? logger)
    {
        if (channel is not null)
        {
            await QuietlyAsync(() => channel.DisposeAsync().AsTask(), "disposing the channel", logger);
        }

        await QuietlyAsync(() => connection.DisposeAsync().AsTask(), "disposing the connection", logger);
    }

    private static async Task QuietlyAsync(Func<Task> step, string what, ILogger? logger)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "AMQP get: {Step} failed after the messages were settled; ignoring.", what);
        }
    }

    /// <summary>
    /// Publishes with mandatory=true on a channel with publisher confirms and confirmation
    /// tracking, so BasicPublishAsync completes only once the broker has confirmed. A basic.return
    /// surfaces as <see cref="PublishException"/> with IsReturn = <see cref="PublishOutcome.Unroutable"/>;
    /// a negative confirm is rethrown as an InvalidOperationException saying so.
    /// </summary>
    public async Task<PublishOutcome> PublishAsync(
        RabbitConnectionSettings settings, string vhost, PublishRequest request, CancellationToken ct = default)
    {
        var properties = AmqpMessageMapper.ToProperties(request);

        await using var connection = await OpenAsync(settings, vhost, ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);

        PublishOutcome outcome;
        try
        {
            await channel.BasicPublishAsync(
                request.Exchange, request.RoutingKey, mandatory: true, properties, request.Body, ct);
            outcome = PublishOutcome.Routed;
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            outcome = PublishOutcome.Unroutable;
        }
        catch (PublishException ex)
        {
            throw new InvalidOperationException(
                "The broker did not accept the message (negative publisher confirm) — it was not published.", ex);
        }

        await channel.CloseAsync(CancellationToken.None);
        await connection.CloseAsync(CancellationToken.None);
        return outcome;
    }

    private static Task<IConnection> OpenAsync(RabbitConnectionSettings settings, string vhost, CancellationToken ct) =>
        CreateFactory(settings, vhost).CreateConnectionAsync(ct);

    internal static ConnectionFactory CreateFactory(RabbitConnectionSettings settings, string vhost) => new()
    {
        HostName = settings.Host,
        Port = settings.AmqpPort,
        VirtualHost = vhost,
        UserName = settings.Username,
        Password = settings.Password,
        ClientProvidedName = "sbconsole",
        RequestedConnectionTimeout = ConnectionTimeout,
        AutomaticRecoveryEnabled = false,
        TopologyRecoveryEnabled = false,
        Ssl = new SslOption
        {
            Enabled = settings.Tls,
            ServerName = settings.Host,
            // verifyCert=false is the connection's explicit opt-out; applies to AMQP and HTTPS alike.
            AcceptablePolicyErrors = settings.VerifyCert
                ? SslPolicyErrors.None
                : SslPolicyErrors.RemoteCertificateNotAvailable
                  | SslPolicyErrors.RemoteCertificateNameMismatch
                  | SslPolicyErrors.RemoteCertificateChainErrors,
        },
    };
}
