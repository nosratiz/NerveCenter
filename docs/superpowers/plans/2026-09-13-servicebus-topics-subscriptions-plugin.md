# Service Bus Topics & Subscriptions Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add topic/subscription management (one combined expandable table, matching the actual UI mockups) plus a cross-connection Dead-letter overview page with a live nav badge to the `SbConsole.Plugins.ServiceBus` plugin.

**Architecture:** Additive throughout. `IServiceBusOperations` gains new parallel methods (topics/subscriptions CRUD, subscription messaging, and one cross-cutting `ListDeadLetterEntriesAsync`) — no existing signature changes. `IPlugin` (the SDK) gains one new method, `GetNavBadgeAsync`, with a default implementation so no other plugin is forced to implement it. `NavMenu.razor` (host) computes live badges in a background loop. `Topics.razor` is one page with a flattened topic/subscription row view-model rendered through a single `MudTable`, not a drill-down across separate pages — this supersedes this plan's own first draft (see docs/design.md §6.2's note on why).

**Tech Stack:** .NET 10, C# latest, warnings-as-errors, Blazor Interactive Server, MudBlazor 9.9.0, `Azure.Messaging.ServiceBus` 7.20.2 (plugin project only), xUnit + FluentAssertions 7.x + NSubstitute + bUnit 2.10.3 (`BunitContext`/`Render<T>()`).

## Global Constraints

- Filter rules are out of scope. The mockup's "Rules" column is not built (docs/design.md §6.2) — every subscription gets the topic's default catch-all rule.
- `IServiceBusOperations`'s existing queue methods, signatures, and behavior do not change. New methods sit alongside them. Sending to a topic reuses the existing `SendMessageAsync` unmodified.
- `AzureServiceBusOperations` shares implementation internally via private helpers between the queue and subscription variants of resubmit/purge/peek (mirrors the already-shipped queue implementation's own internal shape).
- Topic/subscription aggregation (the counts shown on a collapsed topic row) is eager — computed in the query handler by calling `ListSubscriptionsAsync` once per topic when the page loads — not lazy-on-expand (docs/design.md §6.2).
- Plugin handlers follow Core's naming convention and are registered in `ServiceBusPlugin.ConfigureServices` in the **same task** that creates them.
- Every handler wraps its `IServiceBusOperations` call in try/catch, logs the full exception via an injected `ILogger<T>`, and returns `PluginResult[<T>].Fail(ex)` (the `Exception` overload, routing through `FriendlyError`) — **except** `ListDeadLetterOverviewQueryHandler` (Task 10), which is a best-effort cross-connection aggregator: it catches per-connection, logs, and skips that connection rather than failing the whole call, and does not return a `PluginResult` at all (there is no single-connection failure to represent — see Task 10).
- Destructive actions (delete topic, delete subscription, purge subscription dead-letter) go through `IConfirmationService`'s typed-for-prod gate, with `IsProd`/`ConnectionName` always looked up from `IConnectionProvider` by connection id — **never** trusted from a URL query parameter. This is a hard requirement: docs/design.md §6.1 documents this exact class of bug as a Critical finding in the Queues plan. Every new page in this plan applies that pattern from the start, not as a retrofit.
- Testing strategy is unit tests only against a substitute of `IServiceBusOperations`/`IConnectionProvider`/`IPlugin` — no Testcontainers, no real network calls (docs/design.md §8).
- The `NavMenu` badge-refresh loop is tested by calling its `RefreshBadgesAsync` method directly, never by waiting out its real 60-second timer (docs/design.md §8).
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

---

## Task 1: SDK models and admin-level `IServiceBusOperations` additions (topics, subscriptions, dead-letter enumeration)

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Client/TopicSummary.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/SubscriptionSummary.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/CreateTopicRequest.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/CreateSubscriptionRequest.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/DeadLetterEntry.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`

**Interfaces:**
- Consumes: the existing `AzureServiceBusOperations.CreateAdministrationClientOptions()` and (for `ListDeadLetterEntriesAsync`) its own `ListQueuesAsync`/`ListTopicsAsync`/`ListSubscriptionsAsync` added in this same task.
- Produces: `TopicSummary(string Name, int SubscriptionCount, long SizeInBytes, long ScheduledMessageCount)`, `SubscriptionSummary(string Name, long ActiveMessageCount, long DeadLetterMessageCount, long TotalMessageCount)`, `CreateTopicRequest(string Name)`, `CreateSubscriptionRequest(string Name, int MaxDeliveryCount = 10, TimeSpan? LockDuration = null, TimeSpan? DefaultMessageTimeToLive = null)`, `DeadLetterEntry(string EntityType, string? TopicName, string EntityName, long Count)`, and seven new `IServiceBusOperations` methods (`ListTopicsAsync`, `CreateTopicAsync`, `DeleteTopicAsync`, `ListSubscriptionsAsync`, `CreateSubscriptionAsync`, `DeleteSubscriptionAsync`, `ListDeadLetterEntriesAsync`) that later tasks' handlers call.

Every Azure SDK call below is verified against the exact installed `Azure.Messaging.ServiceBus` 7.20.2 assembly (decompiled with `ilspycmd` against `~/.nuget/packages/azure.messaging.servicebus/7.20.2/lib/netstandard2.0/Azure.Messaging.ServiceBus.dll`), not assumed. `ServiceBusAdministrationClient.GetTopicsRuntimePropertiesAsync(CancellationToken)`, `.CreateTopicAsync(CreateTopicOptions, CancellationToken)`, `.DeleteTopicAsync(string, CancellationToken)`, `.GetSubscriptionsRuntimePropertiesAsync(string topicName, CancellationToken)`, `.CreateSubscriptionAsync(CreateSubscriptionOptions, CancellationToken)`, `.DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken)` all exist with these exact signatures. `TopicRuntimeProperties` has `Name`, `SizeInBytes`, `SubscriptionCount`, `ScheduledMessageCount` (confirmed present — a topic itself can have scheduled messages awaiting fan-out). `SubscriptionRuntimeProperties` has `SubscriptionName`, `ActiveMessageCount`, `DeadLetterMessageCount`, `TotalMessageCount`, `TransferMessageCount`, `TransferDeadLetterMessageCount` — **no `ScheduledMessageCount` field exists on it**; a subscription's scheduled count is not something the SDK exposes. `CreateSubscriptionOptions(string topicName, string subscriptionName)` has `MaxDeliveryCount`, `LockDuration`, `DefaultMessageTimeToLive` properties, mirroring `CreateQueueOptions` exactly.

- [ ] **Step 1: Create the five new model files**

`src/SbConsole.Plugins.ServiceBus/Client/TopicSummary.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record TopicSummary(string Name, int SubscriptionCount, long SizeInBytes, long ScheduledMessageCount);
```

`src/SbConsole.Plugins.ServiceBus/Client/SubscriptionSummary.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record SubscriptionSummary(
    string Name,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long TotalMessageCount);
```

`src/SbConsole.Plugins.ServiceBus/Client/CreateTopicRequest.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CreateTopicRequest(string Name);
```

`src/SbConsole.Plugins.ServiceBus/Client/CreateSubscriptionRequest.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record CreateSubscriptionRequest(
    string Name,
    int MaxDeliveryCount = 10,
    TimeSpan? LockDuration = null,
    TimeSpan? DefaultMessageTimeToLive = null);
```

`src/SbConsole.Plugins.ServiceBus/Client/DeadLetterEntry.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

/// <summary>One queue or subscription with a non-zero dead-letter count, for one connection.
/// EntityType is "Queue" or "Subscription"; TopicName is null for a queue.</summary>
public sealed record DeadLetterEntry(string EntityType, string? TopicName, string EntityName, long Count);
```

- [ ] **Step 2: Add the seven new methods to `IServiceBusOperations`**

Open `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`. Insert the following block immediately after the existing `PurgeDeadLetterMessagesAsync` line (the last member), before the closing `}` of the interface:

```csharp

    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string connectionString, CancellationToken ct = default);

    Task CreateTopicAsync(string connectionString, CreateTopicRequest request, CancellationToken ct = default);

    /// <summary>Destructive. Cascades: deletes every subscription on the topic too.</summary>
    Task DeleteTopicAsync(string connectionString, string topicName, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string connectionString, string topicName, CancellationToken ct = default);

    Task CreateSubscriptionAsync(string connectionString, string topicName, CreateSubscriptionRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteSubscriptionAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default);

    /// <summary>Every queue and subscription with a non-zero dead-letter count for this connection.
    /// One seam shared by the Dead-letter nav badge and the Dead-letter overview page.</summary>
    Task<IReadOnlyList<DeadLetterEntry>> ListDeadLetterEntriesAsync(string connectionString, CancellationToken ct = default);
```

- [ ] **Step 3: Implement the six admin (create/list/delete) methods in `AzureServiceBusOperations`**

Open `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`. Insert the following block immediately after the closing brace of `DeleteQueueAsync` (before `PeekMessagesAsync`):

```csharp

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
```

- [ ] **Step 4: Implement `ListDeadLetterEntriesAsync`**

Insert at the very end of the class, just before its closing `}`:

```csharp

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
```

- [ ] **Step 5: Build and confirm no regressions**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` — these methods can't be meaningfully unit-tested without a broker (same precedent as the existing queue CRUD methods), so the build is the correctness gate for Steps 3-4.

