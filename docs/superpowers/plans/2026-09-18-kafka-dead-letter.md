# Kafka dead-letter handling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add dead-letter handling to the Kafka plugin (`SbConsole.Plugins.Kafka`) — list DLQ-topic-convention topics and their retained counts across every connection, with a Peek link, plus dashboard/badge/oldest-dead-letter parity — the third and final slice of the Kafka plugin's original scope.

**Architecture:** One new `IKafkaOperations` method (`ListDeadLetterTopicsAsync`) implemented in the existing `ConfluentKafkaOperations`, one cross-connection handler (unlike every other handler in this plugin, which takes a single `connectionId`), one new page, a small addition to the existing `Topics.razor`, and three `KafkaPlugin` `IPlugin` hook overrides extended (not replaced — they already handle consumer-group lag) to also cover dead-letter. Full design: `docs/superpowers/specs/2026-09-18-kafka-dead-letter-design.md`.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor 9.9.0, `Confluent.Kafka` 2.15.1, xUnit + FluentAssertions 7.x + NSubstitute + bUnit.

## Global Constraints

- .NET 10, C# `latest`, nullable enabled, warnings as errors (`Directory.Build.props`, already enforced).
- Gate before every commit: `dotnet build SbConsole.slnx -warnaserror && dotnet test` both green.
- A DLQ topic is any topic whose name ends with `-dlq` (`StringComparison.Ordinal`) — the only naming convention this plan recognizes. No other suffix, no configurability.
- This plan is **read-only**: list and peek only. No Resubmit, no Purge — Kafka can't delete an individual message or a message range, so neither has a clean analogue here (design spec §9).
- `ApproximateMessageCount` carries the same caveat `TopicSummary`'s already does: "currently retained," not "unprocessed backlog." Unlike consumer-group lag, though, the dashboard-problem threshold for dead-letter is `> 0`, not a large magnitude — any dead-lettered message is inherently abnormal, matching Service Bus's own `DeadLetterMessageCount > 0` semantics.
- `GetNavBadgeAsync` returns `null`, never `0`, when nothing is flagged (`NavMenu.razor` renders a visible "0" for a literal `0`).
- **Real-broker risk, learned from the consumer-groups plan:** that plan's full unit-test suite passed 393/393 while shipping a genuine bug (`ResetConsumerGroupOffsetAsync` passing sentinel offset values to an API that rejects them) that only manual, live testing against a real broker caught. This plan's riskiest new real-broker code is a repeated `Assign`/`Consume`/`Unassign` cycle on one reused consumer instance across every DLQ topic's partitions (§3 of the design) — a pattern nothing else in this plugin does. **Task 6 is a mandatory manual live-broker verification pass — do not skip it and do not consider this plan done without it**, the same kind of check that caught the offset-reset bug.
- Conventional commits, ending with:
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>

---

### Task 1: Data contract, `IKafkaOperations` method, `ConfluentKafkaOperations.ListDeadLetterTopicsAsync`

