# Service Bus Topics & Subscriptions Plugin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full topic/subscription lifecycle management (list, create, delete) plus peek/send/dead-letter-resubmit/purge to the `SbConsole.Plugins.ServiceBus` plugin, reusing everything the shipped Queues plan built.

**Architecture:** Additive throughout — `IServiceBusOperations` gains new parallel methods (no existing signature changes), new handler folders (`Topics/`, `Subscriptions/`) mirror the existing `Queues/` folder's shape exactly, and a message browser component extracted from `Peek.razor` is reused by both queue and subscription peek pages. No new SDK (`SbConsole.Sdk`) surface and no database schema changes — topics/subscriptions live in Azure, exactly as queues do.

**Tech Stack:** .NET 10, C# latest, warnings-as-errors, Blazor Interactive Server, MudBlazor, `Azure.Messaging.ServiceBus` 7.20.2 (already referenced by this plugin project only), xUnit + FluentAssertions 7.x + NSubstitute + bUnit 2.10.3 (`BunitContext`/`Render<T>()`, not the obsolete `TestContext`/`RenderComponent<T>()`).

## Global Constraints

- Filter rules are out of scope. Every subscription gets the topic's default catch-all rule; there is no UI for viewing/adding/removing SQL or correlation filters in this plan (docs/design.md §6.2).
- `IServiceBusOperations`'s existing queue methods, signatures, and behavior do not change. New topic/subscription methods sit alongside them. Sending to a topic reuses the existing `SendMessageAsync` unmodified — no new send method exists (docs/design.md §6.2).
- `AzureServiceBusOperations` shares implementation internally via private helpers between the queue and subscription variants of resubmit/purge/peek, rather than duplicating the logic.
- Plugin handlers follow Core's naming convention (`XxxQueryHandler`/`XxxCommandHandler`, plain classes) and are registered in `ServiceBusPlugin.ConfigureServices` in the **same task** that creates them — never deferred to a later task.
- Every handler wraps its `IServiceBusOperations` call in try/catch, logs the full exception via an injected `ILogger<T>`, and returns `PluginResult[<T>].Fail(ex)` (the `Exception` overload, which routes through `FriendlyError` — never `Fail(ex.Message)` directly).
- Destructive actions (delete topic, delete subscription, purge subscription dead-letter) go through `IConfirmationService`'s typed-for-prod gate, with `IsProd`/`ConnectionName` always looked up from `IConnectionProvider` by connection id — **never** trusted from a URL query parameter. This is a hard requirement, not a style preference: docs/design.md §6.1 documents that this exact class of bug (a client-suppliable `isProd` silently downgrading a prod purge) was a Critical finding in the Queues plan, fixed by deriving it server-side. Every new page in this plan applies that pattern from the start.
- Testing strategy is unit tests only against a substitute of `IServiceBusOperations` — no Testcontainers, no real network calls (docs/design.md §8). `AzureServiceBusOperations`'s own CRUD and messaging methods get no dedicated tests beyond what's already true for the queue versions (client-side validation only) — this mirrors the existing, deliberate precedent: `ListQueuesAsync`/`CreateQueueAsync`/`DeleteQueueAsync` have zero dedicated tests today because they can't be meaningfully unit-tested without a real or emulated broker.
- Gate: `dotnet build -warnaserror` and `dotnet test` green before every commit.

---

## Task 1: SDK models, `IServiceBusOperations` additions, and topic/subscription CRUD in `AzureServiceBusOperations`

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Client/TopicSummary.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/SubscriptionSummary.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/CreateTopicRequest.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Client/CreateSubscriptionRequest.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`

**Interfaces:**
- Consumes: `SbConsole.Sdk.ConnectionTestResult` (unchanged), the existing `ServiceBusAdministrationClient` construction pattern via `AzureServiceBusOperations.CreateAdministrationClientOptions()` (already in the file).
- Produces: `TopicSummary(string Name, int SubscriptionCount, long SizeInBytes)`, `SubscriptionSummary(string Name, long ActiveMessageCount, long DeadLetterMessageCount, long TotalMessageCount)`, `CreateTopicRequest(string Name)`, `CreateSubscriptionRequest(string Name, int MaxDeliveryCount = 10, TimeSpan? LockDuration = null, TimeSpan? DefaultMessageTimeToLive = null)`, and six new `IServiceBusOperations` methods (`ListTopicsAsync`, `CreateTopicAsync`, `DeleteTopicAsync`, `ListSubscriptionsAsync`, `CreateSubscriptionAsync`, `DeleteSubscriptionAsync`) that Tasks 3 and 4's handlers call.

Every Azure SDK call below is verified against the exact installed `Azure.Messaging.ServiceBus` 7.20.2 assembly (decompiled with `ilspycmd` against `~/.nuget/packages/azure.messaging.servicebus/7.20.2/lib/netstandard2.0/Azure.Messaging.ServiceBus.dll`), not assumed — `ServiceBusAdministrationClient.GetTopicsRuntimePropertiesAsync(CancellationToken)`, `.CreateTopicAsync(CreateTopicOptions, CancellationToken)`, `.DeleteTopicAsync(string, CancellationToken)`, `.GetSubscriptionsRuntimePropertiesAsync(string topicName, CancellationToken)`, `.CreateSubscriptionAsync(CreateSubscriptionOptions, CancellationToken)`, `.DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken)` all exist with these exact signatures. `TopicRuntimeProperties` has `Name`, `SizeInBytes`, `SubscriptionCount` (no `ActiveMessageCount`/`ScheduledMessageCount` — those don't exist on a topic, only its subscriptions). `SubscriptionRuntimeProperties` has `SubscriptionName`, `ActiveMessageCount`, `DeadLetterMessageCount`, `TotalMessageCount` (no `ScheduledMessageCount`, no `SizeInBytes` — different fields than `QueueRuntimeProperties`). `CreateSubscriptionOptions(string topicName, string subscriptionName)` has `MaxDeliveryCount`, `LockDuration`, `DefaultMessageTimeToLive` properties, mirroring `CreateQueueOptions` exactly.

- [ ] **Step 1: Create the four new model files**

`src/SbConsole.Plugins.ServiceBus/Client/TopicSummary.cs`:

```csharp
namespace SbConsole.Plugins.ServiceBus.Client;

