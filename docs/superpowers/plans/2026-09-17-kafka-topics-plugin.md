# Kafka plugin — Topics + message browse/produce — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build SbConsole's second plugin — Apache Kafka topic management, message browse (peek), and produce (send) — proving the plugin architecture out for a second messaging system exactly as the Service Bus Queues plan did for the first.

**Architecture:** `SbConsole.Plugins.Kafka` is a compile-time-registered `IPlugin`, mirroring `SbConsole.Plugins.ServiceBus` file-for-file: a substitutable `IKafkaOperations` seam (real implementation `ConfluentKafkaOperations`, wrapping `Confluent.Kafka`), plugin handlers following Core's `XxxQueryHandler`/`XxxCommandHandler` convention, and Razor pages under `Pages/`. The connection secret is a librdkafka config string (`key=value` pairs separated by `;`) parsed by `KafkaConfigParser` straight into `Confluent.Kafka`'s `ClientConfig`-derived types. Full design: `docs/superpowers/specs/2026-09-16-kafka-topics-plugin-design.md`.

Adding this second plugin exposes one real bug in the existing host: `AddSbConsolePlugin<TPlugin>()` registers `IPluginStore` as a single unkeyed scoped service, which silently breaks the moment a second plugin is registered (whichever plugin's `Id` was registered last wins for every `@inject IPluginStore` in the whole app, including Service Bus's `Queues.razor`). Task 3 fixes this with keyed DI before any Kafka page depends on the registration path.

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor, `Confluent.Kafka`, EF Core + SQLite (host, unchanged), xUnit + FluentAssertions 7.x + NSubstitute + bUnit.

## Global Constraints

- .NET 10, C# `latest`, nullable enabled, warnings as errors (`Directory.Build.props`, already enforced).
- No MediatR, no controllers, no second component library, no WebAssembly.
- Gate before every commit: `dotnet build SbConsole.slnx -warnaserror && dotnet test` both green.
- `Confluent.Kafka` is added ONLY to `SbConsole.Plugins.Kafka` — no other project references it (mirrors `Azure.Messaging.ServiceBus` being scoped to `SbConsole.Plugins.ServiceBus` only).
- `IKafkaOperations` methods take the connection config string as a parameter (never pre-configured at construction) — one registered instance operates against whatever connection the caller names.
- **Testing strategy for this plan is unit tests only** — no Testcontainers, no real broker traffic, no Docker. `ConfluentKafkaOperations`'s own methods get light coverage by necessity (most can't be meaningfully unit-tested without a real/emulated broker); the substitute of `IKafkaOperations` is where the real test leverage is. Deliberate, not a shortcut — see the design spec §8.
- `Confluent.Kafka`'s `AdminClient`/`Consumer` APIs used here (`GetMetadata`, `QueryWatermarkOffsets`, `Assign`, `Consume`) are synchronous/blocking calls with no async overload — every method that calls them wraps the blocking body in `Task.Run(..., ct)` so it never blocks the calling ASP.NET/Blazor circuit thread for the length of its timeout.
- Commands write audit rows via `IAuditScope`; queries never write.
- Destructive actions (delete topic) go through `IConfirmationService` exactly like Service Bus's delete queue — plugins consume it by injection, never referencing `SbConsole.Web`.
- Raw `Confluent.Kafka` exception text never reaches a snackbar, an audit row, or `Connection.LastTestError` — every catch site routes through `FriendlyKafkaError`.
- Conventional commits, ending with:
  Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>

---