Unlike the consumer-groups plan's Task 1 (which deliberately left `ConfluentKafkaOperations` not implementing the new interface members, since three new methods needed three separate tasks), this plan has exactly one new method — so the interface addition and its real implementation land in the same task, in the same commit. The solution builds clean at the end of this task; there's no intermediate non-building state to manage.

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Client/DeadLetterTopicSummary.cs`
- Modify: `src/SbConsole.Plugins.Kafka/Client/IKafkaOperations.cs`
- Modify: `src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs`

**Interfaces:**
- Consumes: `AttemptTimeout`, `CreateAdminClientConfig`, `CreateConsumerConfig` (existing, same file).
- Produces (used by every later task): `DeadLetterTopicSummary` record; `IKafkaOperations.ListDeadLetterTopicsAsync(string config, CancellationToken ct = default) : Task<IReadOnlyList<DeadLetterTopicSummary>>`; `internal static bool ConfluentKafkaOperations.IsDlqTopic(string name)` and `internal static string ConfluentKafkaOperations.OriginalTopicName(string dlqTopicName)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
// Add these test class members (same file as the existing ComputeLag/ResolveTimestampLookupResult tests):

    [Theory]
    [InlineData("orders-dlq", true)]
    [InlineData("orders", false)]
    [InlineData("orders-dlq-archive", false)]  // "-dlq" not at the end -- must not match
    [InlineData("-dlq", true)]                  // degenerate but valid: empty original name
    public void IsDlqTopic_matches_only_the_dash_dlq_suffix(string name, bool expected)
    {
        ConfluentKafkaOperations.IsDlqTopic(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("orders-dlq", "orders")]
    [InlineData("-dlq", "")]
    public void OriginalTopicName_strips_the_dash_dlq_suffix(string dlqTopicName, string expected)
    {
        ConfluentKafkaOperations.OriginalTopicName(dlqTopicName).Should().Be(expected);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "IsDlqTopic|OriginalTopicName"`
Expected: FAIL to compile — `ConfluentKafkaOperations.IsDlqTopic`/`OriginalTopicName` don't exist yet.

- [ ] **Step 3: Add the data contract**

```csharp
// src/SbConsole.Plugins.Kafka/Client/DeadLetterTopicSummary.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ApproximateMessageCount carries the same caveat TopicSummary's does (design spec 2026-09-16
/// §4): "currently retained by the topic's retention policy," not "unprocessed backlog" -- Kafka
/// has no such concept. OldestMessageTimestamp is null when every partition is empty (nothing
/// retained right now); otherwise the earliest message's timestamp across all of this topic's
/// partitions.
/// </summary>
public sealed record DeadLetterTopicSummary(
    string DlqTopicName, string OriginalTopicName, int PartitionCount,
    long ApproximateMessageCount, DateTimeOffset? OldestMessageTimestamp);
```

- [ ] **Step 4: Extend `IKafkaOperations`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/IKafkaOperations.cs
// Add inside the existing interface, after ResetConsumerGroupOffsetAsync:

    Task<IReadOnlyList<DeadLetterTopicSummary>> ListDeadLetterTopicsAsync(string config, CancellationToken ct = default);
```

- [ ] **Step 5: Implement `ListDeadLetterTopicsAsync` in `ConfluentKafkaOperations`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
// Add these members at the end of the ConfluentKafkaOperations class:

    // Extracted as pure static functions, same reasoning as ComputeLag/IsEndOfPartition/Decode --
    // unit-testable without a real broker.
    internal static bool IsDlqTopic(string name) => name.EndsWith("-dlq", StringComparison.Ordinal);

    internal static string OriginalTopicName(string dlqTopicName) => dlqTopicName[..^4];

    public Task<IReadOnlyList<DeadLetterTopicSummary>> ListDeadLetterTopicsAsync(string config, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<DeadLetterTopicSummary>>(() =>
        {
            using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
            var metadata = admin.GetMetadata(AttemptTimeout);
            var dlqTopics = metadata.Topics.Where(t => IsDlqTopic(t.Topic)).ToList();
            if (dlqTopics.Count == 0)
            {
                return [];
            }

            // One throwaway consumer, reused sequentially across every DLQ topic's partitions below
            // -- Assign/Consume/Unassign per nonempty partition, never committing. Same "fresh,
            // never-reused group.id" convention every other read-only operation in this class uses;
            // the repeated Assign/Unassign on one instance (rather than a fresh consumer per
            // partition) is new to this method -- see this plan's Global Constraints for why Task 6
            // exists to verify this against a real broker.
            using var consumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();

            var results = new List<DeadLetterTopicSummary>();
            foreach (var topic in dlqTopics)
            {
                long totalCount = 0;
                DateTimeOffset? oldest = null;
                foreach (var partition in topic.Partitions)
                {
                    var topicPartition = new TopicPartition(topic.Topic, new Partition(partition.PartitionId));
                    var watermarks = consumer.QueryWatermarkOffsets(topicPartition, AttemptTimeout);
                    var count = watermarks.High.Value - watermarks.Low.Value;
                    totalCount += count;

                    if (count <= 0)
                    {
                        continue;
                    }

                    consumer.Assign(new TopicPartitionOffset(topicPartition, new Offset(watermarks.Low.Value)));
                    var result = consumer.Consume(AttemptTimeout);
                    consumer.Unassign();

                    if (result is not null && !result.IsPartitionEOF)
                    {
                        var timestamp = result.Message.Timestamp.UtcDateTime;
                        if (oldest is null || timestamp < oldest)
                        {
                            oldest = timestamp;
                        }
                    }
                }

                results.Add(new DeadLetterTopicSummary(topic.Topic, OriginalTopicName(topic.Topic), topic.Partitions.Count, totalCount, oldest));
            }

            return results;
        }, ct);
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "IsDlqTopic|OriginalTopicName"`
Expected: PASS (6 cases).

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS — `ConfluentKafkaOperations` implements every `IKafkaOperations` member again (no build-order gap this time).

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add ConfluentKafkaOperations.ListDeadLetterTopicsAsync

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Handler + `KafkaPlugin` wiring

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/DeadLetter/ListDeadLetterOverviewQueryHandler.cs`
- Modify: `src/SbConsole.Plugins.Kafka/KafkaPlugin.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/DeadLetter/ListDeadLetterOverviewQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations.ListDeadLetterTopicsAsync` (Task 1), `IConnectionProvider` (`SbConsole.Sdk`).
- Produces: `DeadLetterOverviewEntry(Guid ConnectionId, string ConnectionName, string DlqTopicName, string OriginalTopicName, int PartitionCount, long ApproximateMessageCount)`; `ListDeadLetterOverviewQueryHandler.HandleAsync(CancellationToken ct = default) : Task<IReadOnlyList<DeadLetterOverviewEntry>>` — what Task 3's page injects.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/DeadLetter/ListDeadLetterOverviewQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.DeadLetter;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.DeadLetter;

public class ListDeadLetterOverviewQueryHandlerTests
{
    [Fact]
    public async Task Returns_an_empty_list_when_there_are_no_Kafka_connections()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var result = await new ListDeadLetterOverviewQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Merges_dead_letter_topics_across_every_connection()
    {
        var connectionA = new ConnectionInfo(Guid.NewGuid(), "kafka-dev", "kafka", ["dev"]);
        var connectionB = new ConnectionInfo(Guid.NewGuid(), "kafka-staging", "kafka", ["staging"]);
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionA, connectionB });
        connections.GetSecretAsync(connectionA.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=dev:9092");
        connections.GetSecretAsync(connectionB.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=staging:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=dev:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 3, 5, DateTimeOffset.UtcNow) });
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=staging:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("payments-dlq", "payments", 1, 0, null) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEquivalentTo(new[]
        {
            new DeadLetterOverviewEntry(connectionA.Id, "kafka-dev", "orders-dlq", "orders", 3, 5),
            new DeadLetterOverviewEntry(connectionB.Id, "kafka-staging", "payments-dlq", "payments", 1, 0),
        });
    }

    [Fact]
    public async Task A_connection_whose_call_fails_is_skipped_not_fatal()
    {
        var failingConnection = new ConnectionInfo(Guid.NewGuid(), "kafka-unreachable", "kafka", []);
        var healthyConnection = new ConnectionInfo(Guid.NewGuid(), "kafka-dev", "kafka", ["dev"]);
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { failingConnection, healthyConnection });
        connections.GetSecretAsync(failingConnection.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=unreachable:9092");
        connections.GetSecretAsync(healthyConnection.Id, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=dev:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=unreachable:9092", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<DeadLetterTopicSummary>>(new InvalidOperationException("broker unreachable")));
        operations.ListDeadLetterTopicsAsync("bootstrap.servers=dev:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 1, 2, null) });

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().ContainSingle().Which.ConnectionName.Should().Be("kafka-dev");
    }

    [Fact]
    public async Task A_connection_with_no_secret_is_skipped()
    {
        var connection = new ConnectionInfo(Guid.NewGuid(), "kafka-dev", "kafka", []);
        var connections = Substitute.For<IConnectionProvider>();
        connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connection });
        connections.GetSecretAsync(connection.Id, Arg.Any<CancellationToken>()).Returns((string?)null);
        var operations = Substitute.For<IKafkaOperations>();

        var result = await new ListDeadLetterOverviewQueryHandler(operations, connections, NullLogger<ListDeadLetterOverviewQueryHandler>.Instance).HandleAsync();

        result.Should().BeEmpty();
        await operations.DidNotReceive().ListDeadLetterTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
// Replace the existing Declares_the_expected_identity_and_connection_kind test's assertions with:

    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new KafkaPlugin();

        plugin.Id.Should().Be("kafka");
        plugin.ConnectionKind.Should().Be("kafka");
        plugin.DisplayName.Should().Be("Apache Kafka");
        plugin.ConnectionKindDisplayName.Should().Be("Apache Kafka");
        plugin.NavItems.Should().Contain(n => n.Title == "Topics" && n.Href == "/p/kafka/topics");
        plugin.NavItems.Should().Contain(n => n.Title == "Consumer Groups" && n.Href == "/p/kafka/consumer-groups");
        plugin.NavItems.Should().Contain(n => n.Title == "Dead-letter" && n.Href == "/p/kafka/dead-letter");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 5, ActionCount: 5));
    }