Run: `dotnet test`
Expected: same pass count as before this task.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/
git commit -m "feat: add topic/subscription CRUD and dead-letter enumeration to IServiceBusOperations

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: Subscription messaging in `AzureServiceBusOperations` — peek, resubmit, purge

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`

**Interfaces:**
- Consumes: `PeekedMessage` (unchanged), `AzureServiceBusOperations.BulkOperationTimeout`/`CreateClientOptions()` (already in the file).
- Produces: `PeekSubscriptionMessagesAsync`, `ResubmitSubscriptionDeadLetterMessagesAsync`, `PurgeSubscriptionDeadLetterMessagesAsync` on `IServiceBusOperations`, which Task 4's handlers call.

`ServiceBusClient.CreateReceiver(string topicName, string subscriptionName, ServiceBusReceiverOptions options)` exists in the SDK alongside the queue overload (verified via the same decompilation as Task 1) — this task's new methods use it in place of `CreateReceiver(queueName, options)`.

**Design note on resubmit**: Service Bus has no way to inject a message directly into one subscription's queue, bypassing the topic's rule-based fan-out. Resubmitting a subscription's dead-lettered message therefore republishes it onto the **topic**, not the subscription — it will be re-evaluated against every subscription's filters, not routed back only to the one it came from. This is unavoidable, standard Service Bus behavior, called out both in a code comment here and in the `SubscriptionPeek.razor` page built in Task 6.

This task extracts the existing queue resubmit/purge bodies into shared private helpers, then has both the queue method and a new subscription method call the shared helper. The extraction is a behavior-preserving refactor of already-shipped code — Step 5 re-runs the existing test suite to prove nothing broke.

- [ ] **Step 1: Add the three new methods to `IServiceBusOperations`**

Append to the interface (after the block added in Task 1, before the closing `}`):

```csharp

    /// <summary>Non-destructive. Set fromDeadLetter to browse the subscription's dead-letter sub-queue instead.</summary>
    Task<IReadOnlyList<PeekedMessage>> PeekSubscriptionMessagesAsync(
        string connectionString, string topicName, string subscriptionName, bool fromDeadLetter, int maxMessages,
        long? fromSequenceNumber = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the named dead-lettered messages (by sequence number) back into normal delivery by
    /// republishing them onto the topic -- Service Bus has no way to inject a message directly into
    /// one subscription's queue, so a resubmitted message is re-evaluated against every
    /// subscription's filters, not routed back only to this one. Returns how many were actually
    /// found and resubmitted.
    /// </summary>
    Task<int> ResubmitSubscriptionDeadLetterMessagesAsync(string connectionString, string topicName, string subscriptionName, IReadOnlyList<long> sequenceNumbers, CancellationToken ct = default);

    /// <summary>Destructive. Drains and discards every message currently in the subscription's dead-letter sub-queue. Returns how many were purged.</summary>
    Task<int> PurgeSubscriptionDeadLetterMessagesAsync(string connectionString, string topicName, string subscriptionName, CancellationToken ct = default);
```

- [ ] **Step 2: Extract the resubmit scan into a shared private helper**

In `AzureServiceBusOperations.cs`, replace the existing `ResubmitDeadLetterMessagesAsync` method so the whole thing reads:

```csharp
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

    // Design tradeoff: non-matching messages are deferred (not abandoned) during the scan so the
    // scan can make forward progress through the dead-letter sub-queue instead of looping on the
    // same head-of-queue messages; the cost is that deferral is durable (it does NOT self-heal like
    // an expiring PeekLock does), so an explicit, best-effort restoration pass is required afterward
    // -- see the try/finally below -- to avoid permanently stranding messages if the scan is
    // cancelled or fails. Shared by both the queue and subscription resubmit methods above, which
    // differ only in how `receiver`/`sender` were constructed.
    private static async Task<int> ResubmitDeadLetterCoreAsync(ServiceBusReceiver receiver, ServiceBusSender sender, IReadOnlyList<long> sequenceNumbers, CancellationToken ct)
    {
        // Wall-clock ceiling on the scan below. maxAttempts already bounds the iteration count, but
        // not how long each iteration can take, so a degraded namespace could still keep the scan
        // running for many minutes. On expiry the receive/send/defer call throws
        // OperationCanceledException, which propagates to the calling handler's existing catch --
        // no second error path -- after the `finally` has restored every deferred message.
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(BulkOperationTimeout);
        var scanToken = scanCts.Token;

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
            // maxAttempts bounds only this scan loop -- how many 32-message receive batches it will
            // attempt before giving up on finding every requested sequence number. It has no bearing
            // on the separate restore pass below, which always processes every deferred message.
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
                        // Record before deferring, not after: if DeferMessageAsync applies the defer
                        // broker-side but then throws (e.g. a transient fault surfaced by the SDK's
                        // retry policy after the operation already succeeded), the sequence number
                        // must still reach the restore pass in `finally` -- recording first costs at
                        // worst one harmless MessageNotFound if the defer never actually applied,
                        // versus a permanently stranded message if we recorded after and never got there.
                        deferredSequenceNumbers.Add(message.SequenceNumber);
                        await receiver.DeferMessageAsync(message, cancellationToken: scanToken);
                    }
                }
            }
        }
        finally
        {
            // Restore every deferred message back to normal delivery order -- abandoning a deferred
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
```

`RestoreDeferredMessagesAsync` and `TryAbandonAsync` below are unchanged — they already take only a `ServiceBusReceiver`, so both paths reuse them as-is.

- [ ] **Step 3: Extract the purge drain loop into a shared private helper**

Replace the existing `PurgeDeadLetterMessagesAsync` method with:

```csharp
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
```

- [ ] **Step 4: Add `PeekSubscriptionMessagesAsync`**

Insert immediately after the existing `PeekMessagesAsync` method:

```csharp

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
```

- [ ] **Step 5: Build, then re-run the full suite to prove the refactor is behavior-preserving**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: same pass count as after Task 1 — every existing test in `tests/SbConsole.Plugins.ServiceBus.Tests/Client/AzureServiceBusOperationsTests.cs`, `Messages/ResubmitDeadLetterMessagesCommandHandlerTests.cs`, `Messages/PurgeDeadLetterMessagesCommandHandlerTests.cs`, and `Pages/PeekPageTests.cs` must still pass unmodified — this is the proof that extracting the shared helpers didn't change queue behavior.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/
git commit -m "refactor: extract shared resubmit/purge helpers, add subscription messaging to AzureServiceBusOperations

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: Topics handlers — aggregated list, create, delete

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Topics/ListTopicsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Topics/CreateTopicCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Topics/DeleteTopicCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Topics/ListTopicsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Topics/CreateTopicCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Topics/DeleteTopicCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.ListTopicsAsync/ListSubscriptionsAsync/CreateTopicAsync/DeleteTopicAsync` (Task 1).
- Produces: `TopicRow(TopicSummary Topic, long ActiveMessageCount, long DeadLetterMessageCount, IReadOnlyList<SubscriptionSummary> Subscriptions)` and `ListTopicsQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) -> Task<PluginResult<IReadOnlyList<TopicRow>>>`, `CreateTopicCommand(Guid ConnectionId, string ConnectionName, string TopicName)` + handler, `DeleteTopicCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName)` + handler — Task 5's `Topics.razor` and `CreateTopicDialog.razor` inject and call these. **This handler replaces the need for a separate "list subscriptions for a page" handler** — `Topics.razor` never lists subscriptions on its own; it always gets them nested inside `TopicRow`.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Topics/ListTopicsQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Topics;

public class ListTopicsQueryHandlerTests
{
    [Fact]
    public async Task Returns_topics_with_aggregated_subscription_counts()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096, 3) });
        operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6), new("eu-team", 2, 0, 2) });

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Should().ContainSingle().Subject;
        row.Topic.Name.Should().Be("orders");
        row.ActiveMessageCount.Should().Be(7);
        row.DeadLetterMessageCount.Should().Be(1);
        row.Subscriptions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_topic_with_no_subscriptions_aggregates_to_zero()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("empty-topic", 0, 0, 0) });
        operations.ListSubscriptionsAsync("Endpoint=sb://real", "empty-topic", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary>());

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        var row = result.Value.Should().ContainSingle().Subject;
        row.ActiveMessageCount.Should().Be(0);
        row.DeadLetterMessageCount.Should().Be(0);
        row.Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListTopicsQueryHandler(Substitute.For<IServiceBusOperations>(), connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<TopicSummary>>(new InvalidOperationException("namespace unreachable")));

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("namespace unreachable");
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Topics/CreateTopicCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Topics;

public class CreateTopicCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_topic_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "sb-dev", "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateTopicAsync("Endpoint=sb://real", Arg.Is<CreateTopicRequest>(r => r.Name == "orders"), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.create", "sb-dev/orders", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateTopicAsync(Arg.Any<string>(), Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "sb-dev", "orders"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("topic.create", "sb-dev/orders", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Topics/DeleteTopicCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Topics;

public class DeleteTopicCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_topic_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance)
            .HandleAsync(new DeleteTopicCommand(connectionId, "sb-dev", false, "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteTopicAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.delete", "sb-dev/orders", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SbConsole.Plugins.ServiceBus.Tests.Topics`
Expected: FAIL — compile error, nothing in `Topics/` exists yet.

- [ ] **Step 3: Implement the three handlers**

`src/SbConsole.Plugins.ServiceBus/Topics/ListTopicsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Topics;

/// <summary>A topic plus its subscriptions' counts summed into ActiveMessageCount/
/// DeadLetterMessageCount -- so a collapsed topic row can show real aggregate numbers
/// (docs/design.md §6.2) without the caller having to sum Subscriptions itself.</summary>
public sealed record TopicRow(TopicSummary Topic, long ActiveMessageCount, long DeadLetterMessageCount, IReadOnlyList<SubscriptionSummary> Subscriptions);

public sealed class ListTopicsQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListTopicsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<TopicRow>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<TopicRow>>.Fail("Connection not found.");
        }

        try
        {
            var topics = await operations.ListTopicsAsync(secret, ct);
            var rows = new List<TopicRow>();
            foreach (var topic in topics)
            {
                var subscriptions = await operations.ListSubscriptionsAsync(secret, topic.Name, ct);
                var active = subscriptions.Sum(s => s.ActiveMessageCount);
                var deadLetter = subscriptions.Sum(s => s.DeadLetterMessageCount);
                rows.Add(new TopicRow(topic, active, deadLetter, subscriptions));
            }

            return PluginResult<IReadOnlyList<TopicRow>>.Ok(rows);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing topics for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<TopicRow>>.Fail(ex);
        }
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Topics/CreateTopicCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Topics;

public sealed record CreateTopicCommand(Guid ConnectionId, string ConnectionName, string TopicName);

public sealed class CreateTopicCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateTopicCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            await operations.CreateTopicAsync(secret, new CreateTopicRequest(cmd.TopicName), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating topic {Target} failed.", target);
            await audit.RecordAsync("topic.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("topic.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Topics/DeleteTopicCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Topics;

public sealed record DeleteTopicCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName);

public sealed class DeleteTopicCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteTopicCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            await operations.DeleteTopicAsync(secret, cmd.TopicName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting topic {Target} failed.", target);
            await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [ ] **Step 4: Register the three handlers in `ServiceBusPlugin.ConfigureServices`**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, add to the end of `ConfigureServices` (after the existing `PurgeDeadLetterMessagesCommandHandler` line):

```csharp
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SbConsole.Plugins.ServiceBus.Tests.Topics`
Expected: PASS (7 tests: 4 + 2 + 1).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Topics/ src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Topics/
git commit -m "feat: add aggregated topic list handler plus create/delete topic handlers

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: Subscription handlers — create, delete, peek, resubmit dead-letter, purge dead-letter

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Subscriptions/CreateSubscriptionCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Subscriptions/DeleteSubscriptionCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/PeekSubscriptionMessagesQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/ResubmitSubscriptionDeadLetterMessagesCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/PurgeSubscriptionDeadLetterMessagesCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/CreateSubscriptionCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/DeleteSubscriptionCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PeekSubscriptionMessagesQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PurgeSubscriptionDeadLetterMessagesCommandHandlerTests.cs`

There is deliberately **no** `ListSubscriptionsQueryHandler` — Task 3's `ListTopicsQueryHandler` already returns every topic's subscriptions nested in `TopicRow`, and nothing else needs to list subscriptions independently in this plan.

**Interfaces:**
- Consumes: `IServiceBusOperations.CreateSubscriptionAsync/DeleteSubscriptionAsync` (Task 1), `PeekSubscriptionMessagesAsync/ResubmitSubscriptionDeadLetterMessagesAsync/PurgeSubscriptionDeadLetterMessagesAsync` (Task 2).
- Produces: `CreateSubscriptionCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, int MaxDeliveryCount)` + handler, `DeleteSubscriptionCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName)` + handler, `PeekSubscriptionMessagesQueryHandler.HandleAsync(Guid connectionId, string topicName, string subscriptionName, bool fromDeadLetter, long? fromSequenceNumber = null, int maxMessages = 32, CancellationToken ct = default) -> Task<PluginResult<IReadOnlyList<PeekedMessage>>>`, `ResubmitSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, IReadOnlyList<long> SequenceNumbers)` + handler, `PurgeSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName)` + handler — Task 5's `Topics.razor`/`CreateSubscriptionDialog.razor` and Task 7's `SubscriptionPeek.razor` inject and call these. `SendMessageCommandHandler`/`SendMessageDialog.razor` (already shipped) are **reused unmodified** for topic sends.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/CreateSubscriptionCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Subscriptions;

public class CreateSubscriptionCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_subscription_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateSubscriptionCommandHandler(operations, connections, audit, NullLogger<CreateSubscriptionCommandHandler>.Instance)
            .HandleAsync(new CreateSubscriptionCommand(connectionId, "sb-dev", "orders", "uk-team", 10));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateSubscriptionAsync("Endpoint=sb://real", "orders", Arg.Is<CreateSubscriptionRequest>(r => r.Name == "uk-team" && r.MaxDeliveryCount == 10), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("subscription.create", "sb-dev/orders/uk-team", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Azure_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.CreateSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateSubscriptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateSubscriptionCommandHandler(operations, connections, audit, NullLogger<CreateSubscriptionCommandHandler>.Instance)
            .HandleAsync(new CreateSubscriptionCommand(connectionId, "sb-dev", "orders", "uk-team", 10));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("subscription.create", "sb-dev/orders/uk-team", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/DeleteSubscriptionCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Subscriptions;

public class DeleteSubscriptionCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_subscription_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteSubscriptionCommandHandler(operations, connections, audit, NullLogger<DeleteSubscriptionCommandHandler>.Instance)
            .HandleAsync(new DeleteSubscriptionCommand(connectionId, "sb-dev", false, "orders", "uk-team"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteSubscriptionAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("subscription.delete", "sb-dev/orders/uk-team", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PeekSubscriptionMessagesQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PeekSubscriptionMessagesQueryHandlerTests
{
    [Fact]
    public async Task Returns_peeked_messages_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var messages = new[] { new PeekedMessage(1, "{}", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()) };
        operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", false, 32, null, Arg.Any<CancellationToken>()).Returns(messages);

        var result = await new PeekSubscriptionMessagesQueryHandler(operations, connections, NullLogger<PeekSubscriptionMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team", false);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(messages);
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PeekedMessage>>(new InvalidOperationException("subscription not found")));

        var result = await new PeekSubscriptionMessagesQueryHandler(operations, connections, NullLogger<PeekSubscriptionMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", "uk-team", true);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("subscription not found");
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Messages/ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Resubmits_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ResubmitSubscriptionDeadLetterMessagesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>()).Returns(1);
        var audit = Substitute.For<IAuditScope>();

        var result = await new ResubmitSubscriptionDeadLetterMessagesCommandHandler(operations, connections, audit, NullLogger<ResubmitSubscriptionDeadLetterMessagesCommandHandler>.Instance)
            .HandleAsync(new ResubmitSubscriptionDeadLetterMessagesCommand(connectionId, "sb-dev", "orders", "uk-team", [42]));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        await audit.Received(1).RecordAsync("message.resubmit", "sb-dev/orders/uk-team", ActionRisk.Mutating, true, "1 of 1 resubmitted", Arg.Any<CancellationToken>());
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PurgeSubscriptionDeadLetterMessagesCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Messages;

public class PurgeSubscriptionDeadLetterMessagesCommandHandlerTests
{
    [Fact]
    public async Task Purges_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.PurgeSubscriptionDeadLetterMessagesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>()).Returns(3);
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeSubscriptionDeadLetterMessagesCommandHandler(operations, connections, audit, NullLogger<PurgeSubscriptionDeadLetterMessagesCommandHandler>.Instance)
            .HandleAsync(new PurgeSubscriptionDeadLetterMessagesCommand(connectionId, "sb-dev", "orders", "uk-team"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(3);
        await audit.Received(1).RecordAsync("subscription.purge", "sb-dev/orders/uk-team", ActionRisk.Destructive, true, "3 messages purged", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CreateSubscriptionCommandHandlerTests|FullyQualifiedName~DeleteSubscriptionCommandHandlerTests|FullyQualifiedName~PeekSubscriptionMessagesQueryHandlerTests|FullyQualifiedName~ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests|FullyQualifiedName~PurgeSubscriptionDeadLetterMessagesCommandHandlerTests"`