public sealed record TopicSummary(string Name, int SubscriptionCount, long SizeInBytes);
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

- [ ] **Step 2: Add the six new methods to `IServiceBusOperations`**

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
```

- [ ] **Step 3: Implement the six new methods in `AzureServiceBusOperations`**

Open `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`. Insert the following block immediately after the closing brace of `DeleteQueueAsync` (before `PeekMessagesAsync`):

```csharp

    public async Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string connectionString, CancellationToken ct = default)
    {
        var adminClient = new ServiceBusAdministrationClient(connectionString, CreateAdministrationClientOptions());
        var topics = new List<TopicSummary>();
        await foreach (var props in adminClient.GetTopicsRuntimePropertiesAsync(ct).WithCancellation(ct))
        {
            topics.Add(new TopicSummary(props.Name, props.SubscriptionCount, props.SizeInBytes));
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

- [ ] **Step 4: Build and confirm no regressions**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).` — `AzureServiceBusOperations` now implements every `IServiceBusOperations` member, so the build is the correctness gate here (per the Global Constraints note, these CRUD methods can't be meaningfully unit-tested without a broker — the same is already true of the queue CRUD methods they mirror).

Run: `dotnet test`
Expected: same pass count as before this task (no existing test touches these new members) — confirms nothing regressed.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/
git commit -m "feat: add topic/subscription CRUD to IServiceBusOperations and AzureServiceBusOperations

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: Subscription messaging in `AzureServiceBusOperations` — peek, resubmit, purge

**Files:**
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/IServiceBusOperations.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Client/AzureServiceBusOperations.cs`

**Interfaces:**
- Consumes: `PeekedMessage` (unchanged), `AzureServiceBusOperations.BulkOperationTimeout`/`CreateClientOptions()` (already in the file).
- Produces: `PeekSubscriptionMessagesAsync`, `ResubmitSubscriptionDeadLetterMessagesAsync`, `PurgeSubscriptionDeadLetterMessagesAsync` on `IServiceBusOperations`, which Task 5's handlers call.

`ServiceBusClient.CreateReceiver(string topicName, string subscriptionName, ServiceBusReceiverOptions options)` exists in the SDK alongside the queue overload (verified via the same decompilation as Task 1) — this task's new methods use it in place of `CreateReceiver(queueName, options)`.

**Design note on resubmit**: Service Bus has no way to inject a message directly into one subscription's queue, bypassing the topic's rule-based fan-out. Resubmitting a subscription's dead-lettered message therefore republishes it onto the **topic**, not the subscription — it will be re-evaluated against every subscription's filters, not routed back only to the one it came from. This is unavoidable, standard Service Bus behavior (not a bug to fix), and is called out both in a code comment here and in the `SubscriptionPeek.razor` page built in Task 9.

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

In `AzureServiceBusOperations.cs`, replace the existing `ResubmitDeadLetterMessagesAsync` method body (everything from `await using var client = new ServiceBusClient(...)` down to `return resubmitted;`) so the whole method reads:

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
        // doc comment on ResubmitSubscriptionDeadLetterMessagesAsync for why. The sender therefore
        // targets topicName, unlike the queue overload above which sends back to itself.
        await using var sender = client.CreateSender(topicName);
        return await ResubmitDeadLetterCoreAsync(receiver, sender, sequenceNumbers, ct);
    }

    // Design tradeoff: non-matching messages are deferred (not abandoned) during the scan so the
    // scan can make forward progress through the dead-letter sub-queue instead of looping on the
    // same head-of-queue messages; the cost is that deferral is durable (it does NOT self-heal like
    // an expiring PeekLock does), so an explicit, best-effort restoration pass is required afterward
    // -- see the try/finally below -- to avoid permanently stranding messages if the scan is
    // cancelled or fails. Shared by both the queue and subscription resubmit methods above, which
    // differ only in how `receiver`/`sender` were constructed (a queue resubmits onto itself; a
    // subscription resubmits onto its topic).
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

Delete the old standalone `ResubmitDeadLetterMessagesAsync` body you just replaced above (you already overwrote it in place — no separate deletion step needed if you edited it directly). `RestoreDeferredMessagesAsync` and `TryAbandonAsync` below it are unchanged — they already take only a `ServiceBusReceiver`, so both the queue and subscription paths reuse them as-is with no modification.

- [ ] **Step 3: Extract the purge drain loop into a shared private helper**

Replace the existing `PurgeDeadLetterMessagesAsync` method (the last method in the file) with:

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

    // Wall-clock ceiling on the drain loop below, which is otherwise bounded only by how many
    // messages the dead-letter sub-queue holds and how fast the broker gives them up. Live testing
    // against an unreachable namespace saw this loop never return. On expiry ReceiveMessagesAsync
    // throws OperationCanceledException, which propagates to the calling handler's existing catch
    // (and is audited there as a failed purge) rather than through a second error path. Shared by
    // both the queue and subscription purge methods above.
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

(This duplicates the six-line projection from `PeekMessagesAsync` rather than extracting it — the projection is short enough, and both `PeekMessagesAsync` and `PeekSubscriptionMessagesAsync` are already read easily as complete, standalone methods; extracting a five-parameter helper for six lines would cost more in indirection than it saves.)