```

(The rest of `KafkaPluginTests.cs` is unchanged by this task — Task 5 adds its own new tests later.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "FullyQualifiedName~DeadLetter|FullyQualifiedName~KafkaPluginTests"`
Expected: FAIL to compile — `ListDeadLetterOverviewQueryHandler`/`DeadLetterOverviewEntry` don't exist yet, and `KafkaPluginTests`'s new assertions fail against the current nav items/`Contribution`.

- [ ] **Step 3: Write the handler**

```csharp
// src/SbConsole.Plugins.Kafka/DeadLetter/ListDeadLetterOverviewQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.DeadLetter;

public sealed record DeadLetterOverviewEntry(
    Guid ConnectionId, string ConnectionName, string DlqTopicName, string OriginalTopicName,
    int PartitionCount, long ApproximateMessageCount);

/// <summary>
/// Unlike every other handler in this plugin, this one takes no connectionId -- dead-letter
/// monitoring is a cross-connection "check everything at once" task, same shape Service Bus's own
/// dead-letter overview handler uses. No PluginResult wrapper: deliberately best-effort per
/// connection (one unreachable cluster shouldn't blank the whole page) -- see design spec §5.
/// </summary>
public sealed class ListDeadLetterOverviewQueryHandler(
    IKafkaOperations operations, IConnectionProvider connections, ILogger<ListDeadLetterOverviewQueryHandler> logger)
{
    public async Task<IReadOnlyList<DeadLetterOverviewEntry>> HandleAsync(CancellationToken ct = default)
    {
        var kafkaConnections = await connections.ListAsync("kafka", ct);
        var result = new List<DeadLetterOverviewEntry>();
        foreach (var connection in kafkaConnections)
        {
            try
            {
                var secret = await connections.GetSecretAsync(connection.Id, ct);
                if (secret is null)
                {
                    continue;
                }

                var topics = await operations.ListDeadLetterTopicsAsync(secret, ct);
                result.AddRange(topics.Select(t => new DeadLetterOverviewEntry(
                    connection.Id, connection.Name, t.DlqTopicName, t.OriginalTopicName, t.PartitionCount, t.ApproximateMessageCount)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listing dead-letter topics for connection {ConnectionId} failed; skipping it.", connection.Id);
            }
        }

        return result;
    }
}
```