Expected: FAIL — compile error, nothing exists yet.

- [ ] **Step 3: Implement the five handlers**

`src/SbConsole.Plugins.ServiceBus/Subscriptions/CreateSubscriptionCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Subscriptions;

public sealed record CreateSubscriptionCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, int MaxDeliveryCount);

public sealed class CreateSubscriptionCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateSubscriptionCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateSubscriptionCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        try
        {
            await operations.CreateSubscriptionAsync(secret, cmd.TopicName, new CreateSubscriptionRequest(cmd.SubscriptionName, cmd.MaxDeliveryCount), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating subscription {Target} failed.", target);
            await audit.RecordAsync("subscription.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("subscription.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Subscriptions/DeleteSubscriptionCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Subscriptions;

public sealed record DeleteSubscriptionCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName);

public sealed class DeleteSubscriptionCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteSubscriptionCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteSubscriptionCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        try
        {
            await operations.DeleteSubscriptionAsync(secret, cmd.TopicName, cmd.SubscriptionName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting subscription {Target} failed.", target);
            await audit.RecordAsync("subscription.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("subscription.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Messages/PeekSubscriptionMessagesQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed class PeekSubscriptionMessagesQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<PeekSubscriptionMessagesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<PeekedMessage>>> HandleAsync(
        Guid connectionId, string topicName, string subscriptionName, bool fromDeadLetter,
        long? fromSequenceNumber = null, int maxMessages = 32, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<PeekedMessage>>.Fail("Connection not found.");
        }

        try
        {
            var messages = await operations.PeekSubscriptionMessagesAsync(secret, topicName, subscriptionName, fromDeadLetter, maxMessages, fromSequenceNumber, ct);
            return PluginResult<IReadOnlyList<PeekedMessage>>.Ok(messages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Peeking {TopicName}/{SubscriptionName} (dead-letter: {FromDeadLetter}) failed.", topicName, subscriptionName, fromDeadLetter);
            return PluginResult<IReadOnlyList<PeekedMessage>>.Fail(ex);
        }
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Messages/ResubmitSubscriptionDeadLetterMessagesCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record ResubmitSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, IReadOnlyList<long> SequenceNumbers);

public sealed class ResubmitSubscriptionDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<ResubmitSubscriptionDeadLetterMessagesCommandHandler> logger)
{
    public async Task<PluginResult<int>> HandleAsync(ResubmitSubscriptionDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<int>.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        int resubmitted;
        try
        {
            resubmitted = await operations.ResubmitSubscriptionDeadLetterMessagesAsync(secret, cmd.TopicName, cmd.SubscriptionName, cmd.SequenceNumbers, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resubmitting {Count} dead-letter message(s) from {Target} failed.", cmd.SequenceNumbers.Count, target);
            await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult<int>.Fail(ex);
        }

        await audit.RecordAsync("message.resubmit", target, ActionRisk.Mutating, succeeded: true, detail: $"{resubmitted} of {cmd.SequenceNumbers.Count} resubmitted", ct: ct);
        return PluginResult<int>.Ok(resubmitted);
    }
}
```

`src/SbConsole.Plugins.ServiceBus/Messages/PurgeSubscriptionDeadLetterMessagesCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Messages;

public sealed record PurgeSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName);

public sealed class PurgeSubscriptionDeadLetterMessagesCommandHandler(IServiceBusOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PurgeSubscriptionDeadLetterMessagesCommandHandler> logger)
{
    public async Task<PluginResult<int>> HandleAsync(PurgeSubscriptionDeadLetterMessagesCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<int>.Fail("Connection not found.");
        }

        var target = $"{cmd.ConnectionName}/{cmd.TopicName}/{cmd.SubscriptionName}";
        int purged;
        try
        {
            purged = await operations.PurgeSubscriptionDeadLetterMessagesAsync(secret, cmd.TopicName, cmd.SubscriptionName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purging the dead-letter queue of {Target} failed.", target);
            await audit.RecordAsync("subscription.purge", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyError.From(ex), ct: ct);
            return PluginResult<int>.Fail(ex);
        }

        await audit.RecordAsync("subscription.purge", target, ActionRisk.Destructive, succeeded: true, detail: $"{purged} messages purged", ct: ct);
        return PluginResult<int>.Ok(purged);
    }
}
```

- [ ] **Step 4: Register the five handlers in `ServiceBusPlugin.ConfigureServices`**

Add, after the lines added in Task 3:

```csharp
        services.AddScoped<Subscriptions.CreateSubscriptionCommandHandler>();
        services.AddScoped<Subscriptions.DeleteSubscriptionCommandHandler>();
        services.AddScoped<Messages.PeekSubscriptionMessagesQueryHandler>();
        services.AddScoped<Messages.ResubmitSubscriptionDeadLetterMessagesCommandHandler>();
        services.AddScoped<Messages.PurgeSubscriptionDeadLetterMessagesCommandHandler>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CreateSubscriptionCommandHandlerTests|FullyQualifiedName~DeleteSubscriptionCommandHandlerTests|FullyQualifiedName~PeekSubscriptionMessagesQueryHandlerTests|FullyQualifiedName~ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests|FullyQualifiedName~PurgeSubscriptionDeadLetterMessagesCommandHandlerTests"`
Expected: PASS (2 + 1 + 2 + 1 + 1 = 7 tests).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Subscriptions/ src/SbConsole.Plugins.ServiceBus/Messages/ src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/ tests/SbConsole.Plugins.ServiceBus.Tests/Messages/
git commit -m "feat: add subscription create/delete and subscription peek/resubmit/purge dead-letter handlers

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: `Topics.razor` — one combined, expandable table, plus its two create dialogs

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/CreateTopicDialog.razor`
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/CreateSubscriptionDialog.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (nav item)
- Modify: `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateTopicDialogTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateSubscriptionDialogTests.cs`

**Interfaces:**
- Consumes: `Topics.ListTopicsQueryHandler`/`CreateTopicCommandHandler`/`DeleteTopicCommandHandler` (Task 3), `Subscriptions.CreateSubscriptionCommandHandler`/`DeleteSubscriptionCommandHandler` (Task 4), the existing `Pages.SendMessageDialog` (shipped, unmodified), `IConnectionProvider.ListAsync`, `IConfirmationService.ConfirmAsync`.
- Produces: the `/p/azure-servicebus/topics` route and its subscription-row "Peek"/"Dead-letter" links (`/p/azure-servicebus/topics/{topicName}/subscriptions/{subscriptionName}/peek?connectionId=...&deadLetter=...`), which Task 7's `SubscriptionPeek.razor` is reached from.

No new routing infrastructure is needed — `Program.cs`'s `AddAdditionalAssemblies` wiring already scans the whole plugin assembly generically, and `Pages/_Imports.razor`'s `@attribute [Authorize]` already covers this page since it lives in the same `Pages/` folder.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class TopicsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    public TopicsPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "sb-dev", "azure-servicebus", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(_confirmation);
        Services.AddLogging();
        Services.AddSingleton<ListTopicsQueryHandler>();
        Services.AddSingleton<CreateTopicCommandHandler>();
        Services.AddSingleton<DeleteTopicCommandHandler>();
        Services.AddSingleton<CreateSubscriptionCommandHandler>();
        Services.AddSingleton<DeleteSubscriptionCommandHandler>();
        Services.AddSingleton<SendMessageCommandHandler>();
    }

    [Fact]
    public async Task Collapsed_topic_row_shows_aggregated_counts_without_expanding()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096, 3) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6), new("eu-team", 2, 0, 2) });

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("7"); // aggregated Active (5 + 2)
        cut.Markup.Should().Contain("1"); // aggregated Dead-letter (1 + 0)
        cut.FindAll(".subscription-row").Should().BeEmpty("collapsed by default");
    }

    [Fact]
    public async Task Expanding_a_topic_reveals_its_subscription_rows()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6) });

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        cut.Render();

        cut.FindAll(".subscription-row").Should().HaveCount(1);
        cut.Markup.Should().Contain("uk-team");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Dead_letter_link_carries_connectionId_deadLetter_and_the_rows_live_count()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6) });

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        cut.Render();

        var link = cut.Find("a.dead-letter-action");
        link.GetAttribute("href").Should().Be(
            $"/p/azure-servicebus/topics/orders/subscriptions/uk-team/peek?connectionId={_connectionId}&deadLetter=true&deadLetterCount=1");
    }

    [Fact]
    public async Task Delete_topic_goes_through_confirmation_with_the_subscription_count()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0), new("eu-team", 0, 0, 0) });
        _confirmation.ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, 2, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, 2, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteTopicAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_subscription_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 1, 0, 0) });
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0) });
        _confirmation.ConfirmAsync("Delete", "uk-team", false, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.expand-topic").Click();
        cut.Render();
        cut.Find("button.delete-subscription").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteSubscriptionAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateTopicDialogTests.cs`:

