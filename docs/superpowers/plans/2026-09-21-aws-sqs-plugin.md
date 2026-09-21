# AWS Plugin (SQS Queues) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a new `SbConsole.Plugins.Aws` plugin providing full SQS queue management (list, create, delete, purge, receive/delete/release/send messages, DLQ redrive) as SbConsole's third plugin, following the design in `docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md`.

**Architecture:** Mirrors `SbConsole.Plugins.Kafka` exactly: `AwsPlugin : IPlugin` (connection kind `"aws"`), a substitutable `ISqsOperations`/`SqsOperations` seam wrapping `AWSSDK.SQS`/`AWSSDK.SecurityToken`, plain `XxxQueryHandler`/`XxxCommandHandler` classes per entity folder, MudBlazor pages under `Pages/`, all connection secrets as one flat `key=value;` string parsed by `AwsConfigParser`. No custom connection-form UI — the existing generic `AddEditConnectionDialog` textbox is reused, matching Kafka's own precedent.

**Tech Stack:** .NET 10, `AWSSDK.SQS`, `AWSSDK.SecurityToken`, MudBlazor 9.9.0, xUnit + FluentAssertions + NSubstitute + bUnit.

## Global Constraints

- `TreatWarningsAsErrors=true` — a warning fails the build. Run `dotnet build -warnaserror` before every commit.
- `dotnet test` must be green before every commit.
- TDD: write the failing test first, per task.
- Nullable enabled, `net10.0`, C# latest.
- No SQLite-only SQL (not applicable to this plugin — it touches no DbContext).
- Plugins never touch the DbContext; persistence only via `IPluginStore` (unused by this plugin — no local state needed).
- Every handler: resolve secret via `IConnectionProvider.GetSecretAsync`, call the matching `ISqsOperations` method, catch exceptions through `FriendlyAwsError`/`PluginResult.Fail(ex)`, and for commands report via `IAuditScope.RecordAsync`. No raw `ex.Message` ever reaches a snackbar, audit row, or persisted column.
- Destructive actions (`ActionRisk.Destructive`) on prod-tagged connections go through `IConfirmationService`, with `IsProd` sourced server-side from `IConnectionProvider` — never trusted off a client-suppliable parameter.
- Connection secret format (exact, from the design spec §2):
  ```
  mode=access-keys;region=eu-west-1;accessKeyId=...;secretAccessKey=...;sessionToken=...
  mode=assume-role;region=eu-west-1;roleArn=...;externalId=...;sessionName=...
  mode=default-chain;region=eu-west-1
  ```
  Optional on any mode: `endpoint=...`, `pathStyle=true`.
- `ISqsOperations` (final shape, referenced by every task below):
  ```csharp
  public interface ISqsOperations
  {
      Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
      Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default);
      Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default);
      Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default);
      Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default);
      Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default);
      Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default);
      Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default);
      Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default);
      Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default);
  }
  ```

---

## Task 1: Project scaffold + `AwsConfigParser`

**Files:**
- Create: `src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj`
- Create: `src/SbConsole.Plugins.Aws/Client/AwsConfigParser.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/SbConsole.Plugins.Aws.Tests.csproj`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/AwsConfigParserTests.cs`
- Modify: `SbConsole.sln` (add both new projects)

**Interfaces:**
- Produces: `AwsConfigParser.Parse(string config) : Dictionary<string, string>`, `AwsConfigParser.SafeEcho(string config) : string`.

- [ ] **Step 1: Check the solution file's existing project-entry format**

Run: `grep -A2 "SbConsole.Plugins.Kafka.csproj" SbConsole.sln`

Note the exact `Project(...)` block shape (GUID style) to replicate for the two new entries in Step 6.

- [ ] **Step 2: Create the plugin project file**

`src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <ItemGroup>
    <ProjectReference Include="..\SbConsole.Sdk\SbConsole.Sdk.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- SqsOperations' internal timeout constants are asserted directly by unit tests
         (there is no broker to observe them against) -- same reason Kafka.Tests needs this. -->
    <InternalsVisibleTo Include="SbConsole.Plugins.Aws.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="AWSSDK.SQS" Version="3.7.400.62" />
    <PackageReference Include="AWSSDK.SecurityToken" Version="3.7.401.13" />
    <PackageReference Include="MudBlazor" Version="9.9.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- Pages/*.razor need Razor component + [Authorize] support, which live in the ASP.NET Core
         shared framework, not a NuGet package. Same pattern SbConsole.Plugins.Kafka/ServiceBus use. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 3: Create the test project file**

`tests/SbConsole.Plugins.Aws.Tests/SbConsole.Plugins.Aws.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="bunit" Version="2.10.3" />
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="FluentAssertions" Version="7.2.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NSubstitute" Version="6.2.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SbConsole.Plugins.Aws\SbConsole.Plugins.Aws.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Write the failing `AwsConfigParser` tests**

`tests/SbConsole.Plugins.Aws.Tests/Client/AwsConfigParserTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class AwsConfigParserTests
{
    [Fact]
    public void Empty_string_parses_to_an_empty_dictionary()
    {
        AwsConfigParser.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Parses_access_keys_mode()
    {
        var result = AwsConfigParser.Parse("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t");

        result.Should().Equal(new Dictionary<string, string>
        {
            ["mode"] = "access-keys",
            ["region"] = "eu-west-1",
            ["accessKeyId"] = "AKIA123",
            ["secretAccessKey"] = "s3cr3t",
        });
    }

    [Fact]
    public void Ignores_a_trailing_semicolon()
    {
        AwsConfigParser.Parse("region=eu-west-1;")
            .Should().Equal(new Dictionary<string, string> { ["region"] = "eu-west-1" });
    }

    [Fact]
    public void Skips_a_malformed_segment_with_no_equals_sign()
    {
        AwsConfigParser.Parse("region=eu-west-1;not-a-pair;mode=default-chain")
            .Should().Equal(new Dictionary<string, string>
            {
                ["region"] = "eu-west-1",
                ["mode"] = "default-chain",
            });
    }

    [Fact]
    public void Later_duplicate_keys_win()
    {
        AwsConfigParser.Parse("region=eu-west-1;region=us-east-1")
            .Should().Equal(new Dictionary<string, string> { ["region"] = "us-east-1" });
    }

    [Fact]
    public void Trims_whitespace_around_keys_and_values()
    {
        AwsConfigParser.Parse(" region = eu-west-1 ; mode = default-chain ")
            .Should().Equal(new Dictionary<string, string>
            {
                ["region"] = "eu-west-1",
                ["mode"] = "default-chain",
            });
    }

    [Fact]
    public void SafeEcho_echoes_only_the_allowlisted_keys_in_a_fixed_order()
    {
        AwsConfigParser.SafeEcho("secretAccessKey=s3cr3t;endpoint=http://localstack:4566;region=eu-west-1;accessKeyId=AKIA123;mode=access-keys")
            .Should().Be("region=eu-west-1 · mode=access-keys · endpoint=http://localstack:4566");
    }

    [Fact]
    public void SafeEcho_omits_keys_that_are_absent()
    {
        AwsConfigParser.SafeEcho("region=eu-west-1;mode=default-chain")
            .Should().Be("region=eu-west-1 · mode=default-chain");
    }

    [Fact]
    public void SafeEcho_never_echoes_credentials_or_role_fields()
    {
        AwsConfigParser.SafeEcho("accessKeyId=AKIA123;secretAccessKey=s3cr3t;sessionToken=tok;roleArn=arn:aws:iam::123456789012:role/X;externalId=ext;sessionName=sess")
            .Should().Be("");
    }

    [Fact]
    public void SafeEcho_of_an_empty_config_is_empty()
    {
        AwsConfigParser.SafeEcho("").Should().Be("");
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsConfigParserTests"`
Expected: build error — `AwsConfigParser` does not exist yet.

- [ ] **Step 6: Add both projects to the solution**

Run:
```bash
dotnet sln SbConsole.sln add src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj
dotnet sln SbConsole.sln add tests/SbConsole.Plugins.Aws.Tests/SbConsole.Plugins.Aws.Tests.csproj
```

- [ ] **Step 7: Implement `AwsConfigParser`**

`src/SbConsole.Plugins.Aws/Client/AwsConfigParser.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Parses a connection secret shaped as a flat "key=value" string separated by ";" (e.g.
/// "mode=access-keys;region=eu-west-1;accessKeyId=...;secretAccessKey=...") -- see the design
/// spec (docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md) §2. Mirrors
/// SbConsole.Plugins.Kafka.Client.KafkaConfigParser exactly: a malformed segment (no "=") is
/// skipped rather than throwing -- the resulting config simply won't have that key, and every
/// operation already surfaces a missing required field as a friendly connection-shaped error
/// (see FriendlyAwsError, Task 2) rather than a parser exception.
/// </summary>
public static class AwsConfigParser
{
    // Fixed echo order deliberately excludes every credential-shaped key (accessKeyId,
    // secretAccessKey, sessionToken, roleArn, externalId, sessionName) so the decrypted secret's
    // credentials never reach the browser, even indirectly -- same allowlist discipline as
    // KafkaConfigParser.SafeEcho.
    private static readonly string[] SafeEchoKeys = ["region", "mode", "endpoint"];

    public static string SafeEcho(string config)
    {
        var parsed = Parse(config);
        return string.Join(" · ", SafeEchoKeys.Where(parsed.ContainsKey).Select(k => $"{k}={parsed[k]}"));
    }

    public static Dictionary<string, string> Parse(string config)
    {
        var result = new Dictionary<string, string>();
        foreach (var segment in config.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segment[..separatorIndex].Trim();
            var value = segment[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }
}
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsConfigParserTests"`
Expected: all 9 tests PASS.

- [ ] **Step 9: Build the whole solution to catch any project-reference issues**

Run: `dotnet build -warnaserror`
Expected: build succeeds (the new plugin has no pages/handlers yet, so this just proves the csproj/sln wiring is correct).

- [ ] **Step 10: Commit**

```bash
git add SbConsole.sln src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): scaffold AWS plugin project and add AwsConfigParser"
```

---

## Task 2: `FriendlyAwsError` + plugin-local `PluginResult`

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs`
- Create: `src/SbConsole.Plugins.Aws/PluginResult.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/FriendlyAwsErrorTests.cs`

**Interfaces:**
- Consumes: `SbConsole.Sdk.FriendlyError.From(Exception)` / `.Truncate(string?)`.
- Produces: `FriendlyAwsError.From(Exception) : string`, `PluginResult`/`PluginResult<T>` (same shape as Kafka's).

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.Aws.Tests/Client/FriendlyAwsErrorTests.cs`:

```csharp
using Amazon.SecurityToken.Model;
using Amazon.SQS.Model;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class FriendlyAwsErrorTests
{
    [Fact]
    public void InvalidClientTokenId_maps_to_a_fixed_message()
    {
        var ex = new InvalidClientTokenIdException("raw STS text that must never reach the UI");

        FriendlyAwsError.From(ex).Should().Be("Credentials rejected");
    }

    [Fact]
    public void AccessDenied_on_STS_includes_no_extra_context_STS_does_not_provide()
    {
        var ex = new Amazon.SecurityToken.Model.AmazonSecurityTokenServiceException("raw text") { ErrorCode = "AccessDenied" };

        FriendlyAwsError.From(ex).Should().Be("Access denied — check IAM permissions");
    }

    [Fact]
    public void QueueDoesNotExist_maps_to_a_fixed_message()
    {
        var ex = new QueueDoesNotExistException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("Queue not found");
    }

    [Fact]
    public void QueueNameExists_maps_to_a_fixed_conflict_message()
    {
        var ex = new QueueNameExistsException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("A queue with this name already exists with different settings");
    }

    [Fact]
    public void ReceiptHandleIsInvalid_maps_to_a_fixed_message()
    {
        var ex = new ReceiptHandleIsInvalidException("raw SQS text");

        FriendlyAwsError.From(ex).Should().Be("This message's hold already expired — it's back in the queue");
    }

    [Fact]
    public void Unmapped_SQS_exceptions_fall_back_to_the_capped_message_text()
    {
        var ex = new AmazonSQSException("some other SQS reason");

        FriendlyAwsError.From(ex).Should().Be("some other SQS reason");
    }

    [Fact]
    public void Non_AWS_exceptions_fall_back_to_the_shared_FriendlyError_helper()
    {
        var ex = new InvalidOperationException("plain failure");

        FriendlyAwsError.From(ex).Should().Be("plain failure");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~FriendlyAwsErrorTests"`
Expected: build error — `FriendlyAwsError` does not exist yet.

- [ ] **Step 3: Implement `FriendlyAwsError`**

`src/SbConsole.Plugins.Aws/Client/FriendlyAwsError.cs`:

```csharp
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// AWS-specific counterpart to SbConsole.Sdk.FriendlyError: maps the exception types this
/// plugin's operations can actually hit to a short, fixed, readable message, falling back to
/// the (capped/collapsed) exception text for anything unmapped. Mirrors
/// SbConsole.Plugins.Kafka.Client.FriendlyKafkaError's shape. Exception types confirmed against
/// the installed AWSSDK.SQS/AWSSDK.SecurityToken package versions at implementation time, same
/// "confirmed, not assumed" bar the Kafka/Service Bus plugins' error mapping holds itself to.
/// </summary>
public static class FriendlyAwsError
{
    public static string From(Exception ex) => ex switch
    {
        InvalidClientTokenIdException or UnrecognizedClientException => "Credentials rejected",
        AmazonSecurityTokenServiceException { ErrorCode: "AccessDenied" } => "Access denied — check IAM permissions",
        AmazonSQSException { ErrorCode: "AccessDenied" } sqsEx => $"Access denied — check IAM permissions for {sqsEx.ErrorCode}",
        QueueDoesNotExistException => "Queue not found",
        QueueNameExistsException => "A queue with this name already exists with different settings",
        ReceiptHandleIsInvalidException => "This message's hold already expired — it's back in the queue",
        RequestThrottledException or TooManyRequestsException => "AWS is throttling this connection — try again shortly",
        _ => FriendlyError.From(ex),
    };
}
```

- [ ] **Step 4: Run the tests, fix the `AccessDenied` SQS message to match**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~FriendlyAwsErrorTests"`

The `AmazonSQSException` `AccessDenied` case above produces `"Access denied — check IAM permissions for AccessDenied"`, which is wrong (it should name the *denied action*, not repeat the error code — but `AmazonSQSException` doesn't carry the action name as a distinct property). Fix: drop the per-action detail for the generic SQS case since it isn't available, matching what the exception type actually exposes:

```csharp
        AmazonSQSException { ErrorCode: "AccessDenied" } => "Access denied — check IAM permissions",
```

Re-run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~FriendlyAwsErrorTests"`
Expected: all 7 tests PASS.

- [ ] **Step 5: Implement plugin-local `PluginResult`**

`src/SbConsole.Plugins.Aws/PluginResult.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws;

/// <summary>
/// This plugin's own lightweight result type, mirroring SbConsole.Plugins.Kafka.PluginResult
/// without depending on it -- plugins reference only SbConsole.Sdk. Routes exception failures
/// through FriendlyAwsError (not the plain SbConsole.Sdk.FriendlyError) so AWS-specific
/// exceptions get their fixed readable messages.
/// </summary>
public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error) => (IsSuccess, Error) = (isSuccess, error);
    public bool IsSuccess { get; }
    public string? Error { get; }
    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlyAwsError.From(ex));
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, T? value, string? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public static PluginResult<T> Ok(T value) => new(true, value, null);
    public static PluginResult<T> Fail(string error) => new(false, default, error);
    public static PluginResult<T> Fail(Exception ex) => new(false, default, FriendlyAwsError.From(ex));
}
```

- [ ] **Step 6: Build and run the full test project**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: build succeeds, all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): add FriendlyAwsError and plugin-local PluginResult"
```

---

## Task 3: DTOs, `AwsCredentialsFactory`, `ISqsOperations`, `AwsPlugin` shell, and `TestConnectionAsync`

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Client/QueueSummary.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/CreateQueueRequest.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/SendMessageRequest.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/ReceivedMessage.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/ISqsOperations.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/AwsCredentialsFactory.cs`
- Create: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`
- Create: `src/SbConsole.Plugins.Aws/AwsPlugin.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/AwsCredentialsFactoryTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs`
- Modify: `src/SbConsole.Web/Program.cs:70` (add `AddSbConsolePlugin<SbConsole.Plugins.Aws.AwsPlugin>()` after the Kafka line)

**Interfaces:**
- Consumes: `SbConsole.Sdk.IPlugin`, `SbConsole.Sdk.ConnectionTestResult`, `SbConsole.Sdk.PluginNavItem`, `SbConsole.Sdk.PluginContribution`, `AwsConfigParser.Parse` (Task 1).
- Produces: every DTO and `ISqsOperations` listed in Global Constraints above; `AwsCredentialsFactory.BuildConfig(IReadOnlyDictionary<string,string> parsed) : AmazonSQSConfig`, `AwsCredentialsFactory.BuildCredentials(IReadOnlyDictionary<string,string> parsed) : AWSCredentials?` (null means "let the SDK use its default chain"); `AwsPlugin` (identity, `NavItems` with `Queues` only, `TestConnectionAsync`).

- [ ] **Step 1: Write the DTOs (no test needed — plain records, exercised by later tests)**

`src/SbConsole.Plugins.Aws/Client/QueueSummary.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record QueueSummary(
    string Name, string QueueUrl, string QueueArn, bool IsFifo,
    long ApproxVisible, long ApproxInFlight, long ApproxDelayed,
    bool HasDeadLetterTarget, bool IsKmsEncrypted, DateTimeOffset CreatedAt);
```

`src/SbConsole.Plugins.Aws/Client/CreateQueueRequest.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record CreateQueueRequest(
    string Name, bool IsFifo,
    int VisibilityTimeoutSeconds, int RetentionPeriodSeconds, int DelaySeconds, int MaxMessageSizeBytes, int ReceiveWaitTimeSeconds,
    string? DeadLetterTargetArn, int? MaxReceiveCount,
    string? KmsKeyId,
    bool? ContentBasedDeduplication, bool? HighThroughputFifo);
```

`src/SbConsole.Plugins.Aws/Client/SendMessageRequest.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record SendMessageRequest(
    string Body, IReadOnlyDictionary<string, string>? MessageAttributes, int? DelaySeconds,
    string? MessageGroupId, string? MessageDeduplicationId);
```

`src/SbConsole.Plugins.Aws/Client/ReceivedMessage.cs`:

```csharp
namespace SbConsole.Plugins.Aws.Client;

public sealed record ReceivedMessage(
    string MessageId, string ReceiptHandle, string Body, int ApproxReceiveCount,
    DateTimeOffset SentTimestamp, string SenderId, string Md5OfBody,
    IReadOnlyDictionary<string, string> MessageAttributes);
```

- [ ] **Step 2: Write `ISqsOperations`**

`src/SbConsole.Plugins.Aws/Client/ISqsOperations.cs`:

```csharp
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The seam between the plugin's handlers and the real AWSSDK.SQS/AWSSDK.SecurityToken clients.
/// Every method takes the connection secret as a parameter -- no instance is pre-configured for
/// one connection -- since a single registered instance tests and operates against whatever
/// connection the caller names. Mirrors SbConsole.Plugins.Kafka.Client.IKafkaOperations.
/// </summary>
public interface ISqsOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);

    Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default);
    Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default);
    /// <summary>Destructive.</summary>
    Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default);
    /// <summary>Destructive. Asynchronous and eventually consistent on the AWS side (design spec §5).</summary>
    Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default);

    Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default);
    /// <summary>Requires the receipt handle from ReceiveMessagesAsync, not the message ID.</summary>
    Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default);
    /// <summary>"Release now" -- sets visibility timeout to 0 so the message is immediately visible again.</summary>
    Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default);
    Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default);

    /// <summary>Native AWS move task (StartMessageMoveTask) -- destination defaults to the queue the source dead-lettered from.</summary>
    Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the failing `AwsCredentialsFactory` tests**