- [ ] **Step 4: Wire `KafkaPlugin`**

```csharp
// src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
// Replace the NavItems property with:

    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
        new("Consumer Groups", "/p/kafka/consumer-groups"),
        new("Dead-letter", "/p/kafka/dead-letter"),
    ];
```

```csharp
// Replace the Contribution property and its comment with:

    // Topics: Create/Delete topic, Peek, Produce, Reset offset (5).
    // Pages: Topics, Peek, ConsumerGroups, ConsumerGroupDetail, DeadLetterOverview (5).
    public PluginContribution Contribution => new(PageCount: 5, ActionCount: 5);
```

```csharp
// Add this line inside ConfigureServices, after the three ConsumerGroups registrations:

        services.AddScoped<DeadLetter.ListDeadLetterOverviewQueryHandler>();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "FullyQualifiedName~DeadLetter|FullyQualifiedName~KafkaPluginTests"`
Expected: PASS.

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Kafka tests/SbConsole.Plugins.Kafka.Tests
git commit -m "$(cat <<'EOF'
feat(kafka): add dead-letter overview handler and register the nav item

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: `DeadLetterOverview.razor` page

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Pages/DeadLetterOverview.razor`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/DeadLetterOverviewPageTests.cs`

**Interfaces:**
- Consumes: `ListDeadLetterOverviewQueryHandler` (Task 2).
- Produces: a routable page at `/p/kafka/dead-letter`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/DeadLetterOverviewPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.DeadLetter;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class DeadLetterOverviewPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public DeadLetterOverviewPageTests()
    {
        var connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<ListDeadLetterOverviewQueryHandler>();
    }

    [Fact]
    public async Task Lists_dead_letter_topics_across_connections()
    {
        _operations.ListDeadLetterTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 3, 5, DateTimeOffset.UtcNow) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("kafka-dev");
        cut.Markup.Should().Contain("orders-dlq");
        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("5");
    }

    [Fact]
    public async Task No_dead_letter_topics_shows_an_honest_empty_state()
    {
        _operations.ListDeadLetterTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No dead-letter topics found");
    }

    [Fact]
    public async Task Peek_link_carries_connection_topic_and_partition_count()
    {
        _operations.ListDeadLetterTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<DeadLetterTopicSummary> { new("orders-dlq", "orders", 3, 5, DateTimeOffset.UtcNow) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.DeadLetterOverview>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".peek-dead-letter").GetAttribute("href").Should().Be(
            $"/p/kafka/topics/orders-dlq/peek?connectionId={_connectionId}&partitionCount=3");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter DeadLetterOverviewPageTests`
Expected: FAIL to compile — `SbConsole.Plugins.Kafka.Pages.DeadLetterOverview` does not exist.

- [ ] **Step 3: Write `DeadLetterOverview.razor`**

```razor
@page "/p/kafka/dead-letter"
@using SbConsole.Plugins.Kafka.Client
@using SbConsole.Plugins.Kafka.DeadLetter
@using SbConsole.Sdk
@inject ListDeadLetterOverviewQueryHandler ListHandler

<PageTitle>Dead-letter</PageTitle>
<h1>Dead-letter</h1>

@if (_loading)
{
    <MudProgressCircular Class="dead-letter-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
}
else if (_entries.Count == 0)
{
    <MudAlert Severity="Severity.Info">No dead-letter topics found across your Kafka connections.</MudAlert>
}
else
{
    <MudText Class="dead-letter-summary mud-text-secondary mb-2" Typo="Typo.body2">@_entries.Count @(_entries.Count == 1 ? "topic" : "topics")</MudText>

    <MudTable Items="_entries">
        <HeaderContent>
            <MudTh>Connection</MudTh>
            <MudTh>DLQ Topic</MudTh>
            <MudTh>Original Topic</MudTh>
            <MudTh>Messages (approx)</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.ConnectionName</MudTd>
            <MudTd Style="font-family:monospace">@context.DlqTopicName</MudTd>
            <MudTd Style="font-family:monospace">@context.OriginalTopicName</MudTd>
            <MudTd>@context.ApproximateMessageCount</MudTd>
            <MudTd>
                <MudButton Class="peek-dead-letter" Href="@PeekUrl(context)">Peek</MudButton>
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<DeadLetterOverviewEntry> _entries = [];
    private bool _loading;

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _entries = await ListHandler.HandleAsync();
        }
        finally
        {
            _loading = false;
        }
    }

    // Same PeekUrl shape Topics.razor's own PeekUrl builds -- a DLQ topic is peeked exactly like any
    // other topic, nothing on Peek.razor needs to know it's a DLQ topic (design spec §4).
    private static string PeekUrl(DeadLetterOverviewEntry entry) =>
        $"/p/kafka/topics/{Uri.EscapeDataString(entry.DlqTopicName)}/peek?connectionId={entry.ConnectionId}&partitionCount={entry.PartitionCount}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter DeadLetterOverviewPageTests`
