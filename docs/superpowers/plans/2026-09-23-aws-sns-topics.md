# AWS SNS Topics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an SNS "Topics" section to the AWS plugin — topics, subscriptions (including `PendingConfirmation`), publish with a fan-out preview, and per-topic CloudWatch delivery-failure counts — sibling to the shipped "Queues" section.

**Architecture:** New `ISnsOperations`/`SnsOperations` mirroring `ISqsOperations`/`SqsOperations` exactly (every method takes the connection secret, built via the same `AwsConfigParser`/`AwsCredentialsFactory` pipeline). New `Topics`/`Subscriptions` handler folders mirroring `Queues`/`Messages`. Two new routed pages (`Topics.razor`, `TopicDetail.razor`) plus three dialogs, following the exact page/dialog/handler conventions `Queues.razor` and its dialogs already establish.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor, `AWSSDK.SimpleNotificationService`, `AWSSDK.CloudWatch`, xUnit + FluentAssertions + NSubstitute + bUnit.

## Global Constraints

- `dotnet build -warnaserror` and `dotnet test` must be green before every commit.
- Spec: `docs/superpowers/specs/2026-09-23-aws-sns-topics-design.md`.
- Same connection kind as Queues (`"aws"`) — no `AwsConfigParser`/secret-format changes.
- Every plugin operation takes the connection secret as a parameter (no pre-bound instance) — same rule `ISqsOperations` follows.
- Destructive actions (Delete Topic) go through `IConfirmationService` with `IsProd` sourced server-side from `IConnectionProvider`, never a client parameter — same rule every other destructive action in this codebase follows.
- Real AWS SDK exceptions never reach the UI/audit log directly — always routed through `FriendlyAwsError`.
- **This plan does NOT touch `SqsOperations.TestConnectionAsync`'s `Checks` list.** The richer `ConnectionTestResult` (`Identity`/`Checks`, `ConnectionCheck`/`ConnectionCheckStatus`) only exists on the concurrent, not-yet-merged `worktree-feature+connections-page-redesign` branch — it is not present on `main`, which is what this plan targets. Once that branch merges, adding a second `"Topics visible"` check to `SqsOperations.TestConnectionAsync` (a cheap `ListTopics` probe) is a small follow-up, not scheduled as a task here.
- Partial-failure resilience: a single topic's failed per-row call (subscription count, delivery-failure count) degrades that row, never blanks the whole page — same rule `SqsOperations.ListQueuesAsync` already follows for `GetQueueAttributes`.

---