- [ ] **Step 5: Build, then re-run the full suite to prove the refactor is behavior-preserving**

Run: `dotnet build -warnaserror`
Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Run: `dotnet test`
Expected: same pass count as after Task 1 — in particular, every existing test in `tests/SbConsole.Plugins.ServiceBus.Tests/Client/AzureServiceBusOperationsTests.cs`, `Messages/ResubmitDeadLetterMessagesCommandHandlerTests.cs`, `Messages/PurgeDeadLetterMessagesCommandHandlerTests.cs`, and `Pages/PeekPageTests.cs` (which exercise the queue resubmit/purge paths through handlers and the page) must still pass unmodified — this is the proof that extracting the shared helpers didn't change queue behavior.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Client/
git commit -m "refactor: extract shared resubmit/purge helpers, add subscription messaging to AzureServiceBusOperations

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: Topic handlers (list, create, delete)

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Topics/ListTopicsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Topics/CreateTopicCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Topics/DeleteTopicCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Topics/ListTopicsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Topics/CreateTopicCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Topics/DeleteTopicCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.ListTopicsAsync/CreateTopicAsync/DeleteTopicAsync` (Task 1), `IConnectionProvider.GetSecretAsync` (`SbConsole.Sdk`), `IAuditScope.RecordAsync` (`SbConsole.Sdk`).
- Produces: `ListTopicsQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) -> Task<PluginResult<IReadOnlyList<TopicSummary>>>`, `CreateTopicCommand(Guid ConnectionId, string ConnectionName, string TopicName)` + `CreateTopicCommandHandler.HandleAsync(CreateTopicCommand, CancellationToken ct = default) -> Task<PluginResult>`, `DeleteTopicCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName)` + `DeleteTopicCommandHandler.HandleAsync(DeleteTopicCommand, CancellationToken ct = default) -> Task<PluginResult>` — Task 6's `Topics.razor` and `CreateTopicDialog.razor` inject and call these.

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
    public async Task Returns_topics_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var topics = new[] { new TopicSummary("orders", 2, 4096) };
        operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>()).Returns(topics);

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(topics);
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
Expected: FAIL — `ListTopicsQueryHandler`, `CreateTopicCommandHandler`, `CreateTopicCommand`, `DeleteTopicCommandHandler`, `DeleteTopicCommand` do not exist yet (compile error).

- [ ] **Step 3: Implement the three handlers**

`src/SbConsole.Plugins.ServiceBus/Topics/ListTopicsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Topics;

public sealed class ListTopicsQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListTopicsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<TopicSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<TopicSummary>>.Fail("Connection not found.");
        }

        try
        {
            var topics = await operations.ListTopicsAsync(secret, ct);
            return PluginResult<IReadOnlyList<TopicSummary>>.Ok(topics);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing topics for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<TopicSummary>>.Fail(ex);
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
Expected: PASS (7 tests: 3 + 2 + 1... recount — `ListTopicsQueryHandlerTests` has 3, `CreateTopicCommandHandlerTests` has 2, `DeleteTopicCommandHandlerTests` has 1 = 6 tests, all passing).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Topics/ src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Topics/
git commit -m "feat: add topic list/create/delete handlers

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: Subscription handlers (list, create, delete)

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Subscriptions/ListSubscriptionsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Subscriptions/CreateSubscriptionCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Subscriptions/DeleteSubscriptionCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/ListSubscriptionsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/CreateSubscriptionCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/DeleteSubscriptionCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.ListSubscriptionsAsync/CreateSubscriptionAsync/DeleteSubscriptionAsync` (Task 1).
- Produces: `ListSubscriptionsQueryHandler.HandleAsync(Guid connectionId, string topicName, CancellationToken ct = default) -> Task<PluginResult<IReadOnlyList<SubscriptionSummary>>>`, `CreateSubscriptionCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, int MaxDeliveryCount)` + handler, `DeleteSubscriptionCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName, string SubscriptionName)` + handler — Task 7's `Subscriptions.razor` and `CreateSubscriptionDialog.razor` inject and call these.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/ListSubscriptionsQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Subscriptions;

public class ListSubscriptionsQueryHandlerTests
{
    [Fact]
    public async Task Returns_subscriptions_for_the_named_topic()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        var subscriptions = new[] { new SubscriptionSummary("uk-team", 5, 1, 6) };
        operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>()).Returns(subscriptions);

        var result = await new ListSubscriptionsQueryHandler(operations, connections, NullLogger<ListSubscriptionsQueryHandler>.Instance).HandleAsync(connectionId, "orders");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(subscriptions);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListSubscriptionsQueryHandler(Substitute.For<IServiceBusOperations>(), connections, NullLogger<ListSubscriptionsQueryHandler>.Instance).HandleAsync(Guid.NewGuid(), "orders");

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        var operations = Substitute.For<IServiceBusOperations>();
        operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<SubscriptionSummary>>(new InvalidOperationException("topic not found")));

        var result = await new ListSubscriptionsQueryHandler(operations, connections, NullLogger<ListSubscriptionsQueryHandler>.Instance).HandleAsync(connectionId, "orders");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("topic not found");
    }
}
```

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

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SbConsole.Plugins.ServiceBus.Tests.Subscriptions`
Expected: FAIL — compile error, the handlers and commands don't exist yet.

- [ ] **Step 3: Implement the three handlers**

`src/SbConsole.Plugins.ServiceBus/Subscriptions/ListSubscriptionsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Subscriptions;

