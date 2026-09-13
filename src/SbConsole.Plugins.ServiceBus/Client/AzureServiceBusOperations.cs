using Azure;
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
    // docs/design.md §7 wants "a clear 'namespace unreachable' state instead of indefinite
    // spinners". The SDK's defaults do not deliver that: Azure.Core's RetryOptions defaults to
    // MaxRetries 3 with a 100s NetworkTimeout, and ServiceBusRetryOptions to MaxRetries 3 with a
    // 60s TryTimeout, so one operation against an unreachable namespace can sit for minutes.
    // These values bound a single attempt at 10s and allow 2 retries — worst case roughly
    // 3 x 10s plus ~2.4s of exponential backoff, i.e. well under a minute before the UI gets an
    // answer — while still absorbing the ordinary transient blip the design relies on retry for.
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    internal const int MaxRetries = 2;
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    // Hard wall-clock cap for the two operations that loop over an unbounded number of broker
    // round-trips (purge, and resubmit's scan). Tightened retry options bound each individual
    // attempt but not the number of attempts a loop makes, so these get their own ceiling.
    internal static readonly TimeSpan BulkOperationTimeout = TimeSpan.FromMinutes(2);

    // ServiceBusAdministrationClient is HTTP-based: it derives its options from Azure.Core's
    // ClientOptions, whose read-only Retry property is an Azure.Core RetryOptions (NetworkTimeout
    // per attempt) — not the AMQP client's ServiceBusRetryOptions (TryTimeout).
    internal static ServiceBusAdministrationClientOptions CreateAdministrationClientOptions()
    {
        var options = new ServiceBusAdministrationClientOptions
        {
            Retry =
            {
                MaxRetries = MaxRetries,
                NetworkTimeout = AttemptTimeout,
                MaxDelay = MaxRetryDelay
            }
        };
        return options;
    }

    internal static ServiceBusClientOptions CreateClientOptions() => new()
    {
        RetryOptions = new ServiceBusRetryOptions
        {
            MaxRetries = MaxRetries,
            TryTimeout = AttemptTimeout,
            MaxDelay = MaxRetryDelay,
        },
    };

    public async Task<ConnectionTestResult> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        ServiceBusAdministrationClient adminClient;
        try
        {
            adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Construction is pure client-side parsing — no network call — so anything thrown here
            // is a bad connection string, and catching it separately keeps it unambiguous. Verified
            // via decompilation of Azure.Messaging.ServiceBus 7.20.2:
            //   * ServiceBusConnectionStringProperties.Parse throws System.FormatException for a
            //     malformed pair or a non-"sb" / unparseable endpoint, and System.UriFormatException
            //     (which derives from FormatException — this is the live-observed "Invalid URI: The
            //     hostname could not be parsed.") from its `new UriBuilder(value)` fallback.
            //   * The ServiceBusAdministrationClient(string, options) ctor itself throws
            //     System.ArgumentException ("MissingConnectionInformation") when Endpoint host,
            //     SharedAccessKeyName or SharedAccessKey is absent, and ArgumentException /
            //     ArgumentNullException from Argument.AssertNotNullOrEmpty for null-or-empty input.
            // Deliberately scoped to construction: the SDK also rewraps an HTTP 400 *response* as
            // ArgumentException, which is a server verdict, not a malformed string.
            return new ConnectionTestResult(false, "Invalid connection string format");
        }

        try
        {
            await foreach (var _ in adminClient.GetQueuesRuntimePropertiesAsync(ct).WithCancellation(ct))
            {
                break; // one item (or a confirmed-empty-but-authenticated page) is enough
            }

            return new ConnectionTestResult(true);
        }
        catch (UnauthorizedAccessException)
        {
            // ServiceBusAdministrationClient never throws RequestFailedException for a *response* —
            // its HttpRequestAndResponse.ThrowIfRequestFailed rewraps a 401 response as
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
        catch (RequestFailedException)
        {
            // No HTTP response ever came back. Verified via decompilation of Azure.Core 1.60.0:
            // HttpClientTransport.ProcessAsync catches System.Net.Http.HttpRequestException (DNS
            // failure, refused connection, TLS failure) and rethrows it as
            // Azure.RequestFailedException. ThrowIfRequestFailed above converts every *response*
            // into some other type, so a RequestFailedException escaping the admin client means
            // the namespace was never reached. Azure.Core's RetryPolicy rethrows the single
            // captured exception unchanged when only one attempt failed.
            return new ConnectionTestResult(false, "Namespace unreachable");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A per-attempt NetworkTimeout expiry (e.g. a blackholed namespace behind a firewall
            // that drops the SYN instead of refusing it) doesn't produce a RequestFailedException —
            // verified via decompilation of Azure.Core 1.60.0: ResponseBodyPolicy throws a
            // TaskCanceledException via CancellationHelper.CreateOperationCanceledException when
            // its own timeout token fires, and ResponseClassifier.IsRetriable treats any
            // OperationCanceledException whose *caller* token isn't the one that fired as
            // retriable. The `!ct.IsCancellationRequested` guard is what distinguishes this SDK
            // timeout from the caller genuinely cancelling — the latter must propagate, not be
            // reported as a connectivity result.
            return new ConnectionTestResult(false, "Namespace unreachable");
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0
            && ex.InnerExceptions.All(inner => inner is RequestFailedException || (inner is OperationCanceledException && !ct.IsCancellationRequested)))
        {
            // Same failures, retried. Azure.Core 1.60.0's RetryPolicy throws
            // `new AggregateException($"Retry failed after {n} tries. Retry settings can be
            // adjusted in ClientOptions.Retry...", exceptions)` once more than one attempt threw —
            // this is the 543-character message live testing saw against a real unreachable
            // namespace. A repeatedly-timing-out attempt retries as TaskCanceledExceptions, not
            // RequestFailedExceptions, so both are accepted here (guarded the same way as the
            // single-attempt case above).
            return new ConnectionTestResult(false, "Namespace unreachable");
        }
        catch (Exception ex)
        {
            // Last resort for genuinely unanticipated failures. Still never returns raw SDK text:
            // FriendlyError collapses and caps it so it can't flood a snackbar or the persisted
            // Connection.LastTestError column.
            return new ConnectionTestResult(false, FriendlyError.From(ex));
        }
    }

    public async Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string connectionString, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var queues = new List<QueueSummary>();
        await foreach (var props in adminClient.GetQueuesRuntimePropertiesAsync(ct).WithCancellation(ct))
        {
            queues.Add(new QueueSummary(props.Name, props.ActiveMessageCount, props.DeadLetterMessageCount, props.ScheduledMessageCount, props.SizeInBytes));
        }

        return queues;
    }

    public async Task CreateQueueAsync(string connectionString, CreateQueueRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
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
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        await adminClient.DeleteQueueAsync(queueName, ct);
    }

    public async Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string connectionString, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var topics = new List<TopicSummary>();
        await foreach (var props in adminClient.GetTopicsRuntimePropertiesAsync(ct).WithCancellation(ct))
        {
            topics.Add(new TopicSummary(props.Name, props.SubscriptionCount, props.SizeInBytes, props.ScheduledMessageCount));
        }

        return topics;
    }

    public async Task CreateTopicAsync(string connectionString, CreateTopicRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        await adminClient.CreateTopicAsync(new CreateTopicOptions(request.Name), ct);
    }

    public async Task DeleteTopicAsync(string connectionString, string topicName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        await adminClient.DeleteTopicAsync(topicName, ct);
    }

    public async Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string connectionString, string topicName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var subscriptions = new List<SubscriptionSummary>();
        await foreach (var props in adminClient.GetSubscriptionsRuntimePropertiesAsync(topicName, ct).WithCancellation(ct))
        {
            subscriptions.Add(new SubscriptionSummary(props.SubscriptionName, props.ActiveMessageCount, props.DeadLetterMessageCount, props.TotalMessageCount));
        }

        return subscriptions;
    }

    public async Task CreateSubscriptionAsync(string connectionString, string topicName, CreateSubscriptionRequest request, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var options = new CreateSubscriptionOptions(topicName, request.Name) { MaxDeliveryCount = request.MaxDeliveryCount };
        if (request.LockDuration is { } lockDuration)
        {
            options.LockDuration = lockDuration;
        }

        if (request.DefaultMessageTimeToLive is { } ttl)
        {
            options.DefaultMessageTimeToLive = ttl;
        }

        await adminClient.CreateSubscriptionAsync(options, ct);
    }

    public async Task DeleteSubscriptionAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        await adminClient.DeleteSubscriptionAsync(topicName, subscriptionName, ct);
    }

    public async Task<IReadOnlyList<PeekedMessage>> PeekMessagesAsync(
        string connectionString, string queueName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
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

    public async Task<IReadOnlyList<PeekedMessage>> PeekSubscriptionMessagesAsync(
        string connectionString, string topicName, string subscriptionName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = fromDeadLetter ? SubQueue.DeadLetter : SubQueue.None };
        await using var receiver = client.CreateReceiver(topicName, subscriptionName, receiverOptions);

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
        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
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

        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);
        await using var sender = client.CreateSender(queueName);
        return await ResubmitDeadLetterCoreAsync(receiver, sender, sequenceNumbers, ct);
    }

    public async Task<int> ResubmitSubscriptionDeadLetterMessagesAsync(string connectionString, string topicName, string subscriptionName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default)
    {
        if (sequenceNumbers.Count == 0)
        {
            return 0;
        }

        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.PeekLock };
        await using var receiver = client.CreateReceiver(topicName, subscriptionName, receiverOptions);
        // Resubmitting publishes back onto the TOPIC, not the subscription -- see the interface's
        // doc comment on ResubmitSubscriptionDeadLetterMessagesAsync for why.
        await using var sender = client.CreateSender(topicName);
        return await ResubmitDeadLetterCoreAsync(receiver, sender, sequenceNumbers, ct);
    }

    // Design: non-matching messages are simply HELD under their PeekLock during the scan (not
    // deferred, not abandoned, not completed) so the scan makes forward progress through the
    // dead-letter sub-queue instead of looping on the same head-of-queue messages; the `finally`
    // below then abandons every held message, restoring it to normal delivery. Shared by both the
    // queue and subscription resubmit methods above, which differ only in how `receiver`/`sender`
    // were constructed.
    private static async Task<int> ResubmitDeadLetterCoreAsync(ServiceBusReceiver receiver, ServiceBusSender sender, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        // Wall-clock ceiling on the scan below. maxAttempts already bounds the iteration count, but
        // not how long each iteration can take, so a degraded namespace could still keep the scan
        // running for many minutes. On expiry the receive/send/complete call throws
        // OperationCanceledException, which propagates to the calling handler's existing catch --
        // no second error path -- after the `finally` has released every held lock.
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(BulkOperationTimeout);
        var scanToken = scanCts.Token;

        var remaining = new HashSet<long>(sequenceNumbers);
        var heldMessages = new List<ServiceBusReceivedMessage>();
        var resubmitted = 0;

        try
        {
            // Bounded scan: keep receiving batches until every requested sequence number has been
            // found or the dead-letter queue is exhausted, so resubmitting a handful of messages out
            // of a much larger dead-letter queue can't loop forever.
            //
            // maxAttempts bounds only this scan loop -- how many 32-message receive batches it will
            // attempt before giving up on finding every requested sequence number. It has no bearing
            // on the restore pass below, which always processes every held message.
            var maxAttempts = sequenceNumbers.Count * 4 + 20;
            for (var attempt = 0; attempt < maxAttempts && remaining.Count > 0; attempt++)
            {
                var batch = await receiver.ReceiveMessagesAsync(maxMessages: 32, maxWaitTime: TimeSpan.FromSeconds(5), scanToken);
                if (batch.Count == 0)
                {
                    break; // dead-letter queue exhausted before every requested message was found
                }

                foreach (var message in batch)
                {
                    if (remaining.Remove(message.SequenceNumber))
                    {
                        await sender.SendMessageAsync(new ServiceBusMessage(message), scanToken);
                        await receiver.CompleteMessageAsync(message, scanToken);
                        resubmitted++;
                    }
                    else
                    {
                        // Hold this message's PeekLock open (don't complete, abandon, or defer it) so
                        // it simply isn't redelivered to this or any other receiver until the finally
                        // below restores it, or its lock naturally expires. This replaces an earlier
                        // defer-based approach that turned out to be broken against the real broker:
                        // abandoning a message received via ReceiveDeferredMessagesAsync does NOT
                        // un-defer it (there is no un-defer operation in Service Bus), which
                        // permanently stranded every non-matching message the first time this code
                        // ran, made purge silently report success while deleting nothing, and left
                        // the dead-letter badge/overview counts permanently wrong. Holding the lock
                        // instead is also crash-safe in a way defer never was: an expired PeekLock is
                        // redelivered automatically by the broker with no code involved, whereas a
                        // deferred message never returns on its own.
                        heldMessages.Add(message);
                    }
                }
            }
        }
        finally
        {
            // Release every held lock, restoring the messages to normal dead-letter delivery.
            // Deliberately CancellationToken.None: AbandonMessageAsync checks its token up front, so
            // an already-cancelled scanToken (this scan hit BulkOperationTimeout) would make even
            // this cleanup a no-op. This must complete even when the scan above did not.
            foreach (var message in heldMessages)
            {
                await TryAbandonAsync(receiver, message);
            }
        }

        return resubmitted;
    }

    private static async Task TryAbandonAsync(ServiceBusReceiver receiver, ServiceBusReceivedMessage message)
    {
        try
        {
            await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Best-effort: an abandon failure (e.g. a lock already expired, which the broker heals
            // by redelivering the message anyway) must not abort the rest of the restore pass.
        }
    }

    public async Task<int> PurgeDeadLetterMessagesAsync(string connectionString, string queueName, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete };
        await using var receiver = client.CreateReceiver(queueName, receiverOptions);
        return await PurgeDeadLetterCoreAsync(receiver, ct);
    }

    public async Task<int> PurgeSubscriptionDeadLetterMessagesAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default)
    {
        await using var client = new ServiceBusClient(connectionString, CreateClientOptions());
        var receiverOptions = new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter, ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete };
        await using var receiver = client.CreateReceiver(topicName, subscriptionName, receiverOptions);
        return await PurgeDeadLetterCoreAsync(receiver, ct);
    }

    // Wall-clock ceiling on the drain loop below. On expiry ReceiveMessagesAsync throws
    // OperationCanceledException, which propagates to the calling handler's existing catch. Shared
    // by both purge methods above.
    private static async Task<int> PurgeDeadLetterCoreAsync(ServiceBusReceiver receiver, CancellationToken ct)
    {
        using var purgeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        purgeCts.CancelAfter(BulkOperationTimeout);

        var purged = 0;
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromSeconds(3), purgeCts.Token);
            if (batch.Count == 0)
            {
                break;
            }

            purged += batch.Count;
        }

        return purged;
    }

    // Composes the admin-list methods above -- no new Azure SDK surface, just orchestration -- so
    // it can be reused unchanged by both the Dead-letter nav badge (ServiceBusPlugin) and the
    // Dead-letter overview page's handler (docs/design.md §6.3), rather than enumerating queues and
    // topics/subscriptions in two separate places.
    public async Task<IReadOnlyList<DeadLetterEntry>> ListDeadLetterEntriesAsync(string connectionString, CancellationToken ct = default)
    {
        var entries = new List<DeadLetterEntry>();

        var queues = await ListQueuesAsync(connectionString, ct);
        foreach (var queue in queues)
        {
            if (queue.DeadLetterMessageCount > 0)
            {
                entries.Add(new DeadLetterEntry("Queue", null, queue.Name, queue.DeadLetterMessageCount));
            }
        }

        var topics = await ListTopicsAsync(connectionString, ct);
        foreach (var topic in topics)
        {
            var subscriptions = await ListSubscriptionsAsync(connectionString, topic.Name, ct);
            foreach (var subscription in subscriptions)
            {
                if (subscription.DeadLetterMessageCount > 0)
                {
                    entries.Add(new DeadLetterEntry("Subscription", topic.Name, subscription.Name, subscription.DeadLetterMessageCount));
                }
            }
        }

        return entries;
    }
}