## Task 1: SNS client foundation — models, `ISnsOperations`, list/create/delete topic

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Client/TopicSummary.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/CreateTopicRequest.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/SubscriptionSummary.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/SubscribeRequest.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/SnsPublishRequest.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/ISnsOperations.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`
- Modify: `src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs`
- Modify: `src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs`

**Interfaces:**
- Consumes: `AwsConfigParser.Parse` (existing), `AwsCredentialsFactory.BuildConfig`/`BuildCredentials` (existing).
- Produces: `ISnsOperations` (full interface below — later tasks implement `ListSubscriptionsAsync`/`SubscribeAsync`/`UnsubscribeAsync`/`PublishAsync`/`GetDeliveryFailureCountAsync`, all declared here now so every task's brief can reference the complete interface), `TopicSummary`, `CreateTopicRequest`, `SubscriptionSummary`, `SubscribeRequest`, `SnsPublishRequest`, `SnsOperations.TopicNameFromArn(string)` (`internal static string`).

- [ ] **Step 1: Write the failing test**

Create `tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class SnsOperationsTests
{
    [Fact]
    public void TopicNameFromArn_returns_the_last_segment()
    {
        SnsOperations.TopicNameFromArn("arn:aws:sns:eu-west-1:123456789012:order-events-topic")
            .Should().Be("order-events-topic");
    }

    [Fact]
    public async Task ListTopicsAsync_against_an_unreachable_endpoint_throws_a_friendly_exception()
    {
        var ops = new SnsOperations();

        var act = () => ops.ListTopicsAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        // Real network calls can't be unit-tested without a broker/emulator (same "light coverage
        // by necessity" limitation the SQS plan's SqsOperations accepted) -- this only pins that a
        // connection failure surfaces as SOME exception, not a hang or a silently-empty list.
        await act.Should().ThrowAsync<Exception>();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SnsOperationsTests"`
Expected: build error — `SnsOperations`/`ISnsOperations` don't exist yet.

- [ ] **Step 3: Add the package references**

Add to `src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj`'s existing `PackageReference` `ItemGroup` (alongside `AWSSDK.SQS`/`AWSSDK.SecurityToken`/`MudBlazor`):

```xml
    <PackageReference Include="AWSSDK.SimpleNotificationService" Version="3.7.400.62" />
    <PackageReference Include="AWSSDK.CloudWatch" Version="3.7.400.62" />
```

Confirm the exact latest `3.7.400.x`/`3.7.401.x` patch version available at implementation time (matching the existing `AWSSDK.SQS`/`AWSSDK.SecurityToken` versions' vintage) — if `3.7.400.62` isn't resolvable, use whatever patch version `dotnet add package AWSSDK.SimpleNotificationService`/`AWSSDK.CloudWatch` resolves to instead of forcing a version that doesn't exist.

- [ ] **Step 4: Create the model files**

`src/SbConsole.Plugins.Aws/Client/TopicSummary.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record TopicSummary(
    string Name, string TopicArn, bool IsFifo, int SubscriptionCount, int PendingConfirmationCount, bool IsKmsEncrypted);
```

`src/SbConsole.Plugins.Aws/Client/CreateTopicRequest.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record CreateTopicRequest(string Name, bool IsFifo, string? KmsKeyId, bool? ContentBasedDeduplication);
```

`src/SbConsole.Plugins.Aws/Client/SubscriptionSummary.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record SubscriptionSummary(
    string SubscriptionArn, string Protocol, string Endpoint, bool IsPending,
    bool? RawMessageDelivery, string? FilterPolicyJson);
```

`src/SbConsole.Plugins.Aws/Client/SubscribeRequest.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record SubscribeRequest(string TopicArn, string Protocol, string Endpoint, bool RawMessageDelivery);
```

`src/SbConsole.Plugins.Aws/Client/SnsPublishRequest.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record SnsPublishRequest(
    string? Subject, string Message, IReadOnlyDictionary<string, string>? MessageAttributes,
    string? MessageGroupId, string? MessageDeduplicationId);
```

- [ ] **Step 5: Create `ISnsOperations`**

`src/SbConsole.Plugins.Aws/Client/ISnsOperations.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The seam between the plugin's handlers and the real AWSSDK.SimpleNotificationService/
/// AWSSDK.CloudWatch clients. Every method takes the connection secret as a parameter -- mirrors
/// SbConsole.Plugins.Aws.Client.ISqsOperations exactly.
/// </summary>
public interface ISnsOperations
{
    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string secret, CancellationToken ct = default);
    Task<string> CreateTopicAsync(string secret, CreateTopicRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteTopicAsync(string secret, string topicArn, CancellationToken ct = default);

    Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default);
    Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default);
    /// <summary>Mutating, not Destructive -- reversible by subscribing again.</summary>
    Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default);

    Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default);

    /// <summary>Sum of NumberOfNotificationsFailed over the trailing 24h, via CloudWatch GetMetricStatistics. Zero datapoints means zero failures, not an error.</summary>
    Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default);
}
```

- [ ] **Step 6: Implement `SnsOperations`'s topic methods**

`src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`:

```csharp
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The only real implementation of ISnsOperations. Mirrors SqsOperations' construction pattern
/// exactly: a fresh client per call, built from the parsed secret, with ServiceURL/UseHttp
/// forwarded from the shared config so a custom LocalStack endpoint redirects SNS too.
/// </summary>
public sealed class SnsOperations : ISnsOperations
{
    private static AmazonSimpleNotificationServiceClient BuildSnsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var snsConfig = new AmazonSimpleNotificationServiceConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSimpleNotificationServiceClient(snsConfig) : new AmazonSimpleNotificationServiceClient(credentials, snsConfig);
    }

    internal static string TopicNameFromArn(string topicArn) => topicArn[(topicArn.LastIndexOf(':') + 1)..];

    public async Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string secret, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var topicArns = new List<string>();
        string? nextToken = null;
        do
        {
            var page = await sns.ListTopicsAsync(new ListTopicsRequest { NextToken = nextToken }, ct);
            topicArns.AddRange(page.Topics.Select(t => t.TopicArn));
            nextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var summaries = new List<TopicSummary>();
        foreach (var topicArn in topicArns)
        {
            var name = TopicNameFromArn(topicArn);
            var isFifo = name.EndsWith(".fifo", StringComparison.Ordinal);
            var subCount = 0;
            var pendingCount = 0;
            var isKms = false;
            try
            {
                var attrs = await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }, ct);
                subCount = int.TryParse(attrs.Attributes.GetValueOrDefault("SubscriptionsConfirmed"), out var confirmed) ? confirmed : 0;
                pendingCount = int.TryParse(attrs.Attributes.GetValueOrDefault("SubscriptionsPending"), out var pending) ? pending : 0;
                isKms = attrs.Attributes.ContainsKey("KmsMasterKeyId");
            }
            catch (Exception) when (ct.IsCancellationRequested is false)
            {
                // Left at zero/false -- the caller renders this row's counts as unavailable rather
                // than blanking the whole page (design spec §2's partial-failure rule).
            }

            summaries.Add(new TopicSummary(name, topicArn, isFifo, subCount, pendingCount, isKms));
        }

        return summaries;
    }

    public async Task<string> CreateTopicAsync(string secret, CreateTopicRequest request, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var name = request.IsFifo && !request.Name.EndsWith(".fifo", StringComparison.Ordinal)
            ? $"{request.Name}.fifo"
            : request.Name;

        var attributes = new Dictionary<string, string>();
        if (request.IsFifo)
        {
            attributes["FifoTopic"] = "true";
            if (request.ContentBasedDeduplication is { } dedup)
            {
                attributes["ContentBasedDeduplication"] = dedup.ToString().ToLowerInvariant();
            }
        }

        if (request.KmsKeyId is { } kmsKeyId)
        {
            attributes["KmsMasterKeyId"] = kmsKeyId;
        }

        var response = await sns.CreateTopicAsync(new CreateTopicRequest_ { Name = name, Attributes = attributes }, ct);
        return response.TopicArn;
    }

    public async Task DeleteTopicAsync(string secret, string topicArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        await sns.DeleteTopicAsync(topicArn, ct);
    }

    // Placeholder throws for the remaining interface members -- implemented in Tasks 4, 5, 6, 7.
    // These throws exist only so the class compiles as a complete ISnsOperations implementation;
    // nothing calls them until those tasks wire up their own handlers/pages.
    public Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 4.");
    public Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 5.");
    public Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 5.");
    public Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 6.");
    public Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 7.");
}
```

Note: `Amazon.SimpleNotificationService.Model.CreateTopicRequest` collides by name with this
project's own `SbConsole.Plugins.Aws.Client.CreateTopicRequest` — the code above aliases the SDK's
own request type as `CreateTopicRequest_` in the `sns.CreateTopicAsync(...)` call; if the compiler
rejects this alias syntax, add an explicit `using CreateTopicRequest_ = Amazon.SimpleNotificationService.Model.CreateTopicRequest;`
at the top of the file instead (the same disambiguation `SqsOperations.cs` avoids by always writing
`Amazon.SQS.Model.CreateQueueRequest` in full at the one call site that needs it — either approach
is fine, whichever compiles).

- [ ] **Step 7: Add SNS exception mappings to `FriendlyAwsError`**

Add `using Amazon.SimpleNotificationService;` to the top of `src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs`, and add these two arms to the `switch` in `From`, before the `_ => FriendlyError.From(ex)` fallback:

```csharp
        AmazonSimpleNotificationServiceException { ErrorCode: "AuthorizationError" } => "Access denied — check IAM permissions",
        AmazonSimpleNotificationServiceException { ErrorCode: "NotFound" } => "Topic or subscription not found",
```

Confirm these exact `ErrorCode` string values against the installed `AWSSDK.SimpleNotificationService` package at implementation time (same "confirmed, not assumed" bar every other mapping in this file holds itself to) — if the real error codes differ, use the real ones.

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SnsOperationsTests"`
Expected: PASS.

- [ ] **Step 9: Run the full AWS plugin test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Client tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj
git commit -m "feat(aws): add ISnsOperations/SnsOperations foundation (list/create/delete topic)"
```

---

## Task 2: Topics list page

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Topics/ListTopicsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/Topics.razor`
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Topics/ListTopicsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs`

**Interfaces:**
- Consumes: `ISnsOperations.ListTopicsAsync` (Task 1), `PluginResult<T>`/`PluginResult` (existing), `IConnectionProvider` (existing SDK), `GetConnectionEchoQueryHandler` (existing, reused as-is).
- Produces: `ListTopicsQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default)` returning `PluginResult<IReadOnlyList<TopicSummary>>`. `Topics.razor` at `/p/aws/topics`. Task 3 adds Create/Delete actions to this same page; Task 4 adds the "Subs" action.

- [ ] **Step 1: Write the failing test**

Create `tests/SbConsole.Plugins.Aws.Tests/Topics/ListTopicsQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class ListTopicsQueryHandlerTests
{
    [Fact]
    public async Task Returns_topics_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var topics = new List<TopicSummary> { new("order-events-topic", "arn:aws:sns:us-east-1:1:order-events-topic", false, 3, 0, false) };
        operations.ListTopicsAsync("mode=access-keys;region=us-east-1", Arg.Any<CancellationToken>()).Returns(topics);
        var handler = new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(topics);
    }

    [Fact]
    public async Task Unknown_connection_fails()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var handler = new ListTopicsQueryHandler(Substitute.For<ISnsOperations>(), connections, NullLogger<ListTopicsQueryHandler>.Instance);

        var result = await handler.HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ListTopicsQueryHandlerTests"`
Expected: build error — `ListTopicsQueryHandler` doesn't exist yet.

- [ ] **Step 3: Implement the handler**

`src/SbConsole.Plugins.Aws/Topics/ListTopicsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed class ListTopicsQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<ListTopicsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<TopicSummary>>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<TopicSummary>>.Fail("Connection not found.");
            }

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

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ListTopicsQueryHandlerTests"`
Expected: PASS.

- [ ] **Step 5: Wire the nav item and register the handler**

In `src/SbConsole.Plugins.Aws/AwsPlugin.cs`, change `NavItems` to add a second entry, and add the registration + one more `using`:

```csharp
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/aws/queues"),
        new("Topics", "/p/aws/topics"),
    ];
```

Add `using SbConsole.Plugins.Aws.Topics;` to the top of the file, and add this line inside `ConfigureServices`, alongside the existing `services.AddSingleton<ISqsOperations, SqsOperations>();` line:

```csharp
        services.AddSingleton<ISnsOperations, SnsOperations>();
        services.AddScoped<ListTopicsQueryHandler>();
```

Update the `// Queues: ...` comment above `Contribution` to also count Topics, and bump `PageCount`/`ActionCount`:

```csharp
    // Queues: Create/Delete/Purge queue, Receive, Delete message, Release message, Send, Redrive (8).
    // Topics: Create/Delete topic, Subscribe/Unsubscribe, Publish (5).
    // Pages: Queues, Receive, Topics, TopicDetail (4).
    public PluginContribution Contribution => new(PageCount: 4, ActionCount: 13);
```

- [ ] **Step 6: Write the page**

Create `src/SbConsole.Plugins.Aws/Pages/Topics.razor`:

```razor
@page "/p/aws/topics"
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Topics
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject ListTopicsQueryHandler ListHandler
@inject SbConsole.Plugins.Aws.Queues.GetConnectionEchoQueryHandler EchoHandler
@inject ISnackbar Snackbar

<PageTitle>Topics</PageTitle>
<h1>Topics</h1>

@if (_connections.Count == 0)
{
    <MudAlert Severity="Severity.Info">No connections yet. Add an AWS SQS/SNS connection to get started.</MudAlert>
}
else
{
    <div class="d-flex align-center flex-wrap gap-4 my-4">
        <div class="d-flex align-center gap-2">
            <MudText Typo="Typo.overline" Class="mud-text-secondary">Account</MudText>
            <MudMenu Class="connection-menu">
                <ActivatorContent>
                    <div class="d-flex align-center gap-2" style="cursor:pointer">
                        <MudText Typo="Typo.subtitle1" Style="font-family:monospace">@SelectedConnection?.Name</MudText>
                        @if (SelectedConnection?.IsProd == true)
                        {
                            <MudChip T="string" Color="Color.Warning" Size="Size.Small">prod</MudChip>
                        }
                        <MudIcon Icon="@Icons.Material.Filled.ArrowDropDown" Size="Size.Small" />
                    </div>
                </ActivatorContent>
                <ChildContent>
                    @foreach (var connection in _connections)
                    {
                        <MudMenuItem Class="connection-option" OnClick="@(() => OnConnectionChanged(connection.Id))">@connection.Name</MudMenuItem>
                    }
                </ChildContent>
            </MudMenu>
        </div>
        <MudSpacer />
        @if (_loading)
        {
            <MudProgressCircular Class="topics-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudText Typo="Typo.caption" Class="connection-echo mud-text-secondary">@_connectionEcho</MudText>
    <MudText Class="topics-summary mud-text-secondary mt-1 mb-2" Typo="Typo.body2">@_topics.Count @(_topics.Count == 1 ? "topic" : "topics")</MudText>

    <MudTable Items="_topics">
        <HeaderContent>
            <MudTh>Topic</MudTh>
            <MudTh>Type</MudTh>
            <MudTh Style="text-align:right">Subs</MudTh>
            <MudTh Style="text-align:right">Pending</MudTh>
            <MudTh>Flags</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace">@context.Name</MudTd>
            <MudTd>@(context.IsFifo ? "FIFO" : "Standard")</MudTd>
            <MudTd Class="sub-count" Style="text-align:right;font-family:monospace">@context.SubscriptionCount</MudTd>
            <MudTd Class="pending-count" Style="text-align:right;font-family:monospace">@context.PendingConfirmationCount</MudTd>
            <MudTd>
                @if (context.SubscriptionCount == 0)
                {
                    <MudChip T="string" Class="no-subs-flag" Color="Color.Warning" Size="Size.Small" Variant="Variant.Outlined">no subscriptions</MudChip>
                }
                @if (context.IsKmsEncrypted)
                {
                    <MudChip T="string" Class="kms-flag" Size="Size.Small" Variant="Variant.Outlined">KMS</MudChip>
                }
            </MudTd>
            <MudTd>
                <MudButton Class="subs-action" Href="@($"/p/aws/topics/{Uri.EscapeDataString(context.TopicArn)}?connectionId={_selectedConnectionId}")">Subs</MudButton>
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private IReadOnlyList<TopicSummary> _topics = [];
    private Guid _selectedConnectionId;
    private bool _loading;
    private string _connectionEcho = "";

    private ConnectionInfo? SelectedConnection => _connections.FirstOrDefault(c => c.Id == _selectedConnectionId);

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("aws");
        if (_connections.Count > 0)
        {
            _selectedConnectionId = _connections[0].Id;
            await LoadTopicsAsync();
            await LoadEchoAsync();
        }
    }

    private async Task OnConnectionChanged(Guid connectionId)
    {
        _selectedConnectionId = connectionId;
        await LoadTopicsAsync();
        await LoadEchoAsync();
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

    private async Task LoadEchoAsync()
    {
        var result = await EchoHandler.HandleAsync(_selectedConnectionId);
        _connectionEcho = result.IsSuccess ? result.Value! : "";
    }
}
```

- [ ] **Step 7: Write the page test**

Create `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Pages;
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

        var cut = Render<Topics>();

        cut.Markup.Should().Contain("order-events-topic");
        cut.Find("td.sub-count").TextContent.Should().Be("3");
    }

    [Fact]
    public void Shows_a_flag_for_a_topic_with_no_subscriptions()
    {
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("invoice-issued-topic", "arn:aws:sns:us-east-1:1:invoice-issued-topic", false, 0, 0, false)]);

        var cut = Render<Topics>();

        cut.Find("span.no-subs-flag").Should().NotBeNull();
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~TopicsPageTests|FullyQualifiedName~ListTopicsQueryHandlerTests"`
Expected: PASS.

- [ ] **Step 9: Run the full AWS plugin test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Topics/ListTopicsQueryHandler.cs src/SbConsole.Plugins.Aws/Pages/Topics.razor src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/Topics/ListTopicsQueryHandlerTests.cs tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs
git commit -m "feat(aws): add Topics list page"
```

---

## Task 3: Create/Delete topic

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Topics/CreateTopicCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Topics/DeleteTopicCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/CreateTopicDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Topics.razor`
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Topics/CreateTopicCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Topics/DeleteTopicCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs` (append)

**Interfaces:**
- Consumes: `ISnsOperations.CreateTopicAsync`/`DeleteTopicAsync` (Task 1), `IAuditScope`, `IConfirmationService` (existing SDK).
- Produces: `CreateTopicCommandHandler.HandleAsync(CreateTopicCommand)` returning `PluginResult<string>`; `DeleteTopicCommandHandler.HandleAsync(DeleteTopicCommand)` returning `PluginResult`. `CreateTopicDialog.razor` (Standard/FIFO toggle, mirrors `CreateQueueDialog.razor`).

- [ ] **Step 1: Write the failing tests**

Create `tests/SbConsole.Plugins.Aws.Tests/Topics/CreateTopicCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class CreateTopicCommandHandlerTests
{
    [Fact]
    public async Task Creates_and_audits_as_mutating()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var request = new CreateTopicRequest("order-events-topic", false, null, null);
        operations.CreateTopicAsync("mode=access-keys;region=us-east-1", request, Arg.Any<CancellationToken>())
            .Returns("arn:aws:sns:us-east-1:1:order-events-topic");
        var handler = new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CreateTopicCommand(connectionId, "aws-dev", request));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("arn:aws:sns:us-east-1:1:order-events-topic");
        await audit.Received(1).RecordAsync("aws.topic.create", "aws-dev/order-events-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_fails()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var handler = new CreateTopicCommandHandler(Substitute.For<ISnsOperations>(), connections, Substitute.For<IAuditScope>(), NullLogger<CreateTopicCommandHandler>.Instance);

        var result = await handler.HandleAsync(new CreateTopicCommand(Guid.NewGuid(), "aws-dev", new CreateTopicRequest("x", false, null, null)));

        result.IsSuccess.Should().BeFalse();
    }
}
```

Create `tests/SbConsole.Plugins.Aws.Tests/Topics/DeleteTopicCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class DeleteTopicCommandHandlerTests
{
    [Fact]
    public async Task Deletes_and_audits_as_destructive()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var handler = new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteTopicCommand(connectionId, "aws-dev", "arn:aws:sns:us-east-1:1:order-events-topic", "order-events-topic"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteTopicAsync("mode=access-keys;region=us-east-1", "arn:aws:sns:us-east-1:1:order-events-topic", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.topic.delete", "aws-dev/order-events-topic", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~CreateTopicCommandHandlerTests|FullyQualifiedName~DeleteTopicCommandHandlerTests"`
Expected: build error — the handlers don't exist yet.

- [ ] **Step 3: Implement the handlers**

`src/SbConsole.Plugins.Aws/Topics/CreateTopicCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed record CreateTopicCommand(Guid ConnectionId, string ConnectionName, CreateTopicRequest Request);

public sealed class CreateTopicCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateTopicCommandHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(CreateTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Request.Name}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var topicArn = await operations.CreateTopicAsync(secret, cmd.Request, ct);
            await audit.RecordAsync("aws.topic.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(topicArn);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.topic.create", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
```

`src/SbConsole.Plugins.Aws/Topics/DeleteTopicCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed record DeleteTopicCommand(Guid ConnectionId, string ConnectionName, string TopicArn, string TopicName);

public sealed class DeleteTopicCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.DeleteTopicAsync(secret, cmd.TopicArn, ct);
            await audit.RecordAsync("aws.topic.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.topic.delete", target, ActionRisk.Destructive, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Register the handlers**

Add to `AwsPlugin.ConfigureServices`, alongside the `ListTopicsQueryHandler` line added in Task 2:

```csharp
        services.AddScoped<CreateTopicCommandHandler>();
        services.AddScoped<DeleteTopicCommandHandler>();
```

Add `using SbConsole.Plugins.Aws.Client;` is already present; no new `using` needed beyond what Task 2 added.

- [ ] **Step 5: Write the dialog**

Create `src/SbConsole.Plugins.Aws/Pages/CreateTopicDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Topics
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="topic-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudText Typo="Typo.caption" Class="mud-text-secondary">@(_isFifo ? $"{_name}.fifo" : _name) — SNS requires the .fifo suffix on a FIFO topic; it is added for you.</MudText>
        <MudSwitch T="bool" Class="fifo-toggle" @bind-Value="_isFifo" Label="FIFO" Color="Color.Primary" />
        @if (_isFifo)
        {
            <MudSwitch T="bool" Class="content-based-dedup-toggle" @bind-Value="_contentBasedDeduplication" Label="Content-based deduplication" Color="Color.Primary" />
        }
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
    private bool _isFifo;
    private bool _contentBasedDeduplication;
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var request = new CreateTopicRequest(_name, _isFifo, null, _isFifo ? _contentBasedDeduplication : null);
            var result = await CreateHandler.HandleAsync(new CreateTopicCommand(ConnectionId, ConnectionName, request));
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

- [ ] **Step 6: Wire the dialog and Delete action into `Topics.razor`**

Add `@inject IConfirmationService Confirmation` and `@inject IDialogService DialogService` to the top of `src/SbConsole.Plugins.Aws/Pages/Topics.razor`.

Add a "+ Create topic" button and a busy-tracking field, in the same header `<div>` that already has `<MudSpacer />`:

```razor
        <MudButton Class="create-topic-action" Color="Color.Primary" Variant="Variant.Outlined" OnClick="OpenCreate">+ Create topic</MudButton>
```

Add a Delete button to the `Actions` `MudTd` in `RowTemplate`, alongside the existing `Subs` button:

```razor
                <MudButton Class="delete-topic" Color="Color.Error" Disabled="@(_deletingTopic == context.TopicArn)" OnClick="@(() => DeleteAsync(context.TopicArn, context.Name))">Delete</MudButton>
                @if (_deletingTopic == context.TopicArn)
                {
                    <MudProgressCircular Class="delete-topic-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                }
```

Add these fields and methods to the `@code` block:

```csharp
    private string? _deletingTopic;

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

    private async Task DeleteAsync(string topicArn, string topicName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", topicName, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        _deletingTopic = topicArn;
        try
        {
            var result = await DeleteHandler.HandleAsync(new DeleteTopicCommand(connection.Id, connection.Name, topicArn, topicName));
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
```

Add `@inject DeleteTopicCommandHandler DeleteHandler` to the top of the file (alongside `ListHandler`).

- [ ] **Step 7: Append page tests**

Append to `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs`, inside the existing test class (also add `Services.AddSingleton<CreateTopicCommandHandler>();`, `Services.AddSingleton<DeleteTopicCommandHandler>();`, and `Services.AddSingleton(Substitute.For<IConfirmationService>());` to the constructor, and `using SbConsole.Plugins.Aws.Topics;` at the top if not already present from Task 2's imports):

```csharp
    [Fact]
    public async Task Delete_calls_the_handler_only_when_confirmation_service_returns_true()
    {
        var confirmation = Substitute.For<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "order-events-topic", false, null, Arg.Any<CancellationToken>()).Returns(true);
        Services.AddSingleton(confirmation);
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("order-events-topic", "arn:aws:sns:us-east-1:1:order-events-topic", false, 0, 0, false)]);

        var cut = Render<Topics>();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(50);

        await _snsOperations.Received(1).DeleteTopicAsync("mode=access-keys;region=us-east-1", "arn:aws:sns:us-east-1:1:order-events-topic", Arg.Any<CancellationToken>());
    }
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~CreateTopicCommandHandlerTests|FullyQualifiedName~DeleteTopicCommandHandlerTests|FullyQualifiedName~TopicsPageTests"`
Expected: PASS.

- [ ] **Step 9: Run the full AWS plugin test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Topics src/SbConsole.Plugins.Aws/Pages/CreateTopicDialog.razor src/SbConsole.Plugins.Aws/Pages/Topics.razor src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/Topics tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs
git commit -m "feat(aws): add create/delete topic"
```

---

## Task 4: Subscriptions list + Topic detail page

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Subscriptions/ListSubscriptionsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor`
- Modify: `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs` (append)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Subscriptions/ListSubscriptionsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicDetailPageTests.cs`

**Interfaces:**
- Consumes: `ISnsOperations.ListSubscriptionsAsync` (implemented here), `SubscriptionSummary` (Task 1).
- Produces: `SnsOperations.ListSubscriptionsAsync` (real implementation), `SnsOperations.ClassifySubscription` (`internal static SubscriptionSummary`, a pure function mapping an SDK `Subscription`+its attributes to the record — this is what Task 5's Resend logic and this task's state-badge rendering both key off of), `ListSubscriptionsQueryHandler.HandleAsync(Guid connectionId, string topicArn)` returning `PluginResult<IReadOnlyList<SubscriptionSummary>>`. `TopicDetail.razor` at `/p/aws/topics/{topicArn}`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void ClassifySubscription_flags_the_literal_PendingConfirmation_arn_as_pending()
    {
        var summary = SnsOperations.ClassifySubscription("PendingConfirmation", "https", "https://ops.example.com", new Dictionary<string, string>());

        summary.IsPending.Should().BeTrue();
        summary.SubscriptionArn.Should().Be("PendingConfirmation");
    }

    [Fact]
    public void ClassifySubscription_reads_raw_delivery_and_filter_policy_from_attributes()
    {
        var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true", ["FilterPolicy"] = """{"region":["uk"]}""" };

        var summary = SnsOperations.ClassifySubscription("arn:aws:sns:us-east-1:1:topic:sub-id", "sqs", "shipment-updates", attributes);

        summary.IsPending.Should().BeFalse();
        summary.RawMessageDelivery.Should().BeTrue();
        summary.FilterPolicyJson.Should().Be("""{"region":["uk"]}""");
    }

    [Fact]
    public void ClassifySubscription_defaults_raw_delivery_and_filter_policy_to_null_when_absent()
    {
        var summary = SnsOperations.ClassifySubscription("arn:aws:sns:us-east-1:1:topic:sub-id", "email", "ops@example.com", new Dictionary<string, string>());

        summary.RawMessageDelivery.Should().BeNull();
        summary.FilterPolicyJson.Should().BeNull();
    }
```

Create `tests/SbConsole.Plugins.Aws.Tests/Subscriptions/ListSubscriptionsQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class ListSubscriptionsQueryHandlerTests
{
    [Fact]
    public async Task Returns_subscriptions_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var subs = new List<SubscriptionSummary> { new("arn:sub-1", "sqs", "shipment-updates", false, false, null) };
        operations.ListSubscriptionsAsync("mode=access-keys;region=us-east-1", "arn:topic", Arg.Any<CancellationToken>()).Returns(subs);
        var handler = new ListSubscriptionsQueryHandler(operations, connections, NullLogger<ListSubscriptionsQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, "arn:topic");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(subs);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ClassifySubscription|FullyQualifiedName~ListSubscriptionsQueryHandlerTests"`
Expected: build error — `SnsOperations.ClassifySubscription`/`ListSubscriptionsQueryHandler` don't exist yet.

- [ ] **Step 3: Implement `ClassifySubscription` and `ListSubscriptionsAsync`**

In `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`, replace the `ListSubscriptionsAsync` placeholder throw with:

```csharp
    public async Task<IReadOnlyList<SubscriptionSummary>> ListSubscriptionsAsync(string secret, string topicArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var subscriptions = new List<Subscription>();
        string? nextToken = null;
        do
        {
            var page = await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn, NextToken = nextToken }, ct);
            subscriptions.AddRange(page.Subscriptions);
            nextToken = page.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var summaries = new List<SubscriptionSummary>();
        foreach (var sub in subscriptions)
        {
            var attributes = new Dictionary<string, string>();
            if (sub.SubscriptionArn != "PendingConfirmation")
            {
                try
                {
                    var attrsResponse = await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = sub.SubscriptionArn }, ct);
                    attributes = new Dictionary<string, string>(attrsResponse.Attributes);
                }
                catch (Exception) when (ct.IsCancellationRequested is false)
                {
                    // Left empty -- ClassifySubscription treats missing attributes as "unknown,
                    // not failing," same partial-failure rule as everywhere else in this plugin.
                }
            }

            summaries.Add(ClassifySubscription(sub.SubscriptionArn, sub.Protocol, sub.Endpoint, attributes));
        }

        return summaries;
    }

    // Pure static so it's unit-testable without a real AWS account -- mirrors SqsOperations.
    // ToQueueSummary/ExtractDeadLetterTargetArn's reasoning. Internal so SnsOperationsTests can
    // assert it directly (InternalsVisibleTo already covers the test project).
    internal static SubscriptionSummary ClassifySubscription(string subscriptionArn, string protocol, string endpoint, IReadOnlyDictionary<string, string> attributes)
    {
        var isPending = subscriptionArn == "PendingConfirmation";
        bool? rawDelivery = attributes.TryGetValue("RawMessageDelivery", out var raw) && bool.TryParse(raw, out var parsedRaw) ? parsedRaw : null;
        var filterPolicy = attributes.GetValueOrDefault("FilterPolicy");
        return new SubscriptionSummary(subscriptionArn, protocol, endpoint, isPending, rawDelivery, filterPolicy);
    }
```

Add `using Amazon.SimpleNotificationService.Model;` if not already present at the top of the file (it should already be there from Task 1's `using Amazon.SimpleNotificationService.Model;` line, which brings in `Subscription`, `ListSubscriptionsByTopicRequest`, `GetSubscriptionAttributesRequest`).

- [ ] **Step 4: Implement the handler**

`src/SbConsole.Plugins.Aws/Subscriptions/ListSubscriptionsQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

public sealed class ListSubscriptionsQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<ListSubscriptionsQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<SubscriptionSummary>>> HandleAsync(Guid connectionId, string topicArn, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail("Connection not found.");
            }

            var subscriptions = await operations.ListSubscriptionsAsync(secret, topicArn, ct);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Ok(subscriptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing subscriptions for topic {TopicArn} failed.", topicArn);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail(ex);
        }
    }
}
```

- [ ] **Step 5: Register the handler**

Add `using SbConsole.Plugins.Aws.Subscriptions;` and this line to `AwsPlugin.ConfigureServices`:

```csharp
        services.AddScoped<ListSubscriptionsQueryHandler>();
```

- [ ] **Step 6: Write the page**

Create `src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor`:

```razor
@page "/p/aws/topics/{TopicArnEncoded}"
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Subscriptions
@using SbConsole.Sdk
@inject ListSubscriptionsQueryHandler ListHandler
@inject ISnackbar Snackbar
@inject NavigationManager Nav

<PageTitle>@TopicName</PageTitle>
<div class="d-flex align-center gap-2 mb-2">
    <MudLink Href="/p/aws/topics">Topics</MudLink>
    <MudText>/</MudText>
    <MudText Style="font-family:monospace">@TopicName</MudText>
</div>

<MudTabs>
    <MudTabPanel Text="Subscriptions">
        <div class="d-flex align-center gap-2 mb-2">
            <MudText Typo="Typo.caption" Class="topic-arn mud-text-secondary" Style="font-family:monospace">@TopicArn</MudText>
        </div>
        @if (_loading)
        {
            <MudProgressCircular Class="subs-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        else
        {
            <MudTable Items="_subscriptions">
                <HeaderContent>
                    <MudTh>Protocol</MudTh>
                    <MudTh>Endpoint</MudTh>
                    <MudTh>Filter policy</MudTh>
                    <MudTh>Raw delivery</MudTh>
                    <MudTh>State</MudTh>
                </HeaderContent>
                <RowTemplate>
                    <MudTd>@context.Protocol</MudTd>
                    <MudTd Style="font-family:monospace">@context.Endpoint</MudTd>
                    <MudTd Class="filter-policy" Style="font-family:monospace">@(context.FilterPolicyJson ?? "none")</MudTd>
                    <MudTd>@(context.RawMessageDelivery is { } raw ? (raw ? "On" : "Off") : "n/a")</MudTd>
                    <MudTd Class="sub-state">
                        @if (context.IsPending)
                        {
                            <MudChip T="string" Color="Color.Warning" Size="Size.Small">Pending</MudChip>
                        }
                        else
                        {
                            <MudChip T="string" Color="Color.Success" Size="Size.Small">Confirmed</MudChip>
                        }
                    </MudTd>
                </RowTemplate>
            </MudTable>
        }
    </MudTabPanel>
    <MudTabPanel Text="Attributes">
        <MudText Typo="Typo.caption" Style="font-family:monospace">@TopicArn</MudText>
    </MudTabPanel>
</MudTabs>

@code {
    [Parameter] public string TopicArnEncoded { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }

    private IReadOnlyList<SubscriptionSummary> _subscriptions = [];
    private bool _loading;

    private string TopicArn => Uri.UnescapeDataString(TopicArnEncoded);
    private string TopicName => TopicArn[(TopicArn.LastIndexOf(':') + 1)..];

    protected override async Task OnInitializedAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var result = await ListHandler.HandleAsync(ConnectionId, TopicArn);
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
}
```

This route takes `ConnectionId` via `[SupplyParameterFromQuery]` off the URL's query string (`?connectionId=...`), the same pattern `docs/superpowers/plans/2026-09-21-aws-sqs-plugin-design.md`'s `Receive.razor` uses (confirmed precedent: `ServiceBus.Tests/Pages/PeekPageTests.cs`'s `NavigateToPeekQuery`) — Task 2's `Topics.razor` "Subs" button already links to `/p/aws/topics/{TopicArnEncoded}?connectionId={_selectedConnectionId}`.

- [ ] **Step 7: Write the page test**

Create `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicDetailPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Pages;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class TopicDetailPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISnsOperations _snsOperations = Substitute.For<ISnsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public TopicDetailPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_snsOperations);
        Services.AddSingleton<ListSubscriptionsQueryHandler>();
        Services.AddLogging();
    }

    // [SupplyParameterFromQuery]-only properties can't be set via Render<T>(parameters => ...) --
    // must navigate with a real query string (established precedent: ServiceBus.Tests'
    // PeekPageTests.NavigateToPeekQuery).
    [Fact]
    public void Renders_subscriptions_from_the_handler()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync("mode=access-keys;region=us-east-1", topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null)]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>();

        cut.Markup.Should().Contain("shipment-updates");
        cut.Find("td.sub-state").TextContent.Should().Contain("Confirmed");
    }

    [Fact]
    public void Shows_pending_state_for_an_unconfirmed_subscription()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([new SubscriptionSummary("PendingConfirmation", "email", "ops@example.com", true, null, null)]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>();

        cut.Find("td.sub-state").TextContent.Should().Contain("Pending");
    }
}
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ClassifySubscription|FullyQualifiedName~ListSubscriptionsQueryHandlerTests|FullyQualifiedName~TopicDetailPageTests"`
Expected: PASS.

- [ ] **Step 9: Run the full AWS plugin test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Subscriptions src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor src/SbConsole.Plugins.Aws/Client/SnsOperations.cs src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs tests/SbConsole.Plugins.Aws.Tests/Subscriptions tests/SbConsole.Plugins.Aws.Tests/Pages/TopicDetailPageTests.cs
git commit -m "feat(aws): add topic detail page with subscriptions list"
```

---

## Task 5: Subscribe / Unsubscribe / Resend

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Subscriptions/SubscribeCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Subscriptions/UnsubscribeCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/SubscribeDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`
- Modify: `src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor`
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Subscriptions/SubscribeCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Subscriptions/UnsubscribeCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicDetailPageTests.cs` (append)

**Interfaces:**
- Consumes: `SubscribeRequest` (Task 1), `SubscriptionSummary` (Task 1/4).
- Produces: `SnsOperations.SubscribeAsync`/`UnsubscribeAsync` (real implementations), `SubscribeCommandHandler.HandleAsync(SubscribeCommand)` returning `PluginResult<string>` (also used for Resend — same command, same handler, called again with the same fields), `UnsubscribeCommandHandler.HandleAsync(UnsubscribeCommand)` returning `PluginResult`.

- [ ] **Step 1: Write the failing tests**

Create `tests/SbConsole.Plugins.Aws.Tests/Subscriptions/SubscribeCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class SubscribeCommandHandlerTests
{
    [Fact]
    public async Task Subscribes_and_audits_as_mutating()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var request = new SubscribeRequest("arn:topic", "sqs", "shipment-updates", false);
        operations.SubscribeAsync("mode=access-keys;region=us-east-1", request, Arg.Any<CancellationToken>()).Returns("arn:sub-1");
        var handler = new SubscribeCommandHandler(operations, connections, audit, NullLogger<SubscribeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new SubscribeCommand(connectionId, "aws-dev", "shipment-updates-topic", request));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("arn:sub-1");
        await audit.Received(1).RecordAsync("aws.subscription.subscribe", "aws-dev/shipment-updates-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }
}
```

Create `tests/SbConsole.Plugins.Aws.Tests/Subscriptions/UnsubscribeCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Subscriptions;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Subscriptions;

public class UnsubscribeCommandHandlerTests
{
    [Fact]
    public async Task Unsubscribes_and_audits_as_mutating_not_destructive()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var handler = new UnsubscribeCommandHandler(operations, connections, audit, NullLogger<UnsubscribeCommandHandler>.Instance);

        var result = await handler.HandleAsync(new UnsubscribeCommand(connectionId, "aws-dev", "shipment-updates-topic", "arn:sub-1"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).UnsubscribeAsync("mode=access-keys;region=us-east-1", "arn:sub-1", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.subscription.unsubscribe", "aws-dev/shipment-updates-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SubscribeCommandHandlerTests|FullyQualifiedName~UnsubscribeCommandHandlerTests"`
Expected: build error — the handlers don't exist yet.

- [ ] **Step 3: Implement `SubscribeAsync`/`UnsubscribeAsync`**

In `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`, replace the two placeholder throws:

```csharp
    public async Task<string> SubscribeAsync(string secret, SubscribeRequest request, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = request.RawMessageDelivery.ToString().ToLowerInvariant() };
        var response = await sns.SubscribeAsync(new Amazon.SimpleNotificationService.Model.SubscribeRequest
        {
            TopicArn = request.TopicArn,
            Protocol = request.Protocol,
            Endpoint = request.Endpoint,
            Attributes = attributes,
        }, ct);
        return response.SubscriptionArn;
    }

    public async Task UnsubscribeAsync(string secret, string subscriptionArn, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        await sns.UnsubscribeAsync(subscriptionArn, ct);
    }
```

`SubscribeRequest` here refers to this project's own record (Task 1); the SDK call above
disambiguates the SDK's identically-named type with its full namespace, the same approach
`SqsOperations.SendMessageAsync` already uses for `Amazon.SQS.Model.SendMessageRequest`.

- [ ] **Step 4: Implement the handlers**

`src/SbConsole.Plugins.Aws/Subscriptions/SubscribeCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

public sealed record SubscribeCommand(Guid ConnectionId, string ConnectionName, string TopicName, SubscribeRequest Request);

/// <summary>Also used for "Resend" on a pending subscription -- calling this again with the same
/// topic/protocol/endpoint is the only mechanism AWS exposes for redelivering a confirmation.</summary>
public sealed class SubscribeCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SubscribeCommandHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(SubscribeCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var subscriptionArn = await operations.SubscribeAsync(secret, cmd.Request, ct);
            await audit.RecordAsync("aws.subscription.subscribe", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(subscriptionArn);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Subscribing to topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.subscription.subscribe", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
```

`src/SbConsole.Plugins.Aws/Subscriptions/UnsubscribeCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Subscriptions;

public sealed record UnsubscribeCommand(Guid ConnectionId, string ConnectionName, string TopicName, string SubscriptionArn);

public sealed class UnsubscribeCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<UnsubscribeCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(UnsubscribeCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.UnsubscribeAsync(secret, cmd.SubscriptionArn, ct);
            // Mutating, not Destructive -- re-subscribing fully reverses this, unlike deleting a topic.
            await audit.RecordAsync("aws.subscription.unsubscribe", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unsubscribing from topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.subscription.unsubscribe", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 5: Register the handlers**

Add to `AwsPlugin.ConfigureServices`:

```csharp
        services.AddScoped<SubscribeCommandHandler>();
        services.AddScoped<UnsubscribeCommandHandler>();
```

- [ ] **Step 6: Write the dialog and wire actions into `TopicDetail.razor`**

Create `src/SbConsole.Plugins.Aws/Pages/SubscribeDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Subscriptions
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudSelect T="string" Class="protocol-select" @bind-Value="_protocol" Label="Protocol">
            <MudSelectItem Value="@("sqs")">sqs</MudSelectItem>
            <MudSelectItem Value="@("https")">https</MudSelectItem>
            <MudSelectItem Value="@("email")">email</MudSelectItem>
            <MudSelectItem Value="@("lambda")">lambda</MudSelectItem>
        </MudSelect>
        <MudTextField id="subscribe-endpoint" @bind-Value="_endpoint" Label="Endpoint" Required="true" Immediate="true" />
        <MudSwitch T="bool" Class="raw-delivery-toggle" @bind-Value="_rawMessageDelivery" Label="Raw message delivery" Color="Color.Primary" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="subscribe-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="confirm-subscribe" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_endpoint) || _busy)" OnClick="Save">Subscribe</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicArn { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";

    [Inject] private SubscribeCommandHandler SubscribeHandler { get; set; } = default!;

    private string _protocol = "sqs";
    private string _endpoint = "";
    private bool _rawMessageDelivery;
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var request = new SubscribeRequest(TopicArn, _protocol, _endpoint, _rawMessageDelivery);
            var result = await SubscribeHandler.HandleAsync(new SubscribeCommand(ConnectionId, ConnectionName, TopicName, request));
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

In `src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor`, add `@inject IDialogService DialogService`, `@inject UnsubscribeCommandHandler UnsubscribeHandler`, `@inject SubscribeCommandHandler SubscribeHandler`, and `@using SbConsole.Sdk` (already present). Add a "+ Subscribe" button above the Subscriptions table:

```razor
        <MudButton Class="subscribe-action" Color="Color.Primary" Variant="Variant.Outlined" OnClick="OpenSubscribe">+ Subscribe</MudButton>
```

Add an Actions column to the Subscriptions table's `HeaderContent`/`RowTemplate`:

```razor
                    <MudTh>Actions</MudTh>
```

```razor
                    <MudTd>
                        @if (context.IsPending)
                        {
                            <MudButton Class="resend-action" OnClick="@(() => ResendAsync(context))">Resend</MudButton>
                        }
                        <MudButton Class="remove-subscription" Color="Color.Error" OnClick="@(() => RemoveAsync(context))">Remove</MudButton>
                    </MudTd>
```

Add to the `@code` block:

```csharp
    private async Task OpenSubscribe()
    {
        var parameters = new DialogParameters<SubscribeDialog>
        {
            { x => x.ConnectionId, ConnectionId },
            { x => x.TopicArn, TopicArn },
            { x => x.TopicName, TopicName },
        };
        var dialog = await DialogService.ShowAsync<SubscribeDialog>("Subscribe", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadAsync();
        }
    }

    private async Task ResendAsync(SubscriptionSummary subscription)
    {
        var request = new SubscribeRequest(TopicArn, subscription.Protocol, subscription.Endpoint, subscription.RawMessageDelivery ?? false);
        var result = await SubscribeHandler.HandleAsync(new SubscribeCommand(ConnectionId, "", TopicName, request));
        if (!result.IsSuccess)
        {
            Snackbar.Add(result.Error!, Severity.Error);
        }

        await LoadAsync();
    }

    private async Task RemoveAsync(SubscriptionSummary subscription)
    {
        var result = await UnsubscribeHandler.HandleAsync(new UnsubscribeCommand(ConnectionId, "", TopicName, subscription.SubscriptionArn));
        if (!result.IsSuccess)
        {
            Snackbar.Add(result.Error!, Severity.Error);
        }

        await LoadAsync();
    }
```

Note: `ConnectionName` isn't available on this page today (only `ConnectionId` arrives via the
query string) — the audit target's connection-name half is passed as `""` here, which is a real
gap the audit trail will show as `"/topic-name"` instead of `"aws-dev/topic-name"`. Fixing this
properly means either passing `connectionName` through the URL too (a third query parameter) or
having this page call `IConnectionProvider.ListAsync("aws")` once to resolve the name from
`ConnectionId`, mirroring how `Queues.razor` already holds a full `ConnectionInfo` list rather than
just an ID. Do the latter: add `@inject IConnectionProvider Connections` and, in
`OnInitializedAsync` before calling `LoadAsync()`, resolve `_connectionName` via
`(await Connections.ListAsync("aws")).FirstOrDefault(c => c.Id == ConnectionId)?.Name ?? ""` into a
new private field, and use `_connectionName` in place of the two `""` literals above.

- [ ] **Step 7: Append page tests**

Append to `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicDetailPageTests.cs`, inside the existing test class (add `Services.AddSingleton<SubscribeCommandHandler>();`, `Services.AddSingleton<UnsubscribeCommandHandler>();` to the constructor):

```csharp
    [Fact]
    public void Resend_is_only_shown_for_a_pending_subscription()
    {
        var topicArn = "arn:aws:sns:us-east-1:1:shipment-updates-topic";
        _snsOperations.ListSubscriptionsAsync(Arg.Any<string>(), topicArn, Arg.Any<CancellationToken>())
            .Returns([
                new SubscriptionSummary("PendingConfirmation", "email", "ops@example.com", true, null, null),
                new SubscriptionSummary("arn:sub-1", "sqs", "shipment-updates", false, false, null),
            ]);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"/p/aws/topics/{Uri.EscapeDataString(topicArn)}?connectionId={_connectionId}");
        var cut = Render<TopicDetail>();

        cut.FindAll("button.resend-action").Should().ContainSingle();
        cut.FindAll("button.remove-subscription").Should().HaveCount(2);
    }
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SubscribeCommandHandlerTests|FullyQualifiedName~UnsubscribeCommandHandlerTests|FullyQualifiedName~TopicDetailPageTests"`
Expected: PASS.

- [ ] **Step 9: Run the full AWS plugin test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Subscriptions src/SbConsole.Plugins.Aws/Pages/SubscribeDialog.razor src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor src/SbConsole.Plugins.Aws/Client/SnsOperations.cs src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/Subscriptions tests/SbConsole.Plugins.Aws.Tests/Pages/TopicDetailPageTests.cs
git commit -m "feat(aws): add subscribe/unsubscribe/resend"
```

---

## Task 6: Publish with fan-out preview

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Topics/PublishCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Topics/GetSubscriptionFilterPoliciesQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/PublishDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Topics.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor`
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs` (append)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Topics/PublishCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `SnsPublishRequest` (Task 1).
- Produces: `SnsOperations.PublishAsync` (real implementation), `SnsOperations.EvaluateFilterMatch` (`internal static bool`, pure function: does a given filter-policy JSON string match a given attribute dictionary? — this is the fan-out preview's core logic), `PublishCommandHandler.HandleAsync(PublishCommand)` returning `PluginResult`, `GetSubscriptionFilterPoliciesQueryHandler.HandleAsync(Guid connectionId, string topicArn)` returning `PluginResult<IReadOnlyList<SubscriptionSummary>>` (reuses `ListSubscriptionsAsync` — filter policies are already part of `SubscriptionSummary` from Task 4, so this is a thin wrapper the Publish dialog calls under a name that reads clearly at the call site).

- [ ] **Step 1: Write the failing tests**

Append to `tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void EvaluateFilterMatch_returns_true_when_no_filter_policy_is_set()
    {
        SnsOperations.EvaluateFilterMatch(null, new Dictionary<string, string> { ["region"] = "uk" }).Should().BeTrue();
    }

    [Fact]
    public void EvaluateFilterMatch_matches_a_simple_value_list_policy()
    {
        var policy = """{"region":["uk","eu"]}""";

        SnsOperations.EvaluateFilterMatch(policy, new Dictionary<string, string> { ["region"] = "uk" }).Should().BeTrue();
        SnsOperations.EvaluateFilterMatch(policy, new Dictionary<string, string> { ["region"] = "us" }).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_fails_when_the_message_has_no_matching_attribute()
    {
        var policy = """{"region":["uk"]}""";

        SnsOperations.EvaluateFilterMatch(policy, new Dictionary<string, string> { ["eventType"] = "shipment.dispatched" }).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_requires_every_key_in_the_policy_to_match()
    {
        var policy = """{"region":["uk"],"eventType":["shipment.dispatched"]}""";
        var attributes = new Dictionary<string, string> { ["region"] = "uk", ["eventType"] = "shipment.delivered" };

        SnsOperations.EvaluateFilterMatch(policy, attributes).Should().BeFalse();
    }

    [Fact]
    public void EvaluateFilterMatch_treats_malformed_policy_json_as_no_match()
    {
        SnsOperations.EvaluateFilterMatch("not json", new Dictionary<string, string>()).Should().BeFalse();
    }
```

Create `tests/SbConsole.Plugins.Aws.Tests/Topics/PublishCommandHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class PublishCommandHandlerTests
{
    [Fact]
    public async Task Publishes_and_audits_as_mutating()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var audit = Substitute.For<IAuditScope>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        var request = new SnsPublishRequest("Shipment dispatched", "{}", null, null, null);
        var handler = new PublishCommandHandler(operations, connections, audit, NullLogger<PublishCommandHandler>.Instance);

        var result = await handler.HandleAsync(new PublishCommand(connectionId, "aws-dev", "shipment-updates-topic", "arn:topic", request));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).PublishAsync("mode=access-keys;region=us-east-1", "arn:topic", request, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.topic.publish", "aws-dev/shipment-updates-topic", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~EvaluateFilterMatch|FullyQualifiedName~PublishCommandHandlerTests"`
Expected: build error — `SnsOperations.EvaluateFilterMatch`/`PublishCommandHandler` don't exist yet.

- [ ] **Step 3: Implement `PublishAsync` and `EvaluateFilterMatch`**

In `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`, add `using System.Text.Json;` at the top, and replace the `PublishAsync` placeholder throw:

```csharp
    public async Task PublishAsync(string secret, string topicArn, SnsPublishRequest request, CancellationToken ct = default)
    {
        using var sns = BuildSnsClient(secret);
        var sdkRequest = new Amazon.SimpleNotificationService.Model.PublishRequest { TopicArn = topicArn, Message = request.Message };
        if (request.Subject is { } subject)
        {
            sdkRequest.Subject = subject;
        }

        if (request.MessageAttributes is { Count: > 0 } attributes)
        {
            sdkRequest.MessageAttributes = attributes.ToDictionary(
                kv => kv.Key,
                kv => new Amazon.SimpleNotificationService.Model.MessageAttributeValue { DataType = "String", StringValue = kv.Value });
        }

        if (request.MessageGroupId is { } groupId)
        {
            sdkRequest.MessageGroupId = groupId;
        }

        if (request.MessageDeduplicationId is { } dedupId)
        {
            sdkRequest.MessageDeduplicationId = dedupId;
        }

        await sns.PublishAsync(sdkRequest, ct);
    }

    // Pure static, unit-testable without a real AWS account -- the fan-out preview's core logic.
    // SNS filter policies match on MessageAttributes: a policy is a JSON object of
    // attributeName -> array-of-allowed-values (the subset of SNS filter-policy syntax this plugin
    // supports for evaluation -- $or/anything-but/numeric-range operators are not evaluated and a
    // policy using them is treated as "no match," which is the conservative, safe direction to be
    // wrong in for a preview). A null/empty policy always matches (no filter = receives everything).
    // Malformed JSON is treated as no match, not an exception -- a preview must never crash the
    // Publish dialog over a policy it can't parse.
    internal static bool EvaluateFilterMatch(string? filterPolicyJson, IReadOnlyDictionary<string, string> messageAttributes)
    {
        if (string.IsNullOrEmpty(filterPolicyJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(filterPolicyJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!messageAttributes.TryGetValue(property.Name, out var value))
                {
                    return false;
                }

                var matchesThisKey = false;
                foreach (var allowed in property.Value.EnumerateArray())
                {
                    if (allowed.ValueKind == JsonValueKind.String && allowed.GetString() == value)
                    {
                        matchesThisKey = true;
                        break;
                    }
                }

                if (!matchesThisKey)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
```

- [ ] **Step 4: Implement the handlers**

`src/SbConsole.Plugins.Aws/Topics/PublishCommandHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed record PublishCommand(Guid ConnectionId, string ConnectionName, string TopicName, string TopicArn, SnsPublishRequest Request);

public sealed class PublishCommandHandler(ISnsOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<PublishCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(PublishCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.PublishAsync(secret, cmd.TopicArn, cmd.Request, ct);
            await audit.RecordAsync("aws.topic.publish", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing to topic {Target} failed.", target);
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.topic.publish", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

`src/SbConsole.Plugins.Aws/Topics/GetSubscriptionFilterPoliciesQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

/// <summary>Thin wrapper over ISnsOperations.ListSubscriptionsAsync for the Publish dialog's
/// fan-out preview -- SubscriptionSummary already carries each subscription's filter policy
/// (Task 4), so this exists to give the Publish dialog's own dependency a name that reads clearly
/// at its call site rather than reusing Subscriptions.ListSubscriptionsQueryHandler by cross-folder
/// reference.</summary>
public sealed class GetSubscriptionFilterPoliciesQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<GetSubscriptionFilterPoliciesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<SubscriptionSummary>>> HandleAsync(Guid connectionId, string topicArn, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail("Connection not found.");
            }

            var subscriptions = await operations.ListSubscriptionsAsync(secret, topicArn, ct);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Ok(subscriptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving subscription filter policies for topic {TopicArn} failed.", topicArn);
            return PluginResult<IReadOnlyList<SubscriptionSummary>>.Fail(ex);
        }
    }
}
```

- [ ] **Step 5: Register the handlers**

Add to `AwsPlugin.ConfigureServices`:

```csharp
        services.AddScoped<PublishCommandHandler>();
        services.AddScoped<GetSubscriptionFilterPoliciesQueryHandler>();
```

- [ ] **Step 6: Write the dialog**

Create `src/SbConsole.Plugins.Aws/Pages/PublishDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Topics
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">@ConnectionName / @TopicName</MudText>
        <MudTextField id="publish-subject" @bind-Value="_subject" Label="Subject (email only, optional)" Immediate="true" />

        <MudText Typo="Typo.subtitle2" Class="mt-4 mb-2">Message attributes</MudText>
        @for (var i = 0; i < _attributes.Count; i++)
        {
            var index = i;
            <div class="d-flex gap-2 align-center mb-1">
                <MudTextField Class="attribute-key" @bind-Value="_attributes[index].Key" ValueChanged="@((string v) => OnAttributeChanged(index, v, _attributes[index].Value))" Placeholder="Key" />
                <MudTextField Class="attribute-value" @bind-Value="_attributes[index].Value" ValueChanged="@((string v) => OnAttributeChanged(index, _attributes[index].Key, v))" Placeholder="Value" />
                <MudIconButton Class="remove-attribute" Icon="@Icons.Material.Filled.Close" OnClick="@(() => RemoveAttribute(index))" />
            </div>
        }
        <MudButton Class="add-attribute" OnClick="AddAttribute">+ Add attribute</MudButton>

        @if (IsFifo)
        {
            <MudTextField Class="message-group-id" @bind-Value="_messageGroupId" Label="Message group ID" Required="true" Immediate="true" Class="mt-3" />
            <MudTextField Class="message-dedup-id" @bind-Value="_messageDeduplicationId" Label="Deduplication ID" Required="true" Immediate="true" />
        }

        @if (_filterPreviewLoading)
        {
            <MudProgressCircular Class="mt-3" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        else if (_subscriptions.Count > 0)
        {
            <MudText Typo="Typo.caption" Class="fanout-summary mud-text-secondary mt-3">Matches @MatchCount of @_subscriptions.Count subscriptions</MudText>
            @foreach (var sub in _subscriptions)
            {
                <div class="d-flex align-center gap-2 fanout-row">
                    @if (sub.IsPending)
                    {
                        <MudText Class="fanout-status" Typo="Typo.caption">— pending</MudText>
                    }
                    else if (Matches(sub))
                    {
                        <MudText Class="fanout-status" Typo="Typo.caption" Color="Color.Success">✓</MudText>
                    }
                    else
                    {
                        <MudText Class="fanout-status" Typo="Typo.caption" Color="Color.Error">✕ filtered out</MudText>
                    }
                    <MudText Typo="Typo.caption" Style="font-family:monospace">@sub.Endpoint</MudText>
                </div>
            }
        }

        <MudTextField id="publish-message" @bind-Value="_message" Label="Message" Lines="6" Immediate="true" Class="mt-3" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="publish-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="confirm-publish" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanPublish || _busy)" OnClick="Publish">Publish</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicArn { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";
    [Parameter] public bool IsFifo { get; set; }

    [Inject] private PublishCommandHandler PublishHandler { get; set; } = default!;
    [Inject] private GetSubscriptionFilterPoliciesQueryHandler FilterPoliciesHandler { get; set; } = default!;

    private string _subject = "";
    private string _message = "";
    private string _messageGroupId = "";
    private string _messageDeduplicationId = "";
    private readonly List<AttributeRow> _attributes = [];
    private IReadOnlyList<SubscriptionSummary> _subscriptions = [];
    private bool _filterPreviewLoading;
    private bool _busy;

    private sealed class AttributeRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private bool CanPublish => !string.IsNullOrWhiteSpace(_message) && (!IsFifo || !string.IsNullOrWhiteSpace(_messageGroupId));

    private int MatchCount => _subscriptions.Count(s => !s.IsPending && Matches(s));

    private bool Matches(SubscriptionSummary sub) =>
        SbConsole.Plugins.Aws.Client.SnsOperations.EvaluateFilterMatch(sub.FilterPolicyJson, CurrentAttributes());

    private Dictionary<string, string> CurrentAttributes() =>
        _attributes.Where(a => !string.IsNullOrWhiteSpace(a.Key)).ToDictionary(a => a.Key, a => a.Value);

    protected override async Task OnInitializedAsync()
    {
        _filterPreviewLoading = true;
        try
        {
            var result = await FilterPoliciesHandler.HandleAsync(ConnectionId, TopicArn);
            _subscriptions = result.IsSuccess ? result.Value! : [];
        }
        finally
        {
            _filterPreviewLoading = false;
        }
    }

    private void OnAttributeChanged(int index, string key, string value)
    {
        _attributes[index].Key = key;
        _attributes[index].Value = value;
    }

    private void AddAttribute() => _attributes.Add(new AttributeRow());

    private void RemoveAttribute(int index) => _attributes.RemoveAt(index);

    private async Task Publish()
    {
        _busy = true;
        try
        {
            var attributes = CurrentAttributes();
            var request = new SnsPublishRequest(
                string.IsNullOrWhiteSpace(_subject) ? null : _subject, _message,
                attributes.Count > 0 ? attributes : null,
                IsFifo ? _messageGroupId : null,
                IsFifo ? _messageDeduplicationId : null);

            var result = await PublishHandler.HandleAsync(new PublishCommand(ConnectionId, ConnectionName, TopicName, TopicArn, request));
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

- [ ] **Step 7: Wire "Publish" into `Topics.razor` and `TopicDetail.razor`**

In `src/SbConsole.Plugins.Aws/Pages/Topics.razor`, add `@inject IDialogService DialogService` and a Publish button in `RowTemplate`'s Actions cell, before Subs:

```razor
                <MudButton Class="publish-action" OnClick="@(() => OpenPublish(context.TopicArn, context.Name, context.IsFifo))">Publish</MudButton>
```

Add to its `@code` block:

```csharp
    private async Task OpenPublish(string topicArn, string topicName, bool isFifo)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<PublishDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.TopicArn, topicArn },
            { x => x.TopicName, topicName },
            { x => x.IsFifo, isFifo },
        };
        await DialogService.ShowAsync<PublishDialog>("Publish to topic", parameters);
    }
```

In `src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor`, add `@inject IDialogService DialogService` and a Publish button next to "+ Subscribe". This reuses the `_connectionName` field Task 5 added to this page (resolved once in `OnInitializedAsync` via `IConnectionProvider.ListAsync("aws")`) so the publish audit record carries a real connection name instead of an empty string:

```razor
        <MudButton Class="publish-action" OnClick="OpenPublish">Publish</MudButton>
```

```csharp
    private async Task OpenPublish()
    {
        var parameters = new DialogParameters<PublishDialog>
        {
            { x => x.ConnectionId, ConnectionId },
            { x => x.ConnectionName, _connectionName },
            { x => x.TopicArn, TopicArn },
            { x => x.TopicName, TopicName },
            { x => x.IsFifo, TopicName.EndsWith(".fifo", StringComparison.Ordinal) },
        };
        await DialogService.ShowAsync<PublishDialog>("Publish to topic", parameters);
    }
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~EvaluateFilterMatch|FullyQualifiedName~PublishCommandHandlerTests"`
Expected: PASS.

- [ ] **Step 9: Run the full AWS plugin test suite and build**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: 0 warnings, 0 errors, all tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Topics src/SbConsole.Plugins.Aws/Pages/PublishDialog.razor src/SbConsole.Plugins.Aws/Pages/Topics.razor src/SbConsole.Plugins.Aws/Pages/TopicDetail.razor src/SbConsole.Plugins.Aws/Client/SnsOperations.cs src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/Client/SnsOperationsTests.cs tests/SbConsole.Plugins.Aws.Tests/Topics/PublishCommandHandlerTests.cs
git commit -m "feat(aws): add publish with fan-out preview"
```

---

## Task 7: CloudWatch delivery-failure metric

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Topics/GetTopicDeliveryFailureCountQueryHandler.cs`
- Modify: `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`
- Modify: `src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Topics.razor`
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Topics/GetTopicDeliveryFailureCountQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs` (append)

**Interfaces:**
- Consumes: `ISnsOperations.GetDeliveryFailureCountAsync` (implemented here).
- Produces: `SnsOperations.GetDeliveryFailureCountAsync` (real implementation), `GetTopicDeliveryFailureCountQueryHandler.HandleAsync(Guid connectionId, string topicName)` returning `PluginResult<long>`.

- [ ] **Step 1: Write the failing test**

Create `tests/SbConsole.Plugins.Aws.Tests/Topics/GetTopicDeliveryFailureCountQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Topics;

public class GetTopicDeliveryFailureCountQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_count_from_operations()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        operations.GetDeliveryFailureCountAsync("mode=access-keys;region=us-east-1", "shipment-updates-topic", Arg.Any<CancellationToken>()).Returns(1204L);
        var handler = new GetTopicDeliveryFailureCountQueryHandler(operations, connections, NullLogger<GetTopicDeliveryFailureCountQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, "shipment-updates-topic");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1204L);
    }

    [Fact]
    public async Task A_denied_metrics_call_fails_this_handler_without_throwing()
    {
        var operations = Substitute.For<ISnsOperations>();
        var connections = Substitute.For<IConnectionProvider>();
        var connectionId = Guid.NewGuid();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=access-keys;region=us-east-1");
        operations.GetDeliveryFailureCountAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long>(new InvalidOperationException("AccessDenied")));
        var handler = new GetTopicDeliveryFailureCountQueryHandler(operations, connections, NullLogger<GetTopicDeliveryFailureCountQueryHandler>.Instance);

        var result = await handler.HandleAsync(connectionId, "shipment-updates-topic");

        result.IsSuccess.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~GetTopicDeliveryFailureCountQueryHandlerTests"`
Expected: build error — the handler doesn't exist yet.

- [ ] **Step 3: Implement `GetDeliveryFailureCountAsync`**

Add `using Amazon.CloudWatch;` and `using Amazon.CloudWatch.Model;` to the top of `src/SbConsole.Plugins.Aws/Client/SnsOperations.cs`. Add a CloudWatch client builder and replace the last placeholder throw:

```csharp
    private static AmazonCloudWatchClient BuildCloudWatchClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var cwConfig = new AmazonCloudWatchConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
            ServiceURL = sqsConfig.ServiceURL,
            UseHttp = sqsConfig.UseHttp,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonCloudWatchClient(cwConfig) : new AmazonCloudWatchClient(credentials, cwConfig);
    }

    public async Task<long> GetDeliveryFailureCountAsync(string secret, string topicName, CancellationToken ct = default)
    {
        using var cloudWatch = BuildCloudWatchClient(secret);
        var now = DateTime.UtcNow;
        var response = await cloudWatch.GetMetricStatisticsAsync(new GetMetricStatisticsRequest
        {
            Namespace = "AWS/SNS",
            MetricName = "NumberOfNotificationsFailed",
            Dimensions = [new Dimension { Name = "TopicName", Value = topicName }],
            StartTime = now.AddHours(-24),
            EndTime = now,
            Period = 86400,
            Statistics = ["Sum"],
        }, ct);

        // No datapoints means no failures were reported in the window, not an error -- CloudWatch
        // simply has nothing to return when a metric never fired.
        return response.Datapoints.Count == 0 ? 0 : (long)response.Datapoints.Sum(d => d.Sum);
    }
```

- [ ] **Step 4: Add a CloudWatch exception mapping to `FriendlyAwsError`**

Add `using Amazon.CloudWatch;` to the top of `src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs`, and add this arm before the fallback:

```csharp
        AmazonCloudWatchException { ErrorCode: "AccessDenied" } => "Access denied — check IAM permissions for cloudwatch:GetMetricStatistics",
```

Confirm this `ErrorCode` value against the installed `AWSSDK.CloudWatch` package at implementation time.

- [ ] **Step 5: Implement the handler**

`src/SbConsole.Plugins.Aws/Topics/GetTopicDeliveryFailureCountQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Topics;

public sealed class GetTopicDeliveryFailureCountQueryHandler(ISnsOperations operations, IConnectionProvider connections, ILogger<GetTopicDeliveryFailureCountQueryHandler> logger)
{
    public async Task<PluginResult<long>> HandleAsync(Guid connectionId, string topicName, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<long>.Fail("Connection not found.");
            }

            var count = await operations.GetDeliveryFailureCountAsync(secret, topicName, ct);
            return PluginResult<long>.Ok(count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Getting delivery failure count for topic {TopicName} failed.", topicName);
            return PluginResult<long>.Fail(ex);
        }
    }
}
```

- [ ] **Step 6: Register the handler**

Add to `AwsPlugin.ConfigureServices`:

```csharp
        services.AddScoped<GetTopicDeliveryFailureCountQueryHandler>();
```

- [ ] **Step 7: Wire the "Failed 24h" column into `Topics.razor`**

Add `@inject GetTopicDeliveryFailureCountQueryHandler FailureCountHandler` to the top of `Topics.razor`. Add a `Failed 24h` header between `Pending` and `Flags`:

```razor
            <MudTh Style="text-align:right">Failed 24h</MudTh>
```

Add a cell in `RowTemplate`, reading from a per-topic dictionary populated after the initial list load:

```razor
            <MudTd Class="failed-24h" Style="text-align:right;font-family:monospace">@FailedCountText(context.TopicArn)</MudTd>
```

Add to the `@code` block:

```csharp
    private readonly Dictionary<string, long?> _failedCounts = [];

    private string FailedCountText(string topicArn) => _failedCounts.TryGetValue(topicArn, out var count)
        ? (count.HasValue ? count.Value.ToString() : "—")
        : "…";
```

And, at the end of `LoadTopicsAsync` (after the `finally` block that resets `_loading`), kick off the per-topic failure-count loads without blocking the initial render:

```csharp
        _ = LoadFailureCountsAsync(_topics);
    }

    private async Task LoadFailureCountsAsync(IReadOnlyList<TopicSummary> topics)
    {
        _failedCounts.Clear();
        foreach (var topic in topics)
        {
            var result = await FailureCountHandler.HandleAsync(_selectedConnectionId, topic.Name);
            _failedCounts[topic.TopicArn] = result.IsSuccess ? result.Value : null;
            StateHasChanged();
        }
    }
```

(The closing `}` after `_ = LoadFailureCountsAsync(_topics);` replaces `LoadTopicsAsync`'s existing closing brace — add the new statement as the last line inside the method, right after its existing `finally` block, before the method's own closing brace.)

- [ ] **Step 8: Append a page test**

Append to `tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs`, inside the existing class (add `Services.AddSingleton<GetTopicDeliveryFailureCountQueryHandler>();` to the constructor):

```csharp
    [Fact]
    public async Task Shows_the_failure_count_once_it_loads()
    {
        _snsOperations.ListTopicsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new TopicSummary("shipment-updates-topic", "arn:aws:sns:us-east-1:1:shipment-updates-topic", false, 5, 2, false)]);
        _snsOperations.GetDeliveryFailureCountAsync("mode=access-keys;region=us-east-1", "shipment-updates-topic", Arg.Any<CancellationToken>())
            .Returns(1204L);

        var cut = Render<Topics>();
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

        var cut = Render<Topics>();
        await Task.Delay(50);
        cut.Render();

        cut.Find("td.failed-24h").TextContent.Should().Be("—");
    }
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~GetTopicDeliveryFailureCountQueryHandlerTests|FullyQualifiedName~TopicsPageTests"`
Expected: PASS.

- [ ] **Step 10: Run the full solution build and test suite**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings, 0 errors, every test in every project passes.

- [ ] **Step 11: Commit**

```bash
git add src/SbConsole.Plugins.Aws/Topics/GetTopicDeliveryFailureCountQueryHandler.cs src/SbConsole.Plugins.Aws/Client/SnsOperations.cs src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs src/SbConsole.Plugins.Aws/Pages/Topics.razor src/SbConsole.Plugins.Aws/AwsPlugin.cs tests/SbConsole.Plugins.Aws.Tests/Topics/GetTopicDeliveryFailureCountQueryHandlerTests.cs tests/SbConsole.Plugins.Aws.Tests/Pages/TopicsPageTests.cs
git commit -m "feat(aws): add per-topic CloudWatch delivery-failure count"
```

---

## Task 8: Docs — reconcile `docs/design.md` and `docker/README.md`

**Files:**
- Modify: `docs/design.md`
- Modify: `docker/README.md`

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Read the current end of the relevant sections**

Read `docs/design.md`'s §6.7 (AWS plugin — Queues) to find where the AWS plugin's section ends, and read `docker/README.md`'s AWS section (added by the SQS plan) to find its current IAM policy example / permission list.

- [ ] **Step 2: Append a new dated subsection to `docs/design.md`**

Add a new subsection immediately after §6.7 (e.g. `§6.7.1` or the next logical numbering — read the file first to pick a number consistent with its existing scheme), documenting: what shipped (Topics list, Topic detail/Subscriptions, Subscribe/Unsubscribe/Resend, Publish with fan-out preview, CloudWatch delivery-failure metric), the new `cloudwatch:GetMetricStatistics` IAM requirement and its partial-failure handling, the `ISnsOperations`/`SnsOperations` shape mirroring `ISqsOperations`, and the explicit note that `SqsOperations.TestConnectionAsync`'s `Checks` list still does not include a "Topics visible" entry (deferred until the connections-page-redesign branch merges — see this plan's Global Constraints). List what's still out of scope per the spec's §8 (filter-policy authoring, Access Policy tab, delivery logs, nav-badge/dashboard integration). Write actual final prose matching §6.7's style and detail level — no placeholder text.

- [ ] **Step 3: Update `docker/README.md`'s AWS least-privilege policy example**

Add `cloudwatch:GetMetricStatistics` (and `sns:ListTopics`, `sns:GetTopicAttributes`, `sns:CreateTopic`, `sns:DeleteTopic`, `sns:ListSubscriptionsByTopic`, `sns:GetSubscriptionAttributes`, `sns:Subscribe`, `sns:Unsubscribe`, `sns:Publish`) to the documented IAM policy example, alongside the existing `sqs:*` actions.

- [ ] **Step 4: Commit**

```bash
git add docs/design.md docker/README.md
git commit -m "docs: reconcile design.md and docker/README.md with SNS Topics"
```

---

## Final gate

- [ ] Run `dotnet build -warnaserror` from the repo root — expect 0 warnings, 0 errors.
- [ ] Run `dotnet test` from the repo root — expect every test in every project to pass.
- [ ] Run `git status --short` — expect only files this plan intentionally changed (plus any pre-existing untouched files the session started with, which must remain untouched).