```csharp
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
using SbConsole.Plugins.ServiceBus.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateTopicDialogTests : BunitContext, IAsyncLifetime
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

    public CreateTopicDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateTopicCommandHandler>();

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
                inner.OpenComponent<CreateTopicDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateTopicDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateTopicDialog.ConnectionName), "sb-dev");
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
        cut.Find("input#topic-name").Input("orders");

        cut.Find("button.save-topic").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateTopicAsync("Endpoint=sb://real", Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("topic already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#topic-name").Input("orders");

        cut.Find("button.save-topic").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("topic already exists"));
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateSubscriptionDialogTests.cs`:

```csharp
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
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class CreateSubscriptionDialogTests : BunitContext, IAsyncLifetime
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

    public CreateSubscriptionDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<CreateSubscriptionCommandHandler>();

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
                inner.OpenComponent<CreateSubscriptionDialog>(0);
                inner.AddComponentParameter(1, nameof(CreateSubscriptionDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(CreateSubscriptionDialog.ConnectionName), "sb-dev");
                inner.AddComponentParameter(3, nameof(CreateSubscriptionDialog.TopicName), "orders");
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
        cut.Find("input#subscription-name").Input("uk-team");

        cut.Find("button.save-subscription").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateSubscriptionAsync("Endpoint=sb://real", "orders", Arg.Any<CreateSubscriptionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("subscription already exists")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("input#subscription-name").Input("uk-team");

        cut.Find("button.save-subscription").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("subscription already exists"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests|FullyQualifiedName~CreateTopicDialogTests|FullyQualifiedName~CreateSubscriptionDialogTests"`
Expected: FAIL — `Topics`, `CreateTopicDialog`, `CreateSubscriptionDialog` don't exist yet.

- [ ] **Step 3: Implement `CreateTopicDialog.razor`**

```razor
@using SbConsole.Plugins.ServiceBus.Topics
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="topic-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="save-topic-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="save-topic" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_name) || _busy)" OnClick="Save">Create</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";

    [Inject] private CreateTopicCommandHandler CreateHandler { get; set; } = default!;

    private string _name = "";
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var result = await CreateHandler.HandleAsync(new CreateTopicCommand(ConnectionId, ConnectionName, _name));
            if (result.IsSuccess)
            {
                MudDialog.Close(DialogResult.Ok(true));
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 4: Implement `CreateSubscriptionDialog.razor`**

```razor
@using SbConsole.Plugins.ServiceBus.Subscriptions
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="subscription-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudNumericField @bind-Value="_maxDeliveryCount" Label="Max delivery count" Min="1" Max="2000" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="save-subscription-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="save-subscription" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_name) || _busy)" OnClick="Save">Create</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";

    [Inject] private CreateSubscriptionCommandHandler CreateHandler { get; set; } = default!;

    private string _name = "";
    private int _maxDeliveryCount = 10;
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var result = await CreateHandler.HandleAsync(new CreateSubscriptionCommand(ConnectionId, ConnectionName, TopicName, _name, _maxDeliveryCount));
            if (result.IsSuccess)
            {
                MudDialog.Close(DialogResult.Ok(true));
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Cancel() => MudDialog.Cancel();
}
```

- [ ] **Step 5: Implement `Topics.razor`**

```razor
@page "/p/azure-servicebus/topics"
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Subscriptions
@using SbConsole.Plugins.ServiceBus.Topics
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject ListTopicsQueryHandler ListHandler
@inject CreateTopicCommandHandler CreateTopicHandler
@inject DeleteTopicCommandHandler DeleteTopicHandler
@inject DeleteSubscriptionCommandHandler DeleteSubscriptionHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>Topics &amp; Subscriptions</PageTitle>
<h1>Topics &amp; Subscriptions</h1>

@if (_connections.Count == 0)
{
    <MudAlert Severity="Severity.Info">No connections yet. Add an Azure Service Bus connection to get started.</MudAlert>
}
else
{
    <MudSelect T="Guid" Label="Namespace" Value="_selectedConnectionId" ValueChanged="OnConnectionChanged">
        @foreach (var connection in _connections)
        {
            <MudSelectItem Value="@connection.Id">@connection.Name</MudSelectItem>
        }
    </MudSelect>

    <div class="d-flex align-center gap-2 my-4">
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="OpenCreateTopic">+ Create topic</MudButton>
        @if (_loading)
        {
            <MudProgressCircular Class="topics-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudTable Items="_rows" RowClassFunc="@((row, _) => row is SubscriptionRowEntry ? "subscription-row" : "topic-row")">
        <HeaderContent>
            <MudTh>Name</MudTh>
            <MudTh>Active</MudTh>
            <MudTh>Dead-letter</MudTh>
            <MudTh>Scheduled</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            @if (context is TopicRowEntry topicRow)
            {
                <MudTd>
                    <MudButton Class="expand-topic" OnClick="@(() => ToggleExpanded(topicRow.Topic.Name))">@(topicRow.IsExpanded ? "▾" : "▸")</MudButton>
                    <span style="font-family:monospace">@topicRow.Topic.Name</span>
                    <span class="mud-text-secondary" style="font-size:11px"> @topicRow.Topic.SubscriptionCount subs</span>
                </MudTd>
                <MudTd>@topicRow.ActiveMessageCount</MudTd>
                <MudTd>@topicRow.DeadLetterMessageCount</MudTd>
                <MudTd>@topicRow.Topic.ScheduledMessageCount</MudTd>
                <MudTd>
                    <MudButton Class="send-message-action" OnClick="@(() => OpenSend(topicRow.Topic.Name))">Send</MudButton>
                    <MudButton Class="add-subscription-action" OnClick="@(() => OpenCreateSubscription(topicRow.Topic.Name))">+ Subscription</MudButton>
                    <MudButton Class="delete-topic" Color="Color.Error" Disabled="@(_deletingEntity == topicRow.Topic.Name)" OnClick="@(() => DeleteTopicAsync(topicRow.Topic.Name, topicRow.Topic.SubscriptionCount))">Delete</MudButton>
                    @if (_deletingEntity == topicRow.Topic.Name)
                    {
                        <MudProgressCircular Class="delete-topic-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                    }
                </MudTd>
            }
            else if (context is SubscriptionRowEntry subRow)
            {
                var rowKey = $"{subRow.TopicName}/{subRow.Subscription.Name}";
                <MudTd Style="padding-left:34px">@subRow.Subscription.Name</MudTd>
                <MudTd>@subRow.Subscription.ActiveMessageCount</MudTd>
                <MudTd>@subRow.Subscription.DeadLetterMessageCount</MudTd>
                <MudTd>—</MudTd>
                <MudTd>
                    <MudButton Href="@PeekUrl(subRow.TopicName, subRow.Subscription.Name)">Peek</MudButton>
                    <MudButton Class="dead-letter-action" Href="@PeekUrl(subRow.TopicName, subRow.Subscription.Name, deadLetter: true, deadLetterCount: subRow.Subscription.DeadLetterMessageCount)">Dead-letter</MudButton>
                    <MudButton Class="delete-subscription" Color="Color.Error" Disabled="@(_deletingEntity == rowKey)" OnClick="@(() => DeleteSubscriptionAsync(subRow.TopicName, subRow.Subscription.Name))">Delete</MudButton>
                    @if (_deletingEntity == rowKey)
                    {
                        <MudProgressCircular Class="delete-subscription-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                    }
                </MudTd>
            }
        </RowTemplate>
    </MudTable>
}

@code {
    private abstract record TopicsTableRow;
    private sealed record TopicRowEntry(TopicSummary Topic, long ActiveMessageCount, long DeadLetterMessageCount, bool IsExpanded) : TopicsTableRow;
    private sealed record SubscriptionRowEntry(string TopicName, SubscriptionSummary Subscription) : TopicsTableRow;

    private IReadOnlyList<ConnectionInfo> _connections = [];
    private IReadOnlyList<TopicRow> _topics = [];
    private readonly HashSet<string> _expandedTopics = [];
    private Guid _selectedConnectionId;
    private bool _loading;
    private string? _deletingEntity;

    private IReadOnlyList<TopicsTableRow> _rows => _topics.SelectMany(FlattenRows).ToList();

    private IEnumerable<TopicsTableRow> FlattenRows(TopicRow topic)
    {
        var expanded = _expandedTopics.Contains(topic.Topic.Name);
        yield return new TopicRowEntry(topic.Topic, topic.ActiveMessageCount, topic.DeadLetterMessageCount, expanded);
        if (expanded)
        {
            foreach (var subscription in topic.Subscriptions)
            {
                yield return new SubscriptionRowEntry(topic.Topic.Name, subscription);
            }
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("azure-servicebus");
        if (_connections.Count > 0)
        {
            _selectedConnectionId = _connections[0].Id;
            await LoadTopicsAsync();
        }
    }

    private async Task OnConnectionChanged(Guid connectionId)
    {
        _selectedConnectionId = connectionId;
        _expandedTopics.Clear();
        await LoadTopicsAsync();
    }

    private async Task LoadTopicsAsync()
    {
        _loading = true;
        try
        {
            var result = await ListHandler.HandleAsync(_selectedConnectionId);
            if (result.IsSuccess)
            {
                _topics = result.Value!;
            }
            else
            {
                _topics = [];
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void ToggleExpanded(string topicName)
    {
        if (!_expandedTopics.Remove(topicName))
        {
            _expandedTopics.Add(topicName);
        }
    }

    private string PeekUrl(string topicName, string subscriptionName, bool deadLetter = false, long? deadLetterCount = null)
    {
        var url = $"/p/azure-servicebus/topics/{Uri.EscapeDataString(topicName)}/subscriptions/{Uri.EscapeDataString(subscriptionName)}/peek?connectionId={_selectedConnectionId}";
        if (deadLetter)
        {
            url += "&deadLetter=true";
            if (deadLetterCount is not null)
            {
                url += $"&deadLetterCount={deadLetterCount}";
            }
        }

        return url;
    }

    private async Task OpenCreateTopic()
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<CreateTopicDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
        };
        var dialog = await DialogService.ShowAsync<CreateTopicDialog>("Create topic", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadTopicsAsync();
        }
    }

    private async Task OpenCreateSubscription(string topicName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<CreateSubscriptionDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.TopicName, topicName },
        };
        var dialog = await DialogService.ShowAsync<CreateSubscriptionDialog>("Create subscription", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            _expandedTopics.Add(topicName);
            await LoadTopicsAsync();
        }
    }

    // SendMessageDialog is reused unmodified from the Queues plan (docs/design.md §6.2): a topic
    // name is just another entity path to IServiceBusOperations.SendMessageAsync, identical under
    // the hood to a queue name, so its QueueName parameter carries the topic name here.
    private async Task OpenSend(string topicName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<SendMessageDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.QueueName, topicName },
        };
        var dialog = await DialogService.ShowAsync<SendMessageDialog>("Send message", parameters);
        await dialog.Result;
    }

    private async Task DeleteTopicAsync(string topicName, int subscriptionCount)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", topicName, connection.IsProd, count: subscriptionCount > 0 ? subscriptionCount : null);
        if (!confirmed)
        {
            return;
        }

        _deletingEntity = topicName;
        try
        {
            var result = await DeleteTopicHandler.HandleAsync(new DeleteTopicCommand(connection.Id, connection.Name, connection.IsProd, topicName));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _deletingEntity = null;
        }

        await LoadTopicsAsync();
    }

    private async Task DeleteSubscriptionAsync(string topicName, string subscriptionName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", subscriptionName, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        var rowKey = $"{topicName}/{subscriptionName}";
        _deletingEntity = rowKey;
        try
        {
            var result = await DeleteSubscriptionHandler.HandleAsync(new DeleteSubscriptionCommand(connection.Id, connection.Name, connection.IsProd, topicName, subscriptionName));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _deletingEntity = null;
        }

        await LoadTopicsAsync();
    }
}
```

- [ ] **Step 6: Add the "Topics & Subscriptions" nav item**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, change:

```csharp
    public IReadOnlyList<PluginNavItem> NavItems => [new("Queues", "/p/azure-servicebus/queues")];