Expected: PASS (3 tests).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Pages/DeadLetterOverview.razor tests/SbConsole.Plugins.Kafka.Tests/Pages/DeadLetterOverviewPageTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add the Dead-letter overview page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `Topics.razor` DLQ badge

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/Pages/Topics.razor`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/TopicsPageTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing later tasks depend on — this is a small, self-contained visual addition to an existing page.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/TopicsPageTests.cs
// Add this test class member (same file, same class as the existing Topics.razor tests):

    [Fact]
    public async Task A_DLQ_suffixed_topic_shows_the_DLQ_badge()
    {
        // ReplicationFactor 3 (not <= 1) so only the DLQ-badge branch can possibly fire, not the
        // low-replication one -- keeps this test unambiguous about which condition is being checked.
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders-dlq", 3, 3, 5) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("dlq-badge");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter A_DLQ_suffixed_topic_shows_the_DLQ_badge`
Expected: FAIL — `dlq-badge` never appears in the rendered markup (no such branch exists yet).

- [ ] **Step 3: Add the DLQ badge**

```razor
@* src/SbConsole.Plugins.Kafka/Pages/Topics.razor *@
@* Replace the existing badge if/else-if chain (inside the Name column's MudTd) with: *@
                @if (IsInternal(context.Name))
                {
                    <MudChip T="string" Size="Size.Small" Class="internal-badge ms-2">internal</MudChip>
                }
                else if (IsDlqTopic(context.Name))
                {
                    <MudChip T="string" Color="Color.Error" Size="Size.Small" Class="dlq-badge ms-2">DLQ</MudChip>
                }
                else if (context.ReplicationFactor <= 1)
                {
                    <MudChip T="string" Color="Color.Warning" Size="Size.Small" Class="low-replication-badge ms-2">RF @context.ReplicationFactor</MudChip>
                }
```

