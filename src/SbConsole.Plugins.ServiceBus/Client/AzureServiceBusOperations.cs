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
        catch (UnauthorizedAccessException)
        {
            // ServiceBusAdministrationClient never throws RequestFailedException directly — its
            // HttpRequestAndResponse.ThrowIfRequestFailed rewraps a 401 response as
            // UnauthorizedAccessException (verified via decompilation of 7.20.2).
            return new ConnectionTestResult(false, "Unauthorized (401)");
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.QuotaExceeded)
        {
            // A 403 response is rewrapped as either InvalidOperationException (the "forbidden by
            // invalid operation" sub-code) or a ServiceBusException with Reason == QuotaExceeded
            // (verified via decompilation of 7.20.2). The InvalidOperationException case falls
            // through to the generic catch below, which still reports a readable message.
            return new ConnectionTestResult(false, "Forbidden (403)");
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

    // Design tradeoff: non-matching messages are deferred (not abandoned) during the scan so the
    // scan can make forward progress through the queue instead of looping on the same head-of-queue
    // messages; the cost is that deferral is durable (it does NOT self-heal like an expiring
    // PeekLock does), so an explicit, best-effort restoration pass is required afterward — see the
    // try/finally below — to avoid permanently stranding messages if the scan is cancelled or fails.
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
        var deferredSequenceNumbers = new List<long>();
        var resubmitted = 0;

        try
        {
            // Bounded scan: keep receiving batches until every requested sequence number has been
            // found or the dead-letter queue is exhausted, so resubmitting a handful of messages out
            // of a much larger dead-letter queue can't loop forever.
            //
            // Defer (not abandon) every non-matching message while scanning: abandoning would make it
            // immediately redeliverable, causing the same head-of-queue messages to loop back before
            // the scan ever reaches deeper ones. A deferred message is skipped by ordinary receive
            // calls until explicitly re-received by sequence number, which is what makes forward
            // progress possible.
            //
            // maxAttempts bounds only this scan loop — how many 32-message receive batches it will
            // attempt before giving up on finding every requested sequence number. It has no bearing
            // on the separate restore pass below, which always processes every deferred message.
            var maxAttempts = sequenceNumbers.Count * 4 + 20;
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
                        // Record before deferring, not after: if DeferMessageAsync applies the defer
                        // broker-side but then throws (e.g. a transient fault surfaced by the SDK's
                        // retry policy after the operation already succeeded), the sequence number
                        // must still reach the restore pass in `finally` — recording first costs at
                        // worst one harmless MessageNotFound if the defer never actually applied,
                        // versus a permanently stranded message if we recorded after and never got there.
                        deferredSequenceNumbers.Add(message.SequenceNumber);
                        await receiver.DeferMessageAsync(message, cancellationToken: ct);
                    }
                }
            }
        }
        finally
        {
            // Restore every deferred message back to normal delivery order — abandoning a deferred
            // message un-defers it, the correct way to "put back" a message that was only set aside
            // while scanning, not actually re-dead-lettered. This runs no matter how the scan above
            // exits (normal completion, break, or an exception/cancellation) because a deferred
            // message never returns to normal delivery on its own.
            //
            // Deliberately uses CancellationToken.None, not the caller's `ct`: both
            // ReceiveDeferredMessagesAsync and AbandonMessageAsync check their token up front, so if
            // this restoration ran with an already-cancelled `ct` it would throw immediately and
            // restore nothing. This cleanup must complete even when the original request did not.
            await RestoreDeferredMessagesAsync(receiver, deferredSequenceNumbers);
        }

        return resubmitted;
    }

    // Best-effort restoration of messages deferred by the scan above. Batches sequence numbers via
    // the SDK's plural ReceiveDeferredMessagesAsync(IEnumerable<long>, CancellationToken) overload,
    // which (verified by decompiling Azure.Messaging.ServiceBus 7.20.2) throws a ServiceBusException
    // with Reason == MessageNotFound for the WHOLE call if even one requested sequence number is no
    // longer deferred (e.g. a concurrent consumer already received-and-settled it directly) — it
    // does not return a partial/shorter list. So a chunk-level failure falls back to restoring that
    // chunk's messages one at a time, each independently try/caught, so one bad sequence number
    // can't strand the rest of an otherwise-healthy chunk. Any restoration failure is swallowed:
    // this is cleanup for a resubmit that has already happened, not an operation whose failure
    // should mask or abort the original result.
    private static async Task RestoreDeferredMessagesAsync(ServiceBusReceiver receiver, IReadOnlyList<long> deferredSequenceNumbers)
    {
        const int restoreChunkSize = 100;

        foreach (var chunk in deferredSequenceNumbers.Chunk(restoreChunkSize))
        {
            try
            {
                var restored = await receiver.ReceiveDeferredMessagesAsync(chunk, CancellationToken.None);
                foreach (var message in restored)
                {
                    await TryAbandonAsync(receiver, message);
                }
            }
            catch
            {
                foreach (var sequenceNumber in chunk)
                {
                    try
                    {
                        var single = await receiver.ReceiveDeferredMessagesAsync(new[] { sequenceNumber }, CancellationToken.None);
                        if (single.Count > 0)
                        {
                            await TryAbandonAsync(receiver, single[0]);
                        }
                    }
                    catch
                    {
                        // Best-effort: this sequence number could not be restored (e.g. it was no
                        // longer deferred). Move on rather than aborting the rest of the restore pass.
                    }
                }
            }
        }
    }

    private static async Task TryAbandonAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Best-effort: leave it deferred rather than letting an abandon failure (e.g. a lock
            // lost to a concurrent consumer) abort the rest of the restore pass.
        }
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
