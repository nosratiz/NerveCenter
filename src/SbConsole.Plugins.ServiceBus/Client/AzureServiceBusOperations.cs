using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Client;

/// <summary>
/// The only real implementation of IServiceBusOperations. Its own methods get light test
/// coverage by necessity — they can't be meaningfully unit-tested without a real or emulated
/// broker (docs/design.md §6/§8) — the substitutable interface is where the test leverage is.
/// </summary>
public sealed class AzureServiceBusOperations : IServiceBusOperations
{
    public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        try
        {
            var adminClient = new ServiceBusAdministrationClient(connectionString);
            await foreach (var _ in adminClient.GetQueuesRuntimePropertiesAsync(ct).WithCancellation(ct))
            {
                break; // one item (or a confirmed-empty-but-authenticated page) is enough
            }

            return new ConnectionTestResult(true);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            return new ConnectionTestResult(false, $"Unauthorized ({ex.Status})");
        }
        catch (Exception ex)
        {
            // Covers malformed connection strings (thrown synchronously at client construction,
            // before any network call) and every other reachability/auth failure. The SDK's exact
            // exception type for a bad connection string isn't load-bearing here — every failure
            // path becomes a readable ConnectionTestResult, never an unhandled throw.
            return new ConnectionTestResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string connectionString, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString);
        var queues = new List<QueueSummary>();
        await foreach (var props in adminClient.GetQueuesRuntimePropertiesAsync(ct).WithCancellation(ct))
        {
            queues.Add(new QueueSummary(props.Name, props.ActiveMessageCount, props.DeadLetterMessageCount, props.ScheduledMessageCount, props.SizeInBytes));
        }

        return queues;
    }

    public async Task CreateQueueAsync(string connectionString, CreateQueueRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString);
        var options = new CreateQueueOptions(request.Name) { MaxDeliveryCount = request.MaxDeliveryCount };
        if (request.LockDuration is { } lockDuration)
        {
            options.LockDuration = lockDuration;
        }

        if (request.DefaultMessageTimeToLive is { } ttl)
        {
            options.DefaultMessageTimeToLive = ttl;
        }

        await adminClient.CreateQueueAsync(options, ct);
    }

    public async Task DeleteQueueAsync(string connectionString, string queueName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString);
        await adminClient.DeleteQueueAsync(queueName, ct);
    }

    public async Task<IReadOnlyList<PeekedMessage>> PeekMessagesAsync(
        string connectionString, string queueName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString);
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = fromDeadLetter ? SubQueue.DeadLetter : SubQueue.None };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);

        // ServiceBusReceiver.PeekMessagesAsync(int, long?, CancellationToken) already accepts a
        // nullable starting sequence number, so no branch is needed here.
        var received = await receiver.PeekMessagesAsync(maxMessages, fromSequenceNumber, ct);

        return received.Select(m => new PeekedMessage(
            m.SequenceNumber,
            m.Body.ToString(),
            m.ContentType,
            m.EnqueuedTime,
            m.DeliveryCount,
            m.ApplicationProperties.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
            m.DeadLetterReason,
            m.DeadLetterErrorDescription)).ToList();
    }

    public async Task SendMessageAsync(string connectionString, string queueName, SendMessageRequest request, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString);
        await using var sender = client.CreateSender(queueName);

        var message = new ServiceBusMessage(request.Body) { ContentType = request.ContentType };
        if (request.Properties is not null)
        {
            foreach (var (key, value) in request.Properties)
            {
                message.ApplicationProperties[key] = value;
            }
        }

        if (request.ScheduledEnqueueTime is { } scheduled)
        {
            await sender.ScheduleMessageAsync(message, scheduled, ct);
        }
        else
        {
            await sender.SendMessageAsync(message, ct);
        }
    }

    public async Task<int> ResubmitDeadLetterMessagesAsync(string connectionString, string queueName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default)
    {
        if (sequenceNumbers.Count == 0)
        {
            return 0;
        }

        await using var client = new ServiceBusClient(connectionString);
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);
        await using var sender = client.CreateSender(queueName);

        var remaining = new HashSet<long>(sequenceNumbers);
        var resubmitted = 0;
        // Bounded scan: keep receiving batches until every requested sequence number has been
        // found or the dead-letter queue is exhausted, so resubmitting a handful of messages out
        // of a much larger dead-letter queue can't loop forever.
        var maxAttempts = sequenceNumbers.Count * 4 + 10;
        for (var attempt = 0; attempt < maxAttempts && remaining.Count > 0; attempt++)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 32, maxWaitTime: TimeSpan.FromSeconds(5), ct);
            if (batch.Count == 0)
            {
                break; // dead-letter queue exhausted before every requested message was found
            }

            foreach (var message in batch)
            {
                if (remaining.Remove(message.SequenceNumber))
                {
                    await sender.SendMessageAsync(new ServiceBusMessage(message), ct);
                    await receiver.CompleteMessageAsync(message, ct);
                    resubmitted++;
                }
                else
                {
                    await receiver.AbandonMessageAsync(message, cancellationToken: ct);
                }
            }
        }

        return resubmitted;
    }

    public async Task<int> PurgeDeadLetterMessagesAsync(string connectionString, string queueName, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString);
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);

        var purged = 0;
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromSeconds(3), ct);
            if (batch.Count == 0)
            {
                break;
            }

            purged += batch.Count;
        }

        return purged;
    }
}