```csharp
@* Add this method next to the existing IsInternal method in the @code block: *@
    // Page-local, same as IsInternal above (not shared with ConfluentKafkaOperations.IsDlqTopic --
    // this is a one-line predicate, not worth a cross-namespace dependency for).
    private static bool IsDlqTopic(string name) => name.EndsWith("-dlq", StringComparison.Ordinal);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter TopicsPageTests`
Expected: PASS (all existing Topics page tests plus the new one).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Pages/Topics.razor tests/SbConsole.Plugins.Kafka.Tests/Pages/TopicsPageTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): flag DLQ-suffixed topics on the Topics page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Dashboard problems, nav badge, oldest dead-letter

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/KafkaPlugin.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations.ListDeadLetterTopicsAsync` (Task 1), `DeadLetterTopicSummary` (Task 1), `PluginDashboardProblem`/`OldestDeadLetterEntry` (`SbConsole.Sdk`).
- Produces: `KafkaPlugin.GetDashboardProblemsAsync` extended to include dead-letter problems alongside its existing consumer-group-lag problems; `GetNavBadgeAsync` extended with a `/p/kafka/dead-letter` branch; a new `GetOldestDeadLetterAsync` override (currently the SDK no-op default) — this is the last task in the plan before the mandatory live-broker verification (Task 6).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
// Add these test class members:

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void HasDeadLetterMessages_reflects_whether_the_topic_has_any_retained_messages(long count, bool expected)
    {
        var topic = new DeadLetterTopicSummary("orders-dlq", "orders", 1, count, null);

        KafkaPlugin.HasDeadLetterMessages(topic).Should().Be(expected);
    }

    [Fact]
    public void PickOldestDeadLetterTopic_returns_null_when_no_topic_has_a_timestamp()
    {
        var topics = new[] { new DeadLetterTopicSummary("a-dlq", "a", 1, 0, null) };

        KafkaPlugin.PickOldestDeadLetterTopic(topics).Should().BeNull();
    }

    [Fact]
    public void PickOldestDeadLetterTopic_returns_the_topic_with_the_earliest_timestamp()
    {
        var older = new DeadLetterTopicSummary("a-dlq", "a", 1, 3, DateTimeOffset.UtcNow.AddHours(-2));
        var newer = new DeadLetterTopicSummary("b-dlq", "b", 1, 1, DateTimeOffset.UtcNow.AddHours(-1));

        KafkaPlugin.PickOldestDeadLetterTopic([newer, older]).Should().Be(older);
    }
```