### Task 1: Project scaffolding + `KafkaConfigParser`

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/SbConsole.Plugins.Kafka.csproj`
- Create: `tests/SbConsole.Plugins.Kafka.Tests/SbConsole.Plugins.Kafka.Tests.csproj`
- Create: `src/SbConsole.Plugins.Kafka/Client/KafkaConfigParser.cs`
- Modify: `SbConsole.slnx`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/KafkaConfigParserTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (used by every later task): `SbConsole.Plugins.Kafka.Client.KafkaConfigParser.Parse(string config) : Dictionary<string, string>` — turns the librdkafka config string (design spec §2) into the dictionary every `ClientConfig`-derived type wraps.

- [ ] **Step 1: Scaffold the two projects**

```xml
<!-- src/SbConsole.Plugins.Kafka/SbConsole.Plugins.Kafka.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <ItemGroup>
    <ProjectReference Include="..\SbConsole.Sdk\SbConsole.Sdk.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- ConfluentKafkaOperations' internal timeout constants are asserted directly by unit tests
         (there is no broker to observe them against) -- same reason ServiceBus.Tests needs this. -->
    <InternalsVisibleTo Include="SbConsole.Plugins.Kafka.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="9.9.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- Pages/*.razor need Razor component + [Authorize] support, which live in the ASP.NET Core
         shared framework, not a NuGet package -- same pattern SbConsole.Plugins.ServiceBus uses. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

Add the `Confluent.Kafka` package reference via the CLI so it resolves the current latest stable version rather than a version hand-typed into this plan going stale:

```bash
dotnet add src/SbConsole.Plugins.Kafka/SbConsole.Plugins.Kafka.csproj package Confluent.Kafka
```

```xml
<!-- tests/SbConsole.Plugins.Kafka.Tests/SbConsole.Plugins.Kafka.Tests.csproj -->
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
    <ProjectReference Include="..\..\src\SbConsole.Plugins.Kafka\SbConsole.Plugins.Kafka.csproj" />
  </ItemGroup>

</Project>
```

Update `SbConsole.slnx`, adding both new projects to their existing folders:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/SbConsole.Core/SbConsole.Core.csproj" />
    <Project Path="src/SbConsole.Plugins.Kafka/SbConsole.Plugins.Kafka.csproj" />
    <Project Path="src/SbConsole.Plugins.ServiceBus/SbConsole.Plugins.ServiceBus.csproj" />
    <Project Path="src/SbConsole.Sdk/SbConsole.Sdk.csproj" />
    <Project Path="src/SbConsole.Web/SbConsole.Web.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/SbConsole.Core.Tests/SbConsole.Core.Tests.csproj" />
    <Project Path="tests/SbConsole.Plugins.Kafka.Tests/SbConsole.Plugins.Kafka.Tests.csproj" />
    <Project Path="tests/SbConsole.Plugins.ServiceBus.Tests/SbConsole.Plugins.ServiceBus.Tests.csproj" />
    <Project Path="tests/SbConsole.Web.Tests/SbConsole.Web.Tests.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 2: Write the failing test**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/KafkaConfigParserTests.cs
using FluentAssertions;
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka.Tests.Client;

public class KafkaConfigParserTests
{
    [Fact]
    public void Empty_string_parses_to_an_empty_dictionary()
    {
        KafkaConfigParser.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Parses_a_single_key_value_pair()
    {
        KafkaConfigParser.Parse("bootstrap.servers=broker1:9092")
            .Should().Equal(new Dictionary<string, string> { ["bootstrap.servers"] = "broker1:9092" });
    }

    [Fact]
    public void Parses_multiple_pairs_separated_by_semicolons()
    {
        var result = KafkaConfigParser.Parse("bootstrap.servers=broker1:9092;security.protocol=SASL_SSL;sasl.mechanism=PLAIN");

        result.Should().Equal(new Dictionary<string, string>
        {
            ["bootstrap.servers"] = "broker1:9092",
            ["security.protocol"] = "SASL_SSL",
            ["sasl.mechanism"] = "PLAIN",
        });
    }

    [Fact]
    public void Ignores_a_trailing_semicolon()
    {
        KafkaConfigParser.Parse("bootstrap.servers=broker1:9092;")
            .Should().Equal(new Dictionary<string, string> { ["bootstrap.servers"] = "broker1:9092" });
    }

    [Fact]
    public void Skips_a_malformed_segment_with_no_equals_sign()
    {
        KafkaConfigParser.Parse("bootstrap.servers=broker1:9092;not-a-pair;security.protocol=PLAINTEXT")
            .Should().Equal(new Dictionary<string, string>
            {
                ["bootstrap.servers"] = "broker1:9092",
                ["security.protocol"] = "PLAINTEXT",
            });
    }

    [Fact]
    public void Later_duplicate_keys_win()
    {
        KafkaConfigParser.Parse("sasl.username=first;sasl.username=second")
            .Should().Equal(new Dictionary<string, string> { ["sasl.username"] = "second" });
    }

    [Fact]
    public void Trims_whitespace_around_keys_and_values()
    {
        KafkaConfigParser.Parse(" bootstrap.servers = broker1:9092 ; security.protocol = PLAINTEXT ")
            .Should().Equal(new Dictionary<string, string>
            {
                ["bootstrap.servers"] = "broker1:9092",
                ["security.protocol"] = "PLAINTEXT",
            });
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter KafkaConfigParserTests`
Expected: FAIL to compile — `KafkaConfigParser` does not exist.

- [ ] **Step 4: Write `KafkaConfigParser`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/KafkaConfigParser.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Parses a connection secret shaped as a librdkafka config string ("key=value" pairs separated
/// by ";", e.g. "bootstrap.servers=broker1:9092;security.protocol=SASL_SSL;sasl.mechanism=PLAIN")
/// into the dictionary every Confluent.Kafka ClientConfig-derived type wraps directly -- see the
/// design spec (docs/superpowers/specs/2026-09-16-kafka-topics-plugin-design.md) §2. A malformed
/// segment (no "=") is skipped rather than throwing: the resulting config simply won't have that
/// key, which every caller already surfaces as a friendly connection-shaped error rather than a
/// parser exception.
/// </summary>
public static class KafkaConfigParser
{
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

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter KafkaConfigParserTests`
Expected: PASS (7 tests).

Also run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS — proves both new projects are correctly wired into the solution.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Kafka tests/SbConsole.Plugins.Kafka.Tests SbConsole.slnx
git commit -m "$(cat <<'EOF'
feat(kafka): scaffold plugin project and add KafkaConfigParser

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Client contracts, `IKafkaOperations`, `FriendlyKafkaError`, `PluginResult`

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Client/TopicSummary.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/CreateTopicRequest.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/PeekStart.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/KafkaMessageSummary.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/IKafkaOperations.cs`
- Create: `src/SbConsole.Plugins.Kafka/Client/FriendlyKafkaError.cs`
- Create: `src/SbConsole.Plugins.Kafka/PluginResult.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/FriendlyKafkaErrorTests.cs`

**Interfaces:**
- Consumes: nothing new (references only `SbConsole.Sdk`'s `ConnectionTestResult`/`FriendlyError` and `Confluent.Kafka`'s `KafkaException`/`Error`/`ErrorCode`).
- Produces (used by every later task): `IKafkaOperations` (the seam every handler and `ConfluentKafkaOperations` implement), `TopicSummary`/`CreateTopicRequest`/`PeekStart`/`KafkaMessageSummary` records, `FriendlyKafkaError.From(Exception) : string`, `PluginResult`/`PluginResult<T>` (`Ok`/`Fail`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/FriendlyKafkaErrorTests.cs
using Confluent.Kafka;
using FluentAssertions;
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka.Tests.Client;

public class FriendlyKafkaErrorTests
{
    [Theory]
    [InlineData(ErrorCode.Local_AllBrokersDown, "Broker(s) unreachable")]
    [InlineData(ErrorCode.Local_Transport, "Broker(s) unreachable")]
    [InlineData(ErrorCode.SaslAuthenticationFailed, "Authentication failed")]
    [InlineData(ErrorCode.TopicAuthorizationFailed, "Authentication failed")]
    [InlineData(ErrorCode.UnknownTopicOrPart, "Topic not found")]
    public void Known_error_codes_map_to_a_fixed_readable_message(ErrorCode code, string expected)
    {
        var ex = new KafkaException(new Error(code, "raw librdkafka reason text that must never reach the UI"));

        FriendlyKafkaError.From(ex).Should().Be(expected);
    }

    [Fact]
    public void Unmapped_error_codes_fall_back_to_the_librdkafka_reason_text()
    {
        var ex = new KafkaException(new Error(ErrorCode.Unknown, "some other librdkafka reason"));

        FriendlyKafkaError.From(ex).Should().Be("some other librdkafka reason");
    }

    [Fact]
    public void Non_Kafka_exceptions_fall_back_to_the_shared_FriendlyError_helper()
    {
        var ex = new InvalidOperationException("plain failure");

        FriendlyKafkaError.From(ex).Should().Be("plain failure");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter FriendlyKafkaErrorTests`
Expected: FAIL to compile — `FriendlyKafkaError` does not exist.

- [ ] **Step 3: Write the data contracts, `IKafkaOperations`, `FriendlyKafkaError`, and `PluginResult`**

```csharp
// src/SbConsole.Plugins.Kafka/Client/TopicSummary.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// ApproximateMessageCount is the sum of (high - low watermark) across every partition -- what the
/// topic's retention policy is currently holding, NOT an "unprocessed backlog" the way Service
/// Bus's ActiveMessageCount is (Kafka never removes a message on consumption). See design spec §4.
/// </summary>
public sealed record TopicSummary(string Name, int PartitionCount, int ReplicationFactor, long ApproximateMessageCount);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/CreateTopicRequest.cs
namespace SbConsole.Plugins.Kafka.Client;

public sealed record CreateTopicRequest(string Name, int PartitionCount, int ReplicationFactor);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/PeekStart.cs
namespace SbConsole.Plugins.Kafka.Client;

public enum PeekStart { Earliest, Latest, Offset }
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/KafkaMessageSummary.cs
namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Value (and Key, when present) is UTF-8 text when the raw bytes decode cleanly, otherwise
/// base64 with ValueIsBase64 = true -- see design spec §5. Key is null when the message has no key
/// (distinct from an empty-string key, which affects Kafka's default partitioner).
/// </summary>
public sealed record KafkaMessageSummary(
    int Partition, long Offset, DateTimeOffset Timestamp, string? Key, string Value, bool ValueIsBase64);
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/IKafkaOperations.cs
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// The seam between the plugin's handlers and the real Confluent.Kafka client. Every method takes
/// the connection config string as a parameter -- no instance is pre-configured for one connection
/// -- since a single registered instance tests and operates against whatever connection the caller
/// names. Mirrors SbConsole.Plugins.ServiceBus.Client.IServiceBusOperations.
/// </summary>
public interface IKafkaOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string config, CancellationToken ct = default);

    Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string config, CancellationToken ct = default);

    Task CreateTopicAsync(string config, CreateTopicRequest request, CancellationToken ct = default);

    /// <summary>Destructive.</summary>
    Task DeleteTopicAsync(string config, string topicName, CancellationToken ct = default);

    /// <summary>
    /// Non-destructive: a fresh, never-reused consumer group per call, never committing an offset.
    /// offset is read only when start is PeekStart.Offset.
    /// </summary>
    Task<IReadOnlyList<KafkaMessageSummary>> PeekMessagesAsync(
        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default);

    /// <summary>partition null lets Kafka's default partitioner choose (by key hash, or round-robin when key is null).</summary>
    Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default);
}
```

```csharp
// src/SbConsole.Plugins.Kafka/Client/FriendlyKafkaError.cs
using Confluent.Kafka;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// Kafka-specific counterpart to SbConsole.Sdk.FriendlyError: maps the ErrorCodes this plugin's
/// operations can actually hit to a short, fixed, readable message, falling back to the
/// (capped/collapsed) librdkafka reason text for anything unmapped. Confluent.Kafka failures
/// surface as KafkaException or its subclass ProduceException&lt;TKey,TValue&gt; -- both match the
/// KafkaException case below since ProduceException IS-A KafkaException. Verified against the
/// installed Confluent.Kafka version's actual ErrorCode enum at implementation time, same
/// "confirmed, not assumed" bar the Service Bus plugin's error mapping holds itself to.
/// </summary>
public static class FriendlyKafkaError
{
    public static string From(Exception ex) => ex switch
    {
        KafkaException kex => FromKafkaException(kex),
        _ => FriendlyError.From(ex),
    };

    private static string FromKafkaException(KafkaException ex) => ex.Error.Code switch
    {
        ErrorCode.Local_AllBrokersDown or ErrorCode.Local_Transport => "Broker(s) unreachable",
        ErrorCode.SaslAuthenticationFailed or ErrorCode.TopicAuthorizationFailed => "Authentication failed",
        ErrorCode.UnknownTopicOrPart => "Topic not found",
        _ => FriendlyError.Truncate(ex.Error.Reason) is { Length: > 0 } reason ? reason : FriendlyError.From(ex),
    };
}
```

```csharp
// src/SbConsole.Plugins.Kafka/PluginResult.cs
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka;

/// <summary>
/// This plugin's own lightweight result type, mirroring SbConsole.Plugins.ServiceBus.PluginResult
/// without depending on it -- plugins reference only SbConsole.Sdk. Routes exception failures
/// through FriendlyKafkaError (not the plain SbConsole.Sdk.FriendlyError) so Kafka-specific error
/// codes get their fixed readable messages.
/// </summary>
public sealed class PluginResult
{
    private PluginResult(bool isSuccess, string? error) => (IsSuccess, Error) = (isSuccess, error);
    public bool IsSuccess { get; }
    public string? Error { get; }
    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlyKafkaError.From(ex));
}

public sealed class PluginResult<T>
{
    private PluginResult(bool isSuccess, T? value, string? error) => (IsSuccess, Value, Error) = (isSuccess, value, error);
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public static PluginResult<T> Ok(T value) => new(true, value, null);
    public static PluginResult<T> Fail(string error) => new(false, default, error);
    public static PluginResult<T> Fail(Exception ex) => new(false, default, FriendlyKafkaError.From(ex));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter FriendlyKafkaErrorTests`
Expected: PASS (7 tests).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka tests/SbConsole.Plugins.Kafka.Tests
git commit -m "$(cat <<'EOF'
feat(kafka): add IKafkaOperations seam, data contracts, FriendlyKafkaError, PluginResult

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Fix `IPluginStore`'s single-plugin DI collision (keyed services)

Registering a second plugin (Kafka, starting Task 5) exposes a real bug: `AddSbConsolePlugin<TPlugin>()` registers `IPluginStore` as one unkeyed scoped service. With two plugins registered, whichever was registered *last* wins for every unkeyed `IPluginStore` resolution in the entire app — including Service Bus's `Queues.razor`, which uses it to persist metric-history sparkline data under `"azure-servicebus"`. Left unfixed, adding Kafka silently redirects that page's metric history into Kafka's storage namespace. Fixed here, before Task 5 registers a second plugin, by making the registration keyed by plugin `Id`.

**Files:**
- Modify: `src/SbConsole.Web/Plugins/PluginServiceCollectionExtensions.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs`
- Modify: `src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor`
- Modify: `tests/SbConsole.Plugins.ServiceBus.Tests/Pages/QueuesPageTests.cs`
- Test: `tests/SbConsole.Web.Tests/PluginServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `IPluginStoreFactory`/`IPluginStore`/`IPlugin` (existing, `SbConsole.Sdk`).
- Produces: `AddSbConsolePlugin<TPlugin>()` now registers `IPluginStore` as `AddKeyedScoped<IPluginStore>(plugin.Id, ...)` — every later task's Kafka pages/handlers that need it (none do in this plan) would resolve it via `[Inject, FromKeyedServices("kafka")]`, same convention Task 5 onward uses for nothing in this plan but Task 5's `KafkaPlugin` registration relies on this not colliding with Service Bus's.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SbConsole.Web.Tests/PluginServiceCollectionExtensionsTests.cs
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;
using SbConsole.Web.Plugins;

namespace SbConsole.Web.Tests;

/// <summary>
/// Regression guard: AddSbConsolePlugin&lt;TPlugin&gt;() used to register IPluginStore as a single
/// unkeyed scoped service (its own doc comment called this "a single-plugin simplification").
/// Registering a second plugin made every unkeyed IPluginStore resolution in the whole app resolve
/// to whichever plugin was registered LAST, silently corrupting the first plugin's per-connection
/// storage (e.g. Service Bus's metric-history sparklines) the moment a second plugin was added.
/// </summary>
public class PluginServiceCollectionExtensionsTests
{
    private sealed class FakeStore(string pluginId) : IPluginStore
    {
        public string PluginId { get; } = pluginId;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class FakeStoreFactory : IPluginStoreFactory
    {
        public IPluginStore For(string pluginId) => new FakeStore(pluginId);
    }

    private sealed class FakePluginA : IPlugin
    {
        public string Id => "plugin-a";
        public string DisplayName => "Plugin A";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "plugin-a";
        public string ConnectionKindDisplayName => "Plugin A";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
    }

    private sealed class FakePluginB : IPlugin
    {
        public string Id => "plugin-b";
        public string DisplayName => "Plugin B";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public string ConnectionKind => "plugin-b";
        public string ConnectionKindDisplayName => "Plugin B";
        public PluginContribution Contribution => new(0, 0);
        public void ConfigureServices(IServiceCollection services) { }
        public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
            Task.FromResult(new ConnectionTestResult(true));
    }

    [Fact]
    public void Two_registered_plugins_each_resolve_their_own_keyed_IPluginStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPluginStoreFactory, FakeStoreFactory>();
        services.AddSbConsolePlugin<FakePluginA>();
        services.AddSbConsolePlugin<FakePluginB>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var storeA = (FakeStore)scope.ServiceProvider.GetRequiredKeyedService<IPluginStore>("plugin-a");
        var storeB = (FakeStore)scope.ServiceProvider.GetRequiredKeyedService<IPluginStore>("plugin-b");

        storeA.PluginId.Should().Be("plugin-a");
        storeB.PluginId.Should().Be("plugin-b");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Web.Tests --filter PluginServiceCollectionExtensionsTests`
Expected: FAIL — `GetRequiredKeyedService<IPluginStore>("plugin-b")` throws `InvalidOperationException` (no keyed service registered), because the current registration is unkeyed.

- [ ] **Step 3: Make the registration keyed**

```csharp
// src/SbConsole.Web/Plugins/PluginServiceCollectionExtensions.cs
using SbConsole.Core.Plugins;
using SbConsole.Sdk;

namespace SbConsole.Web.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers a plugin at compile time. IPluginStore is registered keyed by the plugin's own Id
    /// (not as a single unkeyed scoped service) so more than one plugin can be registered without
    /// each plugin's per-connection storage colliding -- see docs/superpowers/plans/
    /// 2026-09-17-kafka-topics-plugin.md Task 3 for the bug this replaced.
    /// </summary>
    public static IServiceCollection AddSbConsolePlugin<TPlugin>(this IServiceCollection services)
        where TPlugin : class, IPlugin, new()
    {
        var plugin = new TPlugin();
        services.AddSingleton<IPlugin>(plugin);
        services.AddKeyedScoped<IPluginStore>(plugin.Id, (sp, _) => sp.GetRequiredService<IPluginStoreFactory>().For(plugin.Id));
        plugin.ConfigureServices(services);
        return services;
    }
}
```

Remove `ServiceBusPlugin`'s own now-redundant unkeyed registration (it would otherwise re-shadow the keyed one with a second unkeyed registration that nothing should resolve any more):

```diff
--- a/src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs
+++ b/src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs
@@ (inside ConfigureServices, right after the last services.AddScoped<...>() handler registration)
         services.AddScoped<DeadLetter.ListDeadLetterOverviewQueryHandler>();
-
-        // Pre-bound to this plugin's own Id so pages (Queues.razor) can @inject IPluginStore
-        // directly instead of going through the factory + this plugin's Id at every call site.
-        services.AddScoped<IPluginStore>(sp => sp.GetRequiredService<IPluginStoreFactory>().For(Id));
     }
```

Update `Queues.razor`'s injection to resolve the keyed instance (Blazor's `[Inject]` property injection honors `[FromKeyedServices]`; the `@inject` directive has no keyed-service syntax, so this one property moves into `@code`):

```diff
--- a/src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor
+++ b/src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor
@@ -1,12 +1,12 @@
 @page "/p/azure-servicebus/queues"
 @using SbConsole.Plugins.ServiceBus.Client
 @using SbConsole.Plugins.ServiceBus.Queues
 @using SbConsole.Sdk
+@using Microsoft.Extensions.DependencyInjection
 @inject IConnectionProvider Connections
-@inject IPluginStore Store
 @inject TimeProvider Clock
 @inject ListQueuesQueryHandler ListHandler
 @inject DeleteQueueCommandHandler DeleteHandler
 @inject IConfirmationService Confirmation
 @inject IDialogService DialogService
 @inject ISnackbar Snackbar
```

```diff
--- a/src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor
+++ b/src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor
@@ (inside @code, right after the existing field declarations)
+    [Inject, FromKeyedServices("azure-servicebus")]
+    private IPluginStore Store { get; set; } = default!;
+
```

(Place the new property right after the `private ConnectionInfo? SelectedConnection => ...` line, before `protected override async Task OnInitializedAsync()` — everywhere else in the file that already reads `Store` needs no change, since it's still a plain `Store` reference.)

Update `QueuesPageTests.cs` to register the store keyed instead of unkeyed:

```diff
--- a/tests/SbConsole.Plugins.ServiceBus.Tests/Pages/QueuesPageTests.cs
+++ b/tests/SbConsole.Plugins.ServiceBus.Tests/Pages/QueuesPageTests.cs
@@
         var store = Substitute.For<IPluginStore>();
         store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
-        Services.AddSingleton(store);
+        Services.AddKeyedSingleton<IPluginStore>("azure-servicebus", store);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Web.Tests --filter PluginServiceCollectionExtensionsTests`
Expected: PASS.

Run: `dotnet test tests/SbConsole.Plugins.ServiceBus.Tests --filter QueuesPageTests`
Expected: PASS (all existing Queues page tests still green — proves the keyed-injection change didn't regress Service Bus's own metric history).

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Web/Plugins/PluginServiceCollectionExtensions.cs src/SbConsole.Plugins.ServiceBus/ServiceBusPlugin.cs src/SbConsole.Plugins.ServiceBus/Pages/Queues.razor tests/SbConsole.Plugins.ServiceBus.Tests/Pages/QueuesPageTests.cs tests/SbConsole.Web.Tests/PluginServiceCollectionExtensionsTests.cs
git commit -m "$(cat <<'EOF'
fix(plugins): key IPluginStore registration by plugin Id

AddSbConsolePlugin<TPlugin>() registered IPluginStore as one unkeyed
scoped service; registering a second plugin (Kafka, next) would have
made every unkeyed IPluginStore in the app resolve to whichever plugin
registered last, silently redirecting Service Bus's metric-history
storage into the other plugin's namespace.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `ConfluentKafkaOperations` — connection test + topic management

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations`, `KafkaConfigParser`, `FriendlyKafkaError`, `TopicSummary`/`CreateTopicRequest` (Tasks 1–2), `ConnectionTestResult`/`FriendlyError` (`SbConsole.Sdk`).
- Produces: `ConfluentKafkaOperations`, the only real `IKafkaOperations` implementation — `TestConnectionAsync`/`ListTopicsAsync`/`CreateTopicAsync`/`DeleteTopicAsync` (Task 6 adds the remaining two methods to this same class).

- [ ] **Step 1: Write the failing test**

Only the pure, offline-certain part of this class is worth asserting directly (mirrors `AzureServiceBusOperationsTests`' own scope note — this class can't be meaningfully unit-tested without a real or emulated broker; the config-building helper is the one piece that's pure client-side logic with no network call):

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
using Confluent.Kafka;
using FluentAssertions;
using SbConsole.Plugins.Kafka.Client;

namespace SbConsole.Plugins.Kafka.Tests.Client;

public class ConfluentKafkaOperationsTests
{
    // What's testable here without a broker is exactly the client-side half: building the typed
    // config objects from a parsed connection string. Every network-touching branch (unreachable
    // broker, auth failure) is out of scope for this suite per the design spec §8 -- same
    // "no real AMQP/HTTP traffic" rule AzureServiceBusOperationsTests follows.

    [Fact]
    public void CreateAdminClientConfig_wraps_the_parsed_dictionary()
    {
        var config = ConfluentKafkaOperations.CreateAdminClientConfig("bootstrap.servers=broker1:9092;security.protocol=SASL_SSL");

        config.BootstrapServers.Should().Be("broker1:9092");
        config.SecurityProtocol.Should().Be(SecurityProtocol.SaslSsl);
    }

    [Fact]
    public void CreateConsumerConfig_sets_the_given_group_id_and_disables_auto_commit()
    {
        var config = ConfluentKafkaOperations.CreateConsumerConfig("bootstrap.servers=broker1:9092", "my-group");

        config.BootstrapServers.Should().Be("broker1:9092");
        config.GroupId.Should().Be("my-group");
        config.EnableAutoCommit.Should().BeFalse();
    }

    [Fact]
    public void CreateProducerConfig_wraps_the_parsed_dictionary()
    {
        var config = ConfluentKafkaOperations.CreateProducerConfig("bootstrap.servers=broker1:9092");

        config.BootstrapServers.Should().Be("broker1:9092");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: FAIL to compile — `ConfluentKafkaOperations` does not exist.

- [ ] **Step 3: Write `ConfluentKafkaOperations`' connection-test and topic-management methods**

```csharp
// src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Client;

/// <summary>
/// The only real implementation of IKafkaOperations. Its own methods get light test coverage by
/// necessity -- they can't be meaningfully unit-tested without a real or emulated broker (design
/// spec §8) -- the substitutable interface is where the test leverage is.
/// </summary>
public sealed class ConfluentKafkaOperations : IKafkaOperations
{
    // Bounds every single blocking Confluent.Kafka call (GetMetadata, QueryWatermarkOffsets) so an
    // unreachable cluster fails fast with a clear message instead of hanging the page -- same role
    // AzureServiceBusOperations.AttemptTimeout plays for Service Bus.
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    internal static AdminClientConfig CreateAdminClientConfig(string config) => new(KafkaConfigParser.Parse(config));

    internal static ConsumerConfig CreateConsumerConfig(string config, string groupId) =>
        new(KafkaConfigParser.Parse(config)) { GroupId = groupId, EnableAutoCommit = false };

    internal static ProducerConfig CreateProducerConfig(string config) => new(KafkaConfigParser.Parse(config));

    // Confluent.Kafka's AdminClient/Consumer APIs (GetMetadata, QueryWatermarkOffsets, Assign,
    // Consume) are synchronous/blocking with no async overload -- Task.Run keeps every one of
    // these off the calling ASP.NET/Blazor circuit thread instead of blocking it for the length of
    // AttemptTimeout (or, for PeekMessagesAsync in Task 6, PeekWallClockCap).
    public Task<ConnectionTestResult> TestConnectionAsync(string config, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
                admin.GetMetadata(AttemptTimeout);
                return new ConnectionTestResult(true);
            }
            catch (KafkaException ex)
            {
                return new ConnectionTestResult(false, FriendlyKafkaError.From(ex));
            }
            catch (Exception ex)
            {
                return new ConnectionTestResult(false, FriendlyError.From(ex));
            }
        }, ct);

    public Task<IReadOnlyList<TopicSummary>> ListTopicsAsync(string config, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<TopicSummary>>(() =>
        {
            using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
            var metadata = admin.GetMetadata(AttemptTimeout);

            // One throwaway consumer group per call, purely to ask the cluster for watermark
            // offsets -- never subscribes, never commits. Same "fresh, never-reused group.id"
            // convention PeekMessagesAsync (Task 6) uses.
            using var consumer = new ConsumerBuilder<byte[], byte[]>(CreateConsumerConfig(config, Guid.NewGuid().ToString())).Build();

            var topics = new List<TopicSummary>();
            foreach (var topic in metadata.Topics)
            {
                var partitionCount = topic.Partitions.Count;
                // Replication factor is read from partition 0's replica count -- a topic with
                // non-uniform per-partition replication (possible after a manual reassignment
                // outside this tool) just shows that partition's count. See design spec §4.
                var replicationFactor = partitionCount > 0 ? topic.Partitions[0].Replicas.Length : 0;

                long approximateMessageCount = 0;
                foreach (var partition in topic.Partitions)
                {
                    var topicPartition = new TopicPartition(topic.Topic, new Partition(partition.PartitionId));
                    var watermarks = consumer.QueryWatermarkOffsets(topicPartition, AttemptTimeout);
                    approximateMessageCount += watermarks.High.Value - watermarks.Low.Value;
                }

                topics.Add(new TopicSummary(topic.Topic, partitionCount, replicationFactor, approximateMessageCount));
            }

            return topics;
        }, ct);

    public async Task CreateTopicAsync(string config, CreateTopicRequest request, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name = request.Name,
                NumPartitions = request.PartitionCount,
                ReplicationFactor = (short)request.ReplicationFactor,
            }
        ]);
    }

    public async Task DeleteTopicAsync(string config, string topicName, CancellationToken ct = default)
    {
        using var admin = new AdminClientBuilder(CreateAdminClientConfig(config)).Build();
        await admin.DeleteTopicsAsync([topicName]);
    }

    // PeekMessagesAsync and ProduceMessageAsync are added in Task 6.
    public Task<IReadOnlyList<KafkaMessageSummary>> PeekMessagesAsync(
        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 6.");

    public Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default) =>
        throw new NotImplementedException("Added in Task 6.");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: PASS (3 tests).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add ConfluentKafkaOperations connection test and topic management

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: `KafkaPlugin` + Topics handlers + registration + Topics page (end-to-end slice)

This is the highest-risk task in the plan — it proves `KafkaPlugin` registers correctly alongside `ServiceBusPlugin` without the `IPluginStore` collision Task 3 fixed, and that a real Kafka page renders and authenticates. Routing itself needs no host change: `Routes.razor`/`Program.cs` already scan every registered plugin's assembly generically (`docs/design.md` §5), which Service Bus's own first-plugin plan had to build — this plan gets it for free.

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/KafkaPlugin.cs`
- Create: `src/SbConsole.Plugins.Kafka/Pages/_Imports.razor`
- Create: `src/SbConsole.Plugins.Kafka/Pages/Topics.razor`
- Create: `src/SbConsole.Plugins.Kafka/Pages/CreateTopicDialog.razor`
- Create: `src/SbConsole.Plugins.Kafka/Topics/ListTopicsQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Kafka/Topics/CreateTopicCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Kafka/Topics/DeleteTopicCommandHandler.cs`
- Modify: `src/SbConsole.Web/Program.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Topics/ListTopicsQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Topics/CreateTopicCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Topics/DeleteTopicCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/TopicsPageTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/CreateTopicDialogTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations`/`ConfluentKafkaOperations` (Task 4), `IConnectionProvider`/`IAuditScope`/`IConfirmationService`/`ActionRisk` (`SbConsole.Sdk`), `AddSbConsolePlugin<TPlugin>()` (Task 3).
- Produces: a routable, authenticated, real page at `/p/kafka/topics`; Task 7 builds inside this page (adding the Send button) and `KafkaPlugin.ConfigureServices` on top of what this task establishes.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/KafkaPluginTests.cs
using FluentAssertions;
using SbConsole.Plugins.Kafka;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests;

public class KafkaPluginTests
{
    [Fact]
    public void Declares_the_expected_identity_and_connection_kind()
    {
        var plugin = new KafkaPlugin();

        plugin.Id.Should().Be("kafka");
        plugin.ConnectionKind.Should().Be("kafka");
        plugin.DisplayName.Should().Be("Apache Kafka");
        plugin.ConnectionKindDisplayName.Should().Be("Apache Kafka");
        plugin.NavItems.Should().ContainSingle(n => n.Title == "Topics" && n.Href == "/p/kafka/topics");
        plugin.Contribution.Should().Be(new PluginContribution(PageCount: 2, ActionCount: 4));
    }

    [Fact]
    public async Task TestConnectionAsync_delegates_to_the_real_Kafka_client_and_never_throws()
    {
        var plugin = new KafkaPlugin();

        var result = await plugin.TestConnectionAsync("bootstrap.servers=127.0.0.1:1");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Topics/ListTopicsQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class ListTopicsQueryHandlerTests
{
    [Fact]
    public async Task Returns_topics_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var topics = new[] { new TopicSummary("orders", 3, 1, 42) };
        operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>()).Returns(topics);

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(topics);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new ListTopicsQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Operation_failure_returns_a_failure_instead_of_throwing()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<TopicSummary>>(new InvalidOperationException("cluster unreachable")));

        var result = await new ListTopicsQueryHandler(operations, connections, NullLogger<ListTopicsQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("cluster unreachable");
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Topics/CreateTopicCommandHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class CreateTopicCommandHandlerTests
{
    [Fact]
    public async Task Creates_the_topic_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "kafka-dev", "orders", 3, 1));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).CreateTopicAsync("bootstrap.servers=real:9092", Arg.Is<CreateTopicRequest>(r => r.Name == "orders" && r.PartitionCount == 3 && r.ReplicationFactor == 1), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.create", "kafka-dev/orders", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.CreateTopicAsync(Arg.Any<string>(), Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("already exists")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new CreateTopicCommandHandler(operations, connections, audit, NullLogger<CreateTopicCommandHandler>.Instance)
            .HandleAsync(new CreateTopicCommand(connectionId, "kafka-dev", "orders", 3, 1));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("already exists");
        await audit.Received(1).RecordAsync("topic.create", "kafka-dev/orders", ActionRisk.Mutating, false, "already exists", Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Topics/DeleteTopicCommandHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class DeleteTopicCommandHandlerTests
{
    [Fact]
    public async Task Deletes_the_topic_and_audits_as_destructive()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance)
            .HandleAsync(new DeleteTopicCommand(connectionId, "kafka-dev", false, "orders"));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).DeleteTopicAsync("bootstrap.servers=real:9092", "orders", Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("topic.delete", "kafka-dev/orders", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.DeleteTopicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("not found")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new DeleteTopicCommandHandler(operations, connections, audit, NullLogger<DeleteTopicCommandHandler>.Instance)
            .HandleAsync(new DeleteTopicCommand(connectionId, "kafka-dev", false, "orders"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("not found");
        await audit.Received(1).RecordAsync("topic.delete", "kafka-dev/orders", ActionRisk.Destructive, false, "not found", Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/TopicsPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class TopicsPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ConnectionInfo _connectionInfo;

    public TopicsPageTests()
    {
        _connectionInfo = new ConnectionInfo(_connectionId, "kafka-dev", "kafka", ["dev"]);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo> { _connectionInfo });
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
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
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 42) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("orders");
        cut.Markup.Should().Contain("42");
    }

    [Fact]
    public async Task No_connections_shows_an_honest_empty_state()
    {
        _connections.ListAsync("kafka", Arg.Any<CancellationToken>()).Returns(new List<ConnectionInfo>());

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("No connections");
    }

    [Fact]
    public async Task Delete_goes_through_confirmation_before_calling_the_handler()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 0) });
        var confirmation = Services.GetRequiredService<IConfirmationService>();
        confirmation.ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>()).Returns(true);

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await confirmation.Received(1).ConfirmAsync("Delete", "orders", _connectionInfo.IsProd, null, Arg.Any<CancellationToken>());
        await _operations.Received(1).DeleteTopicAsync("bootstrap.servers=real:9092", "orders", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_does_nothing_when_confirmation_is_denied()
    {
        _operations.ListTopicsAsync("bootstrap.servers=real:9092", Arg.Any<CancellationToken>())
            .Returns(new List<TopicSummary> { new("orders", 3, 1, 0) });

        var cut = Render<SbConsole.Plugins.Kafka.Pages.Topics>();
        await Task.Delay(30);
        cut.Render();
        cut.Find("button.delete-topic").Click();
        await Task.Delay(30);

        await _operations.DidNotReceive().DeleteTopicAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/CreateTopicDialogTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class CreateTopicDialogTests : BunitContext, IAsyncLifetime
{
    // MudBlazor registers a few interop-backed services (key interception for popovers,
    // pointer-events routing) that implement only IAsyncDisposable. xunit v2 tears down a test
    // class via its synchronous IDisposable.Dispose() unless the class also implements
    // Xunit.IAsyncLifetime, in which case DisposeAsync() runs first. Same pattern as
    // CreateQueueDialogTests.cs (SbConsole.Plugins.ServiceBus.Tests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    // MudDialog's own rendering is gated on an internal `IMudDialogInstanceInternal` cascading
    // parameter (distinct from the public `IMudDialogInstance` our component's code-behind uses to
    // call Close/Cancel) -- without it, <MudDialog> treats itself as "inline and not yet shown" and
    // renders nothing. That type isn't public, so it can't be named directly; instead we build one
    // substitute that implements both interfaces via reflection, and cascade it as
    // `IMudDialogInstance`. See CreateQueueDialogTests.cs for the same pattern.
    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public CreateTopicDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
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
        // Must cascade as CascadingValue<TRuntimeProxyType>, not CascadingValue<IMudDialogInstance>
        // -- MudDialog's internal cascading parameter is typed IMudDialogInstanceInternal, and
        // Blazor only matches a CascadingValue<T> to consumers whose parameter type T is
        // assignable from.
        var cascadingValueType = typeof(CascadingValue<>).MakeGenericType(_dialogInstance.GetType());

        RenderFragment fragment = builder =>
        {
            builder.OpenComponent(0, cascadingValueType);
            builder.AddComponentParameter(1, "Value", _dialogInstance);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(inner =>
            {
                inner.OpenComponent<SbConsole.Plugins.Kafka.Pages.CreateTopicDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Kafka.Pages.CreateTopicDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Kafka.Pages.CreateTopicDialog.ConnectionName), "kafka-dev");
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
        await _operations.Received(1).CreateTopicAsync("bootstrap.servers=real:9092", Arg.Is<CreateTopicRequest>(r => r.Name == "orders"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.CreateTopicAsync("bootstrap.servers=real:9092", Arg.Any<CreateTopicRequest>(), Arg.Any<CancellationToken>())
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

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests`
Expected: FAIL to compile — `KafkaPlugin`, the handlers, `Topics.razor`, and `CreateTopicDialog.razor` don't exist yet.

- [ ] **Step 3: Write the Topics handlers**

```csharp
// src/SbConsole.Plugins.Kafka/Topics/ListTopicsQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Topics;

public sealed class ListTopicsQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<ListTopicsQueryHandler> logger)
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

```csharp
// src/SbConsole.Plugins.Kafka/Topics/CreateTopicCommandHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Topics;

public sealed record CreateTopicCommand(Guid ConnectionId, string ConnectionName, string TopicName, int PartitionCount, int ReplicationFactor);

public sealed class CreateTopicCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<CreateTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(CreateTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.CreateTopicAsync(secret, new CreateTopicRequest(cmd.TopicName, cmd.PartitionCount, cmd.ReplicationFactor), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating topic {Target} failed.", target);
            await audit.RecordAsync("topic.create", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("topic.create", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

```csharp
// src/SbConsole.Plugins.Kafka/Topics/DeleteTopicCommandHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Topics;

public sealed record DeleteTopicCommand(Guid ConnectionId, string ConnectionName, bool IsProd, string TopicName);

public sealed class DeleteTopicCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<DeleteTopicCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(DeleteTopicCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.DeleteTopicAsync(secret, cmd.TopicName, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deleting topic {Target} failed.", target);
            await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("topic.delete", target, ActionRisk.Destructive, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

- [ ] **Step 4: Write `KafkaPlugin`**

```csharp
// src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka;

public sealed class KafkaPlugin : IPlugin
{
    public string Id => "kafka";
    public string DisplayName => "Apache Kafka";
    public string Version => "1.0.0";
    public IReadOnlyList<PluginNavItem> NavItems =>
    [
        new("Topics", "/p/kafka/topics"),
    ];
    public string ConnectionKind => "kafka";
    public string ConnectionKindDisplayName => "Apache Kafka";

    // Topics: Create/Delete topic, Peek, Send (4 -- Peek/Send land in Task 7). Pages: Topics, Peek.
    public PluginContribution Contribution => new(PageCount: 2, ActionCount: 4);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IKafkaOperations, ConfluentKafkaOperations>();
        services.AddScoped<Topics.ListTopicsQueryHandler>();
        services.AddScoped<Topics.CreateTopicCommandHandler>();
        services.AddScoped<Topics.DeleteTopicCommandHandler>();
    }

    // Plugins are constructed via a parameterless new() (AddSbConsolePlugin<TPlugin>()'s `new()`
    // constraint), so there's no DI container to pull a registered IKafkaOperations from at this
    // layer -- construct the real implementation directly, same as ServiceBusPlugin does.
    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new ConfluentKafkaOperations().TestConnectionAsync(secret, ct);

    public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(string connectionString, CancellationToken ct = default)
    {
        var topics = await new ConfluentKafkaOperations().ListTopicsAsync(connectionString, ct);
        return
        [
            new PluginDashboardMetric("Topics", topics.Count),
            new PluginDashboardMetric("Partitions", topics.Sum(t => t.PartitionCount)),
        ];
    }
}
```

Register it in `Program.cs`, right after Service Bus:

```diff
--- a/src/SbConsole.Web/Program.cs
+++ b/src/SbConsole.Web/Program.cs
@@
 builder.Services.AddSbConsolePlugin<SbConsole.Plugins.ServiceBus.ServiceBusPlugin>();
+builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Kafka.KafkaPlugin>();
 builder.Services.AddHostedService<MetricsCollectorService>();
```

- [ ] **Step 5: Write `Pages/_Imports.razor`, `Topics.razor`, and `CreateTopicDialog.razor`**

```razor
{{-- src/SbConsole.Plugins.Kafka/Pages/_Imports.razor --}}
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Web
@using MudBlazor
@attribute [Authorize]
```

```razor
{{-- src/SbConsole.Plugins.Kafka/Pages/Topics.razor --}}
@page "/p/kafka/topics"
@using SbConsole.Plugins.Kafka.Client
@using SbConsole.Plugins.Kafka.Topics
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
    <MudAlert Severity="Severity.Info">No connections yet. Add an Apache Kafka connection to get started.</MudAlert>
}
else
{
    <div class="d-flex align-center flex-wrap gap-4 my-4">
        <div class="d-flex align-center gap-2">
            <MudText Typo="Typo.overline" Class="mud-text-secondary">Cluster</MudText>
            <MudMenu Class="cluster-menu">
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
                        <MudMenuItem Class="cluster-option" OnClick="@(() => OnConnectionChanged(connection.Id))">@connection.Name</MudMenuItem>
                    }
                </ChildContent>
            </MudMenu>
        </div>

        <MudSpacer />
        <MudButton Class="create-topic-action" Color="Color.Primary" Variant="Variant.Outlined" OnClick="OpenCreate">+ Create topic</MudButton>
        @if (_loading)
        {
            <MudProgressCircular Class="topics-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
    </div>

    <MudTable Items="_topics">
        <HeaderContent>
            <MudTh>Name</MudTh>
            <MudTh>Partitions</MudTh>
            <MudTh>Replication</MudTh>
            <MudTh>Messages (approx)</MudTh>
            <MudTh>Actions</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd Style="font-family:monospace">@context.Name</MudTd>
            <MudTd>@context.PartitionCount</MudTd>
            <MudTd>@context.ReplicationFactor</MudTd>
            <MudTd>@context.ApproximateMessageCount</MudTd>
            <MudTd>
                <MudButton Href="@PeekUrl(context.Name, context.PartitionCount)">Peek</MudButton>
                <MudButton Class="delete-topic" Color="Color.Error" Disabled="@(_deletingTopic == context.Name)" OnClick="@(() => DeleteAsync(context.Name))">Delete</MudButton>
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

    private ConnectionInfo? SelectedConnection => _connections.FirstOrDefault(c => c.Id == _selectedConnectionId);

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("kafka");
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

    // Peek.razor (Task 7) looks the connection's name and prod status up itself from
    // ConnectionId, rather than trusting them off the URL -- same rule Service Bus's Peek.razor
    // follows. partitionCount is only a display convenience (populating the partition selector
    // without a second round trip), not security-relevant, so it's fine to carry on the URL.
    private string PeekUrl(string topicName, int partitionCount)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        return $"/p/kafka/topics/{Uri.EscapeDataString(topicName)}/peek?connectionId={connection.Id}&partitionCount={partitionCount}";
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

    private async Task DeleteAsync(string topicName)
    {
        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
        var confirmed = await Confirmation.ConfirmAsync("Delete", topicName, connection.IsProd);
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

```razor
{{-- src/SbConsole.Plugins.Kafka/Pages/CreateTopicDialog.razor --}}
@using SbConsole.Plugins.Kafka.Topics
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudTextField id="topic-name" @bind-Value="_name" Label="Name" Required="true" Immediate="true" />
        <MudNumericField @bind-Value="_partitionCount" Label="Partitions" Min="1" Max="1000" />
        <MudNumericField @bind-Value="_replicationFactor" Label="Replication factor" Min="1" Max="32" />
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
    private int _partitionCount = 1;
    private int _replicationFactor = 1;
    private bool _busy;

    private async Task Save()
    {
        _busy = true;
        try
        {
            var result = await CreateHandler.HandleAsync(new CreateTopicCommand(ConnectionId, ConnectionName, _name, _partitionCount, _replicationFactor));
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

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests`
Expected: PASS (all Task 5 tests).

Run: `dotnet build SbConsole.slnx -warnaserror && dotnet test`
Expected: full solution PASS — proves `KafkaPlugin` coexists with `ServiceBusPlugin` with no DI collisions.

- [ ] **Step 7: Manual smoke check**

```bash
dotnet run --project src/SbConsole.Web
```

Log in, navigate to `/p/kafka/topics`, confirm the page renders (empty state if no Kafka connection exists yet), and confirm `/p/azure-servicebus/queues` still renders correctly with sparklines working — proving Task 3's keyed-`IPluginStore` fix holds under the real host, not just the test doubles.

- [ ] **Step 8: Commit**

```bash
git add src/SbConsole.Plugins.Kafka src/SbConsole.Web/Program.cs tests/SbConsole.Plugins.Kafka.Tests
git commit -m "$(cat <<'EOF'
feat(kafka): add KafkaPlugin, topic management handlers, and the Topics page

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `ConfluentKafkaOperations` — peek and produce

**Files:**
- Modify: `src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations`, `KafkaMessageSummary`/`PeekStart` (Task 2).
- Produces: `IKafkaOperations.PeekMessagesAsync`/`ProduceMessageAsync`, now fully implemented — Task 7's handlers call these directly.

- [ ] **Step 1: Write the failing test**

The message-decoding helper is the one piece of this slice that's pure and offline-testable (everything else requires a real broker, per this plan's Global Constraints):

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
// -- add to the existing class from Task 4 --

    [Fact]
    public void Valid_UTF8_bytes_decode_as_text()
    {
        var (text, isBase64) = ConfluentKafkaOperations.Decode(System.Text.Encoding.UTF8.GetBytes("hello world"));

        text.Should().Be("hello world");
        isBase64.Should().BeFalse();
    }

    [Fact]
    public void Non_UTF8_bytes_decode_as_base64()
    {
        byte[] invalidUtf8 = [0xFF, 0xFE, 0x00, 0x01];

        var (text, isBase64) = ConfluentKafkaOperations.Decode(invalidUtf8);

        isBase64.Should().BeTrue();
        text.Should().Be(Convert.ToBase64String(invalidUtf8));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: FAIL to compile — `ConfluentKafkaOperations.Decode` does not exist.

- [ ] **Step 3: Replace the `NotImplementedException` stubs with real implementations**

```diff
--- a/src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
+++ b/src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
@@
+using System.Text;
 using Confluent.Kafka;
 using Confluent.Kafka.Admin;
 using SbConsole.Sdk;
```

```diff
--- a/src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
+++ b/src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs
@@
-    // PeekMessagesAsync and ProduceMessageAsync are added in Task 6.
-    public Task<IReadOnlyList<KafkaMessageSummary>> PeekMessagesAsync(
-        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default) =>
-        throw new NotImplementedException("Added in Task 6.");
-
-    public Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default) =>
-        throw new NotImplementedException("Added in Task 6.");
+    // Bounds the whole peek call, not just one Consume() -- a topic/partition with fewer messages
+    // than maxMessages must return early with what it got, not hang until this expires. Mirrors
+    // AzureServiceBusOperations.BulkOperationTimeout's role, sized down since peek is interactive,
+    // not a bulk drain.
+    internal static readonly TimeSpan PeekWallClockCap = TimeSpan.FromSeconds(30);
+
+    public Task<IReadOnlyList<KafkaMessageSummary>> PeekMessagesAsync(
+        string config, string topicName, int partition, PeekStart start, long? offset, int maxMessages, CancellationToken ct = default) =>
+        Task.Run<IReadOnlyList<KafkaMessageSummary>>(() =>
+        {
+            // A fresh, never-reused group.id every call -- peeking never commits an offset and
+            // never shares a consumer group with anything else. See design spec §5.
+            var consumerConfig = CreateConsumerConfig(config, Guid.NewGuid().ToString());
+            consumerConfig.EnablePartitionEof = true;
+            using var consumer = new ConsumerBuilder<byte[], byte[]>(consumerConfig).Build();
+
+            var topicPartition = new TopicPartition(topicName, new Partition(partition));
+            var startOffset = start switch
+            {
+                PeekStart.Earliest => Offset.Beginning,
+                PeekStart.Offset => new Offset(offset ?? 0),
+                _ => LatestStartOffset(consumer, topicPartition, maxMessages),
+            };
+
+            consumer.Assign(new TopicPartitionOffset(topicPartition, startOffset));
+
+            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
+            timeoutCts.CancelAfter(PeekWallClockCap);
+
+            var messages = new List<KafkaMessageSummary>();
+            while (messages.Count < maxMessages && !timeoutCts.IsCancellationRequested)
+            {
+                var result = consumer.Consume(TimeSpan.FromSeconds(2));
+                if (result is null || result.IsPartitionEOF)
+                {
+                    break;
+                }
+
+                messages.Add(ToSummary(result));
+            }
+
+            return messages;
+        }, ct);
+
+    private static Offset LatestStartOffset(IConsumer<byte[], byte[]> consumer, TopicPartition topicPartition, int maxMessages)
+    {
+        var watermarks = consumer.QueryWatermarkOffsets(topicPartition, AttemptTimeout);
+        var start = watermarks.High.Value - maxMessages;
+        return new Offset(Math.Max(start, watermarks.Low.Value));
+    }
+
+    private static KafkaMessageSummary ToSummary(ConsumeResult<byte[], byte[]> result)
+    {
+        var (value, valueIsBase64) = Decode(result.Message.Value);
+        var key = result.Message.Key is { Length: > 0 } keyBytes ? Decode(keyBytes).Text : null;
+        return new KafkaMessageSummary(result.Partition.Value, result.Offset.Value, result.Message.Timestamp.UtcDateTime, key, value, valueIsBase64);
+    }
+
+    // Internal (not private) so ConfluentKafkaOperationsTests can assert it directly --
+    // InternalsVisibleTo already covers the test project (Task 1's csproj).
+    internal static (string Text, bool IsBase64) Decode(byte[] bytes)
+    {
+        try
+        {
+            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
+            return (text, false);
+        }
+        catch (DecoderFallbackException)
+        {
+            return (Convert.ToBase64String(bytes), true);
+        }
+    }
+
+    public async Task ProduceMessageAsync(string config, string topicName, string? key, string value, int? partition, CancellationToken ct = default)
+    {
+        using var producer = new ProducerBuilder<string?, string>(CreateProducerConfig(config)).Build();
+        var message = new Message<string?, string> { Key = key, Value = value };
+        var topicPartition = new TopicPartition(topicName, partition is { } p ? new Partition(p) : Partition.Any);
+        await producer.ProduceAsync(topicPartition, message, ct);
+    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests --filter ConfluentKafkaOperationsTests`
Expected: PASS (5 tests total).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SbConsole.Plugins.Kafka/Client/ConfluentKafkaOperations.cs tests/SbConsole.Plugins.Kafka.Tests/Client/ConfluentKafkaOperationsTests.cs
git commit -m "$(cat <<'EOF'
feat(kafka): add ConfluentKafkaOperations peek and produce

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Messages handlers + Peek page + Send dialog

**Files:**
- Create: `src/SbConsole.Plugins.Kafka/Messages/PeekMessagesQueryHandler.cs`
- Create: `src/SbConsole.Plugins.Kafka/Messages/SendMessageCommandHandler.cs`
- Create: `src/SbConsole.Plugins.Kafka/Pages/Peek.razor`
- Create: `src/SbConsole.Plugins.Kafka/Pages/ProduceMessageDialog.razor`
- Modify: `src/SbConsole.Plugins.Kafka/KafkaPlugin.cs`
- Modify: `src/SbConsole.Plugins.Kafka/Pages/Topics.razor`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Messages/PeekMessagesQueryHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Messages/SendMessageCommandHandlerTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/PeekPageTests.cs`
- Test: `tests/SbConsole.Plugins.Kafka.Tests/Pages/ProduceMessageDialogTests.cs`

**Interfaces:**
- Consumes: `IKafkaOperations.PeekMessagesAsync`/`ProduceMessageAsync` (Task 6), `KafkaMessageSummary`/`PeekStart` (Task 2).
- Produces: a routable `/p/kafka/topics/{TopicName}/peek` page; `Topics.razor`'s Send button, wired to `ProduceMessageDialog`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Messages/PeekMessagesQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Messages;

public class PeekMessagesQueryHandlerTests
{
    [Fact]
    public async Task Returns_messages_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var messages = new[] { new KafkaMessageSummary(0, 5, DateTimeOffset.UtcNow, "k", "v", false) };
        operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Earliest, null, 32, Arg.Any<CancellationToken>())
            .Returns(messages);

        var result = await new PeekMessagesQueryHandler(operations, connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(connectionId, "orders", 0, PeekStart.Earliest, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(messages);
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_list()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new PeekMessagesQueryHandler(Substitute.For<IKafkaOperations>(), connections, NullLogger<PeekMessagesQueryHandler>.Instance)
            .HandleAsync(Guid.NewGuid(), "orders", 0, PeekStart.Earliest, null);

        result.IsSuccess.Should().BeFalse();
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Messages/SendMessageCommandHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Messages;

public class SendMessageCommandHandlerTests
{
    [Fact]
    public async Task Sends_the_message_and_audits_as_mutating()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit, NullLogger<SendMessageCommandHandler>.Instance)
            .HandleAsync(new SendMessageCommand(connectionId, "kafka-dev", "orders", "my-key", "my-value", null));

        result.IsSuccess.Should().BeTrue();
        await operations.Received(1).ProduceMessageAsync("bootstrap.servers=real:9092", "orders", "my-key", "my-value", null, Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAsync("message.send", "kafka-dev/orders", ActionRisk.Mutating, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Kafka_failure_is_reported_and_audited_as_failed()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        var operations = Substitute.For<IKafkaOperations>();
        operations.ProduceMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("unknown topic")));
        var audit = Substitute.For<IAuditScope>();

        var result = await new SendMessageCommandHandler(operations, connections, audit, NullLogger<SendMessageCommandHandler>.Instance)
            .HandleAsync(new SendMessageCommand(connectionId, "kafka-dev", "orders", null, "my-value", null));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("unknown topic");
        await audit.Received(1).RecordAsync("message.send", "kafka-dev/orders", ActionRisk.Mutating, false, "unknown topic", Arg.Any<CancellationToken>());
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/PeekPageTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class PeekPageTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly Guid _connectionId = Guid.NewGuid();

    public PeekPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddLogging();
        Services.AddSingleton<PeekMessagesQueryHandler>();
    }

    // TopicName is a route parameter (set via Render's parameter builder); ConnectionId is
    // [SupplyParameterFromQuery], so it's carried through real URL navigation instead -- same split
    // SubscriptionPeekPageTests.cs (SbConsole.Plugins.ServiceBus.Tests) uses for its own
    // route-vs-query parameters.
    private void NavigateToPeekQuery(Guid connectionId)
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameters(
            new Dictionary<string, object?> { ["ConnectionId"] = connectionId }));
    }

    private IRenderedComponent<SbConsole.Plugins.Kafka.Pages.Peek> RenderPage() =>
        Render<SbConsole.Plugins.Kafka.Pages.Peek>(parameters => parameters
            .Add(p => p.TopicName, "orders"));

    [Fact]
    public async Task Fetches_and_renders_messages_for_the_selected_partition()
    {
        // _start defaults to PeekStart.Latest in Peek.razor -- match that default rather than
        // Earliest, since Fetch is clicked with no prior interaction with the "Start from" select.
        _operations.PeekMessagesAsync("bootstrap.servers=real:9092", "orders", 0, PeekStart.Latest, null, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<KafkaMessageSummary> { new(0, 5, DateTimeOffset.UtcNow, "k1", "hello", false) });

        NavigateToPeekQuery(_connectionId);
        var cut = RenderPage();
        cut.Find("button.fetch-messages").Click();
        await Task.Delay(30);
        cut.Render();

        cut.Markup.Should().Contain("hello");
    }
}
```

```csharp
// tests/SbConsole.Plugins.Kafka.Tests/Pages/ProduceMessageDialogTests.cs
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Plugins.Kafka.Messages;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Pages;

public class ProduceMessageDialogTests : BunitContext, IAsyncLifetime
{
    // See CreateTopicDialogTests.cs for why this class implements IAsyncLifetime and builds its
    // own IMudDialogInstance substitute rather than relying on bUnit's default dialog wiring.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IKafkaOperations _operations = Substitute.For<IKafkaOperations>();
    private readonly IMudDialogInstance _dialogInstance;
    private readonly Guid _connectionId = Guid.NewGuid();

    private static readonly Type MudDialogInstanceInternalType =
        typeof(IMudDialogInstance).Assembly.GetType("MudBlazor.IMudDialogInstanceInternal")
        ?? throw new InvalidOperationException("MudBlazor.IMudDialogInstanceInternal not found - MudBlazor API may have changed.");

    public ProduceMessageDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("bootstrap.servers=real:9092");
        Services.AddSingleton(_connections);
        Services.AddSingleton(_operations);
        Services.AddSingleton(Substitute.For<IAuditScope>());
        Services.AddLogging();
        Services.AddSingleton<SendMessageCommandHandler>();

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
                inner.OpenComponent<SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog>(0);
                inner.AddComponentParameter(1, nameof(SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog.ConnectionId), _connectionId);
                inner.AddComponentParameter(2, nameof(SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog.ConnectionName), "kafka-dev");
                inner.AddComponentParameter(3, nameof(SbConsole.Plugins.Kafka.Pages.ProduceMessageDialog.TopicName), "orders");
                inner.CloseComponent();
            }));
            builder.CloseComponent();
        };

        return Render(fragment);
    }

    [Fact]
    public async Task Send_closes_the_dialog_and_calls_the_operations_seam_when_the_handler_succeeds()
    {
        var cut = RenderDialog();
        // #message-value (no "input#" prefix) -- the field is multiline (Lines="8") and renders as
        // a <textarea>, not <input>, same reason SendMessageDialogTests.cs finds "#message-body"
        // without an element-tag prefix.
        cut.Find("#message-value").Input("hello world");

        cut.Find("button.send-message").Click();
        await Task.Delay(30);

        _dialogInstance.Received(1).Close(Arg.Is<DialogResult>(r => r != null && !r.Canceled));
        await _operations.Received(1).ProduceMessageAsync("bootstrap.servers=real:9092", "orders", null, "hello world", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_shows_the_error_and_keeps_the_dialog_open_when_the_handler_fails()
    {
        _operations.ProduceMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("unknown topic")));
        var snackbar = Services.GetRequiredService<ISnackbar>();

        var cut = RenderDialog();
        cut.Find("#message-value").Input("hello world");

        cut.Find("button.send-message").Click();
        await Task.Delay(30);

        _dialogInstance.DidNotReceive().Close(Arg.Any<DialogResult>());
        snackbar.ShownSnackbars.Should().Contain(s => s.Message != null && s.Message.Contains("unknown topic"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests`
Expected: FAIL to compile — the handlers, `Peek.razor`, and `ProduceMessageDialog.razor` don't exist yet.

- [ ] **Step 3: Write the Messages handlers**

```csharp
// src/SbConsole.Plugins.Kafka/Messages/PeekMessagesQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Messages;

public sealed class PeekMessagesQueryHandler(IKafkaOperations operations, IConnectionProvider connections, ILogger<PeekMessagesQueryHandler> logger)
{
    public async Task<PluginResult<IReadOnlyList<KafkaMessageSummary>>> HandleAsync(
        Guid connectionId, string topicName, int partition, PeekStart start, long? offset, int maxMessages = 32, CancellationToken ct = default)
    {
        try
        {
            var secret = await connections.GetSecretAsync(connectionId, ct);
            if (secret is null)
            {
                return PluginResult<IReadOnlyList<KafkaMessageSummary>>.Fail("Connection not found.");
            }

            var messages = await operations.PeekMessagesAsync(secret, topicName, partition, start, offset, maxMessages, ct);
            return PluginResult<IReadOnlyList<KafkaMessageSummary>>.Ok(messages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Peeking {TopicName} partition {Partition} failed.", topicName, partition);
            return PluginResult<IReadOnlyList<KafkaMessageSummary>>.Fail(ex);
        }
    }
}
```

```csharp
// src/SbConsole.Plugins.Kafka/Messages/SendMessageCommandHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Kafka.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Messages;

public sealed record SendMessageCommand(Guid ConnectionId, string ConnectionName, string TopicName, string? Key, string Value, int? Partition);

public sealed class SendMessageCommandHandler(IKafkaOperations operations, IConnectionProvider connections, IAuditScope audit, ILogger<SendMessageCommandHandler> logger)
{
    public async Task<PluginResult> HandleAsync(SendMessageCommand cmd, CancellationToken ct = default)
    {
        var target = $"{cmd.ConnectionName}/{cmd.TopicName}";
        try
        {
            var secret = await connections.GetSecretAsync(cmd.ConnectionId, ct);
            if (secret is null)
            {
                return PluginResult.Fail("Connection not found.");
            }

            await operations.ProduceMessageAsync(secret, cmd.TopicName, cmd.Key, cmd.Value, cmd.Partition, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sending a message to {Target} failed.", target);
            await audit.RecordAsync("message.send", target, ActionRisk.Mutating, succeeded: false, detail: FriendlyKafkaError.From(ex), ct: ct);
            return PluginResult.Fail(ex);
        }

        await audit.RecordAsync("message.send", target, ActionRisk.Mutating, succeeded: true, ct: ct);
        return PluginResult.Ok();
    }
}
```

Register both in `KafkaPlugin.ConfigureServices`:

```diff
--- a/src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
+++ b/src/SbConsole.Plugins.Kafka/KafkaPlugin.cs
@@
         services.AddScoped<Topics.ListTopicsQueryHandler>();
         services.AddScoped<Topics.CreateTopicCommandHandler>();
         services.AddScoped<Topics.DeleteTopicCommandHandler>();
+        services.AddScoped<Messages.PeekMessagesQueryHandler>();
+        services.AddScoped<Messages.SendMessageCommandHandler>();
```

- [ ] **Step 4: Write `Peek.razor` and `ProduceMessageDialog.razor`**

```razor
{{-- src/SbConsole.Plugins.Kafka/Pages/Peek.razor --}}
@page "/p/kafka/topics/{TopicName}/peek"
@using SbConsole.Plugins.Kafka.Client
@using SbConsole.Plugins.Kafka.Messages
@using SbConsole.Sdk
@inject PeekMessagesQueryHandler PeekHandler
@inject ISnackbar Snackbar

<PageTitle>Peek — @TopicName</PageTitle>
<h1>Peek: @TopicName</h1>

<div class="d-flex flex-wrap gap-4 align-center my-4">
    <MudSelect T="int" Label="Partition" @bind-Value="_partition" Style="min-width:120px">
        @for (var i = 0; i < PartitionCount; i++)
        {
            <MudSelectItem Value="@i">@i</MudSelectItem>
        }
    </MudSelect>
    <MudSelect T="PeekStart" Label="Start from" @bind-Value="_start" Style="min-width:160px">
        <MudSelectItem Value="PeekStart.Earliest">Earliest</MudSelectItem>
        <MudSelectItem Value="PeekStart.Latest">Latest</MudSelectItem>
        <MudSelectItem Value="PeekStart.Offset">Offset</MudSelectItem>
    </MudSelect>
    @if (_start == PeekStart.Offset)
    {
        <MudNumericField T="long" Label="Offset" @bind-Value="_offset" />
    }
    <MudNumericField T="int" Label="Max messages" @bind-Value="_maxMessages" Min="1" Max="500" />
    <MudButton Class="fetch-messages" Color="Color.Primary" Variant="Variant.Filled" Disabled="_busy" OnClick="LoadAsync">Fetch</MudButton>
    @if (_busy)
    {
        <MudProgressCircular Class="peek-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
    }
</div>

<MudTable Items="_messages">
    <HeaderContent>
        <MudTh>Partition</MudTh>
        <MudTh>Offset</MudTh>
        <MudTh>Timestamp</MudTh>
        <MudTh>Key</MudTh>
        <MudTh>Value</MudTh>
    </HeaderContent>
    <RowTemplate>
        <MudTd>@context.Partition</MudTd>
        <MudTd>@context.Offset</MudTd>
        <MudTd>@context.Timestamp</MudTd>
        <MudTd Style="font-family:monospace">@(context.Key ?? "—")</MudTd>
        <MudTd Style="font-family:monospace">
            @context.Value
            @if (context.ValueIsBase64)
            {
                <MudChip T="string" Size="Size.Small" Class="ms-2">binary (base64)</MudChip>
            }
        </MudTd>
    </RowTemplate>
</MudTable>

@code {
    [Parameter] public string TopicName { get; set; } = "";
    [SupplyParameterFromQuery] public Guid ConnectionId { get; set; }
    // Carried from Topics.razor's own already-fetched TopicSummary.PartitionCount (via PeekUrl) so
    // the partition selector doesn't need a second round trip just to populate itself.
    [SupplyParameterFromQuery] public int PartitionCount { get; set; } = 1;

    private IReadOnlyList<KafkaMessageSummary> _messages = [];
    private int _partition;
    private PeekStart _start = PeekStart.Latest;
    private long _offset;
    private int _maxMessages = 32;
    private bool _busy;

    private async Task LoadAsync()
    {
        _busy = true;
        try
        {
            var result = await PeekHandler.HandleAsync(ConnectionId, TopicName, _partition, _start, _start == PeekStart.Offset ? _offset : null, _maxMessages);
            if (result.IsSuccess)
            {
                _messages = result.Value!;
            }
            else
            {
                _messages = [];
                Snackbar.Add(result.Error!, Severity.Error);
            }
        }
        finally
        {
            _busy = false;
        }
    }
}
```

```razor
{{-- src/SbConsole.Plugins.Kafka/Pages/ProduceMessageDialog.razor --}}
@using SbConsole.Plugins.Kafka.Messages
@inject ISnackbar Snackbar

<MudDialog>
    <DialogContent>
        <MudText Typo="Typo.body2" Class="mb-2">@ConnectionName / @TopicName</MudText>
        <MudTextField id="message-key" @bind-Value="_key" Label="Key (optional)" Immediate="true" />
        <MudTextField id="message-value" @bind-Value="_value" Label="Value" Lines="8" Immediate="true" />
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        @if (_busy)
        {
            <MudProgressCircular Class="send-message-busy" Color="Color.Primary" Size="Size.Small" Indeterminate="true" />
        }
        <MudButton Class="send-message" Color="Color.Primary" Variant="Variant.Filled" Disabled="@(string.IsNullOrWhiteSpace(_value) || _busy)" OnClick="Send">Send</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid ConnectionId { get; set; }
    [Parameter] public string ConnectionName { get; set; } = "";
    [Parameter] public string TopicName { get; set; } = "";

    [Inject] private SendMessageCommandHandler SendHandler { get; set; } = default!;

    private string _key = "";
    private string _value = "";
    private bool _busy;

    private async Task Send()
    {
        _busy = true;
        try
        {
            var key = string.IsNullOrWhiteSpace(_key) ? null : _key;
            var result = await SendHandler.HandleAsync(new SendMessageCommand(ConnectionId, ConnectionName, TopicName, key, _value, null));
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

Add the Send button to `Topics.razor`, now that `ProduceMessageDialog` exists:

```diff
--- a/src/SbConsole.Plugins.Kafka/Pages/Topics.razor
+++ b/src/SbConsole.Plugins.Kafka/Pages/Topics.razor
@@
                 <MudButton Href="@PeekUrl(context.Name, context.PartitionCount)">Peek</MudButton>
+                <MudButton Class="send-message-action" OnClick="@(() => OpenSend(context.Name))">Send</MudButton>
                 <MudButton Class="delete-topic" Color="Color.Error" Disabled="@(_deletingTopic == context.Name)" OnClick="@(() => DeleteAsync(context.Name))">Delete</MudButton>
```

```diff
--- a/src/SbConsole.Plugins.Kafka/Pages/Topics.razor
+++ b/src/SbConsole.Plugins.Kafka/Pages/Topics.razor
@@ (inside @code, right after OpenCreate)
+    private async Task OpenSend(string topicName)
+    {
+        var connection = _connections.Single(c => c.Id == _selectedConnectionId);
+        var parameters = new DialogParameters<ProduceMessageDialog>
+        {
+            { x => x.ConnectionId, connection.Id },
+            { x => x.ConnectionName, connection.Name },
+            { x => x.TopicName, topicName },
+        };
+        await DialogService.ShowAsync<ProduceMessageDialog>("Send message", parameters);
+    }
+
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Kafka.Tests`
Expected: PASS (every test in the project).

Run: `dotnet build SbConsole.slnx -warnaserror`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SbConsole.Plugins.Kafka tests/SbConsole.Plugins.Kafka.Tests
git commit -m "$(cat <<'EOF'
feat(kafka): add message peek/send handlers, Peek page, and Send dialog

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Final integration — regression guards, docs, full gate

**Files:**
- Modify: `tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs`
- Modify: `docs/design.md`

**Interfaces:**
- Consumes: everything from Tasks 1–7.
- Produces: nothing further — this task only adds regression coverage and documentation for what already exists.

- [ ] **Step 1: Write the failing tests**

Add the Kafka route to the existing real-container route sweep, and add a Kafka counterpart to the per-assembly `[Authorize]` sweep:

```diff
--- a/tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
+++ b/tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
@@
-        foreach (var route in new[] { "/", "/audit", "/plugins", "/connections", "/settings", "/p/azure-servicebus/queues", "/p/azure-servicebus/topics", "/p/azure-servicebus/dead-letter" })
+        foreach (var route in new[] { "/", "/audit", "/plugins", "/connections", "/settings", "/p/azure-servicebus/queues", "/p/azure-servicebus/topics", "/p/azure-servicebus/dead-letter", "/p/kafka/topics" })
```

```diff
--- a/tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
+++ b/tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs
@@ (right after Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component)
+
+    /// <summary>
+    /// Same guard as Every_service_bus_plugin_page_declares_Authorize_directly_on_the_component,
+    /// for the Kafka plugin assembly added alongside it.
+    /// </summary>
+    [Fact]
+    public void Every_kafka_plugin_page_declares_Authorize_directly_on_the_component()
+    {
+        var routableTypes = typeof(SbConsole.Plugins.Kafka.Pages.Topics).Assembly.GetTypes()
+            .Where(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: false).Length > 0)
+            .ToList();
+
+        routableTypes.Should().NotBeEmpty("this assembly is expected to contain at least one @page component");
+        foreach (var type in routableTypes)
+        {
+            type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
+                .Should().NotBeEmpty($"{type.Name} must declare [Authorize] directly (or inherit it from " +
+                    "Pages/_Imports.razor), not merely rely on the app's fallback authorization policy to " +
+                    "redirect anonymous requests");
+        }
+    }
```

- [ ] **Step 2: Run tests to verify the new assertions fail before this task's fixes are needed**

Run: `dotnet test tests/SbConsole.Web.Tests --filter ProgramDiRegistrationTests`
Expected: since `KafkaPlugin` and its pages already exist and are correctly `[Authorize]`-gated from Tasks 5–7, both new/changed assertions should actually PASS immediately — this step is a genuine regression *guard*, not new functionality. Confirm they pass; if either fails, it means an earlier task's `[Authorize]` wiring or route is broken and must be fixed before continuing.

- [ ] **Step 3: Update `docs/design.md`**

Add a new top-level section documenting the Kafka plugin, matching §6's introductory style, and note the testing/registration changes in the sections that already describe them:

```diff
--- a/docs/design.md
+++ b/docs/design.md
@@ (end of §6.3, before "## 7. Error handling")
+
+## 6.5 Kafka plugin (`SbConsole.Plugins.Kafka`) — Topics (2026-09-17)
+
+SbConsole's second plugin, built the same way Service Bus's Queues plan proved the
+architecture out for the first: `KafkaPlugin : IPlugin`, `Id`/`ConnectionKind` = `"kafka"`,
+`DisplayName`/`ConnectionKindDisplayName` = `"Apache Kafka"`. Registered via
+`AddSbConsolePlugin<KafkaPlugin>()` in `Program.cs`, right after Service Bus — no host routing
+change was needed (§5's `AdditionalAssemblies` wiring already scans every registered plugin's
+assembly generically).
+
+Connection-string auth model differs from Service Bus's single Azure connection string: since
+every plugin gets exactly one opaque secret string end-to-end (§3), the Kafka connection secret
+is a librdkafka config string (`key=value` pairs separated by `;`), parsed straight into
+`Confluent.Kafka`'s `ClientConfig`-derived types — covers plaintext, SASL/PLAIN, SASL/SCRAM, and
+mTLS without SbConsole inventing its own schema. Full design:
+`docs/superpowers/specs/2026-09-16-kafka-topics-plugin-design.md`.
+
+**Topics (shipped):** list (with per-partition-summed approximate message count — see the design
+spec §4 for why this means "currently retained," not "unprocessed backlog," unlike Service Bus's
+ActiveMessageCount), create, delete (`Destructive`); peek (non-destructive, partition-scoped, not
+merged across partitions — Kafka only orders within a partition); send.
+
+**A pre-existing bug this second plugin exposed and fixed:** `AddSbConsolePlugin<TPlugin>()`
+registered `IPluginStore` as a single unkeyed scoped service (a "single-plugin simplification" its
+own comment flagged). Registering Kafka alongside Service Bus would have made every unkeyed
+`IPluginStore` resolution in the app resolve to whichever plugin registered last — silently
+redirecting Service Bus's `Queues.razor` metric-history sparkline storage into Kafka's namespace.
+Fixed by making the registration keyed by plugin `Id` (`AddKeyedScoped`), with `Queues.razor`'s
+injection moved from `@inject IPluginStore Store` to a keyed `[Inject, FromKeyedServices(...)]`
+property — the one place in the codebase that used the unkeyed convenience registration.
+
+Deferred past this plan, each its own future plan: consumer group management (list, lag,
+offset reset); dead-letter handling via the DLQ-topic convention (Kafka has no native DLQ);
+Schema Registry integration; topic configuration beyond partition count/replication factor;
+integration tests against a real/emulated broker.
```

```diff
--- a/docs/design.md
+++ b/docs/design.md
@@ ## 8. Testing
- - Integration: Testcontainers running the official Azure Service Bus emulator
-   — real peek/send/DLQ flows over AMQP. Deferred past all three (§6); picked
-   up once the plugin's shape has proven out across queues, topics, and the
-   cross-connection overview alike.
+ - Integration: Testcontainers running the official Azure Service Bus emulator
+   — real peek/send/DLQ flows over AMQP. Deferred past all three (§6); picked
+   up once the plugin's shape has proven out across queues, topics, and the
+   cross-connection overview alike.
+ - Kafka plugin (Topics, §6.5): same deferral, same reasoning — unit tests only against a
+   substitute of `IKafkaOperations`, no Testcontainers, no real broker traffic.
```

*(Renumber `## 7. Error handling` onward if inserting `## 6.5` as a numbered `## 7`/shifting sections reads awkwardly in context — read the surrounding headers in the live file first and match whatever numbering scheme keeps the document internally consistent, rather than mechanically forcing `6.5`.)*

- [ ] **Step 4: Full solution gate**

```bash
dotnet build SbConsole.slnx -warnaserror
dotnet test
```

Expected: PASS across every project — `SbConsole.Sdk`, `SbConsole.Core` (+ `.Tests`), `SbConsole.Web` (+ `.Tests`), `SbConsole.Plugins.ServiceBus` (+ `.Tests`), `SbConsole.Plugins.Kafka` (+ `.Tests`).

- [ ] **Step 5: Commit**

```bash
git add tests/SbConsole.Web.Tests/ProgramDiRegistrationTests.cs docs/design.md
git commit -m "$(cat <<'EOF'
test(kafka): add regression guards for routing/auth; docs: describe the Kafka plugin

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## After this plan

Full Kafka plugin scope, per the design spec §1, continues as two further plan/spec cycles:

1. Consumer group management (list groups, per-topic/partition lag, offset reset) — the Kafka analogue of Service Bus's Topics & Subscriptions.
2. Dead-letter handling via the common DLQ-topic convention — the Kafka analogue of Service Bus's Dead-letter overview.

Each gets its own brainstorm → spec → plan cycle once this one has shipped and been used for a while, exactly as Service Bus's did.