public sealed class ListSubscriptionsQueryHandler(IServiceBusOperations operations, IConnectionProvider connections, ILogger<ListSubscriptionsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<SubscriptionSummary>>> HandleAsync(Guid connectionId, string topicName, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail("Connection not found.");
        }

        try
        {
            var subscriptions = await operations.ListSubscriptionsAsync(secret, topicName, ct);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Ok(subscriptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing subscriptions for {TopicName} failed.", topicName);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail(ex);
        }
    }
}
```

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

- [ ] **Step 4: Register the three handlers in `ServiceBusPlugin.ConfigureServices`**

Add, after the lines added in Task 3:

```csharp
        services.AddScoped<Subscriptions.ListSubscriptionsQueryHandler>();
        services.AddScoped<Subscriptions.CreateSubscriptionCommandHandler>();
        services.AddScoped<Subscriptions.DeleteSubscriptionCommandHandler>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SbConsole.Plugins.ServiceBus.Tests.Subscriptions`
Expected: PASS (6 tests).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Subscriptions/ src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Subscriptions/
git commit -m "feat: add subscription list/create/delete handlers

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: Subscription message handlers (peek, resubmit dead-letter, purge dead-letter)

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/PeekSubscriptionMessagesQueryHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/ResubmitSubscriptionDeadLetterMessagesCommandHandler.cs`
- Create: `src/SbConsole.Plugins.ServiceBus/Messages/PurgeSubscriptionDeadLetterMessagesCommandHandler.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PeekSubscriptionMessagesQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Messages/PurgeSubscriptionDeadLetterMessagesCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `IServiceBusOperations.PeekSubscriptionMessagesAsync/ResubmitSubscriptionDeadLetterMessagesAsync/PurgeSubscriptionDeadLetterMessagesAsync` (Task 2).
- Produces: `PeekSubscriptionMessagesQueryHandler.HandleAsync(Guid connectionId, string topicName, string subscriptionName, bool fromDeadLetter, long? fromSequenceNumber = null, int maxMessages = 32, CancellationToken ct = default) -> Task<PluginResult<IReadOnlyList<PeekedMessage>>>`, `ResubmitSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName, IReadOnlyList<long> SequenceNumbers)` + handler returning `Task<PluginResult<int>>`, `PurgeSubscriptionDeadLetterMessagesCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionName)` + handler returning `Task<PluginResult<int>>` — Task 9's `SubscriptionPeek.razor` injects and calls these. Note `SendMessageCommandHandler`/`SendMessageDialog.razor` (already shipped) are **reused unmodified** for topic sends — no new send handler is created in this plan.

- [ ] **Step 1: Write the failing tests**

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

Run: `dotnet test --filter "FullyQualifiedName~PeekSubscriptionMessagesQueryHandlerTests|FullyQualifiedName~ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests|FullyQualifiedName~PurgeSubscriptionDeadLetterMessagesCommandHandlerTests"`
Expected: FAIL — compile error, the handlers and commands don't exist yet.

- [ ] **Step 3: Implement the three handlers**

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

- [ ] **Step 4: Register the three handlers in `ServiceBusPlugin.ConfigureServices`**

Add, after the lines added in Task 4:

```csharp
        services.AddScoped<Messages.PeekSubscriptionMessagesQueryHandler>();
        services.AddScoped<Messages.ResubmitSubscriptionDeadLetterMessagesCommandHandler>();
        services.AddScoped<Messages.PurgeSubscriptionDeadLetterMessagesCommandHandler>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PeekSubscriptionMessagesQueryHandlerTests|FullyQualifiedName~ResubmitSubscriptionDeadLetterMessagesCommandHandlerTests|FullyQualifiedName~PurgeSubscriptionDeadLetterMessagesCommandHandlerTests"`
Expected: PASS (4 tests).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Messages/ src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Messages/
git commit -m "feat: add subscription peek/resubmit/purge dead-letter handlers

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: Topics page, create-topic dialog, nav item, and broadened `[Authorize]` regression coverage

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/CreateTopicDialog.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (add the "Topics" nav item)
- Modify: `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateTopicDialogTests.cs`

**Interfaces:**
- Consumes: `Topics.ListTopicsQueryHandler`/`CreateTopicCommandHandler`/`DeleteTopicCommandHandler` (Task 3), `IConnectionProvider.ListAsync` (`SbConsole.Sdk`), `IConfirmationService.ConfirmAsync` (`SbConsole.Sdk`).
- Produces: the `/p/azure-servicebus/topics` route and its "Subscriptions" row link (`/p/azure-servicebus/topics/{topicName}/subscriptions?connectionId=...`), which Task 7's `Subscriptions.razor` is reached from.

No new routing infrastructure is needed here — `Program.cs`'s `AddAdditionalAssemblies` wiring (docs/design.md §5) already scans the whole `SbConsole.Plugins.ServiceBus` assembly generically, so `Topics.razor` becomes routable the moment it exists in this project, and `Pages/_Imports.razor`'s `@attribute [Authorize]` already covers it since it lives in the same `Pages/` folder.

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
using SbConsole.Plugins.ServiceBus.Pages;
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
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddLogging();
        Services.AddSingleton<ListTopicsQueryHandler>();
        Services.AddSingleton<CreateTopicCommandHandler>();
        Services.AddSingleton<DeleteTopicCommandHandler>();
    }

    [Fact]
    public async Task Lists_topics_for_the_first_available_connection()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096) });

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("2");
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
    public async Task Subscriptions_link_points_at_the_subscriptions_route_with_the_connection_id()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096) });

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();

        var link = cut.Find("a.view-subscriptions-action");
        link.GetAttribute("href").Should().Be($"/p/azure-servicebus/topics/orders/subscriptions?connectionId={_connectionId}");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_with_the_subscription_count_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 2, 4096) });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, 2, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, 2, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteTopicAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_does_nothing_when_confirmation_is_denied()
    {
        _operations.ListTopicsAsync("Endpoint=sb://real", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 0, 0) });

        var cut = Render<Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().DeleteTopicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests|FullyQualifiedName~CreateTopicDialogTests"`