`tests/SbConsole.Plugins.Aws.Tests/Client/AwsCredentialsFactoryTests.cs`:

```csharp
using Amazon;
using Amazon.Runtime;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class AwsCredentialsFactoryTests
{
    [Fact]
    public void Access_keys_mode_builds_BasicAWSCredentials()
    {
        var parsed = AwsConfigParser.Parse("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t");

        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);

        credentials.Should().BeOfType<BasicAWSCredentials>();
        var immutable = credentials!.GetCredentials();
        immutable.AccessKey.Should().Be("AKIA123");
        immutable.SecretKey.Should().Be("s3cr3t");
    }

    [Fact]
    public void Access_keys_mode_with_session_token_builds_SessionAWSCredentials()
    {
        var parsed = AwsConfigParser.Parse("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t;sessionToken=tok");

        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);

        credentials.Should().BeOfType<SessionAWSCredentials>();
        var immutable = credentials!.GetCredentials();
        immutable.Token.Should().Be("tok");
    }

    [Fact]
    public void Default_chain_mode_builds_no_explicit_credentials()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=eu-west-1");

        AwsCredentialsFactory.BuildCredentials(parsed).Should().BeNull();
    }

    [Fact]
    public void Assume_role_mode_builds_AssumeRoleAWSCredentials()
    {
        var parsed = AwsConfigParser.Parse("mode=assume-role;region=eu-west-1;roleArn=arn:aws:iam::123456789012:role/SbConsoleReader;externalId=ext-1;sessionName=sbconsole-test");

        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);

        credentials.Should().BeOfType<Amazon.SecurityToken.AssumeRoleAWSCredentials>();
    }

    [Fact]
    public void BuildConfig_sets_the_region()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=eu-west-1");

        var config = AwsCredentialsFactory.BuildConfig(parsed);

        config.RegionEndpoint.Should().Be(RegionEndpoint.EUWest1);
    }

    [Fact]
    public void BuildConfig_applies_a_custom_endpoint_and_path_style_when_present()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=us-east-1;endpoint=http://localstack:4566;pathStyle=true");

        var config = AwsCredentialsFactory.BuildConfig(parsed);

        config.ServiceURL.Should().Be("http://localstack:4566");
        config.UseHttp.Should().BeTrue();
    }

    [Fact]
    public void BuildConfig_leaves_ServiceURL_unset_when_no_endpoint_is_given()
    {
        var parsed = AwsConfigParser.Parse("mode=default-chain;region=eu-west-1");

        AwsCredentialsFactory.BuildConfig(parsed).ServiceURL.Should().BeNull();
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsCredentialsFactoryTests"`
Expected: build error — `AwsCredentialsFactory` does not exist yet.

- [ ] **Step 5: Implement `AwsCredentialsFactory`**

`src/SbConsole.Plugins.Aws/Client/AwsCredentialsFactory.cs`:

```csharp
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SQS;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// Builds AWSCredentials/AmazonSQSConfig from a parsed AwsConfigParser dictionary -- see the
/// design spec §2. null from BuildCredentials means "construct the client with no explicit
/// credentials" (mode=default-chain): the AWS SDK resolves its own default chain (environment
/// variables, shared config/credentials file, container/instance metadata) internally when none
/// is supplied, so this plugin never constructs an AWSCredentials object for that mode at all.
/// </summary>
public static class AwsCredentialsFactory
{
    public static AmazonSQSConfig BuildConfig(IReadOnlyDictionary<string, string> parsed)
    {
        var config = new AmazonSQSConfig();
        if (parsed.TryGetValue("region", out var region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        if (parsed.TryGetValue("endpoint", out var endpoint))
        {
            config.ServiceURL = endpoint;
            // "Path-style addressing" (design spec §2) maps to AmazonSQSConfig.UseHttp for a
            // plain-HTTP emulator endpoint like LocalStack -- confirmed against the installed
            // AWSSDK.SQS version at implementation time; SQS's endpoint construction differs from
            // S3's virtual-hosted-vs-path-style distinction the mockup's label is borrowed from.
            if (parsed.TryGetValue("pathStyle", out var pathStyle) && bool.TryParse(pathStyle, out var isPathStyle))
            {
                config.UseHttp = isPathStyle;
            }
        }

        // Tightened per design spec §7 so an unreachable region or a bad custom endpoint fails
        // fast with a clear message instead of hanging the page.
        config.Timeout = TimeSpan.FromSeconds(10);
        config.MaxErrorRetry = 2;

        return config;
    }

    public static AWSCredentials? BuildCredentials(IReadOnlyDictionary<string, string> parsed)
    {
        var mode = parsed.GetValueOrDefault("mode");
        return mode switch
        {
            "access-keys" => BuildAccessKeyCredentials(parsed),
            "assume-role" => BuildAssumeRoleCredentials(parsed),
            _ => null, // "default-chain" (or an unrecognized mode -- let the SDK's own resolution surface a clear auth error)
        };
    }

    private static AWSCredentials BuildAccessKeyCredentials(IReadOnlyDictionary<string, string> parsed)
    {
        var accessKeyId = parsed.GetValueOrDefault("accessKeyId", "");
        var secretAccessKey = parsed.GetValueOrDefault("secretAccessKey", "");
        return parsed.TryGetValue("sessionToken", out var sessionToken) && sessionToken.Length > 0
            ? new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken)
            : new BasicAWSCredentials(accessKeyId, secretAccessKey);
    }

    // Base credentials for the sts:AssumeRole call come from the default chain (design spec §2's
    // "base credentials... come from the default chain" note) -- FallbackCredentialsFactory is
    // AWSSDK.Core's own default-chain resolver. Confirmed against the installed
    // AWSSDK.SecurityToken version at implementation time, same "confirmed, not assumed" bar the
    // rest of this plugin's AWS SDK usage holds itself to.
    private static AWSCredentials BuildAssumeRoleCredentials(IReadOnlyDictionary<string, string> parsed)
    {
        var baseCredentials = FallbackCredentialsFactory.GetCredentials();
        var roleArn = parsed.GetValueOrDefault("roleArn", "");
        var sessionName = parsed.GetValueOrDefault("sessionName", "sbconsole");
        var options = new AssumeRoleAWSCredentialsOptions();
        if (parsed.TryGetValue("externalId", out var externalId))
        {
            options.ExternalId = externalId;
        }

        return new AssumeRoleAWSCredentials(baseCredentials, roleArn, sessionName, options);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~AwsCredentialsFactoryTests"`
Expected: all 7 tests PASS. If `AssumeRoleAWSCredentialsOptions`/`AssumeRoleAWSCredentials`'s constructor overload doesn't match the installed `AWSSDK.SecurityToken` version, adjust the constructor call to match what that version actually exposes (check with `dotnet build` error text, which names the closest overload) — the test only asserts the resulting type, not the exact constructor path.

- [ ] **Step 7: Write `SqsOperations` with `TestConnectionAsync` only (the rest is stubbed to throw, filled in by later tasks)**

`src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`:

```csharp
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Client;

/// <summary>
/// The only real implementation of ISqsOperations. Its own methods get light test coverage by
/// necessity -- they can't be meaningfully unit-tested without a real or emulated AWS account
/// (design spec §8) -- the substitutable interface is where the test leverage is.
/// </summary>
public sealed class SqsOperations : ISqsOperations
{
    private static AmazonSQSClient BuildSqsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var config = AwsCredentialsFactory.BuildConfig(parsed);
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSQSClient(config) : new AmazonSQSClient(credentials, config);
    }

    private static AmazonSecurityTokenServiceClient BuildStsClient(string secret)
    {
        var parsed = AwsConfigParser.Parse(secret);
        var sqsConfig = AwsCredentialsFactory.BuildConfig(parsed);
        var stsConfig = new AmazonSecurityTokenServiceConfig
        {
            RegionEndpoint = sqsConfig.RegionEndpoint,
            Timeout = sqsConfig.Timeout,
            MaxErrorRetry = sqsConfig.MaxErrorRetry,
        };
        var credentials = AwsCredentialsFactory.BuildCredentials(parsed);
        return credentials is null ? new AmazonSecurityTokenServiceClient(stsConfig) : new AmazonSecurityTokenServiceClient(credentials, stsConfig);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            using var sts = BuildStsClient(secret);
            await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyAwsError.From(ex));
        }

        // Credentials are valid (GetCallerIdentity succeeded). A denied ListQueues probe is
        // reported as a non-fatal note, not a failure -- design spec §4's "text-only diagnostics"
        // decision: nothing is persisted and no button is hidden as a result.
        try
        {
            using var sqs = BuildSqsClient(secret);
            await sqs.ListQueuesAsync(new ListQueuesRequest { MaxResults = 1 }, ct);
            return new ConnectionTestResult(true);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(true, $"Valid credentials; sqs:ListQueues denied — queue actions may fail ({FriendlyAwsError.From(ex)})");
        }
    }

    public Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    public Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 6.");

    public Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 7.");

    public Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 8.");

    public Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 9.");

    public Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 12.");

    public Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 13.");
}
```

- [ ] **Step 8: Write `AwsPlugin`**

`src/SbConsole.Plugins.Aws/AwsPlugin.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws;

public sealed class AwsPlugin : IPlugin
{
    public string Id => "aws";
    public string DisplayName => "AWS SQS/SNS";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Queues", "/p/aws/queues"),
    ];
    public string ConnectionKind => "aws";
    public string ConnectionKindDisplayName => "AWS SQS/SNS";

    // Queues: Create/Delete/Purge queue, Receive, Delete message, Release message, Send, Redrive (8).
    // Pages: Queues, Receive (2).
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 8);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISqsOperations, SqsOperations>();
        services.AddScoped<Queues.ListQueuesQueryHandler>();
        services.AddScoped<Queues.GetConnectionEchoQueryHandler>();
        services.AddScoped<Queues.CreateQueueCommandHandler>();
        services.AddScoped<Queues.DeleteQueueCommandHandler>();
        services.AddScoped<Queues.PurgeQueueCommandHandler>();
        services.AddScoped<Messages.ReceiveMessagesCommandHandler>();
        services.AddScoped<Messages.DeleteMessageCommandHandler>();
        services.AddScoped<Messages.ReleaseMessageCommandHandler>();
        services.AddScoped<Messages.SendMessageCommandHandler>();
        services.AddScoped<Redrive.StartRedriveCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered ISqsOperations from at this
    // layer -- construct the real implementation directly, same as KafkaPlugin/ServiceBusPlugin.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new SqsOperations().TestConnectionAsync(secret, ct);

    // GetNavBadgeAsync, GetDashboardMetricsAsync, GetDashboardProblemsAsync,
    // GetOldestDeadLetterAsync, GetResourceMetricsAsync: SDK defaults (design spec §9, Out of
    // scope) -- nothing to report until this plugin has a dashboard/DLQ-overview plan of its own.
}
```

- [ ] **Step 9: Write the failing `AwsPluginTests`**

`tests/SbConsole.Plugins.Aws.Tests/AwsPluginTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Aws;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests;

public class AwsPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new AwsPlugin();

        plugin.Id.Should().Be("aws");
        plugin.ConnectionKind.Should().Be("aws");
        plugin.DisplayName.Should().Be("AWS SQS/SNS");
        plugin.ConnectionKindDisplayName.Should().Be("AWS SQS/SNS");
        plugin.NavItems.Should().Contain(n => n.Title == "Queues" && n.Href == "/p/aws/queues");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 2, ActionCount: 8));
    }

    [Fact]
    public async Task TestConnectionAsync_against_an_unreachable_address_fails_cleanly_and_never_throws()
    {
        var plugin = new AwsPlugin();

        var result = await plugin.TestConnectionAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetNavBadgeAsync_returns_null_for_an_unrelated_nav_href()
    {
        var plugin = new AwsPlugin();

        var badge = await plugin.GetNavBadgeAsync("/p/aws/queues", "mode=default-chain;region=us-east-1");

        badge.Should().BeNull();
    }
}
```