(Add `using SbConsole.Plugins.Kafka.Client;` to the file's `using` block if not already present — the file already has it, added in an earlier task for `ConsumerGroupSummary`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "HasDeadLetterMessages|PickOldestDeadLetterTopic"`
Expected: FAIL to compile — `KafkaPlugin.HasDeadLetterMessages`/`PickOldestDeadLetterTopic` don't exist yet.

- [ ] **Step 3: Extend `KafkaPlugin`**

```csharp
// src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
// Replace the existing GetDashboardProblemsAsync method with:

    public async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
        Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default)
    {
        var groups = await GetCachedConsumerGroupsAsync(connectionString, ct);
        var problems = groups
            .Where(HasHighLag)
            .Select(g => new PluginDashboardProblem(
                "Warning",
                $"Consumer group '{g.GroupId}' has high lag",
                $"{g.TotalLag:N0} messages behind",
                $"/p/kafka/consumer-groups/{Uri.EscapeDataString(g.GroupId)}?connectionId={connectionId}"))
            .ToList();

        var dlqTopics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
        problems.AddRange(dlqTopics
            .Where(HasDeadLetterMessages)
            .Select(t => new PluginDashboardProblem(
                "Warning",
                $"Dead-letter topic '{t.DlqTopicName}' has messages",
                $"{t.ApproximateMessageCount:N0} messages retained",
                "/p/kafka/dead-letter")));

        return problems;
    }

    // Extracted as a pure static function, same reasoning as HasHighLag above. Threshold is > 0, not
    // a large magnitude like HasHighLag's -- unlike consumer-group lag (some lag is normal under
    // load), any nonzero dead-letter count is inherently abnormal. See design spec §6.
    internal static bool HasDeadLetterMessages(DeadLetterTopicSummary topic) => topic.ApproximateMessageCount > 0;

    // Extracted as a pure static function so the "pick the minimum-timestamp topic" selection is
    // unit-testable without a real broker, same reasoning as HasHighLag/HasDeadLetterMessages.
    internal static DeadLetterTopicSummary? PickOldestDeadLetterTopic(IEnumerable<DeadLetterTopicSummary> topics) =>
        topics.Where(t => t.OldestMessageTimestamp is not null).OrderBy(t => t.OldestMessageTimestamp).FirstOrDefault();
```

```csharp
// Replace the existing ConsumerGroupsNavHref constant and GetNavBadgeAsync method with:

    private const string ConsumerGroupsNavHref = "/p/kafka/consumer-groups";
    private const string DeadLetterNavHref = "/p/kafka/dead-letter";

    public Task<int?> GetNavBadgeAsync(string navItemHref, string connectionString, CancellationToken ct = default) =>
        navItemHref switch
        {
            ConsumerGroupsNavHref => GetConsumerGroupBadgeAsync(connectionString, ct),
            DeadLetterNavHref => GetDeadLetterBadgeAsync(connectionString, ct),
            _ => Task.FromResult<int?>(null),
        };

    private static async Task<int?> GetDeadLetterBadgeAsync(string connectionString, CancellationToken ct)
    {
        var topics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
        var total = topics.Sum(t => t.ApproximateMessageCount);
        // null (never 0) when nothing is flagged -- same NavMenu.razor rendering rule
        // GetConsumerGroupBadgeAsync already follows.
        return total > 0 ? (int)total : null;
    }
```

```csharp
// Add this method at the end of the KafkaPlugin class, after GetConsumerGroupBadgeAsync:

    public async Task<OldestDeadLetterEntry?> GetOldestDeadLetterAsync(
        Guid connectionId, string connectionString, CancellationToken ct = default)
    {
        var topics = await new ConfluentKafkaOperations().ListDeadLetterTopicsAsync(connectionString, ct);
        var oldest = PickOldestDeadLetterTopic(topics);

        // ResourceName/DeadLetterCount are the DLQ topic's own name and count -- matching how
        // ServiceBusPlugin's implementation reports a single queue's name and that queue's own
        // count, not an aggregate across every dead-lettered resource. See design spec §6.
        return oldest is null
            ? null
            : new OldestDeadLetterEntry(oldest.DlqTopicName, oldest.OldestMessageTimestamp!.Value, oldest.ApproximateMessageCount);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter "HasDeadLetterMessages|PickOldestDeadLetterTopic"`
Expected: PASS (5 cases).

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/KafkaPlugin.cs tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): surface dead-letter topics as a dashboard problem, nav badge, and oldest-message entry

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Mandatory live-broker verification (no code changes)