Expected: FAIL — `Topics`, `CreateTopicDialog` components don't exist yet.

- [ ] **Step 3: Implement `CreateTopicDialog.razor`**

`src/SbConsole.Plugins.ServiceBus/Pages/CreateTopicDialog.razor`:

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

- [ ] **Step 4: Implement `Topics.razor`**

`src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor`:

```razor
@page "/p/azure-servicebus/topics"
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Topics
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject ListTopicsQueryHandler ListHandler
@inject DeleteTopicCommandHandler DeleteHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>Topics</PageTitle>
<h1>Topics</h1>

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
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="OpenCreate">+ Create topic</MudButton>
        @if (_loading)
        {
            <MudProgressCircular Class="topics-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudTable Items="_topics">
        <HeaderContent>
            <MudTh>Topic</MudTh>
            <MudTh>Subscriptions</MudTh>
            <MudTh>Size</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Name</MudTd>
            <MudTd>@context.SubscriptionCount</MudTd>
            <MudTd>@FormatSize(context.SizeInBytes)</MudTd>
            <MudTd>
                <MudButton Class="view-subscriptions-action" Href="@SubscriptionsUrl(context.Name)">Subscriptions</MudButton>
                <MudButton Class="delete-topic" Color="Color.Error" Disabled="@(_deletingTopic == context.Name)" OnClick="@(() => DeleteAsync(context.Name, context.SubscriptionCount))">Delete</MudButton>
                @if (_deletingTopic == context.Name)
                {
                    <MudProgressCircular Class="delete-topic-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                }
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private IReadOnlyList<TopicSummary> _topics = [];
    private Guid _selectedConnectionId;
    private bool _loading;
    private string? _deletingTopic;

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

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    private string SubscriptionsUrl(string topicName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/azure-servicebus/topics/{Uri.EscapeDataString(topicName)}/subscriptions?connectionId={connection.Id}";
    }

    private async Task OpenCreate()
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

    private async Task DeleteAsync(string topicName, int subscriptionCount)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", topicName, connection.IsProd, count: subscriptionCount > 0 ? subscriptionCount : null);
        if (!confirmed)
        {
            return;
        }

        _deletingTopic = topicName;
        try
        {
            var result = await DeleteHandler.HandleAsync(new DeleteTopicCommand(connection.Id, connection.Name, connection.IsProd, topicName));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _deletingTopic = null;
        }

        await LoadTopicsAsync();
    }
}
```

- [ ] **Step 5: Add the "Topics" nav item**

In `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`, change:

```csharp
    public IReadOnlyList<PluginNavItem> NavItems => [new("Queues", "/p/azure-servicebus/queues")];
```

to:

```csharp
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/azure-servicebus/queues"),
        new("Topics", "/p/azure-servicebus/topics"),
    ];
```

- [ ] **Step 6: Broaden the `[Authorize]` regression test and add the new route**

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
    /// The manual smoke test's "anonymous GET redirects" check does not actually prove a component
    /// carries [Authorize] -- it passes just as well from Program.cs's app-wide
    /// SetFallbackPolicy(...RequireAuthenticatedUser()...), which would redirect an unauthenticated
    /// request regardless of whether the component itself declares [Authorize]. This pins the
    /// attribute directly on every compiled, routable component type in the plugin assembly, rather
    /// than one named type -- broadened from the Queues plan's original version (which pinned only
    /// the Queues component) specifically because that plan's own final review flagged that a future
    /// page added to the plugin could escape the check silently. This is that future page: Topics,
    /// Subscriptions, and the subscription peek page (Task 6-9 of the Topics & Subscriptions plan)
    /// are all covered automatically, as would any later page, with no test change required.
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

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TopicsPageTests|FullyQualifiedName~CreateTopicDialogTests"`
Expected: PASS (5 + 2 = 7 tests).

Run: `dotnet test --filter FullyQualifiedName~ProgramDiRegistrationTests`
Expected: PASS — including the broadened `Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component`, which at this point in the plan sees only `Queues` and `Topics` (both carry `[Authorize]` via `Pages/_Imports.razor`).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green.