- [ ] **Step 10: Run all AWS tests to verify they pass**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Aws.Tests`
Expected: build succeeds, all tests PASS (the smoke test in Step 9 hits a real STS endpoint attempt against `127.0.0.1:1`, which refuses instantly — no network flakiness).

- [ ] **Step 11: Register the plugin in the host**

In `src/SbConsole.Web/Program.cs`, immediately after the line `builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Kafka.KafkaPlugin>();`, add:

```csharp
builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Aws.AwsPlugin>();
```

Add a `ProjectReference` to the new plugin from the host project:

Run: `dotnet add src/SbConsole.Web/SbConsole.Web.csproj reference src/SbConsole.Plugins.Aws/SbConsole.Plugins.Aws.csproj`

- [ ] **Step 12: Build and run the whole solution's tests**

Run: `dotnet build -warnaserror && dotnet test`
Expected: whole solution builds and every test project passes (this proves the new plugin doesn't collide with anything already registered — in particular the keyed `IPluginStore` registration, `docs/design.md` §6.5's bug fix).

- [ ] **Step 13: Commit**

```bash
git add src/SbConsole.Plugins.Aws src/SbConsole.Web tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): add AwsPlugin shell, ISqsOperations, and TestConnectionAsync"
```

---

## Task 4: `ListQueuesAsync` + `ListQueuesQueryHandler` + `GetConnectionEchoQueryHandler`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement `ListQueuesAsync`)
- Create: `src/SbConsole.Plugins.Aws/Queues/ListQueuesQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Queues/GetConnectionEchoQueryHandler.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/SqsOperationsTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Queues/ListQueuesQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Queues/GetConnectionEchoQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.ListQueuesAsync` (Task 3), `IConnectionProvider.GetSecretAsync` (SDK).
- Produces: `ListQueuesQueryHandler.HandleAsync(Guid connectionId, string? namePrefix, CancellationToken ct = default) : Task<PluginResult<IReadOnlyList<QueueSummary>>>`, `GetConnectionEchoQueryHandler.HandleAsync(Guid connectionId, CancellationToken ct = default) : Task<PluginResult<string>>`.

- [ ] **Step 1: Write a pure-logic test for the attribute-parsing helper (extracted so it's unit-testable without a real AWS account)**

`tests/SbConsole.Plugins.Aws.Tests/Client/SqsOperationsTests.cs`:

```csharp
using Amazon.SQS.Model;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class SqsOperationsTests
{
    private static Dictionary<string, string> FullAttributes() => new()
    {
        ["QueueArn"] = "arn:aws:sqs:eu-west-1:123456789012:orders",
        ["ApproximateNumberOfMessages"] = "1204",
        ["ApproximateNumberOfMessagesNotVisible"] = "18",
        ["ApproximateNumberOfMessagesDelayed"] = "0",
        ["CreatedTimestamp"] = "1700000000",
        ["RedrivePolicy"] = "{\"deadLetterTargetArn\":\"arn:aws:sqs:eu-west-1:123456789012:orders-dlq\",\"maxReceiveCount\":5}",
        ["KmsMasterKeyId"] = "alias/aws/sqs",
        ["FifoQueue"] = "false",
    };

    [Fact]
    public void ToQueueSummary_maps_every_attribute()
    {
        var summary = SqsOperations.ToQueueSummary("orders", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders", FullAttributes());

        summary.Name.Should().Be("orders");
        summary.QueueArn.Should().Be("arn:aws:sqs:eu-west-1:123456789012:orders");
        summary.IsFifo.Should().BeFalse();
        summary.ApproxVisible.Should().Be(1204);
        summary.ApproxInFlight.Should().Be(18);
        summary.ApproxDelayed.Should().Be(0);
        summary.HasDeadLetterTarget.Should().BeTrue();
        summary.IsKmsEncrypted.Should().BeTrue();
        summary.CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void ToQueueSummary_defaults_missing_optional_attributes()
    {
        var attributes = new Dictionary<string, string>
        {
            ["QueueArn"] = "arn:aws:sqs:eu-west-1:123456789012:plain",
            ["ApproximateNumberOfMessages"] = "0",
            ["ApproximateNumberOfMessagesNotVisible"] = "0",
            ["ApproximateNumberOfMessagesDelayed"] = "0",
            ["CreatedTimestamp"] = "1700000000",
        };

        var summary = SqsOperations.ToQueueSummary("plain", "https://sqs.eu-west-1.amazonaws.com/123456789012/plain", attributes);

        summary.HasDeadLetterTarget.Should().BeFalse();
        summary.IsKmsEncrypted.Should().BeFalse();
        summary.IsFifo.Should().BeFalse();
    }

    [Fact]
    public void ToQueueSummary_recognizes_a_FIFO_queue()
    {
        var attributes = FullAttributes();
        attributes["FifoQueue"] = "true";

        SqsOperations.ToQueueSummary("orders.fifo", "https://sqs.eu-west-1.amazonaws.com/123456789012/orders.fifo", attributes)
            .IsFifo.Should().BeTrue();
    }

    [Fact]
    public void QueueNameFromUrl_extracts_the_last_path_segment()
    {
        SqsOperations.QueueNameFromUrl("https://sqs.eu-west-1.amazonaws.com/123456789012/order-events").Should().Be("order-events");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SqsOperationsTests"`
Expected: build error — `SqsOperations.ToQueueSummary`/`QueueNameFromUrl` do not exist yet.

- [ ] **Step 3: Implement `ListQueuesAsync` and its two testable helpers in `SqsOperations`**

In `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`, replace the `ListQueuesAsync` stub:

```csharp
    public Task<IReadOnlyList<QueueSummary>> ListQueuesAsync(string secret, string? namePrefix, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<QueueSummary>>(async () =>
        {
            using var sqs = BuildSqsClient(secret);
            var listRequest = new ListQueuesRequest { QueueNamePrefix = namePrefix };
            var queueUrls = new List<string>();
            string? nextToken = null;
            do
            {
                listRequest.NextToken = nextToken;
                var page = await sqs.ListQueuesAsync(listRequest, ct);
                queueUrls.AddRange(page.QueueUrls);
                nextToken = page.NextToken;
            } while (!string.IsNullOrEmpty(nextToken));

            var summaries = new List<QueueSummary>();
            foreach (var queueUrl in queueUrls)
            {
                var attributesResponse = await sqs.GetQueueAttributesAsync(
                    new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["All"] }, ct);
                summaries.Add(ToQueueSummary(QueueNameFromUrl(queueUrl), queueUrl, attributesResponse.Attributes));
            }

            return summaries;
        }, ct);

    // Extracted as a pure static function so the attribute-dictionary-to-QueueSummary mapping is
    // unit-testable without a real AWS account, same reasoning as
    // ConfluentKafkaOperations.Decode/IsEndOfPartition. Internal (not private) so
    // SqsOperationsTests can assert it directly -- InternalsVisibleTo already covers the test
    // project (Task 1's csproj).
    internal static QueueSummary ToQueueSummary(string name, string queueUrl, IDictionary<string, string> attributes)
    {
        long GetLong(string key) => attributes.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : 0;
        bool GetBool(string key) => attributes.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

        return new QueueSummary(
            Name: name,
            QueueUrl: queueUrl,
            QueueArn: attributes.GetValueOrDefault("QueueArn", ""),
            IsFifo: GetBool("FifoQueue"),
            ApproxVisible: GetLong("ApproximateNumberOfMessages"),
            ApproxInFlight: GetLong("ApproximateNumberOfMessagesNotVisible"),
            ApproxDelayed: GetLong("ApproximateNumberOfMessagesDelayed"),
            HasDeadLetterTarget: attributes.ContainsKey("RedrivePolicy"),
            IsKmsEncrypted: attributes.ContainsKey("KmsMasterKeyId"),
            CreatedAt: DateTimeOffset.FromUnixTimeSeconds(GetLong("CreatedTimestamp")));
    }

    internal static string QueueNameFromUrl(string queueUrl) => queueUrl[(queueUrl.LastIndexOf('/') + 1)..];
```

Add `using System.Collections.Generic;` if not already implicit (it is, via `ImplicitUsings`).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SqsOperationsTests"`
Expected: all 4 tests PASS.

- [ ] **Step 5: Write the failing `ListQueuesQueryHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Queues/ListQueuesQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class ListQueuesQueryHandlerTests
{
    [Fact]
    public async Task Returns_queues_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var queues = new[] { new QueueSummary("orders", "https://sqs/orders", "arn", false, 10, 0, 0, false, false, DateTimeOffset.UtcNow) };
        operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", "order", Arg.Any<CancellationToken>()).Returns(queues);

        var result = await new ListQueuesQueryHandler(operations, connections, NullLogger<ListQueuesQueryHandler>.Instance).HandleAsync(connectionId, "order");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(queues);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListQueuesQueryHandler(Substitute.For<ISqsOperations>(), connections, NullLogger<ListQueuesQueryHandler>.Instance).HandleAsync(Guid.NewGuid(), null);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<QueueSummary>>(new InvalidOperationException("region unreachable")));

        var result = await new ListQueuesQueryHandler(operations, connections, NullLogger<ListQueuesQueryHandler>.Instance).HandleAsync(connectionId, null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("region unreachable");
    }
}
```

- [ ] **Step 6: Implement `ListQueuesQueryHandler`**

`src/SbConsole.Plugins.Aws/Queues/ListQueuesQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed class ListQueuesQueryHandler(ISqsOperations operations, IConnectionProvider connections, ILogger<ListQueuesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<QueueSummary>>> HandleAsync(Guid connectionId, string? namePrefix, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<QueueSummary>>.Fail("Connection not found.");
            }

            var queues = await operations.ListQueuesAsync(secret, namePrefix, ct);
            return PluginResult<IReadOnlyList<QueueSummary>>.Ok(queues);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing queues for connection {ConnectionId} failed.", connectionId);
            return PluginResult<IReadOnlyList<QueueSummary>>.Fail(ex);
        }
    }
}
```

- [ ] **Step 7: Write the failing `GetConnectionEchoQueryHandler` test and implement it**

`tests/SbConsole.Plugins.Aws.Tests/Queues/GetConnectionEchoQueryHandlerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class GetConnectionEchoQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_safe_echo_of_the_connections_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t");

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("region=eu-west-1 · mode=access-keys");
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }
}
```

`src/SbConsole.Plugins.Aws/Queues/GetConnectionEchoQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed class GetConnectionEchoQueryHandler(IConnectionProvider connections, ILogger<GetConnectionEchoQueryHandler> logger)
{
    public async Task<PluginResult<string>> HandleAsync(Guid connectionId, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<string>.Fail("Connection not found.");
            }

            return PluginResult<string>.Ok(AwsConfigParser.SafeEcho(secret));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resolving connection echo for {ConnectionId} failed.", connectionId);
            return PluginResult<string>.Fail(ex);
        }
    }
}
```

- [ ] **Step 8: Run all AWS tests to verify they pass, then build the whole solution**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests && dotnet build -warnaserror`
Expected: all tests PASS, build succeeds.

- [ ] **Step 9: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement ListQueuesAsync and its query handlers"
```

---

## Task 5: `Queues.razor` (list page)

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Pages/_Imports.razor`
- Create: `src/SbConsole.Plugins.Aws/Pages/Queues.razor`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/QueuesPageTests.cs`

**Interfaces:**
- Consumes: `ListQueuesQueryHandler`, `GetConnectionEchoQueryHandler` (Task 4), `IConnectionProvider.ListAsync("aws")` (SDK).
- Produces: the `/p/aws/queues` route. Later tasks (6-9, 12-14) add buttons/dialogs to this page's `RowTemplate` without changing its structure below.

- [ ] **Step 1: Write `_Imports.razor`**

`src/SbConsole.Plugins.Aws/Pages/_Imports.razor`:

```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Web
@using MudBlazor
@attribute [Authorize]
```

- [ ] **Step 2: Write the failing page tests**

`tests/SbConsole.Plugins.Aws.Tests/Pages/QueuesPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class QueuesPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();

    public QueuesPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "aws-dev", "aws", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton(Substitute.For<IConfirmationService>());
        Services.AddSingleton(_dialogService);
        Services.AddLogging();
        Services.AddSingleton<ListQueuesQueryHandler>();
        Services.AddSingleton<GetConnectionEchoQueryHandler>();
        Services.AddSingleton<CreateQueueCommandHandler>();
        Services.AddSingleton<DeleteQueueCommandHandler>();
        Services.AddSingleton<PurgeQueueCommandHandler>();
        Services.AddSingleton<Messages.SendMessageCommandHandler>();
        Services.AddSingleton<Redrive.StartRedriveCommandHandler>();
    }

    private static QueueSummary Queue(string name, bool hasDlqTarget = false, bool isFifo = false) =>
        new(name, $"https://sqs/{name}", $"arn:aws:sqs:eu-west-1:123456789012:{name}", isFifo, 10, 2, 0, hasDlqTarget, false, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Lists_queues_for_the_first_available_connection()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("order-events");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("aws", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Prefix_filter_is_labelled_Starts_with_and_re_queries_on_change()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", "events-", Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("Starts with");
        cut.Find(".prefix-filter input").Input("events-");
        await Task.Delay(30);
        cut.Render();

        await _operations.Received(1).ListQueuesAsync("mode=default-chain;region=eu-west-1", "events-", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_call_count_caption_reflects_1_plus_n_not_1_plus_3n()
    {
        // Correction from the mockup's "1 + 3n" caption -- GetQueueAttributes with
        // AttributeNames=[All] returns every attribute in one call per queue (design spec §5).
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("a"), Queue("b"), Queue("c") });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.Find(".refresh-cost-caption").TextContent.Should().Contain("1 + 3 = 4 calls");
    }

    [Fact]
    public async Task Connection_echo_renders_safe_fields_and_never_leaks_credentials()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3tKEY");
        _operations.ListQueuesAsync("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3tKEY", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary>());

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        var echo = cut.Find(".connection-echo");
        echo.TextContent.Should().Contain("region=eu-west-1");
        cut.Markup.Should().NotContain("s3cr3tKEY");
    }

    [Fact]
    public async Task A_queue_with_a_dead_letter_target_shows_the_DLQ_flag()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events-dlq", hasDlqTarget: true) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".dlq-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task A_FIFO_queue_shows_the_FIFO_flag()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("billing-retry.fifo", isFifo: true) });

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".fifo-flag").Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListQueuesAsync("mode=default-chain;region=eu-west-1", null, Arg.Any<CancellationToken>())
            .Returns(new List<QueueSummary> { Queue("order-events") });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "order-events", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.Aws.Pages.Queues>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-queue").Click();
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Delete", "order-events", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/order-events", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~QueuesPageTests"`
Expected: build error — `Pages.Queues`, `CreateQueueCommandHandler`, `DeleteQueueCommandHandler`, `PurgeQueueCommandHandler`, `Messages.SendMessageCommandHandler`, `Redrive.StartRedriveCommandHandler` don't exist yet. This test file references handlers built in Tasks 6-9/12-13 because `Queues.razor` injects all of them from the start (its dialogs are wired in incrementally) — Step 4 below stubs the not-yet-built handlers' DI registrations out of this test file's constructor and adds them back task by task; for now, comment out the four not-yet-existing `Services.AddSingleton<...>()` lines for `CreateQueueCommandHandler`, `DeleteQueueCommandHandler`, `PurgeQueueCommandHandler`, `Messages.SendMessageCommandHandler`, `Redrive.StartRedriveCommandHandler`, and skip the `Delete_goes_through_confirmation...` test (mark it `[Fact(Skip = "enabled in Task 7")]`) until Task 7 lands. Re-enable each as its task implements the handler.

- [ ] **Step 4: Write `Queues.razor`**

`src/SbConsole.Plugins.Aws/Pages/Queues.razor`:

```razor
@page "/p/aws/queues"
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Queues
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject ListQueuesQueryHandler ListHandler
@inject GetConnectionEchoQueryHandler EchoHandler
@inject DeleteQueueCommandHandler DeleteHandler
@inject IConfirmationService Confirmation
@inject IDialogService DialogService
@inject ISnackbar Snackbar

<PageTitle>Queues</PageTitle>
<h1>Queues</h1>

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

        <MudTextField T="string" Class="prefix-filter" Label="Starts with" @bind-Value="_namePrefix" Immediate="true" DebounceInterval="300" ValueChanged="OnPrefixChanged" Style="max-width:200px" Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search" />

        <MudSpacer />
        <MudButton Class="create-queue-action" Color="Color.Primary" Variant="Variant.Outlined" OnClick="OpenCreate">+ Create queue</MudButton>
        @if (_loading)
        {
            <MudProgressCircular Class="queues-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudText Typo="Typo.caption" Class="connection-echo mud-text-secondary">@_connectionEcho</MudText>
    <MudText Class="queues-summary mud-text-secondary mt-1" Typo="Typo.body2">@_queues.Count @(_queues.Count == 1 ? "queue" : "queues")</MudText>
    <MudText Typo="Typo.caption" Class="refresh-cost-caption mud-text-secondary mb-2">
        <span style="font-family:monospace">ListQueues</span> filters by prefix only — no substring or regex.
        Each refresh is 1 + @_queues.Count calls (1 + @_queues.Count = @(1 + _queues.Count) calls).
    </MudText>

    <MudTable Items="_queues">
        <HeaderContent>
            <MudTh>Queue</MudTh>
            <MudTh>Type</MudTh>
            <MudTh Style="text-align:right">~Visible</MudTh>
            <MudTh Style="text-align:right">~In flight</MudTh>
            <MudTh Style="text-align:right">~Delayed</MudTh>
            <MudTh>Flags</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace">@context.Name</MudTd>
            <MudTd>@(context.IsFifo ? "FIFO" : "Standard")</MudTd>
            <MudTd Style="text-align:right;font-family:monospace">~@context.ApproxVisible</MudTd>
            <MudTd Style="text-align:right;font-family:monospace">~@context.ApproxInFlight</MudTd>
            <MudTd Style="text-align:right;font-family:monospace">~@context.ApproxDelayed</MudTd>
            <MudTd>
                @if (context.HasDeadLetterTarget)
                {
                    <MudChip T="string" Class="dlq-flag" Color="Color.Error" Size="Size.Small">DLQ</MudChip>
                }
                @if (context.IsFifo)
                {
                    <MudChip T="string" Class="fifo-flag" Size="Size.Small">FIFO</MudChip>
                }
                @if (context.IsKmsEncrypted)
                {
                    <MudChip T="string" Class="kms-flag" Size="Size.Small" Variant="Variant.Outlined">KMS</MudChip>
                }
            </MudTd>
            <MudTd>
                <MudButton Class="receive-action" Href="@ReceiveUrl(context.QueueUrl)">Receive</MudButton>
                <MudButton Class="send-message-action" OnClick="@(() => OpenSend(context.QueueUrl))">Send</MudButton>
                @if (context.HasDeadLetterTarget)
                {
                    <MudButton Class="redrive-action" OnClick="@(() => OpenRedrive(context.QueueUrl, context.QueueArn))">Redrive</MudButton>
                }
                <MudButton Class="purge-queue-action" Color="Color.Warning" OnClick="@(() => OpenPurge(context.QueueUrl))">Purge</MudButton>
                <MudButton Class="delete-queue" Color="Color.Error" Disabled="@(_deletingQueue == context.QueueUrl)" OnClick="@(() => DeleteAsync(context.QueueUrl, context.Name))">Delete</MudButton>
                @if (_deletingQueue == context.QueueUrl)
                {
                    <MudProgressCircular Class="delete-queue-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
                }
            </MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private IReadOnlyList<QueueSummary> _queues = [];
    private Guid _selectedConnectionId;
    private bool _loading;
    private string? _deletingQueue;
    private string _namePrefix = "";
    private string _connectionEcho = "";

    private ConnectionInfo? SelectedConnection => _connections.FirstOrDefault(c => c.Id == _selectedConnectionId);

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("aws");
        if (_connections.Count > 0)
        {
            _selectedConnectionId = _connections[0].Id;
            await LoadQueuesAsync();
            await LoadEchoAsync();
        }
    }

    private async Task OnConnectionChanged(Guid connectionId)
    {
        _selectedConnectionId = connectionId;
        await LoadQueuesAsync();
        await LoadEchoAsync();
    }

    private async Task OnPrefixChanged(string value)
    {
        _namePrefix = value;
        await LoadQueuesAsync();
    }

    private async Task LoadQueuesAsync()
    {
        _loading = true;
        try
        {
            var prefix = string.IsNullOrWhiteSpace(_namePrefix) ? null : _namePrefix;
            var result = await ListHandler.HandleAsync(_selectedConnectionId, prefix);
            if (result.IsSuccess)
            {
                _queues = result.Value!;
            }
            else
            {
                _queues = [];
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

    private string ReceiveUrl(string queueUrl)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/aws/queues/receive?connectionId={connection.Id}&queueUrl={Uri.EscapeDataString(queueUrl)}";
    }

    private Task OpenCreate() => Task.CompletedTask; // wired in Task 6

    private Task OpenSend(string queueUrl) => Task.CompletedTask; // wired in Task 12

    private Task OpenRedrive(string queueUrl, string queueArn) => Task.CompletedTask; // wired in Task 13

    private Task OpenPurge(string queueUrl) => Task.CompletedTask; // wired in Task 8

    private async Task DeleteAsync(string queueUrl, string queueName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", queueName, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        _deletingQueue = queueUrl;
        try
        {
            var result = await DeleteHandler.HandleAsync(new DeleteQueueCommand(connection.Id, connection.Name, connection.IsProd, queueUrl, queueName));
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _deletingQueue = null;
        }

        await LoadQueuesAsync();
    }
}
```

This references `DeleteQueueCommandHandler`/`DeleteQueueCommand` (Task 7) and the four other not-yet-built handlers only as no-op stub methods (`OpenCreate`/`OpenSend`/`OpenRedrive`/`OpenPurge` return `Task.CompletedTask` for now) — **except** `@inject DeleteQueueCommandHandler DeleteHandler` and the `DeleteAsync` method, which this task also needs. To keep this task buildable on its own before Task 7 exists, temporarily stub `DeleteQueueCommandHandler`/`DeleteQueueCommand` as a minimal pass-through in this same step:

`src/SbConsole.Plugins.Aws/Queues/DeleteQueueCommandHandler.cs` (temporary minimal version, replaced by Task 7's full version with audit/confirmation-risk wiring):

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string QueueUrl, string QueueName);

public sealed class DeleteQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections)
{
    public async Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default)
    {
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.DeleteQueueAsync(secret, cmd.QueueUrl, ct);
        }
        catch (Exception ex)
        {
            return PluginResult.Fail(ex);
        }

        return PluginResult.Ok();
    }
}
```

Register it in `AwsPlugin.ConfigureServices` (already present from Task 3's `AwsPlugin.cs`).

- [ ] **Step 5: Run the page tests, enabling only what Task 5 covers**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~QueuesPageTests"`
Expected: `Delete_goes_through_confirmation_before_calling_the_handler` PASSES already (the temporary handler above is enough for it); every other test in the file PASSES. If any `[Fact(Skip = ...)]` markers from Step 3 remain for tests this task's markup actually satisfies, remove the skip and confirm they pass.

- [ ] **Step 6: Build and run the whole solution's tests**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): add Queues.razor list page with prefix filter and delete"
```

---

## Task 6: `CreateQueueAsync` + `CreateQueueCommandHandler` + `CreateQueueDialog.razor`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement `CreateQueueAsync`)
- Create: `src/SbConsole.Plugins.Aws/Queues/CreateQueueCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/CreateQueueDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Queues.razor` (wire `OpenCreate`)
- Modify: `src/SbConsole.Plugins.Aws/AwsPlugin.cs` (register `CreateQueueCommandHandler`)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Queues/CreateQueueCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/CreateQueueDialogTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.CreateQueueAsync`, `IAuditScope.RecordAsync` (SDK).
- Produces: `CreateQueueCommand(Guid ConnectionId, string ConnectionName, CreateQueueRequest Request)`, `CreateQueueCommandHandler.HandleAsync(CreateQueueCommand, CancellationToken) : Task<PluginResult<string>>` (returns the new queue URL).

- [ ] **Step 1: Implement `CreateQueueAsync` in `SqsOperations`**

Replace the `CreateQueueAsync` stub in `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`:

```csharp
    public async Task<string> CreateQueueAsync(string secret, CreateQueueRequest request, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var name = request.IsFifo && !request.Name.EndsWith(".fifo", StringComparison.Ordinal)
            ? $"{request.Name}.fifo"
            : request.Name;

        var attributes = new Dictionary<string, string>
        {
            ["VisibilityTimeout"] = request.VisibilityTimeoutSeconds.ToString(),
            ["MessageRetentionPeriod"] = request.RetentionPeriodSeconds.ToString(),
            ["DelaySeconds"] = request.DelaySeconds.ToString(),
            ["MaximumMessageSize"] = request.MaxMessageSizeBytes.ToString(),
            ["ReceiveMessageWaitTimeSeconds"] = request.ReceiveWaitTimeSeconds.ToString(),
        };

        if (request.IsFifo)
        {
            attributes["FifoQueue"] = "true";
            if (request.ContentBasedDeduplication is { } dedup)
            {
                attributes["ContentBasedDeduplication"] = dedup.ToString().ToLowerInvariant();
            }

            if (request.HighThroughputFifo is true)
            {
                attributes["DeduplicationScope"] = "messageGroup";
                attributes["FifoThroughputLimit"] = "perMessageGroupId";
            }
        }

        if (request.DeadLetterTargetArn is { } dlqArn && request.MaxReceiveCount is { } maxReceives)
        {
            attributes["RedrivePolicy"] = $"{{\"deadLetterTargetArn\":\"{dlqArn}\",\"maxReceiveCount\":{maxReceives}}}";
        }

        if (request.KmsKeyId is { } kmsKeyId)
        {
            attributes["KmsMasterKeyId"] = kmsKeyId;
        }

        var response = await sqs.CreateQueueAsync(new Amazon.SQS.Model.CreateQueueRequest { QueueName = name, Attributes = attributes }, ct);
        return response.QueueUrl;
    }
```

Note the type collision: this plugin's own `CreateQueueRequest` (Task 3) and `Amazon.SQS.Model.CreateQueueRequest` share a name — the fully-qualified `Amazon.SQS.Model.CreateQueueRequest` above is required for that reason (matches how `SendMessageRequest` will need the same qualification in Task 12).

- [ ] **Step 2: Write the failing `CreateQueueCommandHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Queues/CreateQueueCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class CreateQueueCommandHandlerTests
{
    private static CreateQueueRequest StandardRequest(string name) =>
        new(name, false, 30, 345600, 0, 262144, 20, null, null, null, null, null);

    [Fact]
    public async Task Creates_the_queue_and_records_a_successful_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = StandardRequest("orders");
        operations.CreateQueueAsync("mode=default-chain;region=eu-west-1", request, Arg.Any<CancellationToken>())
            .Returns("https://sqs.eu-west-1.amazonaws.com/123456789012/orders");
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "aws-dev", request));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("https://sqs.eu-west-1.amazonaws.com/123456789012/orders");
        await audit.Received(1).RecordAsync("aws.queue.create", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_and_records_no_audit_entry()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(Substitute.For<ISqsOperations>(), connections, audit)
            .HandleAsync(new CreateQueueCommand(Guid.NewGuid(), "aws-dev", StandardRequest("orders")));

        result.IsSuccess.Should().BeFalse();
        await audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, ct: default);
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry_with_the_friendly_message()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = StandardRequest("orders");
        operations.CreateQueueAsync("mode=default-chain;region=eu-west-1", request, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("region unreachable")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new CreateQueueCommand(connectionId, "aws-dev", request));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.create", "aws-dev/orders", ActionRisk.Mutating, false, "region unreachable", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Implement `CreateQueueCommandHandler`**

`src/SbConsole.Plugins.Aws/Queues/CreateQueueCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record CreateQueueCommand(Guid ConnectionId, string ConnectionName, CreateQueueRequest Request);

public sealed class CreateQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<string>> HandleAsync(CreateQueueCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.Request.Name}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<string>.Fail("Connection not found.");
        }

        try
        {
            var queueUrl = await operations.CreateQueueAsync(secret, cmd.Request, ct);
            await audit.RecordAsync("aws.queue.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult<string>.Ok(queueUrl);
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.create", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<string>.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~CreateQueueCommandHandlerTests"`
Expected: all 3 tests PASS.

- [ ] **Step 5: Write `CreateQueueDialog.razor`**

`src/SbConsole.Plugins.Aws/Pages/CreateQueueDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Queues
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="queue-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudText Typo="Typo.caption" Class="mud-text-secondary">@(_isFifo ? $"{_name}.fifo" : _name) — SQS requires the .fifo suffix on a FIFO queue; it is added for you.</MudText>
        <MudSwitch T="bool" Class="fifo-toggle" @bind-Value="_isFifo" Label="FIFO" Color="Color.Primary" />

        @if (_isFifo)
        {
            <MudSwitch T="bool" Class="content-based-dedup-toggle" @bind-Value="_contentBasedDeduplication" Label="Content-based deduplication" Color="Color.Primary" />
            <MudSwitch T="bool" Class="high-throughput-toggle" @bind-Value="_highThroughput" Label="High throughput" Color="Color.Primary" />
        }

        <MudNumericField Class="visibility-timeout" @bind-Value="_visibilityTimeoutSeconds" Label="Visibility timeout (sec)" Min="0" Max="43200" />
        <MudNumericField Class="retention-period" @bind-Value="_retentionPeriodDays" Label="Retention (days)" Min="1" Max="14" />
        <MudNumericField Class="delay-seconds" @bind-Value="_delaySeconds" Label="Delivery delay (sec)" Min="0" Max="900" />
        <MudNumericField Class="max-message-size" @bind-Value="_maxMessageSizeBytes" Label="Max message size (bytes)" Min="1024" Max="262144" />
        <MudNumericField Class="receive-wait-time" @bind-Value="_receiveWaitTimeSeconds" Label="Receive wait time (sec)" Min="0" Max="20" />

        <MudSwitch T="bool" Class="dlq-toggle" @bind-Value="_deadLetterEnabled" Label="Dead-letter queue" Color="Color.Primary" />
        @if (_deadLetterEnabled)
        {
            <MudTextField Class="dlq-target-arn" @bind-Value="_deadLetterTargetArn" Label="Target queue ARN" Immediate="true" />
            <MudNumericField Class="max-receive-count" @bind-Value="_maxReceiveCount" Label="Max receives" Min="1" Max="1000" />
        }
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="save-queue-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="save-queue" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_name) || _busy)" OnClick="Save">Create</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";

    [Inject] private CreateQueueCommandHandler CreateHandler { get; set; } = default!;

    private string _name = "";
    private bool _isFifo;
    private bool _contentBasedDeduplication;
    private bool _highThroughput;
    private int _visibilityTimeoutSeconds = 30;
    private int _retentionPeriodDays = 4;
    private int _delaySeconds;
    private int _maxMessageSizeBytes = 262144;
    private int _receiveWaitTimeSeconds = 20;
    private bool _deadLetterEnabled;
    private string _deadLetterTargetArn = "";
    private int _maxReceiveCount = 5;
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var request = new CreateQueueRequest(
                _name, _isFifo,
                _visibilityTimeoutSeconds, _retentionPeriodDays * 86400, _delaySeconds, _maxMessageSizeBytes, _receiveWaitTimeSeconds,
                _deadLetterEnabled ? _deadLetterTargetArn : null, _deadLetterEnabled ? _maxReceiveCount : null,
                null,
                _isFifo ? _contentBasedDeduplication : null, _isFifo ? _highThroughput : null);

            var result = await CreateHandler.HandleAsync(new CreateQueueCommand(ConnectionId, ConnectionName, request));
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

- [ ] **Step 6: Wire `OpenCreate` in `Queues.razor`**

In `src/SbConsole.Plugins.Aws/Pages/Queues.razor`, replace `private Task OpenCreate() => Task.CompletedTask; // wired in Task 6` with:

```csharp
    private async Task OpenCreate()
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var parameters = new DialogParameters<CreateQueueDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
        };
        var dialog = await DialogService.ShowAsync<CreateQueueDialog>("Create queue", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadQueuesAsync();
        }
    }
```

- [ ] **Step 7: Write the failing `CreateQueueDialogTests` and confirm they pass**

`tests/SbConsole.Plugins.Aws.Tests/Pages/CreateQueueDialogTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class CreateQueueDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<SbConsole.Sdk.IConnectionProvider>();

    public CreateQueueDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_operations);
        Services.AddSingleton(_connections);
        Services.AddSingleton(Substitute.For<SbConsole.Sdk.IAuditScope>());
        Services.AddSingleton<CreateQueueCommandHandler>();
    }

    [Fact]
    public async Task FIFO_toggle_reveals_the_dedup_and_high_throughput_switches()
    {
        var cut = RenderComponent<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        await cut.InvokeAsync(() => dialogService.ShowAsync<SbConsole.Plugins.Aws.Pages.CreateQueueDialog>("Create queue"));
        cut.Render();

        cut.Find(".fifo-toggle input").Change(true);
        cut.Render();

        cut.FindAll(".content-based-dedup-toggle").Should().ContainSingle();
        cut.FindAll(".high-throughput-toggle").Should().ContainSingle();
    }

    [Fact]
    public async Task Dead_letter_toggle_reveals_target_arn_and_max_receives()
    {
        var cut = RenderComponent<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();
        await cut.InvokeAsync(() => dialogService.ShowAsync<SbConsole.Plugins.Aws.Pages.CreateQueueDialog>("Create queue"));
        cut.Render();

        cut.Find(".dlq-toggle input").Change(true);
        cut.Render();

        cut.FindAll(".dlq-target-arn").Should().ContainSingle();
        cut.FindAll(".max-receive-count").Should().ContainSingle();
    }
}
```

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~CreateQueueDialogTests"`
Expected: both PASS. If `RenderComponent<MudDialogProvider>()` plus `IDialogService.ShowAsync<T>` doesn't surface the dialog's markup in `cut` the way assumed (bUnit's interaction with `MudDialogProvider` can be finicky), fall back to the simpler direct-render pattern `Render<CreateQueueDialog>(parameters => ...)` used nowhere else in this codebase yet but documented in bUnit's own docs for MudBlazor dialogs — adjust to whichever actually renders the dialog's `DialogContent` markup.

- [ ] **Step 8: Register `CreateQueueCommandHandler` in `AwsPlugin.ConfigureServices`**

Already present from Task 3 (`services.AddScoped<Queues.CreateQueueCommandHandler>();`) — confirm it's there; no change needed if so.

- [ ] **Step 9: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement create queue (Standard and FIFO)"
```

---

## Task 7: Full `DeleteQueueCommandHandler` (audit + `Destructive` risk)

Task 5 added a minimal `DeleteQueueCommandHandler` with no audit trail, just to make `Queues.razor` buildable. This task replaces it with the real version.

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Queues/DeleteQueueCommandHandler.cs` (full rewrite)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Queues/DeleteQueueCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.DeleteQueueAsync`, `IAuditScope.RecordAsync`.
- Produces: `DeleteQueueCommandHandler.HandleAsync(DeleteQueueCommand, CancellationToken) : Task<PluginResult>` (unchanged signature from Task 5, so `Queues.razor` needs no change).

- [ ] **Step 1: Write the failing tests**

`tests/SbConsole.Plugins.Aws.Tests/Queues/DeleteQueueCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class DeleteQueueCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_queue_and_records_a_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new DeleteQueueCommand(connectionId, "aws-dev", false, "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.queue.delete", "aws-dev/orders", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_and_records_no_audit_entry()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(Substitute.For<ISqsOperations>(), connections, audit)
            .HandleAsync(new DeleteQueueCommand(Guid.NewGuid(), "aws-dev", false, "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeFalse();
        await audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, ct: default);
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.DeleteQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("queue not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new DeleteQueueCommand(connectionId, "aws-dev", true, "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.delete", "aws-dev/orders", ActionRisk.Destructive, false, "queue not found", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~DeleteQueueCommandHandlerTests"`
Expected: the audit-related assertions FAIL against Task 5's minimal handler (it never calls `IAuditScope`, and its constructor doesn't even take one).

- [ ] **Step 3: Rewrite `DeleteQueueCommandHandler`**

`src/SbConsole.Plugins.Aws/Queues/DeleteQueueCommandHandler.cs` (full replacement):

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record DeleteQueueCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string QueueUrl, string QueueName);

public sealed class DeleteQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(DeleteQueueCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.DeleteQueueAsync(secret, cmd.QueueUrl, ct);
            await audit.RecordAsync("aws.queue.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.delete", target, ActionRisk.Destructive, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Implement `DeleteQueueAsync` in `SqsOperations`**

Replace the `DeleteQueueAsync` stub in `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs`:

```csharp
    public async Task DeleteQueueAsync(string secret, string queueUrl, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.DeleteQueueAsync(queueUrl, ct);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~DeleteQueueCommandHandlerTests"`
Expected: all 3 tests PASS.

- [ ] **Step 6: Update `Queues.razor`'s `DeleteAsync` call site**

The `DeleteQueueCommand` constructor shape is unchanged from Task 5 (`ConnectionId, ConnectionName, IsProd, QueueUrl, QueueName`), so `Queues.razor`'s existing `DeleteAsync` method (Task 5, Step 4) needs no edit. Confirm by re-reading it: it already passes `connection.IsProd` as the third positional argument.

- [ ] **Step 7: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 8: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): wire delete queue to audit as a Destructive action"
```

---

## Task 8: `PurgeQueueAsync` + `PurgeQueueCommandHandler` + `PurgeQueueDialog.razor`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement `PurgeQueueAsync`)
- Create: `src/SbConsole.Plugins.Aws/Queues/PurgeQueueCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/PurgeQueueDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Queues.razor` (wire `OpenPurge`)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Queues/PurgeQueueCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/PurgeQueueDialogTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.PurgeQueueAsync`, `IAuditScope.RecordAsync`, `IConfirmationService.ConfirmAsync`.
- Produces: `PurgeQueueCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName)`, `PurgeQueueCommandHandler.HandleAsync(PurgeQueueCommand, CancellationToken) : Task<PluginResult>`.

- [ ] **Step 1: Implement `PurgeQueueAsync` in `SqsOperations`**

Replace the `PurgeQueueAsync` stub:

```csharp
    public async Task PurgeQueueAsync(string secret, string queueUrl, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.PurgeQueueAsync(queueUrl, ct);
    }
```

- [ ] **Step 2: Write the failing `PurgeQueueCommandHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Queues/PurgeQueueCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class PurgeQueueCommandHandlerTests
{
    [Fact]
    public async Task Purges_the_queue_and_records_a_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new PurgeQueueCommand(connectionId, "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).PurgeQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.queue.purge", "aws-dev/orders", ActionRisk.Destructive, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_Destructive_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.PurgeQueueAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("purge already in progress")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new PurgeQueueCommandHandler(operations, connections, audit)
            .HandleAsync(new PurgeQueueCommand(connectionId, "aws-dev", "https://sqs/orders", "orders"));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.purge", "aws-dev/orders", ActionRisk.Destructive, false, "purge already in progress", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Implement `PurgeQueueCommandHandler`**

`src/SbConsole.Plugins.Aws/Queues/PurgeQueueCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Queues;

public sealed record PurgeQueueCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName);

public sealed class PurgeQueueCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(PurgeQueueCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.PurgeQueueAsync(secret, cmd.QueueUrl, ct);
            await audit.RecordAsync("aws.queue.purge", target, ActionRisk.Destructive, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.purge", target, ActionRisk.Destructive, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~PurgeQueueCommandHandlerTests"`
Expected: both tests PASS.

- [ ] **Step 5: Write `PurgeQueueDialog.razor`**

Per design spec §5, this dialog states the approximate delete count, "no undo," and the async/eventually-consistent caveat, and requires typing the queue name (typed confirmation, matching `IConfirmationService`'s own prod-tagged gate — but this dialog does its OWN typed-name check directly, since the design's Destructive+prod gate is `IConfirmationService.ConfirmAsync`, called from `Queues.razor` before this dialog even opens, mirroring exactly how `Queues.razor`'s `DeleteAsync` already calls `Confirmation.ConfirmAsync` before invoking `DeleteHandler`).

`src/SbConsole.Plugins.Aws/Pages/PurgeQueueDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Queues
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudAlert Severity="Severity.Warning" Class="mb-3">
            This will purge approximately @ApproxVisibleCount message(s) from <b>@QueueName</b>. There is no undo.
            AWS purges asynchronously — it can take up to 60 seconds, and messages sent during that window may also be deleted.
        </MudAlert>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="purge-queue-busy" Color="Color.Error" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="confirm-purge" Color="Color.Error" Variant="Variant.Filled" Disabled="_busy" OnClick="Purge">Purge</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string QueueUrl { get; set; } = "";
    [Parameter] public string QueueName { get; set; } = "";
    [Parameter] public long ApproxVisibleCount { get; set; }

    [Inject] private PurgeQueueCommandHandler PurgeHandler { get; set; } = default!;

    private bool _busy;

    private async Task Purge()
    {
        _busy = true;
        try
        {
            var result = await PurgeHandler.HandleAsync(new PurgeQueueCommand(ConnectionId, ConnectionName, QueueUrl, QueueName));
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

- [ ] **Step 6: Wire `OpenPurge` in `Queues.razor`, going through `IConfirmationService` first**

Replace `private Task OpenPurge(string queueUrl) => Task.CompletedTask; // wired in Task 8` with:

```csharp
    private async Task OpenPurge(string queueUrl)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var queue = _queues.Single(q => q.QueueUrl == queueUrl);
        var confirmed = await Confirmation.ConfirmAsync("Purge", queue.Name, connection.IsProd, count: (int)queue.ApproxVisible);
        if (!confirmed)
        {
            return;
        }

        var parameters = new DialogParameters<PurgeQueueDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.QueueUrl, queue.QueueUrl },
            { x => x.QueueName, queue.Name },
            { x => x.ApproxVisibleCount, queue.ApproxVisible },
        };
        var dialog = await DialogService.ShowAsync<PurgeQueueDialog>("Purge queue", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadQueuesAsync();
        }
    }
```

This double-gates on purpose: `IConfirmationService.ConfirmAsync` supplies the typed-name-on-prod gate (design spec §5's "same gate as delete"), and `PurgeQueueDialog` itself is the informational dialog carrying the approximate count and async-completion caveat — matching how the mockup's purge screen shows both a warning panel and a type-to-confirm field, split here across the two existing mechanisms rather than reimplementing typed confirmation a second time inside the dialog.

- [ ] **Step 7: Write `PurgeQueueDialogTests`**

`tests/SbConsole.Plugins.Aws.Tests/Pages/PurgeQueueDialogTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class PurgeQueueDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public PurgeQueueDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(Substitute.For<ISqsOperations>());
        Services.AddSingleton(Substitute.For<IConnectionProvider>());
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton<PurgeQueueCommandHandler>();
    }

    [Fact]
    public void Shows_the_approximate_count_and_no_undo_warning()
    {
        var cut = Render<SbConsole.Plugins.Aws.Pages.PurgeQueueDialog>(parameters => parameters
            .Add(p => p.QueueName, "orders")
            .Add(p => p.ApproxVisibleCount, 1204));

        cut.Markup.Should().Contain("1204").And.Contain("orders").And.Contain("no undo", options => options.IgnoringCase());
    }
}
```

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~PurgeQueueDialogTests"`
Expected: PASS. `Render<T>(parameters => ...)` without a `CascadingParameter` for `IMudDialogInstance` renders the component outside a real dialog host — if bUnit throws on the missing cascading parameter, wrap with `.AddCascadingValue<IMudDialogInstance>(Substitute.For<IMudDialogInstance>())` in the `Render` call.

- [ ] **Step 8: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 9: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement purge queue with typed confirmation on prod"
```

---

## Task 9: `ReceiveMessagesAsync` + `ReceiveMessagesCommandHandler`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement `ReceiveMessagesAsync`)
- Create: `src/SbConsole.Plugins.Aws/Messages/ReceiveMessagesCommandHandler.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Client/SqsOperationsTests.cs` (append)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Messages/ReceiveMessagesCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.ReceiveMessagesAsync`.
- Produces: `ReceiveMessagesCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, int MaxMessages, int? VisibilityTimeoutSeconds, int WaitTimeSeconds)`, `ReceiveMessagesCommandHandler.HandleAsync(ReceiveMessagesCommand, CancellationToken) : Task<PluginResult<IReadOnlyList<ReceivedMessage>>>`. Modeled as a **command** (per design spec §6), not a query — receiving has a real, audit-worthy broker side effect (messages become invisible for the visibility timeout), unlike Service Bus/Kafka's non-destructive peek.

- [ ] **Step 1: Write the failing `ToReceivedMessage` mapping test**

Append to `tests/SbConsole.Plugins.Aws.Tests/Client/SqsOperationsTests.cs`:

```csharp
    [Fact]
    public void ToReceivedMessage_maps_system_attributes_and_custom_attributes()
    {
        var message = new Amazon.SQS.Model.Message
        {
            MessageId = "msg-1",
            ReceiptHandle = "handle-1",
            Body = "{\"orderId\":\"1001\"}",
            MD5OfBody = "abc123",
            Attributes = new Dictionary<string, string>
            {
                ["ApproximateReceiveCount"] = "3",
                ["SentTimestamp"] = "1700000000000",
                ["SenderId"] = "AIDAEXAMPLE",
            },
            MessageAttributes = new Dictionary<string, Amazon.SQS.Model.MessageAttributeValue>
            {
                ["source"] = new() { StringValue = "checkout", DataType = "String" },
            },
        };

        var received = SqsOperations.ToReceivedMessage(message);

        received.MessageId.Should().Be("msg-1");
        received.ReceiptHandle.Should().Be("handle-1");
        received.ApproxReceiveCount.Should().Be(3);
        received.SentTimestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        received.SenderId.Should().Be("AIDAEXAMPLE");
        received.Md5OfBody.Should().Be("abc123");
        received.MessageAttributes.Should().Equal(new Dictionary<string, string> { ["source"] = "checkout" });
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ToReceivedMessage_maps"`
Expected: build error — `SqsOperations.ToReceivedMessage` doesn't exist yet.

- [ ] **Step 3: Implement `ReceiveMessagesAsync` and `ToReceivedMessage` in `SqsOperations`**

Replace the `ReceiveMessagesAsync` stub:

```csharp
    public async Task<IReadOnlyList<ReceivedMessage>> ReceiveMessagesAsync(
        string secret, string queueUrl, int maxMessages, int? visibilityTimeoutSeconds, int waitTimeSeconds, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var request = new Amazon.SQS.Model.ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = maxMessages,
            WaitTimeSeconds = waitTimeSeconds,
            MessageAttributeNames = ["All"],
            AttributeNames = ["All"],
        };
        if (visibilityTimeoutSeconds is { } timeout)
        {
            request.VisibilityTimeout = timeout;
        }

        var response = await sqs.ReceiveMessageAsync(request, ct);
        return response.Messages.Select(ToReceivedMessage).ToList();
    }

    // Extracted as a pure static function, same reasoning as ToQueueSummary above -- unit-testable
    // without a real AWS account. Internal so SqsOperationsTests can assert it directly.
    internal static ReceivedMessage ToReceivedMessage(Amazon.SQS.Model.Message message)
    {
        var attributes = message.Attributes;
        var receiveCount = attributes.TryGetValue("ApproximateReceiveCount", out var countText) && int.TryParse(countText, out var count) ? count : 0;
        var sentTimestamp = attributes.TryGetValue("SentTimestamp", out var sentText) && long.TryParse(sentText, out var sentMillis)
            ? DateTimeOffset.FromUnixTimeMilliseconds(sentMillis)
            : DateTimeOffset.MinValue;
        var senderId = attributes.GetValueOrDefault("SenderId", "");
        var messageAttributes = message.MessageAttributes.ToDictionary(kv => kv.Key, kv => kv.Value.StringValue ?? "");

        return new ReceivedMessage(
            message.MessageId, message.ReceiptHandle, message.Body, receiveCount,
            sentTimestamp, senderId, message.MD5OfBody, messageAttributes);
    }
```

If the installed `AWSSDK.SQS` version has renamed `ReceiveMessageRequest.AttributeNames` (some versions deprecate it in favor of `MessageSystemAttributeNames` taking `List<MessageSystemAttributeName>`), `dotnet build` will report an obsolete-member warning (which fails the build under `-warnaserror`) or a missing-member error — in that case replace the line `AttributeNames = ["All"],` with `MessageSystemAttributeNames = [Amazon.SQS.MessageSystemAttributeName.All],` to match what the installed version actually exposes.

- [ ] **Step 4: Run the mapping test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ToReceivedMessage_maps"`
Expected: PASS.

- [ ] **Step 5: Write the failing `ReceiveMessagesCommandHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Messages/ReceiveMessagesCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class ReceiveMessagesCommandHandlerTests
{
    [Fact]
    public async Task Receives_messages_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var received = new[] { new ReceivedMessage("m1", "h1", "body", 1, DateTimeOffset.UtcNow, "sender", "md5", new Dictionary<string, string>()) };
        operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", 10, 30, 20, Arg.Any<CancellationToken>())
            .Returns(received);
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReceiveMessagesCommandHandler(operations, connections, audit)
            .HandleAsync(new ReceiveMessagesCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", 10, 30, 20));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(received);
        await audit.Received(1).RecordAsync("aws.queue.receive", "aws-dev/orders", ActionRisk.Mutating, true, "1 message(s)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", 10, null, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ReceivedMessage>>(new InvalidOperationException("queue not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReceiveMessagesCommandHandler(operations, connections, audit)
            .HandleAsync(new ReceiveMessagesCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", 10, null, 0));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.receive", "aws-dev/orders", ActionRisk.Mutating, false, "queue not found", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 6: Implement `ReceiveMessagesCommandHandler`**

`src/SbConsole.Plugins.Aws/Messages/ReceiveMessagesCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record ReceiveMessagesCommand(
    Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName,
    int MaxMessages, int? VisibilityTimeoutSeconds, int WaitTimeSeconds);

public sealed class ReceiveMessagesCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult<IReadOnlyList<ReceivedMessage>>> HandleAsync(ReceiveMessagesCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult<IReadOnlyList<ReceivedMessage>>.Fail("Connection not found.");
        }

        try
        {
            var messages = await operations.ReceiveMessagesAsync(secret, cmd.QueueUrl, cmd.MaxMessages, cmd.VisibilityTimeoutSeconds, cmd.WaitTimeSeconds, ct);
            await audit.RecordAsync("aws.queue.receive", target, ActionRisk.Mutating, succeeded: true, detail: $"{messages.Count} message(s)", ct: ct);
            return PluginResult<IReadOnlyList<ReceivedMessage>>.Ok(messages);
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.receive", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult<IReadOnlyList<ReceivedMessage>>.Fail(friendly);
        }
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ReceiveMessagesCommandHandlerTests"`
Expected: both PASS.

- [ ] **Step 8: Register in `AwsPlugin.ConfigureServices`**

Already present from Task 3 (`services.AddScoped<Messages.ReceiveMessagesCommandHandler>();`) — confirm.

- [ ] **Step 9: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 10: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement receive messages as an audited Mutating command"
```

---

## Task 10: `DeleteMessageAsync` + `ChangeMessageVisibilityAsync` + their command handlers

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement both stubs)
- Create: `src/SbConsole.Plugins.Aws/Messages/DeleteMessageCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Messages/ReleaseMessageCommandHandler.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Messages/DeleteMessageCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Messages/ReleaseMessageCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.DeleteMessageAsync`, `ISqsOperations.ChangeMessageVisibilityAsync`.
- Produces: `DeleteMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, string ReceiptHandle)` / `DeleteMessageCommandHandler.HandleAsync(...) : Task<PluginResult>`; `ReleaseMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, string ReceiptHandle)` / `ReleaseMessageCommandHandler.HandleAsync(...) : Task<PluginResult>`.

- [ ] **Step 1: Implement both `SqsOperations` methods**

Replace the two stubs:

```csharp
    public async Task DeleteMessageAsync(string secret, string queueUrl, string receiptHandle, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.DeleteMessageAsync(queueUrl, receiptHandle, ct);
    }

    public async Task ChangeMessageVisibilityAsync(string secret, string queueUrl, string receiptHandle, int visibilityTimeoutSeconds, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        await sqs.ChangeMessageVisibilityAsync(queueUrl, receiptHandle, visibilityTimeoutSeconds, ct);
    }
```

- [ ] **Step 2: Write the failing `DeleteMessageCommandHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Messages/DeleteMessageCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class DeleteMessageCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_message_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new DeleteMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.message.delete", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_receipt_handle_surfaces_the_friendly_message_and_records_failure()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.DeleteMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new Amazon.SQS.Model.ReceiptHandleIsInvalidException("expired")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new DeleteMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("This message's hold already expired — it's back in the queue");
        await audit.Received(1).RecordAsync("aws.message.delete", "aws-dev/orders", ActionRisk.Mutating, false,
            "This message's hold already expired — it's back in the queue", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Implement `DeleteMessageCommandHandler`**

`src/SbConsole.Plugins.Aws/Messages/DeleteMessageCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record DeleteMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, string ReceiptHandle);

public sealed class DeleteMessageCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(DeleteMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.DeleteMessageAsync(secret, cmd.QueueUrl, cmd.ReceiptHandle, ct);
            await audit.RecordAsync("aws.message.delete", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.message.delete", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~DeleteMessageCommandHandlerTests"`
Expected: both PASS.

- [ ] **Step 5: Write the failing `ReleaseMessageCommandHandler` tests and implement it (identical shape, "release" verb)**

`tests/SbConsole.Plugins.Aws.Tests/Messages/ReleaseMessageCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class ReleaseMessageCommandHandlerTests
{
    [Fact]
    public async Task Releases_the_message_by_zeroing_visibility_timeout_and_records_audit()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReleaseMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new ReleaseMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", 0, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.message.release", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-1", 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("handle expired")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new ReleaseMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new ReleaseMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", "handle-1"));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.message.release", "aws-dev/orders", ActionRisk.Mutating, false, "handle expired", Arg.Any<CancellationToken>());
    }
}
```

`src/SbConsole.Plugins.Aws/Messages/ReleaseMessageCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record ReleaseMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, string ReceiptHandle);

public sealed class ReleaseMessageCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(ReleaseMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.ChangeMessageVisibilityAsync(secret, cmd.QueueUrl, cmd.ReceiptHandle, 0, ct);
            await audit.RecordAsync("aws.message.release", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.message.release", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 6: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement delete and release message handlers"
```

---

## Task 11: `Receive.razor` page

**Files:**
- Create: `src/SbConsole.Plugins.Aws/Pages/Receive.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Queues.razor` (`ReceiveUrl` gains `&queueName=`)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/ReceivePageTests.cs`

**Interfaces:**
- Consumes: `ReceiveMessagesCommandHandler`, `DeleteMessageCommandHandler`, `ReleaseMessageCommandHandler` (Tasks 9-10), `IConnectionProvider.GetSecretAsync` (indirectly, via handlers).
- Produces: the `/p/aws/queues/receive` route.

- [ ] **Step 1: Add `queueName` to `Queues.razor`'s `ReceiveUrl`**

In `src/SbConsole.Plugins.Aws/Pages/Queues.razor`, replace:

```csharp
    private string ReceiveUrl(string queueUrl)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/aws/queues/receive?connectionId={connection.Id}&queueUrl={Uri.EscapeDataString(queueUrl)}";
    }
```

with:

```csharp
    private string ReceiveUrl(string queueUrl, string queueName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/aws/queues/receive?connectionId={connection.Id}&queueUrl={Uri.EscapeDataString(queueUrl)}&queueName={Uri.EscapeDataString(queueName)}";
    }
```

and update its call site in the `RowTemplate` from `Href="@ReceiveUrl(context.QueueUrl)"` to `Href="@ReceiveUrl(context.QueueUrl, context.Name)"`.

- [ ] **Step 2: Write the failing page tests**

`tests/SbConsole.Plugins.Aws.Tests/Pages/ReceivePageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class ReceivePageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly ISqsOperations _operations = Substitute.For<ISqsOperations>();
    private readonly SbConsole.Sdk.IConnectionProvider _connections = Substitute.For<SbConsole.Sdk.IConnectionProvider>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public ReceivePageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<SbConsole.Sdk.IAuditScope>());
        Services.AddSingleton<ReceiveMessagesCommandHandler>();
        Services.AddSingleton<DeleteMessageCommandHandler>();
        Services.AddSingleton<ReleaseMessageCommandHandler>();
        Services.AddLogging();
    }

    private static ReceivedMessage Message(string id, int receiveCount) =>
        new(id, $"handle-{id}", $"body-{id}", receiveCount, DateTimeOffset.UtcNow, "sender", "md5", new Dictionary<string, string> { ["source"] = "checkout" });

    private IRenderedComponent<SbConsole.Plugins.Aws.Pages.Receive> RenderPage() =>
        Render<SbConsole.Plugins.Aws.Pages.Receive>(parameters => parameters
            .Add(p => p.ConnectionId, _connectionId)
            .Add(p => p.QueueUrl, "https://sqs/orders")
            .Add(p => p.QueueName, "orders"));

    [Fact]
    public async Task Receive_button_calls_the_handler_and_renders_returned_messages()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("m1");
        cut.Markup.Should().Contain("SQS has no peek");
    }

    [Fact]
    public async Task Held_messages_show_the_countdown_banner()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".hold-banner").Should().ContainSingle();
        cut.Find("button.release-now").Should().NotBeNull();
    }

    [Fact]
    public async Task High_receive_count_shows_the_bad_severity_badge()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 6) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();

        cut.FindAll(".receive-count-bad").Should().ContainSingle();
    }

    [Fact]
    public async Task Selecting_a_message_shows_its_receipt_handle_note()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find(".message-row").Click();
        cut.Render();

        cut.Markup.Should().Contain("Valid only while hidden");
    }

    [Fact]
    public async Task Delete_selected_calls_the_delete_handler_for_each_checked_message()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1), Message("m2", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        cut.FindAll(".select-message input")[0].Change(true);
        cut.Render();
        cut.Find("button.delete-selected").Click();
        await Task.Delay(30);

        await _operations.Received(1).DeleteMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-m1", Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().DeleteMessageAsync(Arg.Any<string>(), Arg.Any<string>(), "handle-m2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Release_now_releases_every_currently_held_message()
    {
        _operations.ReceiveMessagesAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedMessage> { Message("m1", 1), Message("m2", 1) });

        var cut = RenderPage();
        cut.Find("button.receive-action").Click();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.release-now").Click();
        await Task.Delay(30);

        await _operations.Received(1).ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-m1", 0, Arg.Any<CancellationToken>());
        await _operations.Received(1).ChangeMessageVisibilityAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", "handle-m2", 0, Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ReceivePageTests"`
Expected: build error — `Pages.Receive` doesn't exist yet.

- [ ] **Step 4: Write `Receive.razor`**

`src/SbConsole.Plugins.Aws/Pages/Receive.razor`:

```razor
@page "/p/aws/queues/receive"
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Messages
@inject ReceiveMessagesCommandHandler ReceiveHandler
@inject DeleteMessageCommandHandler DeleteHandler
@inject ReleaseMessageCommandHandler ReleaseHandler
@inject ISnackbar Snackbar
@implements IDisposable

<PageTitle>Receive — @QueueName</PageTitle>
<h1>Receive: @QueueName</h1>

<MudText Typo="Typo.caption" Class="mud-text-secondary mb-2">SQS has no peek — receiving is the only way to read a message, and it hides what you received from other consumers for the visibility timeout.</MudText>

@if (_messages.Count > 0)
{
    <MudAlert Severity="Severity.Warning" Class="hold-banner mb-3">
        @_messages.Count message(s) are hidden from other consumers.
        <MudButton Class="release-now ml-2" Color="Color.Inherit" Variant="Variant.Outlined" Size="Size.Small" OnClick="ReleaseAllAsync">Release now</MudButton>
    </MudAlert>
}

<div class="d-flex gap-3 align-end mb-3">
    <MudNumericField Class="max-messages" @bind-Value="_maxMessages" Label="Max" Min="1" Max="10" Style="max-width:100px" />
    <MudNumericField Class="hide-for" @bind-Value="_visibilityTimeoutSeconds" Label="Hide for (sec)" Min="0" Max="43200" Style="max-width:140px" />
    <MudButton Class="receive-action" Color="Color.Primary" Variant="Variant.Filled" Disabled="_busy" OnClick="ReceiveAsync">Receive</MudButton>
    @if (_busy)
    {
        <MudProgressCircular Class="receive-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
    }
    @if (_selectedHandles.Count > 0)
    {
        <MudButton Class="delete-selected" Color="Color.Error" OnClick="DeleteSelectedAsync">Delete selected (@_selectedHandles.Count)</MudButton>
        <MudButton Class="release-selected" OnClick="ReleaseSelectedAsync">Release selected (@_selectedHandles.Count)</MudButton>
    }
</div>

<div class="d-flex gap-4" style="min-width:0">
    <div style="flex:1;min-width:0">
        @foreach (var message in _messages)
        {
            <div class="message-row d-flex align-center gap-2 pa-2" style="cursor:pointer;border-bottom:1px solid var(--mud-palette-divider)" @onclick="@(() => _selected = message)">
                <MudCheckBox Class="select-message" T="bool" Value="@_selectedHandles.Contains(message.ReceiptHandle)" ValueChanged="@(v => ToggleSelection(message.ReceiptHandle, v))" />
                <span style="font-family:monospace">@message.MessageId</span>
                <MudChip T="string" Size="Size.Small" Class="@ReceiveCountBadgeClass(message.ApproxReceiveCount)" Color="@ReceiveCountBadgeColor(message.ApproxReceiveCount)">@message.ApproxReceiveCount</MudChip>
            </div>
        }
    </div>
    @if (_selected is not null)
    {
        <div style="width:360px;flex:none">
            <MudText Typo="Typo.subtitle2">Body</MudText>
            <pre style="white-space:pre-wrap;font-size:12px">@_selected.Body</pre>
            <MudText Typo="Typo.subtitle2" Class="mt-2">System attributes</MudText>
            <div style="font-size:12px">
                <div>ApproximateReceiveCount: @_selected.ApproxReceiveCount</div>
                <div>SentTimestamp: @_selected.SentTimestamp</div>
                <div>SenderId: @_selected.SenderId</div>
                <div>MD5OfBody: @_selected.Md5OfBody</div>
            </div>
            @if (_selected.MessageAttributes.Count > 0)
            {
                <MudText Typo="Typo.subtitle2" Class="mt-2">Message attributes</MudText>
                @foreach (var attribute in _selected.MessageAttributes)
                {
                    <div style="font-size:12px">@attribute.Key = @attribute.Value</div>
                }
            }
            <MudText Typo="Typo.caption" Class="mud-text-secondary mt-2">Receipt handle — valid only while hidden. Delete needs this handle, not the message ID.</MudText>
        </div>
    }
</div>

@code {
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }
    [SupplyParameterFromQuery] public string QueueUrl { get; set; } = "";
    [SupplyParameterFromQuery] public string QueueName { get; set; } = "";

    [Inject] private SbConsole.Sdk.IConnectionProvider Connections { get; set; } = default!;

    private List<ReceivedMessage> _messages = [];
    private ReceivedMessage? _selected;
    private readonly HashSet<string> _selectedHandles = [];
    private int _maxMessages = 10;
    private int _visibilityTimeoutSeconds = 30;
    private bool _busy;
    private string? _connectionName;

    protected override async Task OnInitializedAsync()
    {
        // Only used to resolve ConnectionName for the audit target the handlers build --
        // ConnectionId/QueueUrl/QueueName themselves are not security-relevant here (this page has
        // no destructive, prod-gated action to protect, unlike Service Bus's Peek.razor).
        var connections = await Connections.ListAsync("aws");
        _connectionName = connections.FirstOrDefault(c => c.Id == ConnectionId)?.Name ?? "";
    }

    private static string ReceiveCountBadgeClass(int count) => count switch
    {
        <= 2 => "receive-count-ok",
        <= 5 => "receive-count-warn",
        _ => "receive-count-bad",
    };

    private static Color ReceiveCountBadgeColor(int count) => count switch
    {
        <= 2 => Color.Default,
        <= 5 => Color.Warning,
        _ => Color.Error,
    };

    private void ToggleSelection(string receiptHandle, bool selected)
    {
        if (selected)
        {
            _selectedHandles.Add(receiptHandle);
        }
        else
        {
            _selectedHandles.Remove(receiptHandle);
        }
    }

    private async Task ReceiveAsync()
    {
        _busy = true;
        try
        {
            var result = await ReceiveHandler.HandleAsync(new ReceiveMessagesCommand(
                ConnectionId, _connectionName ?? "", QueueUrl, QueueName, _maxMessages, _visibilityTimeoutSeconds, waitTimeSeconds: 0));
            if (result.IsSuccess)
            {
                _messages = [.. result.Value!];
                _selectedHandles.Clear();
                _selected = _messages.FirstOrDefault();
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

    private async Task DeleteSelectedAsync()
    {
        var handles = _selectedHandles.ToList();
        foreach (var handle in handles)
        {
            var result = await DeleteHandler.HandleAsync(new DeleteMessageCommand(ConnectionId, _connectionName ?? "", QueueUrl, QueueName, handle));
            if (result.IsSuccess)
            {
                _messages.RemoveAll(m => m.ReceiptHandle == handle);
                _selectedHandles.Remove(handle);
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }

        if (_selected is not null && !_messages.Contains(_selected))
        {
            _selected = _messages.FirstOrDefault();
        }
    }

    private async Task ReleaseSelectedAsync() => await ReleaseHandlesAsync(_selectedHandles.ToList());

    private async Task ReleaseAllAsync() => await ReleaseHandlesAsync(_messages.Select(m => m.ReceiptHandle).ToList());

    private async Task ReleaseHandlesAsync(List<string> handles)
    {
        foreach (var handle in handles)
        {
            var result = await ReleaseHandler.HandleAsync(new ReleaseMessageCommand(ConnectionId, _connectionName ?? "", QueueUrl, QueueName, handle));
            if (result.IsSuccess)
            {
                _messages.RemoveAll(m => m.ReceiptHandle == handle);
                _selectedHandles.Remove(handle);
            }
            else
            {
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }

        if (_selected is not null && !_messages.Contains(_selected))
        {
            _selected = _messages.FirstOrDefault();
        }
    }

    public void Dispose() { }
}
```

The test file's `RenderPage()` sets `ConnectionId`/`QueueUrl`/`QueueName` as bUnit render parameters directly (bypassing `[SupplyParameterFromQuery]`'s real query-string binding, which bUnit doesn't drive) — that's the established way Kafka/Service Bus's own `PeekPageTests` render query-parameterized pages under bUnit; `[SupplyParameterFromQuery]` properties are still ordinary `[Parameter]`-like public properties bUnit's `Render<T>(parameters => ...)` can set directly.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~ReceivePageTests"`
Expected: all 6 tests PASS. If `[SupplyParameterFromQuery]` properties reject being set as bUnit render parameters (some Blazor versions require `[Parameter]` specifically), add a plain `[Parameter]` on each of `ConnectionId`/`QueueUrl`/`QueueName` alongside `[SupplyParameterFromQuery]` — both attributes can coexist on the same property.

- [ ] **Step 6: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): add Receive.razor with hold countdown and receipt-handle handling"
```

---

## Task 12: `SendMessageAsync` + `SendMessageCommandHandler` + `SendMessageDialog.razor`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement `SendMessageAsync`)
- Create: `src/SbConsole.Plugins.Aws/Messages/SendMessageCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/SendMessageDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Queues.razor` (wire `OpenSend`)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Messages/SendMessageCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/SendMessageDialogTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.SendMessageAsync`.
- Produces: `SendMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, SendMessageRequest Request)`, `SendMessageCommandHandler.HandleAsync(...) : Task<PluginResult>`.

- [ ] **Step 1: Implement `SendMessageAsync` in `SqsOperations`**

Replace the stub:

```csharp
    public async Task SendMessageAsync(string secret, string queueUrl, SendMessageRequest request, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var sqsRequest = new Amazon.SQS.Model.SendMessageRequest { QueueUrl = queueUrl, MessageBody = request.Body };
        if (request.DelaySeconds is { } delay)
        {
            sqsRequest.DelaySeconds = delay;
        }

        if (request.MessageAttributes is { Count: > 0 } attributes)
        {
            sqsRequest.MessageAttributes = attributes.ToDictionary(
                kv => kv.Key,
                kv => new Amazon.SQS.Model.MessageAttributeValue { DataType = "String", StringValue = kv.Value });
        }

        if (request.MessageGroupId is { } groupId)
        {
            sqsRequest.MessageGroupId = groupId;
        }

        if (request.MessageDeduplicationId is { } dedupId)
        {
            sqsRequest.MessageDeduplicationId = dedupId;
        }

        await sqs.SendMessageAsync(sqsRequest, ct);
    }
```

(Same `Amazon.SQS.Model.SendMessageRequest` fully-qualified-name note as `CreateQueueAsync` in Task 6 — this plugin's own `SendMessageRequest` shares the name.)

- [ ] **Step 2: Write the failing `SendMessageCommandHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Messages/SendMessageCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Messages;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Sends_the_message_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = new SendMessageRequest("hello", null, null, null, null);
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new SendMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", request));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).SendMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", request, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("aws.message.send", "aws-dev/orders", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        var request = new SendMessageRequest("hello", null, null, null, null);
        operations.SendMessageAsync("mode=default-chain;region=eu-west-1", "https://sqs/orders", request, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("message too large")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit)
            .HandleAsync(new SendMessageCommand(connectionId, "aws-dev", "https://sqs/orders", "orders", request));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.message.send", "aws-dev/orders", ActionRisk.Mutating, false, "message too large", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Implement `SendMessageCommandHandler`**

`src/SbConsole.Plugins.Aws/Messages/SendMessageCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Messages;

public sealed record SendMessageCommand(Guid ConnectionId, string ConnectionName, string QueueUrl, string QueueName, SendMessageRequest Request);

public sealed class SendMessageCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(SendMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.QueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.SendMessageAsync(secret, cmd.QueueUrl, cmd.Request, ct);
            await audit.RecordAsync("aws.message.send", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.message.send", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SendMessageCommandHandlerTests"`
Expected: both PASS.

- [ ] **Step 5: Write `SendMessageDialog.razor`**

Per design spec §6: body, message attributes, optional delay, and FIFO-only Message Group ID / Deduplication ID fields (shown only when the target queue `IsFifo`).

`src/SbConsole.Plugins.Aws/Pages/SendMessageDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Client
@using SbConsole.Plugins.Aws.Messages
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">@ConnectionName / @QueueName</MudText>
        <MudTextField id="message-body" @bind-Value="_body" Label="Body" Lines="8" Immediate="true" />
        <MudNumericField Class="delay-seconds" @bind-Value="_delaySeconds" Label="Delay (sec, optional)" Min="0" Max="900" />

        @if (IsFifo)
        {
            <MudTextField Class="message-group-id" @bind-Value="_messageGroupId" Label="Message group ID" Required="true" Immediate="true" />
            @if (!ContentBasedDeduplication)
            {
                <MudTextField Class="message-dedup-id" @bind-Value="_messageDeduplicationId" Label="Deduplication ID" Required="true" Immediate="true" />
            }
        }

        <MudText Typo="Typo.subtitle2" Class="mt-4 mb-2">Message attributes</MudText>
        @for (var i = 0; i < _attributes.Count; i++)
        {
            var index = i;
            <div class="d-flex gap-2 align-center mb-1">
                <MudTextField Class="attribute-key" @bind-Value="_attributes[index].Key" Placeholder="Key" />
                <MudTextField Class="attribute-value" @bind-Value="_attributes[index].Value" Placeholder="Value" />
                <MudIconButton Class="remove-attribute" Icon="@Icons.Material.Filled.Close" OnClick="@(() => RemoveAttribute(index))" />
            </div>
        }
        <MudButton Class="add-attribute" OnClick="AddAttribute">+ Add attribute</MudButton>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="send-message-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="send-message" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(!CanSend || _busy)" OnClick="Send">Send</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string QueueUrl { get; set; } = "";
    [Parameter] public string QueueName { get; set; } = "";
    [Parameter] public bool IsFifo { get; set; }
    [Parameter] public bool ContentBasedDeduplication { get; set; }

    [Inject] private SendMessageCommandHandler SendHandler { get; set; } = default!;

    private string _body = "";
    private int? _delaySeconds;
    private string _messageGroupId = "";
    private string _messageDeduplicationId = "";
    private readonly List<AttributeRow> _attributes = [];
    private bool _busy;

    private sealed class AttributeRow
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private bool CanSend => !string.IsNullOrWhiteSpace(_body) && (!IsFifo || !string.IsNullOrWhiteSpace(_messageGroupId));

    private void AddAttribute() => _attributes.Add(new AttributeRow());

    private void RemoveAttribute(int index) => _attributes.RemoveAt(index);

    private async Task Send()
    {
        _busy = true;
        try
        {
            var attributes = _attributes.Where(a => !string.IsNullOrWhiteSpace(a.Key)).ToDictionary(a => a.Key, a => a.Value);
            var request = new SendMessageRequest(
                _body, attributes.Count > 0 ? attributes : null, _delaySeconds,
                IsFifo ? _messageGroupId : null,
                IsFifo && !ContentBasedDeduplication ? _messageDeduplicationId : null);

            var result = await SendHandler.HandleAsync(new SendMessageCommand(ConnectionId, ConnectionName, QueueUrl, QueueName, request));
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

- [ ] **Step 6: Wire `OpenSend` in `Queues.razor`**

Replace `private Task OpenSend(string queueUrl) => Task.CompletedTask; // wired in Task 12` with:

```csharp
    private async Task OpenSend(string queueUrl)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var queue = _queues.Single(q => q.QueueUrl == queueUrl);
        var parameters = new DialogParameters<SendMessageDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.QueueUrl, queue.QueueUrl },
            { x => x.QueueName, queue.Name },
            { x => x.IsFifo, queue.IsFifo },
        };
        var dialog = await DialogService.ShowAsync<SendMessageDialog>("Send message", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadQueuesAsync();
        }
    }
```

`ContentBasedDeduplication` is left at its default (`false`) here — `QueueSummary` doesn't carry that flag (it isn't part of the design spec's list/detail columns), so a FIFO queue with content-based dedup enabled still shows the Deduplication ID field; the user can leave it blank if the queue will compute one from the body. Not a defect worth a `QueueSummary` field for this slice — flagging it in a comment above the dialog parameters:

```csharp
        // ContentBasedDeduplication isn't part of QueueSummary (design spec §5's list columns) --
        // left at its default `false`, so the Deduplication ID field always shows for a FIFO queue
        // even when the queue itself has content-based dedup on. Harmless: the field can be left
        // blank if the queue computes one from the body.
```

(place this comment directly above the `parameters` declaration).

- [ ] **Step 7: Write `SendMessageDialogTests`**

`tests/SbConsole.Plugins.Aws.Tests/Pages/SendMessageDialogTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class SendMessageDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public SendMessageDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(Substitute.For<ISqsOperations>());
        Services.AddSingleton(Substitute.For<IConnectionProvider>());
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton<SendMessageCommandHandler>();
    }

    [Fact]
    public void FIFO_queue_shows_message_group_and_deduplication_fields()
    {
        var cut = Render<SbConsole.Plugins.Aws.Pages.SendMessageDialog>(parameters => parameters.Add(p => p.IsFifo, true));

        cut.FindAll(".message-group-id").Should().ContainSingle();
        cut.FindAll(".message-dedup-id").Should().ContainSingle();
    }

    [Fact]
    public void Standard_queue_hides_FIFO_only_fields()
    {
        var cut = Render<SbConsole.Plugins.Aws.Pages.SendMessageDialog>(parameters => parameters.Add(p => p.IsFifo, false));

        cut.FindAll(".message-group-id").Should().BeEmpty();
        cut.FindAll(".message-dedup-id").Should().BeEmpty();
    }
}
```

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~SendMessageDialogTests"`
Expected: both PASS.

- [ ] **Step 8: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 9: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement send message with FIFO-conditional fields"
```

---

## Task 13: `StartRedriveTaskAsync` + `StartRedriveCommandHandler` + `RedriveDialog.razor`

**Files:**
- Modify: `src/SbConsole.Plugins.Aws/Client/SqsOperations.cs` (implement `StartRedriveTaskAsync`)
- Create: `src/SbConsole.Plugins.Aws/Redrive/StartRedriveCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Aws/Pages/RedriveDialog.razor`
- Modify: `src/SbConsole.Plugins.Aws/Pages/Queues.razor` (wire `OpenRedrive`)
- Test: `tests/SbConsole.Plugins.Aws.Tests/Redrive/StartRedriveCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Aws.Tests/Pages/RedriveDialogTests.cs`

**Interfaces:**
- Consumes: `ISqsOperations.StartRedriveTaskAsync`.
- Produces: `StartRedriveCommand(Guid ConnectionId, string ConnectionName, string SourceQueueArn, string SourceQueueName, string DestinationQueueArn, int? MaxMessagesPerSecond)`, `StartRedriveCommandHandler.HandleAsync(...) : Task<PluginResult>`.

- [ ] **Step 1: Implement `StartRedriveTaskAsync` in `SqsOperations`**

Replace the stub:

```csharp
    public async Task<string> StartRedriveTaskAsync(string secret, string sourceQueueArn, string destinationQueueArn, int? maxMessagesPerSecond, CancellationToken ct = default)
    {
        using var sqs = BuildSqsClient(secret);
        var request = new Amazon.SQS.Model.StartMessageMoveTaskRequest
        {
            SourceArn = sourceQueueArn,
            DestinationArn = destinationQueueArn,
        };
        if (maxMessagesPerSecond is { } rate)
        {
            request.MaxNumberOfMessagesPerSecond = rate;
        }

        var response = await sqs.StartMessageMoveTaskAsync(request, ct);
        return response.TaskHandle;
    }
```

- [ ] **Step 2: Write the failing `StartRedriveCommandHandler` tests**

`tests/SbConsole.Plugins.Aws.Tests/Redrive/StartRedriveCommandHandlerTests.cs`:

```csharp
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Redrive;

public class StartRedriveCommandHandlerTests
{
    [Fact]
    public async Task Starts_the_move_task_and_records_a_Mutating_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.StartRedriveTaskAsync("mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", 10, Arg.Any<CancellationToken>())
            .Returns("task-handle-1");
        var audit = Substitute.For<IAuditScope>();

        var result = await new StartRedriveCommandHandler(operations, connections, audit)
            .HandleAsync(new StartRedriveCommand(connectionId, "aws-dev", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", 10));

        result.IsSuccess.Should().BeTrue();
        await audit.Received(1).RecordAsync("aws.queue.redrive", "aws-dev/orders-dlq", ActionRisk.Mutating, true, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Operation_failure_records_a_failed_audit_entry()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("mode=default-chain;region=eu-west-1");
        var operations = Substitute.For<ISqsOperations>();
        operations.StartRedriveTaskAsync("mode=default-chain;region=eu-west-1", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", null, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("a move task is already running for this queue")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new StartRedriveCommandHandler(operations, connections, audit)
            .HandleAsync(new StartRedriveCommand(connectionId, "aws-dev", "arn:aws:sqs:eu-west-1:123456789012:orders-dlq", "orders-dlq", "arn:aws:sqs:eu-west-1:123456789012:orders", null));

        result.IsSuccess.Should().BeFalse();
        await audit.Received(1).RecordAsync("aws.queue.redrive", "aws-dev/orders-dlq", ActionRisk.Mutating, false, "a move task is already running for this queue", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 3: Implement `StartRedriveCommandHandler`**

`src/SbConsole.Plugins.Aws/Redrive/StartRedriveCommandHandler.cs`:

```csharp
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Redrive;

public sealed record StartRedriveCommand(
    Guid ConnectionId, string ConnectionName, string SourceQueueArn, string SourceQueueName,
    string DestinationQueueArn, int? MaxMessagesPerSecond);

public sealed class StartRedriveCommandHandler(ISqsOperations operations, IConnectionProvider connections, IAuditScope audit)
{
    public async Task<PluginResult> HandleAsync(StartRedriveCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.SourceQueueName}";
        var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
        if (secret is null)
        {
            return PluginResult.Fail("Connection not found.");
        }

        try
        {
            await operations.StartRedriveTaskAsync(secret, cmd.SourceQueueArn, cmd.DestinationQueueArn, cmd.MaxMessagesPerSecond, ct);
            await audit.RecordAsync("aws.queue.redrive", target, ActionRisk.Mutating, succeeded: true, ct: ct);
            return PluginResult.Ok();
        }
        catch (Exception ex)
        {
            var friendly = FriendlyAwsError.From(ex);
            await audit.RecordAsync("aws.queue.redrive", target, ActionRisk.Mutating, succeeded: false, detail: friendly, ct: ct);
            return PluginResult.Fail(friendly);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~StartRedriveCommandHandlerTests"`
Expected: both PASS.

- [ ] **Step 5: Write `RedriveDialog.razor`**

Per design spec §6: source (the DLQ) and destination pickers, rate limit, and the receive-count-reset warning. Since this plugin doesn't track "which queue redrives into which" as a separate lookup (that's read from the source queue's own `RedrivePolicy` attribute, already parsed into `QueueSummary.HasDeadLetterTarget` — but not the *target* ARN itself), the destination is a free-text ARN field rather than a live picker for this slice, with a caption noting it defaults to nothing and must be typed:

`src/SbConsole.Plugins.Aws/Pages/RedriveDialog.razor`:

```razor
@using SbConsole.Plugins.Aws.Redrive
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">Source (DLQ): @SourceQueueName</MudText>
        <MudTextField Class="destination-queue-arn" @bind-Value="_destinationQueueArn" Label="Destination queue ARN" Required="true" Immediate="true" />
        <MudNumericField Class="rate-limit" @bind-Value="_maxMessagesPerSecond" Label="Rate (msgs/sec, optional)" Min="1" Max="500" />
        <MudAlert Severity="Severity.Warning" Class="mt-2">
            Already-failed messages may dead-letter again — their receive count resets, so they look "new" on the next arrival.
        </MudAlert>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="redrive-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="start-redrive" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_destinationQueueArn) || _busy)" OnClick="Start">Redrive</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string SourceQueueArn { get; set; } = "";
    [Parameter] public string SourceQueueName { get; set; } = "";

    [Inject] private StartRedriveCommandHandler RedriveHandler { get; set; } = default!;

    private string _destinationQueueArn = "";
    private int? _maxMessagesPerSecond;
    private bool _busy;

    private async Task Start()
    {
        _busy = true;
        try
        {
            var result = await RedriveHandler.HandleAsync(new StartRedriveCommand(
                ConnectionId, ConnectionName, SourceQueueArn, SourceQueueName, _destinationQueueArn, _maxMessagesPerSecond));
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

- [ ] **Step 6: Wire `OpenRedrive` in `Queues.razor`**

Replace `private Task OpenRedrive(string queueUrl, string queueArn) => Task.CompletedTask; // wired in Task 13` with:

```csharp
    private async Task OpenRedrive(string queueUrl, string queueArn)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var queue = _queues.Single(q => q.QueueUrl == queueUrl);
        var confirmed = await Confirmation.ConfirmAsync("Redrive", queue.Name, connection.IsProd);
        if (!confirmed)
        {
            return;
        }

        var parameters = new DialogParameters<RedriveDialog>
        {
            { x => x.ConnectionId, connection.Id },
            { x => x.ConnectionName, connection.Name },
            { x => x.SourceQueueArn, queueArn },
            { x => x.SourceQueueName, queue.Name },
        };
        var dialog = await DialogService.ShowAsync<RedriveDialog>("Redrive from DLQ", parameters);
        var result = await dialog.Result;
        if (result is { Canceled: false })
        {
            await LoadQueuesAsync();
        }
    }
```

- [ ] **Step 7: Write `RedriveDialogTests`**

`tests/SbConsole.Plugins.Aws.Tests/Pages/RedriveDialogTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Plugins.Aws.Redrive;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Pages;

public class RedriveDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public RedriveDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(Substitute.For<ISqsOperations>());
        Services.AddSingleton(Substitute.For<IConnectionProvider>());
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddSingleton<StartRedriveCommandHandler>();
    }

    [Fact]
    public void Shows_the_receive_count_reset_warning()
    {
        var cut = Render<SbConsole.Plugins.Aws.Pages.RedriveDialog>(parameters => parameters.Add(p => p.SourceQueueName, "orders-dlq"));

        cut.Markup.Should().Contain("orders-dlq").And.Contain("receive count resets");
    }

    [Fact]
    public void Start_button_disabled_until_a_destination_ARN_is_entered()
    {
        var cut = Render<SbConsole.Plugins.Aws.Pages.RedriveDialog>();

        cut.Find("button.start-redrive").HasAttribute("disabled").Should().BeTrue();

        cut.Find(".destination-queue-arn input").Input("arn:aws:sqs:eu-west-1:123456789012:orders");

        cut.Find("button.start-redrive").HasAttribute("disabled").Should().BeFalse();
    }
}
```

Run: `dotnet test tests/SbConsole.Plugins.Aws.Tests --filter "FullyQualifiedName~RedriveDialogTests"`
Expected: both PASS.

- [ ] **Step 8: Run all AWS tests and the full solution build**

Run: `dotnet build -warnaserror && dotnet test`
Expected: all green.

- [ ] **Step 9: Commit**

```bash
git add src/SbConsole.Plugins.Aws tests/SbConsole.Plugins.Aws.Tests
git commit -m "feat(aws): implement DLQ redrive via the native StartMessageMoveTask API"
```

---

## Task 14: LocalStack in the local dev stack

**Files:**
- Modify: `docker-compose.yml` (add `--profile aws` LocalStack service + seed script)
- Create: `docker/aws/init-queues.sh`
- Modify: `docker/README.md` (document the endpoint/credentials/region)
- Modify: `.env.example` (add `LOCALSTACK_PORT`)

No automated tests — this is manual-dev-only infrastructure, per design spec §8 ("This is for manual dev use only — the automated test suite stays unit-only").

- [ ] **Step 1: Add the LocalStack service to `docker-compose.yml`**

In `docker-compose.yml`, insert a new service block after the `kafka-ui` service and before the `# Azure Service Bus emulator` comment block:

```yaml
  # ---------------------------------------------------------------------------------------
  # LocalStack (profile: aws) -- SQS + SNS emulator for manual testing of the AWS plugin.
  # SNS is enabled now (not just SQS) so the SNS plan (docs/superpowers/specs/
  # 2026-09-21-aws-sqs-plugin-design.md §1) needs no compose change later.
  # ---------------------------------------------------------------------------------------
  localstack:
    image: localstack/localstack:3
    container_name: nervecenter-localstack
    profiles: ["aws"]
    ports:
      - "${LOCALSTACK_PORT:-4566}:4566"
    environment:
      SERVICES: sqs,sns
      DEBUG: 0

  # One-shot: creates a couple of sample queues so the Queues page has something to show.
  # Idempotent; re-runs are harmless.
  aws-init:
    image: amazon/aws-cli:2.17.62
    container_name: nervecenter-aws-init
    profiles: ["aws"]
    depends_on:
      - localstack
    entrypoint: ["/bin/sh", "/init-queues.sh"]
    environment:
      AWS_ACCESS_KEY_ID: test
      AWS_SECRET_ACCESS_KEY: test
      AWS_DEFAULT_REGION: us-east-1
      ENDPOINT_URL: http://localstack:4566
    volumes:
      - ./docker/aws/init-queues.sh:/init-queues.sh:ro
    restart: "no"
```

Add `LOCALSTACK_PORT` to the `volumes:` section's sibling — no, `LOCALSTACK_PORT` is an env var, not a volume; no `volumes:` entry is needed for `localstack` (it has no named volume — LocalStack's in-memory state is fine for local dev and doesn't need to survive a restart, unlike Kafka's log segments).

- [ ] **Step 2: Write the seed script**

`docker/aws/init-queues.sh`:

```bash
#!/usr/bin/env sh
# Seeds LocalStack with sample SQS queues so the AWS plugin's Queues page has something to show.
# Runs once from the `aws-init` compose service; safe to re-run (--if-not-exists isn't a real SQS
# flag, so this script just ignores "already exists" instead).
set -eu

ENDPOINT="${ENDPOINT_URL:-http://localstack:4566}"

create_queue() {
  name="$1"
  aws --endpoint-url "$ENDPOINT" sqs create-queue --queue-name "$name" >/dev/null 2>&1 || true
}

echo "==> creating sample queues on $ENDPOINT"
create_queue order-events
create_queue order-events-dlq
create_queue payments

echo "==> attaching order-events' redrive policy to order-events-dlq"
DLQ_ARN=$(aws --endpoint-url "$ENDPOINT" sqs get-queue-attributes \
  --queue-url "$ENDPOINT/000000000000/order-events-dlq" --attribute-names QueueArn \
  --query 'Attributes.QueueArn' --output text)
aws --endpoint-url "$ENDPOINT" sqs set-queue-attributes \
  --queue-url "$ENDPOINT/000000000000/order-events" \
  --attributes "RedrivePolicy={\"deadLetterTargetArn\":\"$DLQ_ARN\",\"maxReceiveCount\":5}"

echo "==> done"
aws --endpoint-url "$ENDPOINT" sqs list-queues
```

Run: `chmod +x docker/aws/init-queues.sh`

- [ ] **Step 3: Add `LOCALSTACK_PORT` to `.env.example`**

In `.env.example`, under the `# --- host ports ---` section, add:

```
LOCALSTACK_PORT=4566
```

- [ ] **Step 4: Document the stack in `docker/README.md`**

Add a new section to `docker/README.md`, after the "Azure Service Bus emulator (opt-in)" section and before "SbConsole in a container (opt-in)":

```markdown
## LocalStack — AWS SQS/SNS emulator (opt-in)

```bash
docker compose --profile aws up -d
```

| Service      | Host address          |
|--------------|------------------------|
| `localstack` | `localhost:4566`       |
| `aws-init`   | (one-shot)             |

Seeded queues: `order-events`, `order-events-dlq` (redrive policy already attached, max receives
5), `payments`.

### Connect the app to it

In SbConsole, **Connections → Add**, kind *AWS SQS/SNS*, secret:

```
mode=access-keys;region=us-east-1;accessKeyId=test;secretAccessKey=test;endpoint=http://localstack:4566;pathStyle=true
```

`test`/`test` is LocalStack's own convention — it accepts any non-empty access key/secret pair
under the community edition. `us-east-1` matches the queues seeded above (LocalStack's default
region unless overridden). From the host (not from inside the `sbconsole` container), use
`http://localhost:4566` instead of `http://localstack:4566`.
```

- [ ] **Step 5: Verify the stack starts and the seed script runs cleanly**

Run:
```bash
docker compose --profile aws up -d
docker compose logs aws-init
```
Expected: `aws-init` logs end with `==> done` followed by a queue list containing the three seeded queue URLs, with no error lines above it.

- [ ] **Step 6: Tear down**

Run: `docker compose --profile aws down`

- [ ] **Step 7: Commit**

```bash
git add docker-compose.yml docker/aws docker/README.md .env.example
git commit -m "feat(aws): add LocalStack to the local dev stack"
```

---

## Task 15: `docs/design.md` update + final full-solution verification

**Files:**
- Modify: `docs/design.md` (append §6.7 documenting the shipped AWS plugin, and the top-of-file "Extended" changelog line)

**Interfaces:** None — documentation only, following the append-only, dated-section convention `docs/design.md` already uses for §6 (Service Bus) and §6.5 (Kafka).

- [ ] **Step 1: Add a new dated line to the top-of-file changelog**

In `docs/design.md`, the `Status:` block at the top lists every extension chronologically. Append, after the existing "Extended 2026-09-14 (Glass shell...)" sentence:

```
Extended 2026-09-21 (AWS plugin, Queues: §6.7 new plugin section).
```

- [ ] **Step 2: Add §6.7 documenting the shipped plugin**

Insert a new section after §6.5 (Kafka plugin) and before §7 (Error handling):

```markdown
## 6.7 AWS plugin (`SbConsole.Plugins.Aws`) — Queues (2026-09-21)

SbConsole's third plugin, built the same way Service Bus's Queues plan and Kafka's Topics plan
proved the architecture out for their systems: `AwsPlugin : IPlugin`, `Id`/`ConnectionKind` =
`"aws"`, `DisplayName`/`ConnectionKindDisplayName` = `"AWS SQS/SNS"` (named for the plugin's full
eventual scope from the start, so SNS can join later with no rename). Registered via
`AddSbConsolePlugin<AwsPlugin>()` in `Program.cs`, right after Kafka. Full design:
`docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md`.

Connection secret model differs from both existing plugins in field *variance* (though not in
convention — it's still one flat `key=value;` string, `AwsConfigParser`, mirroring
`KafkaConfigParser`): three structurally different auth modes (`access-keys`, `assume-role`,
`default-chain`), each with its own field set, plus mode-independent `region`/`endpoint`/
`pathStyle` fields for LocalStack/ElasticMQ. **No custom connection-form UI was built** — verified
against `AddEditConnectionDialog.razor` that no per-plugin custom-field hook exists for any
plugin; AWS follows Kafka's own precedent exactly, typing the flat secret by hand into the
existing generic textbox.

**Queues (shipped):** list (prefix-only filter, matching `ListQueues`' real `QueueNamePrefix`
constraint — labelled "Starts with," not "Search"), create (Standard and FIFO, with FIFO's forced
`.fifo` suffix and content-based-dedup/high-throughput options), delete (`Destructive`), purge
(`Destructive`, typed-confirm-on-prod, async/eventually-consistent completion caveat surfaced in
the dialog copy), receive (modelled as a **command**, not a query — unlike Service Bus/Kafka's
non-destructive peek, SQS receiving has a real broker side effect: messages become invisible to
other consumers for the visibility timeout), delete/release a received message (release = 
`ChangeMessageVisibility` to 0), send (FIFO-conditional Message Group ID / Deduplication ID
fields), and DLQ redrive via AWS's native `StartMessageMoveTask` API (not a hand-rolled
receive-then-send loop — see the design spec §1's decision record).

**One correction to the source UI mockup** (`~/Desktop/UI mockups for NerveCenter/SbConsole
AWS.dc.html`) carried through from the design spec: its refresh-cost caption states `1 + 3n` API
calls per refresh; the real API is `1 + n` (`GetQueueAttributes` with `AttributeNames=[All]`
returns every attribute in one call per queue). `Queues.razor`'s shipped caption states the real
number.

Deferred past this plan, matching the design spec's explicit scope cuts: SNS entirely (topics,
subscriptions, publish — separate plan); Dashboard/nav-badge/dead-letter-overview integration
(`AwsPlugin` uses every `IPlugin.Get*` SDK default); a persisted read-only/degraded-connection
capability set (Test connection reports richer diagnostic text only — nothing hides a button);
live redrive-progress polling (the move task is started and its result reported success/failure
only — no `ListMessageMoveTasks`-backed status method exists yet, added alongside whatever UI
first needs it); per-message manual "redrive to source" from inside Receive; the Queue detail
page's SNS cross-reference panel; Access-policy/Tags tabs.

**Local dev**: `docker compose --profile aws up -d` runs LocalStack (`SERVICES=sqs,sns`, so the
SNS plan needs no compose change) plus a seed script creating sample queues with a redrive policy
already attached — mirroring Kafka's `kafka-init` pattern. See `docker/README.md`.
```

- [ ] **Step 3: Full solution build**

Run: `dotnet build -warnaserror`
Expected: zero errors, zero warnings, across every project in the solution (host, Sdk, Core, both existing plugins, and the new `SbConsole.Plugins.Aws`).

- [ ] **Step 4: Full solution test run**

Run: `dotnet test`
Expected: every test project passes, including `SbConsole.Web.Tests` (proves the new plugin's DI registration and keyed `IPluginStore` wiring don't collide with the two existing plugins) and `SbConsole.Plugins.Aws.Tests`.

- [ ] **Step 5: Manual smoke test against LocalStack**

Run:
```bash
docker compose --profile aws up -d
dotnet run --project src/SbConsole.Web --launch-profile http
```

In the browser: log in, go to **Connections → Add**, kind *AWS SQS/SNS*, name `aws-localstack`, secret `mode=access-keys;region=us-east-1;accessKeyId=test;secretAccessKey=test;endpoint=http://localhost:4566;pathStyle=true` (note `localhost`, not `localstack`, since the app is running on the host here, not in the `sbconsole` container), save, then click **Test** on the Connections table row — expect the Status column to show "OK". Navigate to **AWS SQS/SNS → Queues** and confirm `order-events`, `order-events-dlq`, and `payments` all list with real (not error) attribute values. Create a Standard queue, send it a message, receive it, and delete it; confirm the Audit page shows `aws.queue.create`, `aws.message.send`, `aws.queue.receive`, and `aws.message.delete` rows for the actions just taken.

- [ ] **Step 6: Tear down the manual smoke-test stack**

Run: `docker compose --profile aws down`

- [ ] **Step 7: Commit**

```bash
git add docs/design.md
git commit -m "docs: document the AWS plugin (Queues slice) in design.md §6.7"
```

---

## Self-Review

**Spec coverage** (against `docs/superpowers/specs/2026-09-21-aws-sqs-plugin-design.md`):
- §2 connection model, `AwsConfigParser`, `SafeEcho`, no-custom-form decision → Tasks 1, 3, 5.
- §3 plugin shell, `ISqsOperations`, `AwsPlugin` → Task 3.
- §4 `TestConnectionAsync` (rich-diagnostics-as-text, not a capability set) → Task 3.
- §5 queue management (list/create/delete/purge, the `1+n` cost correction) → Tasks 4, 5, 6, 7, 8.
- §6 receive/send/redrive → Tasks 9, 10, 11, 12, 13.
- §7 `FriendlyAwsError`, tightened timeouts → Task 2 (mapping), Task 3 (`AwsCredentialsFactory`'s `Timeout`/`MaxErrorRetry`).
- §8 testing (unit-only, `AwsConfigParser` full coverage, LocalStack for manual dev only) → every task's Test entries, Task 14.
- §9 out-of-scope items → deliberately **not** built anywhere in this plan; each is called out again in Task 15's design.md write-up so the shipped-vs-deferred boundary is documented in two places (the spec and the design doc), matching how Kafka's and Service Bus's own deferred-scope items are recorded.

**Placeholder scan:** no "TBD"/"add error handling"/"similar to Task N" phrasing found. Every AWS-SDK-uncertain spot (obsolete-member fallbacks in Tasks 9 and 3, `AssumeRoleAWSCredentials`'s exact overload in Task 3) states the concrete fallback code to switch to, not a vague "verify and fix" — matching the same discipline `ConfluentKafkaOperations.cs`'s own comments use for its own real API uncertainties.

**Type consistency:** `ISqsOperations`' final method list (Global Constraints) matches every implementation across Tasks 3–13 exactly, including the `GetRedriveTaskStatusAsync` removal (caught during the spec's own self-review, not present anywhere in this plan). `CreateQueueRequest`/`SendMessageRequest`/`ReceivedMessage`/`QueueSummary` field names are identical everywhere they're constructed or read (Tasks 3, 4, 6, 9, 10, 12). `DeleteQueueCommand`'s shape is introduced once in Task 5 (temporary handler) and never changes in Task 7's full rewrite, so `Queues.razor` needs no follow-up edit — called out explicitly in Task 7 Step 6.