```

to:

```csharp
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/azure-servicebus/queues"),
        new("Topics & Subscriptions", "/p/azure-servicebus/topics"),
    ];
```

(The "Dead-letter" nav item is added in Task 8, once `GetNavBadgeAsync` exists to back its badge.)

- [ ] **Step 7: Broaden the `[Authorize]` regression test and add the new route**

In `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`:

Change the route list:

```csharp
        foreach (var route in new[] { "/", "/audit", "/plugins", "/connections", "/settings", "/p/azure-servicebus/queues" })
```

to:

```csharp
        foreach (var route in new[] { "/", "/audit", "/plugins", "/connections", "/settings", "/p/azure-servicebus/queues", "/p/azure-servicebus/topics" })
```

Replace the whole `Queues_plugin_page_declares_Authorize_directly_on_the_component` test (including its doc comment) with:

```csharp
    /// <summary>
    /// Pins [Authorize] directly on every compiled, routable component type in the plugin
    /// assembly, rather than one named type -- broadened from the Queues plan's original version
    /// (which pinned only the Queues component) because that plan's own final review flagged that a
    /// future page could escape the check silently. This is that future page: Topics (this task)
    /// and the subscription-peek / Dead-letter-overview pages (later tasks) are all covered
    /// automatically, as would any later page, with no test change required.
    /// </summary>
    [Fact]
    public void Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component()
    {
        var routableTypes = typeof(SbConsole.Plugins.ServiceBus.Pages.Queues).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false).Length > 0)
            .ToList();

        routableTypes.Should().NotBeEmpty("this assembly is expected to contain at least one @page component");
        foreach (var type in routableTypes)
        {
            type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .Should().NotBeEmpty($"{type.Name} must declare [Authorize] directly (or inherit it from " +
                    "Pages/_Imports.razor), not merely rely on the app's fallback authorization policy to " +
                    "redirect anonymous requests");
        }
    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests|FullyQualifiedName~CreateTopicDialogTests|FullyQualifiedName~CreateSubscriptionDialogTests"`
Expected: PASS (6 + 2 + 2 = 10 tests).

Run: `dotnet test --filter FullyQualifiedName~ProgramDiRegistrationTests`
Expected: PASS.

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 9: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor src/SbConsole.Plugins.ServiceBus/Pages/CreateTopicDialog.razor src/SbConsole.Plugins.ServiceBus/Pages/CreateSubscriptionDialog.razor src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateTopicDialogTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateSubscriptionDialogTests.cs tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
git commit -m "feat: add combined Topics & Subscriptions page with expandable rows

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: Extract the shared dead-letter message browser component from `Peek.razor`

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterMessageBrowser.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor`

**Interfaces:**
- Produces: `DeadLetterMessageBrowser` — parameters `Messages` (`IReadOnlyList<PeekedMessage>`), `DeadLetter` (`bool`), `SelectedSequenceNumbers` (`HashSet<long>`), `Selected` (`PeekedMessage?`), `Busy` (`bool`), `CanPurge` (`bool`), `PurgeLabel` (`string`), and callbacks `OnSelect` (`EventCallback<PeekedMessage>`), `OnToggleSelection` (`EventCallback<(long SequenceNumber, bool Selected)>`), `OnResubmitSelected` (`EventCallback`), `OnPurge` (`EventCallback`) — consumed by `Peek.razor` here and `SubscriptionPeek.razor` in Task 7.

This is a pure, behavior-preserving refactor: `Peek.razor`'s markup moves into the new component; every method in its `@code` block is unchanged. The component keeps the exact CSS classes (`resubmit-selected`, `purge-queue`, `select-message`, `peek-busy`) `PeekPageTests.cs` already asserts against, so that entire file must pass unmodified — the regression proof.

- [ ] **Step 1: Create `DeadLetterMessageBrowser.razor`**

```razor
@* Shared message browser for both queue and subscription peek pages: message list, message
   detail pane, and (dead-letter mode) the resubmit/purge action bar. Parent pages own all state;
   this component is presentation and user-input routing only -- extracted from Peek.razor so
   SubscriptionPeek.razor (Task 7) can reuse it exactly. CSS classes are kept identical to
   Peek.razor's pre-extraction markup so PeekPageTests.cs needs no changes. *@
@using SbConsole.Plugins.ServiceBus.Client

@if (DeadLetter)
{
    <div class="d-flex align-center gap-2 mb-2">
        <MudButton Class="resubmit-selected" Disabled="@(SelectedSequenceNumbers.Count == 0 || Busy)" OnClick="@(() => OnResubmitSelected.InvokeAsync())">
            Resubmit selected (@SelectedSequenceNumbers.Count)
        </MudButton>
        @* Disabled until the real connection record resolves (CanPurge): purge reads the caller's
           connection.IsProd to decide between the typed-confirmation gate and a plain two-button
           confirm, and while that lookup is still in flight it would fail open. Also disabled while
           any dead-letter operation is in flight -- purge and resubmit act on the same sub-queue. *@
        <MudButton Class="purge-queue" Color="Color.Error" Disabled="@(!CanPurge || Busy)" OnClick="@(() => OnPurge.InvokeAsync())">@PurgeLabel</MudButton>
        @if (Busy)
        {
            <MudProgressCircular Class="peek-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
        }
    </div>
}

<MudGrid>
    <MudItem xs="5">
        @foreach (var message in Messages)
        {
            <div class="d-flex align-center gap-2">
                @if (DeadLetter)
                {
                    <input type="checkbox" class="select-message"
                           checked="@SelectedSequenceNumbers.Contains(message.SequenceNumber)"
                           @onchange="@(e => OnToggleSelection.InvokeAsync((message.SequenceNumber, (bool)e.Value!)))" />
                }
                <MudButton OnClick="@(() => OnSelect.InvokeAsync(message))">
                    #@message.SequenceNumber · @message.EnqueuedTime.ToLocalTime() · deliveries @message.DeliveryCount
                </MudButton>
            </div>
        }
    </MudItem>
    <MudItem xs="7">
        @if (Selected is not null)
        {
            @if (Selected.DeadLetterReason is { } reason)
            {
                <MudAlert Severity="Severity.Warning">@reason — @Selected.DeadLetterErrorDescription</MudAlert>
            }
            <MudText Typo="Typo.subtitle2">@Selected.ContentType</MudText>
            <pre>@Selected.Body</pre>
            <MudText Typo="Typo.subtitle2" Class="mt-4">Application properties</MudText>
            <MudTable Items="Selected.Properties">
                <HeaderContent>
                    <MudTh>Key</MudTh>
                    <MudTh>Value</MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd>@context.Key</MudTd>
                    <MudTd>@context.Value</MudTd>
                </RowTemplate>
            </MudTable>
        }
    </MudItem>
</MudGrid>

@code {
    [Parameter, EditorRequired] public IReadOnlyList<PeekedMessage> Messages { get; set; } = [];
    [Parameter] public bool DeadLetter { get; set; }
    [Parameter, EditorRequired] public HashSet<long> SelectedSequenceNumbers { get; set; } = [];
    [Parameter] public PeekedMessage? Selected { get; set; }
    [Parameter] public bool Busy { get; set; }
    [Parameter] public bool CanPurge { get; set; }
    [Parameter] public string PurgeLabel { get; set; } = "Purge queue";
    [Parameter] public EventCallback<PeekedMessage> OnSelect { get; set; }
    [Parameter] public EventCallback<(long SequenceNumber, bool Selected)> OnToggleSelection { get; set; }
    [Parameter] public EventCallback OnResubmitSelected { get; set; }
    [Parameter] public EventCallback OnPurge { get; set; }
}
```

- [ ] **Step 2: Replace `Peek.razor`'s markup with the shared component**

In `src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor`, replace everything from the opening `@if (DeadLetter)` block down through the closing `</MudGrid>` with:

```razor
<DeadLetterMessageBrowser
    Messages="_messages"
    DeadLetter="DeadLetter"
    SelectedSequenceNumbers="_selectedSequenceNumbers"
    Selected="_selected"
    Busy="_busy"
    CanPurge="_connection is not null"
    PurgeLabel="Purge queue"
    OnSelect="@(m => _selected = m)"
    OnToggleSelection="@(t => ToggleSelection(t.SequenceNumber, t.Selected))"
    OnResubmitSelected="ResubmitSelectedAsync"
    OnPurge="PurgeAsync" />