- [ ] **Step 8: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/Topics.razor src/SbConsole.Plugins.ServiceBus/Pages/CreateTopicDialog.razor src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/TopicsPageTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateTopicDialogTests.cs tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
git commit -m "feat: add Topics page, create-topic dialog, and Topics nav item

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 7: Subscriptions page, create-subscription dialog, and reused send-message dialog

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/Subscriptions.razor`
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/CreateSubscriptionDialog.razor`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionsPageTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateSubscriptionDialogTests.cs`

**Interfaces:**
- Consumes: `Subscriptions.ListSubscriptionsQueryHandler`/`CreateSubscriptionCommandHandler`/`DeleteSubscriptionCommandHandler` (Task 4), the existing `Messages.SendMessageCommandHandler`/`SendMessageCommand`/`Pages.SendMessageDialog` (shipped in the Queues plan, unmodified), `IConnectionProvider.ListAsync`.
- Produces: the `/p/azure-servicebus/topics/{TopicName}/subscriptions` route and its "Peek"/"Dead-letter" row links (`/p/azure-servicebus/topics/{topicName}/subscriptions/{subscriptionName}/peek?connectionId=...&deadLetter=...`), which Task 9's `SubscriptionPeek.razor` is reached from.

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionsPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.ServiceBus.Client;
using SbConsole.Plugins.ServiceBus.Messages;
using SbConsole.Plugins.ServiceBus.Pages;
using SbConsole.Plugins.ServiceBus.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.ServiceBus.Tests.Pages;

public class SubscriptionsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IServiceBusOperations _operations = Substitute.For<IServiceBusOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConfirmationService _confirmation = Substitute.For<IConfirmationService>();

    public SubscriptionsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("azure-servicebus", Arg.Any<CancellationToken>())
            .Returns(new List<ConnectionInfo> { new(_connectionId, "sb-dev", "azure-servicebus", ["dev"]) });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Endpoint=sb://real");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(_confirmation);
        Services.AddLogging();
        Services.AddSingleton<ListSubscriptionsQueryHandler>();
        Services.AddSingleton<CreateSubscriptionCommandHandler>();
        Services.AddSingleton<DeleteSubscriptionCommandHandler>();
        Services.AddSingleton<SendMessageCommandHandler>();
    }

    private void NavigateToSubscriptionsQuery(Guid connectionId)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(new Dictionary<string, object?> { ["ConnectionId"] = connectionId }));
    }

    [Fact]
    public async Task Lists_subscriptions_for_the_topic()
    {
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6) });

        NavigateToSubscriptionsQuery(_connectionId);
        var cut = Render<Subscriptions>(parameters => parameters.Add(p => p.TopicName, "orders"));
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("uk-team");
        cut.Markup.Should().Contain("5");
    }

    [Fact]
    public async Task Dead_letter_link_carries_connectionId_deadLetter_and_the_rows_live_count()
    {
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 5, 1, 6) });

        NavigateToSubscriptionsQuery(_connectionId);
        var cut = Render<Subscriptions>(parameters => parameters.Add(p => p.TopicName, "orders"));
        await Task.Delay(30);
        cut.Render();

        var link = cut.Find("a.dead-letter-action");
        link.GetAttribute("href").Should().Be(
            $"/p/azure-servicebus/topics/orders/subscriptions/uk-team/peek?connectionId={_connectionId}&deadLetter=true&deadLetterCount=1");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0) });
        _confirmation.ConfirmAsync("Delete", "uk-team", false, null, Arg.Any<CancellationToken>()).Returns(true);

        NavigateToSubscriptionsQuery(_connectionId);
        var cut = Render<Subscriptions>(parameters => parameters.Add(p => p.TopicName, "orders"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-subscription").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteSubscriptionAsync("Endpoint=sb://real", "orders", "uk-team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_does_nothing_when_confirmation_is_denied()
    {
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary> { new("uk-team", 0, 0, 0) });

        NavigateToSubscriptionsQuery(_connectionId);
        var cut = Render<Subscriptions>(parameters => parameters.Add(p => p.TopicName, "orders"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-subscription").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().DeleteSubscriptionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_message_reuses_the_shared_dialog_targeting_the_topic()
    {
        _operations.ListSubscriptionsAsync("Endpoint=sb://real", "orders", Arg.Any<CancellationToken>())
            .Returns(new List<SubscriptionSummary>());

        NavigateToSubscriptionsQuery(_connectionId);
        var cut = Render<Subscriptions>(parameters => parameters.Add(p => p.TopicName, "orders"));
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.send-message-action").Click();
        await Task.Delay(30);
        cut.Render();

        // SendMessageDialog is the exact same shared component the Queues page uses -- proving reuse,
        // not a parallel implementation, by asserting its own input id is present.
        cut.Find("input#message-body").Should().NotBeNull();
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

Run: `dotnet test --filter "FullyQualifiedName~SubscriptionsPageTests|FullyQualifiedName~CreateSubscriptionDialogTests"`
Expected: FAIL — `Subscriptions`, `CreateSubscriptionDialog` don't exist yet.

- [ ] **Step 3: Implement `CreateSubscriptionDialog.razor`**

`src/SbConsole.Plugins.ServiceBus/Pages/CreateSubscriptionDialog.razor`:

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

- [ ] **Step 4: Implement `Subscriptions.razor`**

`src/SbConsole.Plugins.ServiceBus/Pages/Subscriptions.razor`:

```razor
@page "/p/azure-servicebus/topics/{TopicName}/subscriptions"
@using SbConsole.Plugins.ServiceBus.Client
@using SbConsole.Plugins.ServiceBus.Messages
@using SbConsole.Plugins.ServiceBus.Subscriptions
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject ListSubscriptionsQueryHandler ListHandler
@inject DeleteSubscriptionCommandHandler DeleteHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>Subscriptions — @TopicName</PageTitle>
<h1>Subscriptions: @TopicName</h1>