**This task has no code deliverable — it is a required manual verification pass, not optional polish.** The consumer-groups plan's full unit-test suite passed 393/393 while shipping a real bug that only manual testing against a real broker caught (`ResetConsumerGroupOffsetAsync` passing sentinel offset values to an API that rejects them — every unit test necessarily substitutes `IKafkaOperations`, so none of them exercise the real `Confluent.Kafka` call shape). This plan's riskiest new real-broker code is `ListDeadLetterTopicsAsync`'s repeated `Assign`/`Consume`/`Unassign` cycle on one reused consumer instance across every DLQ topic's partitions (Task 1, §3 of the design) — a pattern nothing else in this plugin does; every other `Assign` call in this codebase happens once per consumer instance, never repeated.

**Prerequisites:** a reachable Kafka broker and a saved Kafka connection in the running app pointing at it (if you don't have one, the consumer-groups plan's own live-verification pass — see its plan file's execution history — set one up locally; reuse that broker/connection if it's still available, or stand up a new one).

- [ ] **Step 1: Produce test messages to at least two differently-named DLQ topics**

Using the app's own "Produce" button on the Topics page (or any Kafka client), produce at least one message each to two topics named `{something}-dlq` — e.g. `orders-dlq` and `payments-dlq`. Use topics with more than one partition if your broker/topic setup allows it, to exercise the per-partition loop.

- [ ] **Step 2: Load the Topics page and confirm the DLQ badge**

Navigate to `/p/kafka/topics`. Confirm `orders-dlq` and `payments-dlq` each show the red "DLQ" chip next to their name (Task 4), and that this chip does NOT also show on non-DLQ topics.

- [ ] **Step 3: Load the Dead-letter overview page and confirm the data**

Navigate to `/p/kafka/dead-letter`. Confirm:
- Both DLQ topics appear, each with its correct `Original Topic` (the DLQ name with `-dlq` stripped).
- `Messages (approx)` matches what you produced.
- Neither topic's count or original-topic derivation was corrupted by the other's `Assign`/`Unassign` cycle (this is the specific risk Task 1's implementation carries — confirm the two topics' rows are independently correct, not swapped or merged).

- [ ] **Step 4: Peek a DLQ topic from the overview page**

Click **Peek** on one of the DLQ topic rows. Confirm it navigates to the existing Peek page (`/p/kafka/topics/{name}/peek`) with the correct topic pre-selected and successfully fetches the message(s) you produced.

- [ ] **Step 5: Confirm the dashboard problem and nav badge**

Navigate to the Dashboard (`/`). Confirm a "Needs attention" entry appears for at least one of the DLQ topics, with a working link back to `/p/kafka/dead-letter`. Wait up to 60 seconds (or reload) and confirm the "Dead-letter" nav item shows a numeric badge matching the total retained count across both DLQ topics.

- [ ] **Step 6: Record the outcome**

If everything in Steps 2-5 works as described: this task is complete, note so in your final report (no commit needed — nothing in this task changes source code). If anything is wrong, fix it in the relevant task's file (most likely Task 1's `ListDeadLetterTopicsAsync` or Task 3's `DeadLetterOverview.razor`), re-run the full test suite (`dotnet build SbConsole.slnx -warnaserror && dotnet test`), commit the fix with a `fix(kafka): ...` message, and repeat Steps 1-5 until they pass.