```

Do not change anything in the `@code` block or the `@page`/`@inject`/`<PageTitle>`/`<h1>` lines above it.

- [ ] **Step 3: Run the existing Peek tests to prove the refactor is behavior-preserving**

Run: `dotnet test --filter FullyQualifiedName~PeekPageTests`
Expected: PASS — every test in `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/PeekPageTests.cs` passes **unmodified**, including `Purge_passes_the_connections_prod_flag_to_the_confirmation_prompt_not_a_url_parameter` and `Purge_is_disabled_until_the_real_connection_record_has_resolved` — the two tests protecting the Critical security fix from the Queues plan. If any fail, the extraction changed behavior and must be fixed — do not modify the tests to make them pass.

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green, same total count as after Task 5.

- [ ] **Step 4: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterMessageBrowser.razor src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor
git commit -m "refactor: extract DeadLetterMessageBrowser from Peek.razor for reuse by subscription peek

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 7: Subscription peek page, applying the prod-purge security pattern from day one

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/SubscriptionPeek.razor`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionPeekPageTests.cs`

**Interfaces:**
- Consumes: `Messages.PeekSubscriptionMessagesQueryHandler`/`ResubmitSubscriptionDeadLetterMessagesCommandHandler`/`PurgeSubscriptionDeadLetterMessagesCommandHandler` (Task 4), `Pages.DeadLetterMessageBrowser` (Task 6), `IConnectionProvider.ListAsync`, `IConfirmationService.ConfirmAsync`.
- Produces: the `/p/azure-servicebus/topics/{TopicName}/subscriptions/{SubscriptionName}/peek` route, reached from `Topics.razor`'s subscription rows (Task 5).

This page applies, from the start, the exact security pattern the Queues plan's final review found missing and had to retrofit into `Peek.razor`: `IsProd` and `ConnectionName` are looked up from the real `ConnectionInfo` record via `IConnectionProvider`, **never** trusted from a URL query parameter. The tests below directly mirror `PeekPageTests.cs`'s identically-purposed tests.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionPeekPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class SubscriptionPeekPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    public SubscriptionPeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        SeedConnection(isProd: false);
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<PeekSubscriptionMessagesQueryHandler>();
        Services.AddSingleton(_confirmation);
        Services.AddSingleton(new ResubmitSubscriptionDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>(), NullLogger<ResubmitSubscriptionDeadLetterMessagesCommandHandler>.Instance));
        Services.AddSingleton(new PurgeSubscriptionDeadLetterMessagesCommandHandler(_operations, _connections, Substitute.For<IAuditScope>(), NullLogger<PurgeSubscriptionDeadLetterMessagesCommandHandler>.Instance));
    }

    private void SeedConnection(bool isProd, string name = "sb-conn")
    {
        var tags = isProd ? new[] { "prod" } : Array.Empty<string>();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, name, "azure-servicebus", tags) });
    }

    private void NavigateToPeekQuery(Guid connectionId, bool deadLetter, long? deadLetterCount = null)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        var queryParams = new Dictionary<string, object?>
        {
            ["ConnectionId"] = connectionId,
            ["DeadLetter"] = deadLetter,
        };
        if (deadLetterCount is not null)
        {
            queryParams["DeadLetterCount"] = deadLetterCount;
        }

        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(queryParams));
    }

    private IRenderedComponent<SbConsole.Plugins.ServiceBus.Pages.SubscriptionPeek> RenderPage() =>
        Render<SbConsole.Plugins.ServiceBus.Pages.SubscriptionPeek>(parameters => parameters
            .Add(p => p.TopicName, "orders")
            .Add(p => p.SubscriptionName, "uk-team"));

    [Fact]
    public async Task Shows_message_list_and_selecting_one_shows_its_body()
    {
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage>
            {
                new(1, "BODY-ONE", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()),
                new(2, "BODY-TWO", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()),
            });

        NavigateToPeekQuery(_connectionId, false);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("BODY-ONE");
        cut.Markup.Should().NotContain("BODY-TWO");
    }

    [Fact]
    public async Task Dead_letter_mode_shows_resubmit_and_purge_actions_but_regular_mode_does_not()
    {
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", false, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 1, new Dictionary<string, string>()) });
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, false);
        var regularModeCut = RenderPage();
        await Task.Delay(30);
        regularModeCut.Render();

        regularModeCut.FindAll("button.purge-queue").Should().BeEmpty();

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll("button.purge-queue").Should().HaveCount(1);
    }

    [Fact]
    public async Task Resubmit_selected_calls_the_operations_seam_with_the_checked_sequence_numbers()
    {
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });
        _operations.ResubmitSubscriptionDeadLetterMessagesAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();
        cut.Find("input.select-message").Change(true);
        cut.Find("button.resubmit-selected").Click();
        await Task.Delay(30);

        await _operations.Received(1).ResubmitSubscriptionDeadLetterMessagesAsync(
            "Endpoint=sb://real", "orders", "uk-team",
            Arg.Is<IReadOnlyList<long>>(l => l.SequenceEqual(new long[] { 1 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_passes_the_connections_prod_flag_to_the_confirmation_prompt_not_a_url_parameter()
    {
        // Direct analogue of PeekPageTests.cs's identically-named test: applies the same
        // security pattern from day one instead of retrofitting it. NavigateToPeekQuery below
        // carries no isProd (or any prod-related) query parameter at all.
        SeedConnection(isProd: true);
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.purge-queue").Click();
        await Task.Delay(30);

        await _confirmation.Received(1).ConfirmAsync("Purge", "uk-team", true, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_is_disabled_until_the_real_connection_record_has_resolved()
    {
        var pendingLookup = new TaskCompletionSource<IReadOnlyList<ConnectionInfo>>();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(pendingLookup.Task);
        _operations.PeekSubscriptionMessagesAsync("Endpoint=sb://real", "orders", "uk-team", true, 32, null, Arg.Any<CancellationToken>())
            .Returns(new List<PeekedMessage> { new(1, "{}", "application/json", DateTimeOffset.UtcNow, 10, new Dictionary<string, string>()) });

        NavigateToPeekQuery(_connectionId, true);
        var cut = RenderPage();
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeTrue();

        pendingLookup.SetResult(new List<ConnectionInfo> { new(_connectionId, "sb-conn", "azure-servicebus", ["prod"]) });
        await Task.Delay(30);
        cut.Render();

        cut.Find("button.purge-queue").HasAttribute("disabled").Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SubscriptionPeekPageTests`
Expected: FAIL — `SubscriptionPeek` doesn't exist yet.

- [ ] **Step 3: Implement `SubscriptionPeek.razor`**

```razor
@* Message browser for a subscription's messages/dead-letter sub-queue -- same shape as
   Peek.razor, sharing DeadLetterMessageBrowser (Task 6), applying the same IConnectionProvider-
   sourced IsProd/ConnectionName pattern that Peek.razor only gained after a Critical finding in
   the Queues plan's final review. This page never trusts IsProd/ConnectionName off the URL. *@
@page "/p/azure-servicebus/topics/{TopicName}/subscriptions/{SubscriptionName}/peek"
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Messages
@using SbConsole.Sdk
@inject PeekSubscriptionMessagesQueryHandler PeekHandler
@inject ResubmitSubscriptionDeadLetterMessagesCommandHandler ResubmitHandler
@inject PurgeSubscriptionDeadLetterMessagesCommandHandler PurgeHandler
@inject IConnectionProvider Connections
@inject IConfirmationService Confirmation
@inject ISnackbar Snackbar

<PageTitle>@(DeadLetter ? "Dead-letter" : "Peek") — @SubscriptionName</PageTitle>
<h1>@(DeadLetter ? "Dead-letter" : "Peek"): @TopicName / @SubscriptionName</h1>

@if (DeadLetter)
{
    <MudAlert Severity="Severity.Info" Class="mb-2">
        Resubmitting republishes onto the topic <b>@TopicName</b>, not directly into this subscription — the message
        is re-evaluated against every subscription's filters, so it may also reach other subscriptions.
    </MudAlert>
}

<DeadLetterMessageBrowser
    Messages="_messages"
    DeadLetter="DeadLetter"
    SelectedSequenceNumbers="_selectedSequenceNumbers"
    Selected="_selected"
    Busy="_busy"
    CanPurge="_connection is not null"
    PurgeLabel="Purge subscription"
    OnSelect="@(m => _selected = m)"
    OnToggleSelection="@(t => ToggleSelection(t.SequenceNumber, t.Selected))"
    OnResubmitSelected="ResubmitSelectedAsync"
    OnPurge="PurgeAsync" />

@code {
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public string SubscriptionName { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }
    [SupplyParameterFromQuery] public bool DeadLetter { get; set; }
    [SupplyParameterFromQuery] public long? DeadLetterCount { get; set; }

    private IReadOnlyList<PeekedMessage> _messages = [];
    private PeekedMessage? _selected;
    private readonly HashSet<long> _selectedSequenceNumbers = [];
    private bool _busy;
    private (Guid ConnectionId, string TopicName, string SubscriptionName, bool DeadLetter)? _lastLoaded;
    private ConnectionInfo? _connection;

    protected override async Task OnParametersSetAsync()
    {
        var current = (ConnectionId, TopicName, SubscriptionName, DeadLetter);
        if (_lastLoaded == current)
        {
            return;
        }

        _lastLoaded = current;
        var connections = await Connections.ListAsync("azure-servicebus");
        _connection = connections.FirstOrDefault(c => c.Id == ConnectionId);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _busy = true;
        try
        {
            var result = await PeekHandler.HandleAsync(ConnectionId, TopicName, SubscriptionName, DeadLetter);
            _selectedSequenceNumbers.Clear();
            if (result.IsSuccess)
            {
                _messages = result.Value!;
                _selected = _messages.FirstOrDefault();
            }
            else
            {
                _messages = [];
                _selected = null;
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void ToggleSelection(long sequenceNumber, bool selected)
    {
        if (selected)
        {
            _selectedSequenceNumbers.Add(sequenceNumber);
        }
        else
        {
            _selectedSequenceNumbers.Remove(sequenceNumber);
        }
    }

    private async Task ResubmitSelectedAsync()
    {
        var requested = _selectedSequenceNumbers.Count;
        _busy = true;
        try
        {
            var result = await ResubmitHandler.HandleAsync(new ResubmitSubscriptionDeadLetterMessagesCommand(
                ConnectionId, _connection?.Name ?? "", TopicName, SubscriptionName, [.. _selectedSequenceNumbers]));
            if (result.IsSuccess)
            {
                Snackbar.Add($"{result.Value} of {requested} resubmitted", Severity.Success);
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    private async Task PurgeAsync()
    {
        var count = DeadLetterCount.HasValue ? (int)DeadLetterCount.Value : _messages.Count;
        var confirmed = await Confirmation.ConfirmAsync("Purge", SubscriptionName, _connection?.IsProd ?? false, count: count);
        if (!confirmed)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await PurgeHandler.HandleAsync(new PurgeSubscriptionDeadLetterMessagesCommand(ConnectionId, _connection?.Name ?? "", TopicName, SubscriptionName));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SubscriptionPeekPageTests`
Expected: PASS (5 tests).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green — this also re-confirms `Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component` (Task 5) now sees `Queues`, `Topics`, and `SubscriptionPeek`, all passing.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/SubscriptionPeek.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionPeekPageTests.cs
git commit -m "feat: add subscription peek page with dead-letter resubmit/purge, applying the prod-purge security pattern from the start

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 8: SDK v1.3 — `IPlugin.GetNavBadgeAsync`, plus the "Dead-letter" nav item

**Files:**
- Modify: `src/SbConsole.Sdk/IPlugin.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs` (extend)

**Interfaces:**
- Consumes: `IServiceBusOperations.ListDeadLetterEntriesAsync` (Task 1).
- Produces: `IPlugin.GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) -> Task<int?>` with a default implementation returning `null`, and `ServiceBusPlugin`'s override answering only for the Dead-letter route — consumed by Task 9's `NavMenu.razor`.