@if (_connection is null)
{
    <MudAlert Severity="Severity.Info">Loading…</MudAlert>
}
else
{
    <div class="d-flex align-center gap-2 my-4">
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="OpenCreate">+ Create subscription</MudButton>
        <MudButton Class="send-message-action" OnClick="OpenSend">Send message</MudButton>
        @if (_loading)
        {
            <MudProgressCircular Class="subscriptions-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudTable Items="_subscriptions">
        <HeaderContent>
            <MudTh>Subscription</MudTh>
            <MudTh>Active</MudTh>
            <MudTh>Dead-letter</MudTh>
            <MudTh>Total</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Name</MudTd>
            <MudTd>@context.ActiveMessageCount</MudTd>
            <MudTd>@context.DeadLetterMessageCount</MudTd>
            <MudTd>@context.TotalMessageCount</MudTd>
            <MudTd>
                <MudButton Href="@PeekUrl(context.Name)">Peek</MudButton>
                <MudButton Class="dead-letter-action" Href="@PeekUrl(context.Name, deadLetter: true, deadLetterCount: context.DeadLetterMessageCount)">Dead-letter</MudButton>
                <MudButton Class="delete-subscription" Color="Color.Error" Disabled="@(_deletingSubscription == context.Name)" OnClick="@(() => DeleteAsync(context.Name))">Delete</MudButton>
                @if (_deletingSubscription == context.Name)
                {
                    <MudProgressCircular Class="delete-subscription-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                }
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    [Parameter] public string TopicName { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }

    private ConnectionInfo? _connection;
    private IReadOnlyList<SubscriptionSummary> _subscriptions = [];
    private bool _loading;
    private string? _deletingSubscription;
    private (Guid ConnectionId, string TopicName)? _lastLoaded;

    // Same reasoning as Peek.razor (docs/design.md §6.1): this page is reached via a query-string-
    // carrying link, and Blazor reuses the component instance on same-route navigation, so the load
    // lives in OnParametersSetAsync (not OnInitializedAsync) guarded against redundant reloads.
    protected override async Task OnParametersSetAsync()
    {
        var current = (ConnectionId, TopicName);
        if (_lastLoaded == current)
        {
            return;
        }

        _lastLoaded = current;
        var connections = await Connections.ListAsync("azure-servicebus");
        _connection = connections.FirstOrDefault(c => c.Id == ConnectionId);
        await LoadSubscriptionsAsync();
    }

    private async Task LoadSubscriptionsAsync()
    {
        _loading = true;
        try
        {
            var result = await ListHandler.HandleAsync(ConnectionId, TopicName);
            if (result.IsSuccess)
            {
                _subscriptions = result.Value!;
            }
            else
            {
                _subscriptions = [];
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private string PeekUrl(string subscriptionName, bool deadLetter = false, long? deadLetterCount = null)
    {
        var url = $"/p/azure-servicebus/topics/{Uri.EscapeDataString(TopicName)}/subscriptions/{Uri.EscapeDataString(subscriptionName)}/peek?connectionId={ConnectionId}";
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

    private async Task OpenCreate()
    {
        var parameters = new DialogParameters<CreateSubscriptionDialog>
        {
            { x => x.ConnectionId, ConnectionId },
            { x => x.ConnectionName, _connection!.Name },
            { x => x.TopicName, TopicName },
        };
        var dialog = await DialogService.ShowAsync<CreateSubscriptionDialog>("Create subscription", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadSubscriptionsAsync();
        }
    }

    // SendMessageDialog is reused unmodified from the Queues plan (docs/design.md §6.2): a topic
    // name is just another entity path to IServiceBusOperations.SendMessageAsync, identical under
    // the hood to a queue name, so its QueueName parameter carries the topic name here.
    private async Task OpenSend()
    {
        var parameters = new DialogParameters<SendMessageDialog>
        {
            { x => x.ConnectionId, ConnectionId },
            { x => x.ConnectionName, _connection!.Name },
            { x => x.QueueName, TopicName },
        };
        var dialog = await DialogService.ShowAsync<SendMessageDialog>("Send message", parameters);
        await dialog.Result;
    }

    private async Task DeleteAsync(string subscriptionName)
    {
        var confirmed = await Confirmation.ConfirmAsync("Delete", subscriptionName, _connection!.IsProd);
        if (!confirmed)
        {
            return;
        }

        _deletingSubscription = subscriptionName;
        try
        {
            var result = await DeleteHandler.HandleAsync(new DeleteSubscriptionCommand(ConnectionId, _connection!.Name, _connection!.IsProd, TopicName, subscriptionName));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _deletingSubscription = null;
        }

        await LoadSubscriptionsAsync();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SubscriptionsPageTests|FullyQualifiedName~CreateSubscriptionDialogTests"`
Expected: PASS (5 + 2 = 7 tests).

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green — note `Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component` (Task 6) now also covers `Subscriptions`, still passing since it's in the same `Pages/` folder.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/Subscriptions.razor src/SbConsole.Plugins.ServiceBus/Pages/CreateSubscriptionDialog.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionsPageTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/CreateSubscriptionDialogTests.cs
git commit -m "feat: add Subscriptions page and create-subscription dialog, reusing SendMessageDialog for topic sends

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 8: Extract the shared dead-letter message browser component from `Peek.razor`

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterMessageBrowser.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor`

**Interfaces:**
- Produces: `DeadLetterMessageBrowser` — a presentation-only component with parameters `Messages` (`IReadOnlyList<PeekedMessage>`), `DeadLetter` (`bool`), `SelectedSequenceNumbers` (`HashSet<long>`), `Selected` (`PeekedMessage?`), `Busy` (`bool`), `CanPurge` (`bool`), `PurgeLabel` (`string`), and callbacks `OnSelect` (`EventCallback<PeekedMessage>`), `OnToggleSelection` (`EventCallback<(long SequenceNumber, bool Selected)>`), `OnResubmitSelected` (`EventCallback`), `OnPurge` (`EventCallback`) — consumed by `Peek.razor` in this task and `SubscriptionPeek.razor` in Task 9.

This is a pure, behavior-preserving refactor: `Peek.razor`'s markup moves into the new component; every method in its `@code` block (`LoadAsync`, `OnParametersSetAsync`, `ToggleSelection`, `ResubmitSelectedAsync`, `PurgeAsync`) is unchanged. The component keeps the exact CSS classes (`resubmit-selected`, `purge-queue`, `select-message`, `peek-busy`) the existing `PeekPageTests.cs` already asserts against, so that entire test file must pass unmodified after this task — it is the regression proof.

- [ ] **Step 1: Create `DeadLetterMessageBrowser.razor`**

`src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterMessageBrowser.razor`:

```razor
@* Shared message browser for both queue and subscription peek pages: message list, message
   detail pane, and (dead-letter mode) the resubmit/purge action bar. Parent pages own all state;
   this component is presentation and user-input routing only -- extracted from Peek.razor so
   SubscriptionPeek.razor (Task 9 of the Topics & Subscriptions plan) can reuse it exactly rather
   than duplicating it. CSS classes are kept identical to Peek.razor's pre-extraction markup so
   PeekPageTests.cs needs no changes. *@
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
           any dead-letter operation is in flight -- purge and resubmit act on the same sub-queue, so
           letting one start while the other runs is a conflict, not independence. *@
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

In `src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor`, replace everything from the opening `@if (DeadLetter)` block down through the closing `</MudGrid>` (i.e. replace lines 16-75 of the file as it exists after the Queues plan) with:

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

Do not change anything in the `@code` block — `ToggleSelection`, `ResubmitSelectedAsync`, `PurgeAsync`, `LoadAsync`, `OnParametersSetAsync`, and every field stay exactly as they are. The page's top-of-file `@page`, `@inject`, and the leading `<PageTitle>`/`<h1>` lines are unchanged too.

- [ ] **Step 3: Run the existing Peek tests to prove the refactor is behavior-preserving**

Run: `dotnet test --filter FullyQualifiedName~PeekPageTests`
Expected: PASS — all tests in `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/PeekPageTests.cs` pass **unmodified**, including `Purge_passes_the_connections_prod_flag_to_the_confirmation_prompt_not_a_url_parameter` and `Purge_is_disabled_until_the_real_connection_record_has_resolved` — the two tests protecting the Critical security fix from the Queues plan. If any of these fail, the extraction changed behavior and must be fixed before proceeding — do not modify the tests to make them pass.

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green, same total count as after Task 7 (this task adds no new tests — it only refactors and revalidates existing ones).

- [ ] **Step 4: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/DeadLetterMessageBrowser.razor src/SbConsole.Plugins.ServiceBus/Pages/Peek.razor
git commit -m "refactor: extract DeadLetterMessageBrowser from Peek.razor for reuse by subscription peek

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 9: Subscription peek page, applying the same prod-purge security pattern from day one

**Files:**
- Create: `src/SbConsole.Plugins.ServiceBus/Pages/SubscriptionPeek.razor`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs` (final `Contribution` tally)
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionPeekPageTests.cs`
- Test: `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs` (extend)

**Interfaces:**
- Consumes: `Messages.PeekSubscriptionMessagesQueryHandler`/`ResubmitSubscriptionDeadLetterMessagesCommandHandler`/`PurgeSubscriptionDeadLetterMessagesCommandHandler` (Task 5), `Pages.DeadLetterMessageBrowser` (Task 8), `IConnectionProvider.ListAsync`, `IConfirmationService.ConfirmAsync`.
- Produces: the `/p/azure-servicebus/topics/{TopicName}/subscriptions/{SubscriptionName}/peek` route, completing the drill-down from `Subscriptions.razor` (Task 7).

This page applies, from the start, the exact security pattern the Queues plan's final review found missing and had to retrofit into `Peek.razor`: `IsProd` and `ConnectionName` are looked up from the real `ConnectionInfo` record via `IConnectionProvider`, **never** trusted from a URL query parameter. The two tests marked below directly mirror `PeekPageTests.cs`'s `Purge_passes_the_connections_prod_flag_to_the_confirmation_prompt_not_a_url_parameter` and `Purge_is_disabled_until_the_real_connection_record_has_resolved` — this plan does not get to skip that proof just because the pattern is now "established."

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
        // security pattern from day one instead of retrofitting it. Seeding a prod-tagged
        // connection and asserting ConfirmAsync receives isProd: true proves the value is trusted
        // from the connection record, not the URL -- NavigateToPeekQuery below carries no isProd
        // (or any prod-related) query parameter at all.
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
        // Direct analogue of PeekPageTests.cs's identically-named test.
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

`src/SbConsole.Plugins.ServiceBus/Pages/SubscriptionPeek.razor`:

```razor
@* Message browser for a subscription's messages/dead-letter sub-queue -- same shape as
   Peek.razor, sharing DeadLetterMessageBrowser (Task 8), applying the same IConnectionProvider-
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

    // Security-critical, applied from day one (docs/design.md §6.1/§6.2): IsProd/ConnectionName
    // are looked up from the real connection record via IConnectionProvider, never trusted off
    // the URL.
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

- [ ] **Step 4: Update `ServiceBusPlugin.Contribution` and its identity test**

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
    // Pages: Queues, Topics, Subscriptions, SubscriptionPeek (Peek reuses the Queues route).
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 13);
```

In `tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs`, extend `Declares_the_expected_identity_and_connection_kind` by adding this assertion right after the existing `plugin.NavItems.Should().ContainSingle(...)` line:

```csharp
        plugin.NavItems.Should().ContainSingle(n => n.Title == "Topics" && n.Href == "/p/azure-servicebus/topics");
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SubscriptionPeekPageTests`
Expected: PASS (5 tests).

Run: `dotnet test --filter FullyQualifiedName~ServiceBusPluginTests`
Expected: PASS.

Run: `dotnet build -warnaserror && dotnet test`
Expected: full suite green — this also re-confirms `Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component` (Task 6) now sees `Queues`, `Topics`, `Subscriptions`, and `SubscriptionPeek`, all passing.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.ServiceBus/Pages/SubscriptionPeek.razor src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs tests/SbConsole.Plugins.ServiceBus.Tests/Pages/SubscriptionPeekPageTests.cs tests/SbConsole.Plugins.ServiceBus.Tests/ServiceBusPluginTests.cs
git commit -m "feat: add subscription peek page with dead-letter resubmit/purge, applying the prod-purge security pattern from the start

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## After this plan

Filter rules (view/add/delete SQL/correlation filters per subscription) are the natural next slice, reusing this plan's `Subscriptions.razor` drill-down. Deferred-message tooling, sessions tooling beyond basic display, metrics dashboards/history, ARM/namespace creation, and Entra ID auth remain out of v1 scope generally (docs/design.md §6.2).