- [ ] **Step 1: Add the default-implemented method to `IPlugin`**

In `src/SbConsole.Sdk/IPlugin.cs`, insert immediately after `TestConnectionAsync` (before the interface's closing `}`):

```csharp

    /// <summary>
    /// Optional live badge for a specific nav item (matched by exact Href), for one connection.
    /// The host calls this once per connection of the plugin's ConnectionKind and sums the non-null
    /// results into one badge per nav item (see NavMenu.razor). Returning null means "nothing to
    /// report for this href" -- the default implementation does exactly that, so a plugin written
    /// before this method existed, or one with nothing to badge, needs no change at all.
    /// </summary>
    Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);
```

- [ ] **Step 2: Write the failing test**

Append to `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`:

```csharp
    [Fact]
    public async Task GetNavBadgeAsync_returns_null_for_hrefs_it_does_not_recognize()
    {
        var plugin = new ServiceBusPlugin();

        var result = await plugin.GetNavBadgeAsync("/p/azure-servicebus/queues", "not-a-real-connection-string");

        result.Should().BeNull();
    }
```

Also add this line to the existing `Declares_the_expected_identity_and_connection_kind` test, right after its current `plugin.NavItems.Should().ContainSingle(n => n.Title == "Topics & Subscriptions" ...)` assertion (added in Task 5):

```csharp
        plugin.NavItems.Should().ContainSingle(n => n.Title == "Dead-letter" && n.Href == "/p/azure-servicebus/dead-letter");
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ServiceBusPluginTests`
Expected: FAIL — `GetNavBadgeAsync` isn't overridden yet and the "Dead-letter" nav item doesn't exist yet.

- [ ] **Step 4: Implement `ServiceBusPlugin.GetNavBadgeAsync` and add the nav item**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, change the `NavItems` property (from Task 5) to:

```csharp
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/azure-servicebus/queues"),
        new("Topics & Subscriptions", "/p/azure-servicebus/topics"),
        new("Dead-letter", DeadLetterNavHref),
    ];
```

Add this constant and method to the class (anywhere after `NavItems`, e.g. right before `TestConnectionAsync`):

```csharp
    private const string DeadLetterNavHref = "/p/azure-servicebus/dead-letter";

    // Constructs AzureServiceBusOperations directly, same as TestConnectionAsync above -- plugins
    // have no DI container at this layer (AddSbConsolePlugin<TPlugin>()'s `new()` constraint).
    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref == DeadLetterNavHref
            ? GetDeadLetterBadgeAsync(connectionString, ct)
            : Task.FromResult<int?>(null);

    private static async Task<int?> GetDeadLetterBadgeAsync(string connectionString, CancellationToken ct)
    {
        var entries = await new AzureServiceBusOperations().ListDeadLetterEntriesAsync(connectionString, ct);
        var total = entries.Sum(e => e.Count);
        return total > 0 ? (int)total : null;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ServiceBusPluginTests`
Expected: PASS (4 tests total: the existing identity test now with two more assertions, the existing `TestConnectionAsync` test, and the new `GetNavBadgeAsync` test).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green — `Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component` is unaffected (this task adds no new `@page` component).

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Sdk/IPlugin.cs src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs
git commit -m "feat: add IPlugin.GetNavBadgeAsync (SDK v1.3) and the Dead-letter nav item

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 9: `NavMenu` badge-refresh loop

**Files:**
- Modify: `src/SbConsole.Web/Components/Layout/NavMenu.razor`
- Modify: `tests/SbConsole.Web.Tests/NavMenuTests.cs`

**Interfaces:**
- Consumes: `IPlugin.GetNavBadgeAsync` (Task 8), `IConnectionProvider.ListAsync`/`GetSecretAsync` (`SbConsole.Sdk`), `PluginRegistry.Plugins` (already injected).
- Produces: a public `RefreshBadgesAsync(CancellationToken ct = default)` method on `NavMenu`, called by the component's own background loop and directly by tests (docs/design.md §8) — no other component depends on this.

The existing `NavMenuTests.cs` uses a hand-written `FakePlugin : IPlugin` class. Because `GetNavBadgeAsync` has a default interface implementation, `FakePlugin` does **not** need to implement it to keep compiling. It **does** need an `IConnectionProvider` registered in DI, since `NavMenu` will now inject one — the existing test's `Render<NavMenu>()` call would otherwise fail to resolve the component.

- [ ] **Step 1: Update the existing test's DI setup and write the new failing tests**

In `tests/SbConsole.Web.Tests/NavMenuTests.cs`, add this line to `Renders_host_sections_and_plugin_nav_items`'s existing `Services.AddSingleton(...)` block (so the component still resolves once it injects `IConnectionProvider`):

```csharp
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());
        Services.AddSingleton(connections);
```

(Add this before `var cut = Render<NavMenu>();`, and add `using NSubstitute;` to the file's usings if not already present.)

Then append these new tests to the same class:

```csharp
    [Fact]
    public async Task Shows_a_badge_for_a_nav_item_the_plugin_reports_a_count_for()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var plugin = Substitute.For<IPlugin>();
        plugin.DisplayName.Returns("Fake Plugin");
        plugin.ConnectionKind.Returns("fake");
        plugin.NavItems.Returns(new List<PluginNavItem> { new("Dead-letter", "/p/fake/dead-letter") });
        Services.AddSingleton(new PluginRegistry([plugin]));

        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "fake-conn", "fake", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("secret");
        plugin.GetNavBadgeAsync("/p/fake/dead-letter", "secret", Arg.Any<CancellationToken>()).Returns(12);
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();
        await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());
        cut.Render();

        cut.Markup.Should().Contain("12");
    }

    [Fact]
    public async Task Shows_no_badge_when_the_plugin_reports_null()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var plugin = Substitute.For<IPlugin>();
        plugin.DisplayName.Returns("Fake Plugin");
        plugin.ConnectionKind.Returns("fake");
        plugin.NavItems.Returns(new List<PluginNavItem> { new("Queues", "/p/fake/queues") });
        Services.AddSingleton(new PluginRegistry([plugin]));

        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("fake", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();
        await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());
        cut.Render();

        cut.FindAll(".nav-badge").Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_whose_secret_is_missing_is_skipped_not_fatal()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var plugin = Substitute.For<IPlugin>();
        plugin.DisplayName.Returns("Fake Plugin");
        plugin.ConnectionKind.Returns("fake");
        plugin.NavItems.Returns(new List<PluginNavItem> { new("Dead-letter", "/p/fake/dead-letter") });
        Services.AddSingleton(new PluginRegistry([plugin]));

        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("fake", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "fake-conn", "fake", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);
        Services.AddSingleton(connections);

        var cut = Render<NavMenu>();
        var act = async () => await cut.InvokeAsync(() => cut.Instance.RefreshBadgesAsync());

        await act.Should().NotThrowAsync();
        cut.Render();
        cut.FindAll(".nav-badge").Should().BeEmpty();
    }
```

Add `using SbConsole.Sdk;` and `using NSubstitute;` to the top of the file if not already present (the existing `FakePlugin` already uses `SbConsole.Sdk` types, so `using SbConsole.Sdk;` should already be there).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~NavMenuTests`
Expected: FAIL — `RefreshBadgesAsync` doesn't exist yet, and the existing test fails to resolve `NavMenu` once you've added the `IConnectionProvider` registration but before `NavMenu.razor` itself is updated (a transient state — the real failure to watch for is the compile error from `cut.Instance.RefreshBadgesAsync()` not existing).

- [ ] **Step 3: Implement the badge loop in `NavMenu.razor`**

Replace the entire contents of `src/SbConsole.Web/Components/Layout/NavMenu.razor` with:

```razor
@inject PluginRegistry Registry
@inject IConnectionProvider Connections
@implements IDisposable

<MudNavMenu>
    <MudNavLink Href="/" Match="NavLinkMatch.All" Icon="@Icons.Material.Filled.Dashboard">Dashboard</MudNavLink>
    <MudNavLink Href="/connections" Icon="@Icons.Material.Filled.Cable">Connections</MudNavLink>
    <MudNavLink Href="/audit" Icon="@Icons.Material.Filled.History">Audit</MudNavLink>
    <MudNavLink Href="/plugins" Icon="@Icons.Material.Filled.Extension">Plugins</MudNavLink>
    <MudNavLink Href="/settings" Icon="@Icons.Material.Filled.Settings">Settings</MudNavLink>

    @foreach (var plugin in Registry.Plugins)
    {
        <MudNavGroup Title="@plugin.DisplayName" Expanded="true">
            @foreach (var item in plugin.NavItems)
            {
                <MudNavLink Href="@item.Href" Icon="@(item.Icon ?? Icons.Material.Filled.Extension)">
                    @item.Title
                    @if (_badges.TryGetValue(item.Href, out var count) && count > 0)
                    {
                        <span class="nav-badge" style="margin-left:6px;font:600 10px monospace;padding:1px 6px;border-radius:9px;background:var(--mud-palette-error);color:#fff">@count</span>
                    }
                </MudNavLink>
            }
        </MudNavGroup>
    }
</MudNavMenu>

@code {
    private readonly Dictionary<string, int> _badges = new();
    private readonly CancellationTokenSource _cts = new();

    // Fire-and-forget: never awaited from a lifecycle method, so the very first render of any page
    // (this component is part of the persistent layout, not re-created per page) is never blocked
    // on a Service Bus or database call (docs/design.md §5).
    protected override void OnInitialized()
    {
        _ = RefreshLoopAsync(_cts.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshBadgesAsync(ct);
            }
            catch
            {
                // Best-effort: a failed refresh tick leaves the last-known badges in place and
                // tries again next tick, rather than tearing down the nav menu.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Public so tests can call one refresh directly instead of waiting on the real loop/timer
    // (docs/design.md §8) -- the loop above is just this method plus a delay, in a while loop.
    public async Task RefreshBadgesAsync(CancellationToken ct = default)
    {
        foreach (var plugin in Registry.Plugins)
        {
            var connections = await Connections.ListAsync(plugin.ConnectionKind, ct);
            foreach (var navItem in plugin.NavItems)
            {
                int? total = null;
                foreach (var connection in connections)
                {
                    var secret = await Connections.GetSecretAsync(connection.Id, ct);
                    if (secret is null)
                    {
                        continue;
                    }

                    int? count;
                    try
                    {
                        count = await plugin.GetNavBadgeAsync(navItem.Href, secret, ct);
                    }
                    catch
                    {
                        continue;
                    }

                    if (count is { } c)
                    {
                        total = (total ?? 0) + c;
                    }
                }

                if (total is { } t)
                {
                    _badges[navItem.Href] = t;
                }
                else
                {
                    _badges.Remove(navItem.Href);
                }
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    public void Dispose() => _cts.Cancel();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~NavMenuTests`
Expected: PASS (4 tests: the existing one plus the three new ones).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Web/Components/Layout/NavMenu.razor tests/SbConsole.Web.Tests/NavMenuTests.cs
git commit -m "feat: compute live plugin nav badges in a background refresh loop

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 10: Dead-letter overview — cross-connection handler and page

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/DeadLetter/ListDeadLetterOverviewQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterOverview.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (register the handler)
- Modify: `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs` (add the route)
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/DeadLetter/ListDeadLetterOverviewQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/DeadLetterOverviewPageTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.ListDeadLetterEntriesAsync` (Task 1), `IConnectionProvider.ListAsync("azure-servicebus")`/`GetSecretAsync` (`SbConsole.Sdk`).
- Produces: `DeadLetterOverviewEntry(Guid ConnectionId, string ConnectionName, string EntityType, string? TopicName, string EntityName, long Count)` and `ListDeadLetterOverviewQueryHandler.HandleAsync(CancellationToken ct = default) -> Task<IReadOnlyList<DeadLetterOverviewEntry>>`, and the `/p/azure-servicebus/dead-letter` route.

This handler deliberately does **not** return a `PluginResult` (see Global Constraints) — it spans every connection and is designed to never fail as a whole; a connection whose secret is missing or whose Azure call throws is skipped and logged, not fatal to the rest.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/DeadLetter/ListDeadLetterOverviewQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.DeadLetter;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.DeadLetter;

public class ListDeadLetterOverviewQueryHandlerTests
{
    [Fact]
    public async Task Merges_entries_across_connections_and_tags_each_with_its_connection()
    {
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionA, "sb-dev", "azure-servicebus", []), new(connectionB, "sb-uk-prod", "azure-servicebus", ["prod"]) });
        connections.GetSecretAsync(connectionA, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://dev");
        connections.GetSecretAsync(connectionB, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://prod");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://dev", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry> { new("Queue", null, "orders-inbound", 3) });
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://prod", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry> { new("Subscription", "orders", "uk-team", 11) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().HaveCount(2);
        result.Should().ContainSingle(e => e.ConnectionName == "sb-dev" && e.EntityName == "orders-inbound" && e.Count == 3);
        result.Should().ContainSingle(e => e.ConnectionName == "sb-uk-prod" && e.TopicName == "orders" && e.EntityName == "uk-team" && e.Count == 11);
    }

    [Fact]
    public async Task A_failing_connection_is_skipped_not_fatal_to_the_rest()
    {
        var connectionA = Guid.NewGuid();
        var connectionB = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionA, "sb-broken", "azure-servicebus", []), new(connectionB, "sb-ok", "azure-servicebus", []) });
        connections.GetSecretAsync(connectionA, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://broken");
        connections.GetSecretAsync(connectionB, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://ok");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://broken", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<DeadLetterEntry>>(new InvalidOperationException("unreachable")));
        operations.ListDeadLetterEntriesAsync("Endpoint=sb://ok", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry> { new("Queue", null, "orders-inbound", 3) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().ContainSingle(e => e.ConnectionName == "sb-ok");
    }

    [Fact]
    public async Task A_connection_with_a_missing_secret_is_skipped_not_fatal()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "sb-dev", "azure-servicebus", []) });
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListDeadLetterOverviewQueryHandler(Substitute.For<IServiceBusOperations>(), connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEmpty();
    }
}
```

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/DeadLetterOverviewPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.DeadLetter;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class DeadLetterOverviewPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();

    public DeadLetterOverviewPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<ListDeadLetterOverviewQueryHandler>();
    }

    [Fact]
    public async Task Shows_entries_with_peek_links_for_both_queue_and_subscription_rows()
    {
        var connectionId = Guid.NewGuid();
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(connectionId, "sb-dev", "azure-servicebus", []) });
        _connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        _operations.ListDeadLetterEntriesAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterEntry>
            {
                new("Queue", null, "orders-inbound", 3),
                new("Subscription", "orders", "uk-team", 11),
            });

        var cut = Render<DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders-inbound");
        cut.Markup.Should().Contain("uk-team");
        var links = cut.FindAll("a.peek-dead-letter");
        links.Should().HaveCount(2);
        links[0].GetAttribute("href").Should().Be($"/p/azure-servicebus/queues/orders-inbound/peek?connectionId={connectionId}&deadLetter=true&deadLetterCount=3");
        links[1].GetAttribute("href").Should().Be($"/p/azure-servicebus/topics/orders/subscriptions/uk-team/peek?connectionId={connectionId}&deadLetter=true&deadLetterCount=11");
    }

    [Fact]
    public async Task No_entries_shows_a_success_state()
    {
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No dead-lettered messages");
    }
}
```

Using `Services.AddSingleton<ListDeadLetterOverviewQueryHandler>()` requires it to resolve `ILogger<ListDeadLetterOverviewQueryHandler>` — `Services.AddLogging()` (already called) provides that.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ListDeadLetterOverviewQueryHandlerTests|FullyQualifiedName~DeadLetterOverviewPageTests"`
Expected: FAIL — nothing in `DeadLetter/` exists yet, and `DeadLetterOverview` doesn't exist yet.

- [ ] **Step 3: Implement the handler**

`src/SbConsole.Plugins.ServiceBus/DeadLetter/ListDeadLetterOverviewQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.DeadLetter;

public sealed record DeadLetterOverviewEntry(Guid ConnectionId, string ConnectionName, string EntityType, string? TopicName, string EntityName, long Count);

/// <summary>
/// Spans every azure-servicebus connection at once (docs/design.md §6.3) -- unlike every other
/// handler in this plugin, which operates on one connection named by the caller. Deliberately does
/// not return a PluginResult: there is no single "the operation failed" outcome to represent when
/// the whole point is "show me everything, best effort" -- a connection whose secret is missing or
/// whose Azure call throws is skipped and logged, never fatal to the rest of the page.
/// </summary>
public sealed class ListDeadLetterOverviewQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListDeadLetterOverviewQueryHandler> logger)
{
    public async Task<IReadOnlyList<DeadLetterOverviewEntry>> HandleAsync(CancellationToken ct = default)
    {
        var results = new List<DeadLetterOverviewEntry>();
        var allConnections = await connections.ListAsync("azure-servicebus", ct);
        foreach (var connection in allConnections)
        {
            var secret = await connections.GetSecretAsync(connection.Id, ct);
            if (secret is null)
            {
                continue;
            }

            try
            {
                var entries = await operations.ListDeadLetterEntriesAsync(secret, ct);
                results.AddRange(entries.Select(e => new DeadLetterOverviewEntry(connection.Id, connection.Name, e.EntityType, e.TopicName, e.EntityName, e.Count)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing dead-letter entries for connection {ConnectionName} failed; skipping it for this overview.", connection.Name);
            }
        }

        return results;
    }
}
```

- [ ] **Step 4: Implement `DeadLetterOverview.razor`**

```razor
@page "/p/azure-servicebus/dead-letter"
@using SbConsole.Plugins.ServiceBus.DeadLetter
@inject ListDeadLetterOverviewQueryHandler ListHandler

<PageTitle>Dead-letter</PageTitle>
<h1>Dead-letter</h1>

@if (_loading)
{
    <MudProgressCircular Class="dead-letter-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
}
else if (_entries.Count == 0)
{
    <MudAlert Severity="Severity.Success">No dead-lettered messages across any connection.</MudAlert>
}
else
{
    <MudTable Items="_entries">
        <HeaderContent>
            <MudTh>Namespace</MudTh>
            <MudTh>Entity</MudTh>
            <MudTh>Type</MudTh>
            <MudTh>Count</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.ConnectionName</MudTd>
            <MudTd>@EntityLabel(context)</MudTd>
            <MudTd>@context.EntityType</MudTd>
            <MudTd>@context.Count</MudTd>
            <MudTd><MudButton Class="peek-dead-letter" Href="@PeekUrl(context)">Peek</MudButton></MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<DeadLetterOverviewEntry> _entries = [];
    private bool _loading = true;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _entries = await ListHandler.HandleAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    private static string EntityLabel(DeadLetterOverviewEntry entry) =>
        entry.TopicName is { } topic ? $"{topic} / {entry.EntityName}" : entry.EntityName;

    private static string PeekUrl(DeadLetterOverviewEntry entry) => entry.TopicName is { } topic
        ? $"/p/azure-servicebus/topics/{Uri.EscapeDataString(topic)}/subscriptions/{Uri.EscapeDataString(entry.EntityName)}/peek?connectionId={entry.ConnectionId}&deadLetter=true&deadLetterCount={entry.Count}"
        : $"/p/azure-servicebus/queues/{Uri.EscapeDataString(entry.EntityName)}/peek?connectionId={entry.ConnectionId}&deadLetter=true&deadLetterCount={entry.Count}";
}
```

This reuses **both** existing peek routes (the queue `Peek.razor` from the Queues plan, and `SubscriptionPeek.razor` from Task 7) depending on entity type, with no new peek UI of its own.

- [ ] **Step 5: Register the handler and add the route to the regression tests**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, add after the lines added in Task 4:

```csharp
        services.AddScoped<DeadLetter.ListDeadLetterOverviewQueryHandler>();
```

In `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`, add `/p/azure-servicebus/dead-letter` to the route list from Task 5:

```csharp
        foreach (var route in new[] { "/", "/audit", "/plugins", "/connections", "/settings", "/p/azure-servicebus/queues", "/p/azure-servicebus/topics", "/p/azure-servicebus/dead-letter" })
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ListDeadLetterOverviewQueryHandlerTests|FullyQualifiedName~DeadLetterOverviewPageTests"`
Expected: PASS (3 + 2 = 5 tests).

Run: `dotnet test --filter FullyQualifiedName~ProgramDiRegistrationTests`
Expected: PASS — `Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component` now also covers `DeadLetterOverview`.

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/DeadLetter/ src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterOverview.razor src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/DeadLetter/ tests/SbConsole.Plugins.ServiceBus.Tests/Pages/DeadLetterOverviewPageTests.cs tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
git commit -m "feat: add cross-connection Dead-letter overview page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 11: Final `Contribution` tally and full regression pass

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`

**Interfaces:** None new — this task only updates the static summary shown on the host's Plugins page (`docs/design.md §4`/`§6`) now that every page and action in this plan exists.

- [ ] **Step 1: Update `Contribution`**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, change:

```csharp
    // Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter.
    public PluginContribution Contribution => new(PageCount: 1, ActionCount: 6);
```

to:

```csharp
    // Queues: Create/Delete queue, Peek, Send, Resubmit dead-letter, Purge dead-letter (6).
    // Topics & Subscriptions: Create/Delete topic, Create/Delete subscription, Peek subscription,
    // Resubmit/Purge subscription dead-letter (7 -- Send is reused, not counted again).
    // Pages: Queues, Topics & Subscriptions (combined), SubscriptionPeek, DeadLetterOverview.
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 13);
```

- [ ] **Step 2: Update the test**

In `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`, find whatever assertion (if any) checks `Contribution` today and update it to `new PluginContribution(4, 13)`; if none exists yet, add one to `Declares_the_expected_identity_and_connection_kind`:

```csharp
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 4, ActionCount: 13));
```

- [ ] **Step 3: Run the full suite one final time**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: every test across the solution passes — this is the final gate before the whole-branch review. Report the total count (it will be noticeably larger than the Queues plan's 154, given the extra Dead-letter/NavMenu scope).

- [ ] **Step 4: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs
git commit -m "chore: update plugin Contribution tally for Topics, Subscriptions, and Dead-letter overview

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## After this plan

Filter rules (view/add/delete SQL/correlation filters per subscription) are the natural next slice, reusing this plan's `Topics.razor` expandable rows for a per-subscription rules sub-panel. Per-entity DLQ growth alerting and historical trend charts on the Dead-letter overview are explicitly out of scope (docs/design.md §6.3) — this plan's overview is a live snapshot only.
