# Redis plugin (scaffold, connection, Overview) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Stand up the new `SbConsole.Plugins.Redis` plugin — project scaffold, connection secret handling (Standalone + Sentinel), a custom connection form, and a working read-only Overview page (memory, hit ratio, evictions, memory-by-prefix, slow log) — as the first working, testable slice of the plugin described in `docs/superpowers/specs/2026-09-28-redis-plugin-design.md`.

**Architecture:** New project `src/Plugins/SbConsole.Plugins.Redis/` (Microsoft.NET.Sdk.Razor, references only `SbConsole.Sdk`, same shape as the other four plugins) and its test project `tests/SbConsole.Plugins.Redis.Tests/`. `RedisOperations` wraps `StackExchange.Redis`; all raw-reply parsing lives in pure static classes under `Client/` so it's unit-testable without a live server. Keys/Streams/Pub-Sub — and their nav items, mutating actions, and `IRedisOperations` methods — are out of scope here and ship as their own follow-on plans once this slice lands, per the design spec's phasing (§1) and `superpowers:writing-plans`' guidance to keep each plan independently shippable.

**Tech Stack:** .NET 10 / C# latest / nullable enabled / warnings as errors. `StackExchange.Redis` 3.3.1. MudBlazor 9.9.0 (matching the other plugins). xUnit + FluentAssertions 7.2.0 + NSubstitute 6.2.0 + bUnit 2.10.3.

## Global Constraints

- `TreatWarningsAsErrors=true` — every project must build clean.
- The plugin project references **only** `SbConsole.Sdk` (never `SbConsole.Core` or another plugin).
- Every `IRedisOperations` method takes the raw connection secret as its first parameter — one shared singleton `RedisOperations` instance serves every connection (`docs/design.md` §6.8).
- Plugins never touch `DbContext`; persistence only via `IPluginStore`, injected keyed on the plugin id (`[Inject(Key = "redis")]`) — never the unkeyed form (`docs/design.md` §3/§6.5).
- Raw client-library exceptions never reach a snackbar or the DB — route through `FriendlyRedisError` (`SbConsole.Sdk.FriendlyError` underneath).
- Query handlers never write; there are no command handlers in this plan (Overview is read-only), so `IAuditScope`/`IConfirmationService` are not wired up yet — they arrive with the Keys plan's first mutating action.
- Every interactive/data element in a `.razor` file gets a stable `Class="..."` so bUnit tests can select it (RabbitMQ's convention — `docs/design.md` §6.8).
- Commit messages end with the attribution footer configured for this repo/session (see the conversation's own instructions — do not invent a different one).

---

### Task 1: Project scaffold, `RedisTopology`, `RedisConnectionSettings`

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/SbConsole.Plugins.Redis.csproj`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisTopology.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisConnectionSettings.cs`
- Create: `tests/SbConsole.Plugins.Redis.Tests/SbConsole.Plugins.Redis.Tests.csproj`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Client/RedisConnectionSettingsTests.cs`
- Modify: `SbConsole.slnx` (add both new projects)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `RedisTopology` enum, `RedisConnectionSettings` record with `Parse(string)` / `ToSecretString()` / `ToConfigurationOptions()` — every later task that needs a `ConnectionMultiplexer` goes through `ToConfigurationOptions()`.

- [ ] **Step 1: Create the plugin project file**

```xml
<!-- src/Plugins/SbConsole.Plugins.Redis/SbConsole.Plugins.Redis.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <ItemGroup>
    <ProjectReference Include="..\..\SbConsole.Sdk\SbConsole.Sdk.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Internal seams (connection factory, parsers) are exercised directly by unit tests,
         since there is no live Redis server in the unit-test run. -->
    <InternalsVisibleTo Include="SbConsole.Plugins.Redis.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MudBlazor" Version="9.9.0" />
    <PackageReference Include="StackExchange.Redis" Version="3.3.1" />
  </ItemGroup>

  <ItemGroup>
    <!-- Pages/*.razor need Razor component + [Authorize] support, which live in the ASP.NET Core
         shared framework, not a NuGet package. Same pattern the other plugins use. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Create the test project file**

```xml
<!-- tests/SbConsole.Plugins.Redis.Tests/SbConsole.Plugins.Redis.Tests.csproj -->
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
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="NSubstitute" Version="6.2.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Plugins\SbConsole.Plugins.Redis\SbConsole.Plugins.Redis.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add both projects to the solution**

Run:
```bash
dotnet sln SbConsole.slnx add src/Plugins/SbConsole.Plugins.Redis/SbConsole.Plugins.Redis.csproj --solution-folder src/Plugins
dotnet sln SbConsole.slnx add tests/SbConsole.Plugins.Redis.Tests/SbConsole.Plugins.Redis.Tests.csproj --solution-folder tests
```
If `--solution-folder` isn't accepted by the installed SDK's `dotnet sln` for `.slnx`, add the two `<Project Path="..." />` lines directly into `SbConsole.slnx` under the existing `<Folder Name="/src/">` and `<Folder Name="/tests/">` elements instead, matching the existing entries' style exactly.

- [ ] **Step 4: Write the failing test for `RedisTopology`/`RedisConnectionSettings`**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Client/RedisConnectionSettingsTests.cs
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisConnectionSettingsTests
{
    [Fact]
    public void Round_trips_a_standalone_connection_through_the_secret_string()
    {
        var original = new RedisConnectionSettings(
            Topology: RedisTopology.Standalone,
            EndpointHostPorts: ["redis-01.uk.internal:6380"],
            User: "sbconsole",
            Password: "hunter2",
            Ssl: true,
            ServiceName: null,
            PreferReplicaForReads: true,
            DefaultDatabase: 0);

        var roundTripped = RedisConnectionSettings.Parse(original.ToSecretString());

        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Round_trips_a_sentinel_connection_with_multiple_endpoints_and_service_name()
    {
        var original = new RedisConnectionSettings(
            Topology: RedisTopology.Sentinel,
            EndpointHostPorts: ["sentinel-1:26379", "sentinel-2:26379", "sentinel-3:26379"],
            User: null,
            Password: "hunter2",
            Ssl: false,
            ServiceName: "uk-prod",
            PreferReplicaForReads: false,
            DefaultDatabase: 0);

        var roundTripped = RedisConnectionSettings.Parse(original.ToSecretString());

        roundTripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Password_containing_a_comma_survives_the_round_trip()
    {
        var original = new RedisConnectionSettings(
            RedisTopology.Standalone, ["localhost:6379"], null, "p@ss,word=weird", false, null, false, 0);

        var roundTripped = RedisConnectionSettings.Parse(original.ToSecretString());

        roundTripped.Password.Should().Be("p@ss,word=weird");
    }

    [Fact]
    public void Parse_reports_the_host_and_port_it_was_given_verbatim()
    {
        var settings = RedisConnectionSettings.Parse(
            new RedisConnectionSettings(RedisTopology.Standalone, ["redis-01:6380"], "u", "p", true, null, false, 3)
                .ToSecretString());

        settings.EndpointHostPorts.Should().Equal("redis-01:6380");
        settings.DefaultDatabase.Should().Be(3);
    }

    [Fact]
    public void The_configuration_options_portion_is_a_genuine_ConfigurationOptions_string()
    {
        // Proves RedisConnectionSettings reuses StackExchange.Redis's own parser/serializer
        // rather than a hand-rolled one: everything after the second '|' must be independently
        // parseable by ConfigurationOptions.Parse and carry the same endpoint/Ssl/ServiceName —
        // fields a hand-rolled parser could drop or corrupt without any of the other tests here
        // noticing (they only ever round-trip through this class's own Parse/ToSecretString).
        var secret = new RedisConnectionSettings(
            RedisTopology.Sentinel, ["sentinel-1:26379", "sentinel-2:26379"], "sbconsole", "hunter2",
            true, "uk-prod", false, 0).ToSecretString();

        var configPortion = secret.Split('|', 3)[2];
        var options = StackExchange.Redis.ConfigurationOptions.Parse(configPortion);

        options.EndPoints.Should().HaveCount(2);
        options.Ssl.Should().BeTrue();
        options.ServiceName.Should().Be("uk-prod");
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisConnectionSettingsTests"`
Expected: build failure — `RedisTopology`/`RedisConnectionSettings` don't exist yet.

- [ ] **Step 6: Write `RedisTopology`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisTopology.cs
namespace SbConsole.Plugins.Redis.Client;

public enum RedisTopology { Standalone, Sentinel, Cluster }
```

- [ ] **Step 7: Write `RedisConnectionSettings`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisConnectionSettings.cs
using System.Net;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// The plugin's connection secret is "{topology}|{preferReplica}|{configurationOptionsString}" —
/// StackExchange.Redis's own ConfigurationOptions format already handles multi-endpoint lists,
/// TLS, ACL user/password and comma/quote escaping in passwords, so it's reused verbatim rather
/// than inventing a parser (docs/superpowers/specs/2026-09-28-redis-plugin-design.md §2). The
/// topology/preferReplica prefix carries the two things ConfigurationOptions can't unambiguously
/// express on its own — see the design spec for why.
/// </summary>
public sealed record RedisConnectionSettings(
    RedisTopology Topology,
    IReadOnlyList<string> EndpointHostPorts,
    string? User,
    string? Password,
    bool Ssl,
    string? ServiceName,
    bool PreferReplicaForReads,
    int DefaultDatabase)
{
    public static RedisConnectionSettings Parse(string secret)
    {
        var parts = secret.Split('|', 3);
        if (parts.Length != 3)
        {
            throw new FormatException(
                "Redis connection secret must be '{topology}|{preferReplica}|{configurationOptions}'.");
        }

        var topology = Enum.Parse<RedisTopology>(parts[0], ignoreCase: true);
        var preferReplica = parts[1] == "1";
        // Genuinely reuse StackExchange.Redis's own parser for everything (endpoints, TLS,
        // ServiceName, DefaultDatabase) — the ONLY hand-written layer is decoding User/Password,
        // because ConfigurationOptions has no escape syntax for a comma inside a value (comma is
        // its field separator; see the design spec §2 for the upstream issue links). Every other
        // field round-trips through the library exactly as it would for any other caller.
        var options = ConfigurationOptions.Parse(parts[2]);

        return new RedisConnectionSettings(
            topology,
            options.EndPoints.Select(EndpointToHostPort).ToList(),
            options.User is null ? null : Uri.UnescapeDataString(options.User),
            options.Password is null ? null : Uri.UnescapeDataString(options.Password),
            options.Ssl,
            options.ServiceName,
            preferReplica,
            options.DefaultDatabase ?? 0);
    }

    public string ToSecretString()
    {
        var options = ToConfigurationOptionsCore(encodeCredentials: true);
        var topologyToken = Topology.ToString().ToLowerInvariant();
        var preferReplicaToken = PreferReplicaForReads ? "1" : "0";
        return $"{topologyToken}|{preferReplicaToken}|{options}";
    }

    /// <summary>The real, connectable options — raw (unescaped) User/Password.</summary>
    public ConfigurationOptions ToConfigurationOptions() => ToConfigurationOptionsCore(encodeCredentials: false);

    private ConfigurationOptions ToConfigurationOptionsCore(bool encodeCredentials)
    {
        var options = new ConfigurationOptions
        {
            User = EncodeIfNeeded(User, encodeCredentials),
            Password = EncodeIfNeeded(Password, encodeCredentials),
            Ssl = Ssl,
            ServiceName = ServiceName,
            AllowAdmin = true, // SbConsole is an operator tool and needs MEMORY/SLOWLOG/CLIENT — the
                                // Redis ACL, not this client-side flag, is the real gate.
            DefaultDatabase = Topology == RedisTopology.Cluster ? 0 : DefaultDatabase,
        };

        foreach (var hostPort in EndpointHostPorts)
        {
            options.EndPoints.Add(hostPort);
        }

        return options;
    }

    private static string? EncodeIfNeeded(string? value, bool encode) =>
        value is null || !encode ? value : Uri.EscapeDataString(value);

    /// <summary>
    /// EndPoint.ToString() renders "AddressFamily/host:port" (e.g. "Unspecified/redis-01:6380"),
    /// not the plain "host:port" this plugin's secret format and UI need — hence this helper
    /// instead of calling ToString() on the raw EndPoint.
    /// </summary>
    private static string EndpointToHostPort(EndPoint endpoint) => endpoint switch
    {
        DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
        IPEndPoint ip => $"{ip.Address}:{ip.Port}",
        _ => endpoint.ToString()!,
    };
}
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisConnectionSettingsTests"`
Expected: PASS (5 tests).

- [ ] **Step 9: Commit**

```bash
git add SbConsole.slnx src/Plugins/SbConsole.Plugins.Redis tests/SbConsole.Plugins.Redis.Tests
git commit -m "feat(redis): scaffold plugin project and connection secret parsing"
```

---

### Task 2: `FriendlyRedisError`

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/FriendlyRedisError.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Client/FriendlyRedisErrorTests.cs`

**Interfaces:**
- Consumes: `SbConsole.Sdk.FriendlyError` (existing SDK type — `FriendlyError.From(Exception)`).
- Produces: `FriendlyRedisError.From(Exception ex) : string`, used by `PluginResult.Fail(Exception)` (Task 6) and every later operations method's catch block.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Client/FriendlyRedisErrorTests.cs
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class FriendlyRedisErrorTests
{
    [Fact]
    public void Maps_connection_exception_to_a_short_reachability_message()
    {
        var ex = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "boom, lots of internal detail here");

        FriendlyRedisError.From(ex).Should().Be("Could not reach Redis: UnableToConnect");
    }

    [Fact]
    public void Maps_timeout_exception_to_a_fixed_message()
    {
        var ex = new RedisTimeoutException("timeout detail nobody needs to see", CommandStatus.Unknown);

        FriendlyRedisError.From(ex).Should().Be("Redis command timed out");
    }

    [Theory]
    [InlineData("NOAUTH Authentication required.", "Authentication required or failed (NOAUTH)")]
    [InlineData("WRONGTYPE Operation against a key holding the wrong kind of value", "Wrong type for this key — it may have changed since the page loaded")]
    [InlineData("CLUSTERDOWN Hash slot not served", "Cluster is down")]
    public void Maps_known_server_error_prefixes(string serverMessage, string friendlyMessage)
    {
        var ex = new RedisServerException(serverMessage);

        FriendlyRedisError.From(ex).Should().Be(friendlyMessage);
    }

    [Fact]
    public void Passes_a_NOPERM_message_through_verbatim_because_it_already_names_the_missing_permission()
    {
        var ex = new RedisServerException("NOPERM User sbconsole has no permissions to run the 'xclaim' command");

        FriendlyRedisError.From(ex).Should().Be("Command refused by ACL (NOPERM): User sbconsole has no permissions to run the 'xclaim' command");
    }

    [Fact]
    public void Falls_back_to_the_sdk_helper_for_anything_unmapped()
    {
        var ex = new InvalidOperationException("some other failure");

        FriendlyRedisError.From(ex).Should().Be(SbConsole.Sdk.FriendlyError.From(ex));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~FriendlyRedisErrorTests"`
Expected: build failure — `FriendlyRedisError` doesn't exist yet.

- [ ] **Step 3: Write `FriendlyRedisError`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/FriendlyRedisError.cs
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public static class FriendlyRedisError
{
    public static string From(Exception ex) => ex switch
    {
        RedisConnectionException conn => $"Could not reach Redis: {conn.FailureType}",
        RedisTimeoutException => "Redis command timed out",
        RedisServerException server when server.Message.StartsWith("NOAUTH", StringComparison.Ordinal) =>
            "Authentication required or failed (NOAUTH)",
        RedisServerException server when server.Message.StartsWith("NOPERM", StringComparison.Ordinal) =>
            $"Command refused by ACL (NOPERM): {server.Message["NOPERM".Length..].Trim()}",
        RedisServerException server when server.Message.StartsWith("WRONGTYPE", StringComparison.Ordinal) =>
            "Wrong type for this key — it may have changed since the page loaded",
        RedisServerException server when server.Message.StartsWith("CLUSTERDOWN", StringComparison.Ordinal) =>
            "Cluster is down",
        _ => SbConsole.Sdk.FriendlyError.From(ex),
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~FriendlyRedisErrorTests"`
Expected: PASS (6 tests). If `RedisServerException`'s constructor signature or `RedisTimeoutException`'s `CommandStatus` argument differs from the pinned 3.3.1 API, adjust the test construction to match — the mapping logic under test doesn't change.

- [ ] **Step 5: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis/Client/FriendlyRedisError.cs tests/SbConsole.Plugins.Redis.Tests/Client/FriendlyRedisErrorTests.cs
git commit -m "feat(redis): map StackExchange.Redis exceptions to friendly messages"
```

---

### Task 3: `IRedisOperations`, `RedisConnectionFactory`, `TestConnectionAsync`, `RedisPlugin`, host registration

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/IRedisOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisConnectionFactory.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisOperations.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/RedisPluginTests.cs`
- Modify: `src/SbConsole.Web/Program.cs` (register the plugin)

**Interfaces:**
- Consumes: `RedisConnectionSettings` (Task 1), `FriendlyRedisError` (Task 2), SDK's `IPlugin`/`PluginNavItem`/`PluginContribution`/`ConnectionTestResult`/`ConnectionCheck`/`ConnectionCheckStatus`.
- Produces: `IRedisOperations` interface (grows in later tasks of this plan), `RedisOperations` class, `RedisConnectionFactory.ConnectAsync(string secret, CancellationToken) : Task<IConnectionMultiplexer>` (used by every later operations method), `RedisPlugin` registered with the host.

- [ ] **Step 1: Write the failing plugin test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/RedisPluginTests.cs
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests;

public class RedisPluginTests
{
    [Fact]
    public void Id_and_connection_kind_are_redis()
    {
        var plugin = new RedisPlugin();

        plugin.Id.Should().Be("redis");
        plugin.ConnectionKind.Should().Be("redis");
    }

    [Fact]
    public void Nav_items_start_with_overview_only()
    {
        var plugin = new RedisPlugin();

        plugin.NavItems.Select(n => n.Href).Should().Equal("/p/redis/overview");
    }

    [Fact]
    public void Contribution_reports_no_actions_yet()
    {
        new RedisPlugin().Contribution.ActionCount.Should().Be(0);
    }

    [Fact]
    public void Connection_form_component_falls_back_to_the_host_default_for_now()
    {
        new RedisPlugin().ConnectionFormComponentType.Should().BeNull();
    }

    [Fact]
    public void Connection_summary_reports_the_first_endpoint_and_topology()
    {
        var plugin = new RedisPlugin();
        var secret = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", true, null, false, 0)
            .ToSecretString();

        var summary = plugin.GetConnectionSummary(secret);

        summary["Host"].Should().Be("redis-01:6380");
        summary["Topology"].Should().Be("Standalone");
    }

    [Fact]
    public void Connection_summary_is_empty_for_an_unparsable_secret_instead_of_throwing()
    {
        new RedisPlugin().GetConnectionSummary("not a valid secret").Should().BeEmpty();
    }

    [Fact]
    public void Connection_summary_is_empty_for_a_well_formed_but_invalid_topology_token()
    {
        // Three '|'-delimited parts (so it does NOT hit the FormatException branch), but
        // "bogus" isn't a RedisTopology member — Enum.Parse throws ArgumentException, which
        // GetConnectionSummary must also swallow, not just FormatException.
        new RedisPlugin().GetConnectionSummary("bogus|0|host:6379").Should().BeEmpty();
    }

    [Fact]
    public void Connection_summary_is_empty_for_a_configuration_options_keyword_it_rejects()
    {
        // ConfigurationOptions.Parse throws ArgumentException on an unrecognized keyword —
        // a different source of the same exception type as the case above, still must not throw.
        new RedisPlugin().GetConnectionSummary("standalone|0|host:6379,notarealkeyword=1").Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisPluginTests"`
Expected: build failure — `RedisPlugin` doesn't exist yet.

- [ ] **Step 3: Write `IRedisOperations` (first slice)**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/IRedisOperations.cs
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Client;

/// <summary>
/// Every method takes the raw connection secret as its first parameter — one shared instance
/// serves every connection (docs/design.md §6.8). Grows with each later task/plan; see
/// docs/superpowers/specs/2026-09-28-redis-plugin-design.md §4 for the full target shape.
/// </summary>
public interface IRedisOperations
{
    Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default);
}
```

- [ ] **Step 4: Write `RedisConnectionFactory`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisConnectionFactory.cs
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

internal static class RedisConnectionFactory
{
    public static async Task<IConnectionMultiplexer> ConnectAsync(string secret, CancellationToken ct)
    {
        var settings = RedisConnectionSettings.Parse(secret);
        if (settings.Topology == RedisTopology.Cluster)
        {
            throw new NotSupportedException("Cluster support ships in a later update.");
        }

        var options = settings.ToConfigurationOptions();
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 5_000;

        // Setting ServiceName puts StackExchange.Redis into Sentinel mode automatically: it
        // treats the configured endpoints as sentinels, discovers the current primary for that
        // service, and hands back a connection to the primary itself — no separate
        // SentinelConnectAsync/GetSentinelMasterConnectionAsync dance needed.
        return await ConnectionMultiplexer.ConnectAsync(options).WaitAsync(ct);
    }
}
```

- [ ] **Step 5: Write `RedisOperations` with `TestConnectionAsync`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisOperations.cs
using SbConsole.Sdk;
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public sealed class RedisOperations : IRedisOperations
{
    public async Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default)
    {
        try
        {
            await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
            var db = muxer.GetDatabase();
            var ping = await db.PingAsync();

            var server = muxer.GetServer(muxer.GetEndPoints()[0]);
            var sections = await GetInfoSectionsAsync(server, ct);
            var version = sections.TryGetValue("Server", out var serverSection)
                && serverSection.TryGetValue("redis_version", out var v) ? v : "unknown";

            return new ConnectionTestResult(
                Success: true,
                ErrorMessage: null,
                Identity: $"Redis {version}",
                Checks: [new ConnectionCheck("PING", ConnectionCheckStatus.Passed, $"{ping.TotalMilliseconds:N1} ms")]);
        }
        catch (Exception ex)
        {
            return new ConnectionTestResult(false, FriendlyRedisError.From(ex), null, null);
        }
    }

    internal static async Task<Dictionary<string, IReadOnlyDictionary<string, string>>> GetInfoSectionsAsync(
        IServer server, CancellationToken ct)
    {
        var groups = await server.InfoAsync().WaitAsync(ct);
        return groups.ToDictionary(
            g => g.Key,
            g => (IReadOnlyDictionary<string, string>)g.ToDictionary(
                kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 6: Write `RedisPlugin`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis;

public sealed class RedisPlugin : IPlugin
{
    public string Id => "redis";
    public string DisplayName => "Redis";
    public string Version => "0.1.0";
    public string ConnectionKind => "redis";
    public string ConnectionKindDisplayName => "Redis";

    public IReadOnlyList<PluginNavItem> NavItems { get; } =
    [
        new PluginNavItem("Overview", "/p/redis/overview", "speedometer"),
    ];

    // A custom connection form (mockup 1f) ships later in this same plan; until then the host
    // falls back to its default flat-textbox secret editor (SDK default behaviour).
    public Type? ConnectionFormComponentType => null;

    public IReadOnlyDictionary<string, string> GetConnectionSummary(string secret)
    {
        try
        {
            var settings = RedisConnectionSettings.Parse(secret);
            return new Dictionary<string, string>
            {
                ["Host"] = settings.EndpointHostPorts.FirstOrDefault() ?? "?",
                ["Topology"] = settings.Topology.ToString(),
            };
        }
        catch (Exception)
        {
            // RedisConnectionSettings.Parse can throw FormatException (wrong '|' count) or
            // ArgumentException (bad topology token, or a ConfigurationOptions keyword it
            // rejects) — this method's contract is "never throw", so both are swallowed the
            // same way: an unparsable secret just gets no summary chips.
            return new Dictionary<string, string>();
        }
    }

    // Pages so far: Overview. No command handlers yet — this is a read-only slice; Keys/Streams/
    // Pub-Sub and their mutating actions ship in later plans (docs/superpowers/specs/
    // 2026-09-28-redis-plugin-design.md §1).
    public PluginContribution Contribution => new(PageCount: 1, ActionCount: 0);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IRedisOperations, RedisOperations>();
        // A new handler must be added here or its page fails to render at runtime.
    }

    public Task<ConnectionTestResult> TestConnectionAsync(string secret, CancellationToken ct = default) =>
        new RedisOperations().TestConnectionAsync(secret, ct);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisPluginTests"`
Expected: PASS (8 tests).

- [ ] **Step 8: Register the plugin with the host**

In `src/SbConsole.Web/Program.cs`, after the existing line
`builder.Services.AddSbConsolePlugin<SbConsole.Plugins.RabbitMq.RabbitMqPlugin>();`, add:

```csharp
builder.Services.AddSbConsolePlugin<SbConsole.Plugins.Redis.RedisPlugin>();
```

- [ ] **Step 9: Build the whole solution to catch any wiring mistakes**

Run: `dotnet build -warnaserror`
Expected: builds clean, including `SbConsole.Web` picking up the new plugin project reference. If `SbConsole.Web.csproj` doesn't automatically pick up the new plugin (it references plugin projects explicitly — see the other four `<ProjectReference>` lines), add:

```xml
<ProjectReference Include="..\Plugins\SbConsole.Plugins.Redis\SbConsole.Plugins.Redis.csproj" />
```

to `src/SbConsole.Web/SbConsole.Web.csproj`'s existing plugin `ItemGroup`, alongside the RabbitMq line.

- [ ] **Step 10: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis src/SbConsole.Web/Program.cs src/SbConsole.Web/SbConsole.Web.csproj tests/SbConsole.Plugins.Redis.Tests
git commit -m "feat(redis): add IRedisOperations, RedisPlugin, and register it with the host"
```

---

### Task 4: `RedisConnectionFields.razor` (connection form)

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisConnectionFields.razor`
- Modify: `src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs` (`ConnectionFormComponentType`)
- Test: `tests/SbConsole.Plugins.Redis.Tests/Client/RedisConnectionFieldsTests.cs`

**Interfaces:**
- Consumes: `RedisConnectionSettings` (Task 1). The three SDK-mandated parameters: `InitialSecret` (string?), `SecretChanged` (EventCallback\<string\>), `IsProd` (bool).
- Produces: nothing later tasks depend on — this is a leaf UI component.

- [ ] **Step 1: Write the failing bUnit test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Client/RedisConnectionFieldsTests.cs
using Bunit;
using MudBlazor.Services;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisConnectionFieldsTests : TestContext
{
    public RedisConnectionFieldsTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Parses_the_initial_secret_into_the_host_field()
    {
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", true, null, false, 0)
            .ToSecretString();

        var cut = RenderComponent<RedisConnectionFields>(p => p.Add(x => x.InitialSecret, initial));

        cut.Find(".redis-conn-host").GetAttribute("value").Should().Be("redis-01:6380");
    }

    [Fact]
    public async Task Editing_the_host_raises_secret_changed_with_the_updated_secret()
    {
        string? raised = null;
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", true, null, false, 0)
            .ToSecretString();

        var cut = RenderComponent<RedisConnectionFields>(p => p
            .Add(x => x.InitialSecret, initial)
            .Add(x => x.SecretChanged, EventCallback.Factory.Create<string>(this, s => raised = s)));

        await cut.Find(".redis-conn-host").ChangeAsync(new ChangeEventArgs { Value = "redis-02:6380" });

        raised.Should().NotBeNull();
        RedisConnectionSettings.Parse(raised!).EndpointHostPorts.Should().Equal("redis-02:6380");
    }

    [Fact]
    public void Warns_when_prod_tagged_without_tls()
    {
        var initial = new RedisConnectionSettings(
            RedisTopology.Standalone, ["redis-01:6380"], "sbconsole", "hunter2", Ssl: false, null, false, 0)
            .ToSecretString();

        var cut = RenderComponent<RedisConnectionFields>(p => p
            .Add(x => x.InitialSecret, initial)
            .Add(x => x.IsProd, true));

        cut.Find(".redis-conn-notls-warning").TextContent.Should().Contain("TLS");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisConnectionFieldsTests"`
Expected: build failure — `RedisConnectionFields` doesn't exist yet.

- [ ] **Step 3: Write `RedisConnectionFields.razor`**

Before writing, copy the top-of-file `@using` directives from `src/Plugins/SbConsole.Plugins.RabbitMq/Client/RabbitConnectionFields.razor` verbatim (MudBlazor namespaces etc.), then build the body below:

```razor
@* src/Plugins/SbConsole.Plugins.Redis/Client/RedisConnectionFields.razor *@
@using MudBlazor

<MudTextField T="string" Label="Host:port" Value="@_host" ValueChanged="OnHostChanged"
              Class="redis-conn-host" Immediate="true" />

<MudSelect T="RedisTopology" Label="Topology" Value="_topology" ValueChanged="OnTopologyChanged" Class="redis-conn-topology">
    <MudSelectItem Value="RedisTopology.Standalone">Standalone</MudSelectItem>
    <MudSelectItem Value="RedisTopology.Sentinel">Sentinel</MudSelectItem>
    <MudSelectItem Value="RedisTopology.Cluster">Cluster</MudSelectItem>
</MudSelect>

@if (_topology == RedisTopology.Sentinel)
{
    <MudTextField T="string" Label="Service name" Value="@_serviceName" ValueChanged="OnServiceNameChanged"
                  Class="redis-conn-servicename" Immediate="true" />
}

<MudTextField T="string" Label="ACL user" Value="@_user" ValueChanged="OnUserChanged"
              Class="redis-conn-user" Immediate="true" />
<MudTextField T="string" Label="Password" Value="@_password" ValueChanged="OnPasswordChanged"
              InputType="InputType.Password" Class="redis-conn-password" Immediate="true" />

<MudCheckBox T="bool" Value="_ssl" ValueChanged="OnSslChanged" Class="redis-conn-tls" Label="TLS · verify certificate" />
<MudCheckBox T="bool" Value="_preferReplica" ValueChanged="OnPreferReplicaChanged" Class="redis-conn-preferreplica"
             Label="Read from replica when possible" />

@if (IsProd && !_ssl)
{
    <MudAlert Severity="Severity.Warning" Class="redis-conn-notls-warning">
        This is a prod-tagged connection without TLS — credentials travel in plaintext.
    </MudAlert>
}

@code {
    [Parameter] public string? InitialSecret { get; set; }
    [Parameter] public EventCallback<string> SecretChanged { get; set; }
    [Parameter] public bool IsProd { get; set; }

    private RedisTopology _topology = RedisTopology.Standalone;
    private string _host = "";
    private string? _serviceName;
    private string? _user;
    private string? _password;
    private bool _ssl;
    private bool _preferReplica;

    protected override void OnInitialized()
    {
        if (string.IsNullOrEmpty(InitialSecret))
        {
            return;
        }

        try
        {
            var settings = RedisConnectionSettings.Parse(InitialSecret);
            _topology = settings.Topology;
            _host = settings.EndpointHostPorts.FirstOrDefault() ?? "";
            _serviceName = settings.ServiceName;
            _user = settings.User;
            _password = settings.Password;
            _ssl = settings.Ssl;
            _preferReplica = settings.PreferReplicaForReads;
        }
        catch (FormatException)
        {
            // Unparsable/empty initial secret — leave the defaults above so the form still renders.
        }
    }

    private Task OnHostChanged(string value) { _host = value; return RaiseChangedAsync(); }
    private Task OnTopologyChanged(RedisTopology value) { _topology = value; return RaiseChangedAsync(); }
    private Task OnServiceNameChanged(string value) { _serviceName = value; return RaiseChangedAsync(); }
    private Task OnUserChanged(string value) { _user = value; return RaiseChangedAsync(); }
    private Task OnPasswordChanged(string value) { _password = value; return RaiseChangedAsync(); }
    private Task OnSslChanged(bool value) { _ssl = value; return RaiseChangedAsync(); }
    private Task OnPreferReplicaChanged(bool value) { _preferReplica = value; return RaiseChangedAsync(); }

    private Task RaiseChangedAsync()
    {
        var settings = new RedisConnectionSettings(
            _topology,
            string.IsNullOrWhiteSpace(_host) ? [] : [_host],
            _user, _password, _ssl, _serviceName, _preferReplica, DefaultDatabase: 0);
        return SecretChanged.InvokeAsync(settings.ToSecretString());
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisConnectionFieldsTests"`
Expected: PASS (3 tests). If the rendered `<input>`'s `value` attribute isn't queryable the way `RedisConnectionFieldsTests` expects (MudBlazor sometimes renders text fields with the value on a nested element), adjust the CSS selector in the test to match what `cut.Markup` actually shows — check `src/Plugins/SbConsole.Plugins.RabbitMq/Tests` equivalents for the exact selector idiom this MudBlazor version needs, since `RabbitConnectionFieldsTests` already solved this.

- [ ] **Step 5: Wire it into `RedisPlugin`**

In `src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs`, change:
```csharp
public Type? ConnectionFormComponentType => null;
```
to:
```csharp
public Type? ConnectionFormComponentType => typeof(RedisConnectionFields);
```
(resolves via the `using SbConsole.Plugins.Redis.Client;` already at the top of `RedisPlugin.cs`). Also update `RedisPluginTests.Connection_form_component_falls_back_to_the_host_default_for_now` (Task 3) to instead assert:
```csharp
[Fact]
public void Connection_form_component_is_the_redis_connection_fields_component()
{
    new RedisPlugin().ConnectionFormComponentType.Should().Be(typeof(RedisConnectionFields));
}
```
(rename the test method accordingly).

- [ ] **Step 6: Run the full Redis test project to confirm nothing regressed**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests`
Expected: PASS, all tests so far green.

- [ ] **Step 7: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis tests/SbConsole.Plugins.Redis.Tests
git commit -m "feat(redis): add the custom connection form component"
```

---

### Task 5: `RedisInfoParser`

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/ServerOverview.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisInfoParser.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Client/RedisInfoParserTests.cs`

**Interfaces:**
- Consumes: nothing new (pure function over plain dictionaries — no StackExchange.Redis types).
- Produces: `ServerOverview` record, `RedisInfoParser.Parse(IReadOnlyDictionary<string, IReadOnlyDictionary<string,string>> sections) : ServerOverview`, consumed by `RedisOperations.GetOverviewAsync` (Task 6).

**Deviation from the design spec (§3):** `ServerOverview` drops `EvictedPerMinute` — Redis's `INFO` only exposes a cumulative `evicted_keys` counter, not a rate; a proper per-minute rate needs the SDK's `MetricHistoryStore` sampling two points over time, which is a bigger, separable piece of work than this task and isn't needed for the Overview page to be useful. `EvictedKeysTotal` (cumulative) ships now; a rate can be layered on later without changing this shape.

- [ ] **Step 1: Write `ServerOverview`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/ServerOverview.cs
namespace SbConsole.Plugins.Redis.Client;

public sealed record ServerOverview(
    string RedisVersion,
    string Mode,
    int ConnectedReplicas,
    long UsedMemoryBytes,
    long? MaxMemoryBytes,
    string MaxMemoryPolicy,
    double MemoryFragmentationRatio,
    double HitRatioPercent,
    long OpsPerSecond,
    long EvictedKeysTotal,
    long ExpiredKeysTotal,
    long ConnectedClients,
    long BlockedClients,
    IReadOnlyList<KeyspaceDatabaseInfo> Databases);

public sealed record KeyspaceDatabaseInfo(int Index, long KeyCount, long KeysWithExpiry);
```

- [ ] **Step 2: Write the failing test with a realistic `INFO` fixture**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Client/RedisInfoParserTests.cs
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisInfoParserTests
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SampleSections() =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["Server"] = new Dictionary<string, string> { ["redis_version"] = "7.2.4", ["redis_mode"] = "standalone" },
            ["Clients"] = new Dictionary<string, string> { ["connected_clients"] = "412", ["blocked_clients"] = "3" },
            ["Memory"] = new Dictionary<string, string>
            {
                ["used_memory"] = "7405032140",
                ["maxmemory"] = "8589934592",
                ["maxmemory_policy"] = "allkeys-lru",
                ["mem_fragmentation_ratio"] = "1.12",
            },
            ["Stats"] = new Dictionary<string, string>
            {
                ["instantaneous_ops_per_sec"] = "41800",
                ["evicted_keys"] = "1240000",
                ["expired_keys"] = "98213",
                ["keyspace_hits"] = "9482013",
                ["keyspace_misses"] = "583021",
            },
            ["Replication"] = new Dictionary<string, string> { ["role"] = "master", ["connected_slaves"] = "2" },
            ["Keyspace"] = new Dictionary<string, string>
            {
                ["db0"] = "keys=2410000,expires=1820000,avg_ttl=0",
                ["db1"] = "keys=18200,expires=0,avg_ttl=0",
            },
        };

    [Fact]
    public void Parses_server_identity_and_replica_count()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.RedisVersion.Should().Be("7.2.4");
        overview.Mode.Should().Be("standalone");
        overview.ConnectedReplicas.Should().Be(2);
    }

    [Fact]
    public void Parses_memory_fields()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.UsedMemoryBytes.Should().Be(7405032140);
        overview.MaxMemoryBytes.Should().Be(8589934592);
        overview.MaxMemoryPolicy.Should().Be("allkeys-lru");
        overview.MemoryFragmentationRatio.Should().Be(1.12);
    }

    [Fact]
    public void Treats_maxmemory_zero_as_unbounded()
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(SampleSections())
        {
            ["Memory"] = new Dictionary<string, string>
            {
                ["used_memory"] = "100", ["maxmemory"] = "0", ["maxmemory_policy"] = "noeviction", ["mem_fragmentation_ratio"] = "1.0",
            },
        };

        RedisInfoParser.Parse(sections).MaxMemoryBytes.Should().BeNull();
    }

    [Fact]
    public void Computes_hit_ratio_percent_from_hits_and_misses()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        // 9482013 / (9482013 + 583021) * 100
        overview.HitRatioPercent.Should().BeApproximately(94.2, 0.1);
    }

    [Fact]
    public void Parses_ops_evicted_expired_and_client_counts()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.OpsPerSecond.Should().Be(41800);
        overview.EvictedKeysTotal.Should().Be(1240000);
        overview.ExpiredKeysTotal.Should().Be(98213);
        overview.ConnectedClients.Should().Be(412);
        overview.BlockedClients.Should().Be(3);
    }

    [Fact]
    public void Parses_one_keyspace_entry_per_database_sorted_by_index()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.Databases.Should().Equal(
            new KeyspaceDatabaseInfo(0, 2410000, 1820000),
            new KeyspaceDatabaseInfo(1, 18200, 0));
    }

    [Fact]
    public void Missing_keyspace_section_yields_no_databases()
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(SampleSections());
        sections.Remove("Keyspace");

        RedisInfoParser.Parse(sections).Databases.Should().BeEmpty();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisInfoParserTests"`
Expected: build failure — `RedisInfoParser` doesn't exist yet.

- [ ] **Step 4: Write `RedisInfoParser`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisInfoParser.cs
using System.Globalization;

namespace SbConsole.Plugins.Redis.Client;

public static class RedisInfoParser
{
    public static ServerOverview Parse(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        var server = Section(sections, "Server");
        var clients = Section(sections, "Clients");
        var memory = Section(sections, "Memory");
        var stats = Section(sections, "Stats");
        var replication = Section(sections, "Replication");

        var maxMemory = GetLong(memory, "maxmemory");
        var hits = GetLong(stats, "keyspace_hits");
        var misses = GetLong(stats, "keyspace_misses");
        var totalLookups = hits + misses;

        return new ServerOverview(
            RedisVersion: GetString(server, "redis_version"),
            Mode: GetString(server, "redis_mode"),
            ConnectedReplicas: (int)GetLong(replication, "connected_slaves"),
            UsedMemoryBytes: GetLong(memory, "used_memory"),
            MaxMemoryBytes: maxMemory == 0 ? null : maxMemory,
            MaxMemoryPolicy: GetString(memory, "maxmemory_policy"),
            MemoryFragmentationRatio: GetDouble(memory, "mem_fragmentation_ratio"),
            HitRatioPercent: totalLookups == 0 ? 0 : 100.0 * hits / totalLookups,
            OpsPerSecond: GetLong(stats, "instantaneous_ops_per_sec"),
            EvictedKeysTotal: GetLong(stats, "evicted_keys"),
            ExpiredKeysTotal: GetLong(stats, "expired_keys"),
            ConnectedClients: GetLong(clients, "connected_clients"),
            BlockedClients: GetLong(clients, "blocked_clients"),
            Databases: ParseKeyspace(sections));
    }

    private static IReadOnlyList<KeyspaceDatabaseInfo> ParseKeyspace(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        if (!sections.TryGetValue("Keyspace", out var keyspace))
        {
            return [];
        }

        var result = new List<KeyspaceDatabaseInfo>();
        foreach (var (dbKey, value) in keyspace)
        {
            if (!dbKey.StartsWith("db", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(dbKey.AsSpan(2), out var index))
            {
                continue;
            }

            var fields = value.Split(',')
                .Select(f => f.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0], kv => kv[1]);

            var keys = fields.TryGetValue("keys", out var k) ? long.Parse(k, CultureInfo.InvariantCulture) : 0;
            var expires = fields.TryGetValue("expires", out var e) ? long.Parse(e, CultureInfo.InvariantCulture) : 0;
            result.Add(new KeyspaceDatabaseInfo(index, keys, expires));
        }

        return result.OrderBy(d => d.Index).ToList();
    }

    private static IReadOnlyDictionary<string, string> Section(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections, string name) =>
        sections.TryGetValue(name, out var section) ? section : new Dictionary<string, string>();

    private static string GetString(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) ? value : "";

    private static long GetLong(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) && long.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;

    private static double GetDouble(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) && double.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisInfoParserTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis/Client/ServerOverview.cs src/Plugins/SbConsole.Plugins.Redis/Client/RedisInfoParser.cs tests/SbConsole.Plugins.Redis.Tests/Client/RedisInfoParserTests.cs
git commit -m "feat(redis): parse INFO sections into ServerOverview"
```

---

### Task 6: `GetOverviewAsync`, `PluginResult`, `HandlerRunner`, `GetOverviewQueryHandler`

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/PluginResult.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/HandlerRunner.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Overview/GetOverviewQueryHandler.cs`
- Modify: `src/Plugins/SbConsole.Plugins.Redis/Client/IRedisOperations.cs` (add `GetOverviewAsync`)
- Modify: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisOperations.cs` (implement it)
- Modify: `src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs` (register the handler)
- Test: `tests/SbConsole.Plugins.Redis.Tests/Overview/GetOverviewQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `RedisInfoParser`/`ServerOverview` (Task 5), `FriendlyRedisError` (Task 2), SDK's `IConnectionProvider`.
- Produces: `PluginResult`/`PluginResult<T>` (used by every later handler), `HandlerRunner.QueryAsync<T>` (used by every later query handler), `GetOverviewQueryHandler.HandleAsync(Guid connectionId, CancellationToken) : Task<PluginResult<ServerOverview>>`.

- [ ] **Step 1: Write `PluginResult`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/PluginResult.cs
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis;

public sealed class PluginResult
{
    public bool IsSuccess { get; }
    public string? Error { get; }

    private PluginResult(bool isSuccess, string? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static PluginResult Ok() => new(true, null);
    public static PluginResult Fail(string error) => new(false, error);
    public static PluginResult Fail(Exception ex) => new(false, FriendlyRedisError.From(ex));
}

public sealed class PluginResult<T>
{
    public bool IsSuccess { get; }
    public string? Error { get; }
    public T? Value { get; }

    private PluginResult(bool isSuccess, string? error, T? value)
    {
        IsSuccess = isSuccess;
        Error = error;
        Value = value;
    }

    public static PluginResult<T> Ok(T value) => new(true, null, value);
    public static PluginResult<T> Fail(string error) => new(false, error, default);
    public static PluginResult<T> Fail(Exception ex) => new(false, FriendlyRedisError.From(ex), default);
}
```

- [ ] **Step 2: Write `HandlerRunner`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/HandlerRunner.cs
using Microsoft.Extensions.Logging;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis;

internal static class HandlerRunner
{
    public const string ConnectionNotFound = "Connection not found.";

    public static async Task<PluginResult<T>> QueryAsync<T>(
        IConnectionProvider connections,
        ILogger logger,
        Guid connectionId,
        string description,
        Func<string, Task<T>> run,
        CancellationToken ct)
    {
        var secret = await connections.GetSecretAsync(connectionId, ct);
        if (secret is null)
        {
            return PluginResult<T>.Fail(ConnectionNotFound);
        }

        try
        {
            var value = await run(secret);
            return PluginResult<T>.Ok(value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Redis query failed: {Description}", description);
            return PluginResult<T>.Fail(ex);
        }
    }
}
```

- [ ] **Step 3: Write the failing handler test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Overview/GetOverviewQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Overview;

public class GetOverviewQueryHandlerTests
{
    private const string Secret = "standalone|0|localhost:6379";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IRedisOperations _operations = Substitute.For<IRedisOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private GetOverviewQueryHandler CreateHandler() =>
        new(_operations, _connections, NullLogger<GetOverviewQueryHandler>.Instance);

    private static ServerOverview SampleOverview() => new(
        "7.2.4", "standalone", 2, 100, 200, "allkeys-lru", 1.1, 94.2, 41800, 1240000, 98213, 412, 3, []);

    [Fact]
    public async Task Returns_the_overview_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(SampleOverview());

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(SampleOverview());
    }

    [Fact]
    public async Task Fails_without_calling_the_broker_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
        await _operations.DidNotReceive().GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_a_friendly_error_when_the_broker_call_throws()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<ServerOverview>>(_ => throw new InvalidOperationException("raw broker noise"));

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~GetOverviewQueryHandlerTests"`
Expected: build failure — `GetOverviewQueryHandler` and `IRedisOperations.GetOverviewAsync` don't exist yet.

- [ ] **Step 5: Add `GetOverviewAsync` to the interface and implement it**

In `src/Plugins/SbConsole.Plugins.Redis/Client/IRedisOperations.cs`, add:
```csharp
Task<ServerOverview> GetOverviewAsync(string secret, CancellationToken ct = default);
```

In `src/Plugins/SbConsole.Plugins.Redis/Client/RedisOperations.cs`, add:
```csharp
public async Task<ServerOverview> GetOverviewAsync(string secret, CancellationToken ct = default)
{
    await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
    var server = muxer.GetServer(muxer.GetEndPoints()[0]);
    var sections = await GetInfoSectionsAsync(server, ct);
    return RedisInfoParser.Parse(sections);
}
```

- [ ] **Step 6: Write `GetOverviewQueryHandler`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Overview/GetOverviewQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Overview;

public sealed class GetOverviewQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<GetOverviewQueryHandler> logger)
{
    public Task<PluginResult<ServerOverview>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.overview.get",
            secret => operations.GetOverviewAsync(secret, ct), ct);
}
```

- [ ] **Step 7: Register the handler**

In `RedisPlugin.ConfigureServices`, add:
```csharp
services.AddScoped<Overview.GetOverviewQueryHandler>();
```

- [ ] **Step 8: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~GetOverviewQueryHandlerTests"`
Expected: PASS (3 tests).

- [ ] **Step 9: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis tests/SbConsole.Plugins.Redis.Tests/Overview
git commit -m "feat(redis): implement GetOverviewAsync and its query handler"
```

---

### Task 7: Memory by prefix

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/MemoryByPrefixEntry.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisMemoryByPrefixSampler.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Overview/GetMemoryByPrefixQueryHandler.cs`
- Modify: `IRedisOperations.cs`, `RedisOperations.cs`, `RedisPlugin.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Client/RedisMemoryByPrefixSamplerTests.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Overview/GetMemoryByPrefixQueryHandlerTests.cs`

**Interfaces:**
- Consumes: nothing new for the sampler (pure logic).
- Produces: `MemoryByPrefixEntry`, `RedisMemoryByPrefixSampler.Summarize(...)`, `IRedisOperations.GetMemoryByPrefixAsync`, `GetMemoryByPrefixQueryHandler`.

**Deviation from the design spec (§3):** dropped the `Kind`/`EstimatedBytes` naming — this reports the actual sampled bytes for whatever keys were sampled (matching the mockup's own "sampled · MEMORY USAGE" caption), not an extrapolated total, and doesn't fetch each key's type (an extra round trip with no functional use yet).

- [ ] **Step 1: Write `MemoryByPrefixEntry`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/MemoryByPrefixEntry.cs
namespace SbConsole.Plugins.Redis.Client;

public sealed record MemoryByPrefixEntry(string Prefix, long SampledBytes, int SampledKeyCount, int KeysWithoutTtlCount);

internal readonly record struct SampledKey(string Key, long Bytes, bool HasTtl);
```

- [ ] **Step 2: Write the failing sampler test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Client/RedisMemoryByPrefixSamplerTests.cs
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisMemoryByPrefixSamplerTests
{
    [Fact]
    public void Groups_by_prefix_up_to_the_delimiter_and_sums_bytes()
    {
        SampledKey[] samples =
        [
            new("session:u:1", 1000, true),
            new("session:u:2", 2000, false),
            new("cart:u:1", 500, true),
        ];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, delimiter: ':', topN: 4);

        result.Should().Contain(e => e.Prefix == "session:" && e.SampledBytes == 3000 && e.SampledKeyCount == 2 && e.KeysWithoutTtlCount == 1);
        result.Should().Contain(e => e.Prefix == "cart:" && e.SampledBytes == 500 && e.SampledKeyCount == 1 && e.KeysWithoutTtlCount == 0);
    }

    [Fact]
    public void Orders_prefixes_by_sampled_bytes_descending()
    {
        SampledKey[] samples =
        [
            new("small:1", 10, true),
            new("big:1", 900, true),
        ];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, ':', topN: 4);

        result.Select(e => e.Prefix).Should().Equal("big:", "small:");
    }

    [Fact]
    public void Folds_prefixes_beyond_topN_into_an_other_bucket()
    {
        SampledKey[] samples =
        [
            new("a:1", 400, true), new("b:1", 300, true), new("c:1", 200, true), new("d:1", 100, true),
        ];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, ':', topN: 2);

        result.Should().HaveCount(3);
        result.Should().Contain(e => e.Prefix == "a:" && e.SampledBytes == 400);
        result.Should().Contain(e => e.Prefix == "b:" && e.SampledBytes == 300);
        result.Should().Contain(e => e.Prefix == "other" && e.SampledBytes == 300 && e.SampledKeyCount == 2);
    }

    [Fact]
    public void A_key_with_no_delimiter_is_grouped_under_its_own_full_name()
    {
        SampledKey[] samples = [new("standalonekey", 42, true)];

        var result = RedisMemoryByPrefixSampler.Summarize(samples, ':', topN: 4);

        result.Should().ContainSingle().Which.Prefix.Should().Be("standalonekey");
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisMemoryByPrefixSamplerTests"`
Expected: build failure — `RedisMemoryByPrefixSampler` doesn't exist yet.

- [ ] **Step 4: Write `RedisMemoryByPrefixSampler`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisMemoryByPrefixSampler.cs
namespace SbConsole.Plugins.Redis.Client;

public static class RedisMemoryByPrefixSampler
{
    public static IReadOnlyList<MemoryByPrefixEntry> Summarize(
        IReadOnlyList<SampledKey> samples, char delimiter, int topN)
    {
        var groups = samples
            .GroupBy(s => PrefixOf(s.Key, delimiter))
            .Select(g => new MemoryByPrefixEntry(
                g.Key,
                g.Sum(s => s.Bytes),
                g.Count(),
                g.Count(s => !s.HasTtl)))
            .OrderByDescending(e => e.SampledBytes)
            .ToList();

        if (groups.Count <= topN)
        {
            return groups;
        }

        var top = groups.Take(topN).ToList();
        var rest = groups.Skip(topN).ToList();
        top.Add(new MemoryByPrefixEntry(
            "other",
            rest.Sum(e => e.SampledBytes),
            rest.Sum(e => e.SampledKeyCount),
            rest.Sum(e => e.KeysWithoutTtlCount)));
        return top;
    }

    private static string PrefixOf(string key, char delimiter)
    {
        var index = key.IndexOf(delimiter);
        return index < 0 ? key : key[..(index + 1)];
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisMemoryByPrefixSamplerTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Write the failing handler test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Overview/GetMemoryByPrefixQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Overview;

public class GetMemoryByPrefixQueryHandlerTests
{
    private const string Secret = "standalone|0|localhost:6379";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IRedisOperations _operations = Substitute.For<IRedisOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private GetMemoryByPrefixQueryHandler CreateHandler() =>
        new(_operations, _connections, NullLogger<GetMemoryByPrefixQueryHandler>.Instance);

    [Fact]
    public async Task Returns_entries_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        IReadOnlyList<MemoryByPrefixEntry> entries = [new("session:", 4000, 2, 1)];
        _operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>()).Returns(entries);

        var result = await CreateHandler().HandleAsync(_connectionId, database: 0, ct: CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task Fails_without_calling_the_broker_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(_connectionId, database: 0, ct: CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _operations.DidNotReceive().GetMemoryByPrefixAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<char>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 7: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~GetMemoryByPrefixQueryHandlerTests"`
Expected: build failure.

- [ ] **Step 8: Add `GetMemoryByPrefixAsync` to the interface and implement it**

In `IRedisOperations.cs`, add:
```csharp
Task<IReadOnlyList<MemoryByPrefixEntry>> GetMemoryByPrefixAsync(
    string secret, int database, char delimiter, int sampleSize, CancellationToken ct = default);
```

In `RedisOperations.cs`, add:
```csharp
public async Task<IReadOnlyList<MemoryByPrefixEntry>> GetMemoryByPrefixAsync(
    string secret, int database, char delimiter, int sampleSize, CancellationToken ct = default)
{
    await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
    var server = muxer.GetServer(muxer.GetEndPoints()[0]);
    var db = muxer.GetDatabase(database);

    var samples = new List<SampledKey>();
    await foreach (var key in server.KeysAsync(database: database, pattern: "*", pageSize: 1000))
    {
        if (samples.Count >= sampleSize)
        {
            break;
        }

        ct.ThrowIfCancellationRequested();
        var sizeResult = await db.ExecuteAsync("MEMORY", "USAGE", (string)key!);
        var size = sizeResult.IsNull ? 0L : (long)sizeResult;
        var ttl = await db.KeyTimeToLiveAsync(key);
        samples.Add(new SampledKey(key!, size, ttl.HasValue));
    }

    return RedisMemoryByPrefixSampler.Summarize(samples, delimiter, topN: 4);
}
```

- [ ] **Step 9: Write `GetMemoryByPrefixQueryHandler`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Overview/GetMemoryByPrefixQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Overview;

public sealed class GetMemoryByPrefixQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<GetMemoryByPrefixQueryHandler> logger)
{
    private const char Delimiter = ':';
    private const int SampleSize = 5000;

    public Task<PluginResult<IReadOnlyList<MemoryByPrefixEntry>>> HandleAsync(
        Guid connectionId, int database, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.overview.memorybyprefix",
            secret => operations.GetMemoryByPrefixAsync(secret, database, Delimiter, SampleSize, ct), ct);
}
```

- [ ] **Step 10: Register the handler**

In `RedisPlugin.ConfigureServices`, add:
```csharp
services.AddScoped<Overview.GetMemoryByPrefixQueryHandler>();
```

- [ ] **Step 11: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~GetMemoryByPrefixQueryHandlerTests"`
Expected: PASS (2 tests). If `IServer.KeysAsync`'s exact parameter names/order differ from the pinned 3.3.1 API, adjust the call in `RedisOperations.GetMemoryByPrefixAsync` to match — verified via `dotnet build`.

- [ ] **Step 12: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis tests/SbConsole.Plugins.Redis.Tests
git commit -m "feat(redis): add memory-by-prefix sampling"
```

---

### Task 8: Slow log

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/SlowLogEntry.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Client/RedisSlowLogParser.cs`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Overview/GetSlowLogQueryHandler.cs`
- Modify: `IRedisOperations.cs`, `RedisOperations.cs`, `RedisPlugin.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Client/RedisSlowLogParserTests.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Overview/GetSlowLogQueryHandlerTests.cs`

**Interfaces:**
- Consumes: nothing new for the parser (works directly on `StackExchange.Redis.RedisResult`, which can be constructed in tests via `RedisResult.Create`).
- Produces: `SlowLogEntry`, `RedisSlowLogParser.Parse(RedisResult reply) : IReadOnlyList<SlowLogEntry>`, `IRedisOperations.GetSlowLogAsync`, `GetSlowLogQueryHandler`.

- [ ] **Step 1: Write `SlowLogEntry`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/SlowLogEntry.cs
namespace SbConsole.Plugins.Redis.Client;

public sealed record SlowLogEntry(DateTimeOffset At, TimeSpan Duration, string Command, string? ClientAddress);
```

- [ ] **Step 2: Write the failing parser test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Client/RedisSlowLogParserTests.cs
using StackExchange.Redis;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisSlowLogParserTests
{
    private static RedisResult Scalar(RedisValue v) => RedisResult.Create(v);
    private static RedisResult Arr(params RedisResult[] items) => RedisResult.Create(items);

    [Fact]
    public void Parses_id_timestamp_duration_command_and_client_address()
    {
        var reply = Arr(
            Arr(
                Scalar(14),                       // entry id (ignored — not part of SlowLogEntry)
                Scalar(1759050012),                // unix seconds
                Scalar(412000),                    // duration, microseconds
                Arr(Scalar("KEYS"), Scalar("session:*")),
                Scalar("10.4.8.31:51322"),
                Scalar("")));

        var entries = RedisSlowLogParser.Parse(reply);

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.At.ToUnixTimeSeconds().Should().Be(1759050012);
        entry.Duration.Should().Be(TimeSpan.FromMicroseconds(412000));
        entry.Command.Should().Be("KEYS session:*");
        entry.ClientAddress.Should().Be("10.4.8.31:51322");
    }

    [Fact]
    public void An_empty_reply_yields_no_entries()
    {
        RedisSlowLogParser.Parse(Arr()).Should().BeEmpty();
    }

    [Fact]
    public void Blank_client_name_field_does_not_affect_client_address()
    {
        var reply = Arr(Arr(Scalar(1), Scalar(1000), Scalar(500), Arr(Scalar("PING")), Scalar("127.0.0.1:1"), Scalar("my-client")));

        RedisSlowLogParser.Parse(reply)[0].ClientAddress.Should().Be("127.0.0.1:1");
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisSlowLogParserTests"`
Expected: build failure — `RedisSlowLogParser` doesn't exist yet. If `RedisResult.Create(RedisValue)` / `RedisResult.Create(RedisResult[])` don't match this exact overload shape in the pinned 3.3.1 package, adjust the `Scalar`/`Arr` test helpers to whatever construction API that version exposes — the parsing logic under test is unaffected either way.

- [ ] **Step 4: Write `RedisSlowLogParser`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Client/RedisSlowLogParser.cs
using StackExchange.Redis;

namespace SbConsole.Plugins.Redis.Client;

public static class RedisSlowLogParser
{
    public static IReadOnlyList<SlowLogEntry> Parse(RedisResult reply)
    {
        var entries = (RedisResult[]?)reply ?? [];
        var result = new List<SlowLogEntry>(entries.Length);

        foreach (var entry in entries)
        {
            var fields = (RedisResult[])entry!;
            var unixSeconds = (long)fields[1];
            var microseconds = (long)fields[2];
            var args = (RedisResult[])fields[3]!;
            var command = string.Join(' ', args.Select(a => (string)a!));
            var clientAddress = (string?)fields[4];

            result.Add(new SlowLogEntry(
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
                TimeSpan.FromMicroseconds(microseconds),
                command,
                string.IsNullOrEmpty(clientAddress) ? null : clientAddress));
        }

        return result;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisSlowLogParserTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Write the failing handler test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Overview/GetSlowLogQueryHandlerTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Overview;

public class GetSlowLogQueryHandlerTests
{
    private const string Secret = "standalone|0|localhost:6379";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IRedisOperations _operations = Substitute.For<IRedisOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    private GetSlowLogQueryHandler CreateHandler() =>
        new(_operations, _connections, NullLogger<GetSlowLogQueryHandler>.Instance);

    [Fact]
    public async Task Returns_entries_for_a_known_connection()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        IReadOnlyList<SlowLogEntry> entries = [new(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(412), "KEYS session:*", "10.4.8.31")];
        _operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>()).Returns(entries);

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(entries);
    }

    [Fact]
    public async Task Fails_without_calling_the_broker_when_the_connection_is_unknown()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateHandler().HandleAsync(_connectionId, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await _operations.DidNotReceive().GetSlowLogAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 7: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~GetSlowLogQueryHandlerTests"`
Expected: build failure.

- [ ] **Step 8: Add `GetSlowLogAsync` to the interface and implement it**

In `IRedisOperations.cs`, add:
```csharp
Task<IReadOnlyList<SlowLogEntry>> GetSlowLogAsync(string secret, int count, CancellationToken ct = default);
```

In `RedisOperations.cs`, add:
```csharp
public async Task<IReadOnlyList<SlowLogEntry>> GetSlowLogAsync(string secret, int count, CancellationToken ct = default)
{
    await using var muxer = await RedisConnectionFactory.ConnectAsync(secret, ct);
    var db = muxer.GetDatabase();
    var raw = await db.ExecuteAsync("SLOWLOG", "GET", count.ToString()).WaitAsync(ct);
    return RedisSlowLogParser.Parse(raw);
}
```

- [ ] **Step 9: Write `GetSlowLogQueryHandler`**

```csharp
// src/Plugins/SbConsole.Plugins.Redis/Overview/GetSlowLogQueryHandler.cs
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Overview;

public sealed class GetSlowLogQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<GetSlowLogQueryHandler> logger)
{
    private const int Count = 10;

    public Task<PluginResult<IReadOnlyList<SlowLogEntry>>> HandleAsync(Guid connectionId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.overview.slowlog",
            secret => operations.GetSlowLogAsync(secret, Count, ct), ct);
}
```

- [ ] **Step 10: Register the handler**

In `RedisPlugin.ConfigureServices`, add:
```csharp
services.AddScoped<Overview.GetSlowLogQueryHandler>();
```

- [ ] **Step 11: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~GetSlowLogQueryHandlerTests"`
Expected: PASS (2 tests).

- [ ] **Step 12: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis tests/SbConsole.Plugins.Redis.Tests
git commit -m "feat(redis): add slow log parsing and query handler"
```

---

### Task 9: `Overview.razor`

**Files:**
- Create: `src/Plugins/SbConsole.Plugins.Redis/Pages/_Imports.razor`
- Create: `src/Plugins/SbConsole.Plugins.Redis/Pages/Overview.razor`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Pages/RedisPageTestBase.cs`
- Test: `tests/SbConsole.Plugins.Redis.Tests/Pages/OverviewPageTests.cs`

**Interfaces:**
- Consumes: `GetOverviewQueryHandler`, `GetMemoryByPrefixQueryHandler`, `GetSlowLogQueryHandler` (Tasks 6–8), `IConnectionProvider`, `[Inject(Key = "redis")] IPluginStore`.
- Produces: nothing later tasks in this plan depend on — last task in the slice.

- [ ] **Step 1: Copy `_Imports.razor`**

Copy `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/_Imports.razor` verbatim to `src/Plugins/SbConsole.Plugins.Redis/Pages/_Imports.razor`, adjusting only the root namespace line (`@using SbConsole.Plugins.RabbitMq` → `@using SbConsole.Plugins.Redis`) and any other RabbitMq-specific `@using` lines it contains that don't apply here.

- [ ] **Step 2: Write the shared bUnit test base**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Pages/RedisPageTestBase.cs
using Bunit;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public abstract class RedisPageTestBase : TestContext
{
    protected readonly IRedisOperations Operations = Substitute.For<IRedisOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly Guid ConnectionId = Guid.NewGuid();
    protected const string Secret = "standalone|0|localhost:6379";

    protected RedisPageTestBase()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        Connections.ListAsync("redis", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, "redis-uk-prod", "redis", ["prod"], true, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, null),
        ]);
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);

        Services.AddSingleton(Operations);
        Services.AddSingleton(Connections);
        Services.AddSingleton(new GetOverviewQueryHandler(Operations, Connections, NullLogger<GetOverviewQueryHandler>.Instance));
        Services.AddSingleton(new GetMemoryByPrefixQueryHandler(Operations, Connections, NullLogger<GetMemoryByPrefixQueryHandler>.Instance));
        Services.AddSingleton(new GetSlowLogQueryHandler(Operations, Connections, NullLogger<GetSlowLogQueryHandler>.Instance));
    }

    protected static async Task SettleAsync(IRenderedFragment cut)
    {
        await Task.Delay(30);
        cut.Render();
    }
}
```

- [ ] **Step 3: Write the failing page test**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/Pages/OverviewPageTests.cs
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Plugins.Redis.Pages;

namespace SbConsole.Plugins.Redis.Tests.Pages;

public class OverviewPageTests : RedisPageTestBase
{
    private static ServerOverview SampleOverview() => new(
        RedisVersion: "7.2.4", Mode: "standalone", ConnectedReplicas: 2,
        UsedMemoryBytes: 6_900_000_000, MaxMemoryBytes: 8_000_000_000,
        MaxMemoryPolicy: "allkeys-lru", MemoryFragmentationRatio: 1.12,
        HitRatioPercent: 94.2, OpsPerSecond: 41800, EvictedKeysTotal: 1240000, ExpiredKeysTotal: 98213,
        ConnectedClients: 412, BlockedClients: 3,
        Databases: [new KeyspaceDatabaseInfo(0, 2_410_000, 1_820_000)]);

    [Fact]
    public async Task Renders_the_memory_and_hit_ratio_tiles()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(SampleOverview());
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MemoryByPrefixEntry>)[]);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = RenderComponent<Overview>();
        await SettleAsync(cut);

        cut.Find(".redis-overview-memory-used").TextContent.Should().Contain("6.9");
        cut.Find(".redis-overview-hit-ratio").TextContent.Should().Contain("94.2");
    }

    [Fact]
    public async Task Shows_the_broker_error_when_the_overview_call_fails()
    {
        Operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>())
            .Returns<Task<ServerOverview>>(_ => throw new InvalidOperationException("down"));
        Operations.GetMemoryByPrefixAsync(Secret, 0, ':', 5000, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<MemoryByPrefixEntry>)[]);
        Operations.GetSlowLogAsync(Secret, 10, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SlowLogEntry>)[]);

        var cut = RenderComponent<Overview>();
        await SettleAsync(cut);

        cut.Find(".redis-overview-error").Should().NotBeNull();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~OverviewPageTests"`
Expected: build failure — `Overview.razor` doesn't exist yet.

- [ ] **Step 5: Write `Overview.razor`**

Start by copying the top-of-file directives (`@page`, `@attribute`/`@rendermode` if present, `@inject` boilerplate style) from `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/Queues.razor` verbatim, then build the body below — swap the route to `/p/redis/overview` and drop anything vhost-specific (Overview has no vhost/db axis, it's server-wide):

```razor
@* src/Plugins/SbConsole.Plugins.Redis/Pages/Overview.razor *@
@page "/p/redis/overview"
@using SbConsole.Plugins.Redis.Client
@using SbConsole.Plugins.Redis.Overview
@using SbConsole.Sdk
@inject IConnectionProvider Connections
@inject GetOverviewQueryHandler GetOverview
@inject GetMemoryByPrefixQueryHandler GetMemoryByPrefix
@inject GetSlowLogQueryHandler GetSlowLog
@implements IAsyncDisposable

<MudSelect T="Guid" Label="Connection" Value="_connectionId" ValueChanged="OnConnectionChangedAsync" Class="redis-overview-connection">
    @foreach (var c in _connections)
    {
        <MudSelectItem Value="c.Id">@c.Name</MudSelectItem>
    }
</MudSelect>

@if (_error is not null)
{
    <MudAlert Severity="Severity.Error" Class="redis-overview-error">@_error</MudAlert>
}
else if (_overview is not null)
{
    <MudGrid>
        <MudItem xs="6">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.caption">Memory</MudText>
                <MudText Typo="Typo.h5" Class="redis-overview-memory-used">
                    @FormatBytes(_overview.UsedMemoryBytes)
                    @if (_overview.MaxMemoryBytes is { } max)
                    {
                        <span>(@((100.0 * _overview.UsedMemoryBytes / max).ToString("N0"))%)</span>
                    }
                </MudText>
            </MudPaper>
        </MudItem>
        <MudItem xs="2">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.caption">Hit ratio</MudText>
                <MudText Typo="Typo.h5" Class="redis-overview-hit-ratio">@_overview.HitRatioPercent.ToString("N1")%</MudText>
            </MudPaper>
        </MudItem>
        <MudItem xs="2">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.caption">Ops / sec</MudText>
                <MudText Typo="Typo.h5" Class="redis-overview-ops">@_overview.OpsPerSecond.ToString("N0")</MudText>
            </MudPaper>
        </MudItem>
        <MudItem xs="2">
            <MudPaper Class="pa-4">
                <MudText Typo="Typo.caption">Evicted (total)</MudText>
                <MudText Typo="Typo.h5" Class="redis-overview-evicted">@_overview.EvictedKeysTotal.ToString("N0")</MudText>
            </MudPaper>
        </MudItem>
    </MudGrid>

    <MudTable Items="_memoryByPrefix" Class="redis-overview-memory-by-prefix" T="MemoryByPrefixEntry">
        <HeaderContent>
            <MudTh>Prefix</MudTh><MudTh>Bytes (sampled)</MudTh><MudTh>Keys</MudTh><MudTh>No TTL</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.Prefix</MudTd>
            <MudTd>@FormatBytes(context.SampledBytes)</MudTd>
            <MudTd>@context.SampledKeyCount</MudTd>
            <MudTd>@context.KeysWithoutTtlCount</MudTd>
        </RowTemplate>
    </MudTable>

    <MudTable Items="_slowLog" Class="redis-overview-slowlog" T="SlowLogEntry">
        <HeaderContent>
            <MudTh>Time</MudTh><MudTh>ms</MudTh><MudTh>Command</MudTh><MudTh>Client</MudTh>
        </HeaderContent>
        <RowTemplate>
            <MudTd>@context.At.ToLocalTime().ToString("HH:mm:ss")</MudTd>
            <MudTd>@context.Duration.TotalMilliseconds.ToString("N0")</MudTd>
            <MudTd>@context.Command</MudTd>
            <MudTd>@context.ClientAddress</MudTd>
        </RowTemplate>
    </MudTable>
}

@code {
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private Guid _connectionId;
    private ServerOverview? _overview;
    private IReadOnlyList<MemoryByPrefixEntry> _memoryByPrefix = [];
    private IReadOnlyList<SlowLogEntry> _slowLog = [];
    private string? _error;
    private CancellationTokenSource _cts = new();

    protected override async Task OnInitializedAsync()
    {
        _connections = await Connections.ListAsync("redis", _cts.Token);
        if (_connections.Count > 0)
        {
            _connectionId = _connections[0].Id;
            await LoadAsync();
        }
    }

    private async Task OnConnectionChangedAsync(Guid connectionId)
    {
        _connectionId = connectionId;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _error = null;

        var overviewResult = await GetOverview.HandleAsync(_connectionId, _cts.Token);
        if (!overviewResult.IsSuccess)
        {
            _error = overviewResult.Error;
            _overview = null;
            return;
        }
        _overview = overviewResult.Value;

        var memoryResult = await GetMemoryByPrefix.HandleAsync(_connectionId, database: 0, ct: _cts.Token);
        _memoryByPrefix = memoryResult.IsSuccess ? memoryResult.Value! : [];

        var slowLogResult = await GetSlowLog.HandleAsync(_connectionId, _cts.Token);
        _slowLog = slowLogResult.IsSuccess ? slowLogResult.Value! : [];
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:N1} {units[unit]}";
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~OverviewPageTests"`
Expected: PASS (2 tests). If `MudSelect`/`MudTable` render differently than assumed under bUnit (e.g. content projected into a different DOM shape), adjust the CSS-class placement in `Overview.razor` so the asserted classes land on an element bUnit can actually select — check `src/Plugins/SbConsole.Plugins.RabbitMq/Pages/Queues.razor` and its test for the exact idiom this MudBlazor version needs.

- [ ] **Step 7: Run the whole Redis test project**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests`
Expected: PASS, everything from Tasks 1–9 green.

- [ ] **Step 8: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis/Pages tests/SbConsole.Plugins.Redis.Tests/Pages
git commit -m "feat(redis): add the Overview page"
```

---

### Task 10: Dashboard hooks, local dev stack, docs

**Files:**
- Modify: `src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs` (`GetDashboardMetricsAsync`, `GetDashboardProblemsAsync`)
- Modify: `docker-compose.yml`
- Create: `docker/redis/init-keys.sh`
- Modify: `docker/README.md`
- Modify: `docs/design.md` (new §6.9 addendum)
- Test: `tests/SbConsole.Plugins.Redis.Tests/RedisPluginDashboardTests.cs`

**Interfaces:**
- Consumes: `IRedisOperations.GetOverviewAsync` (Task 6), SDK's `PluginDashboardMetric`/`PluginDashboardProblem`.
- Produces: nothing later tasks depend on — closes out this plan.

- [ ] **Step 1: Write the failing dashboard-hook tests**

```csharp
// tests/SbConsole.Plugins.Redis.Tests/RedisPluginDashboardTests.cs
using NSubstitute;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests;

public class RedisPluginDashboardTests
{
    private static ServerOverview OverviewAt(double usedPercentOfMax)
    {
        const long max = 8_000_000_000;
        var used = (long)(max * usedPercentOfMax / 100);
        return new ServerOverview("7.2.4", "standalone", 0, used, max, "allkeys-lru", 1.0, 90, 100, 0, 0, 1, 0, []);
    }

    [Fact]
    public async Task No_problem_when_memory_is_comfortably_below_the_threshold()
    {
        var plugin = new RedisPlugin();
        var operations = Substitute.For<IRedisOperations>();
        operations.GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(OverviewAt(50));

        var problems = await plugin.GetDashboardProblemsAsyncForTesting(operations, Guid.NewGuid(), "secret");

        problems.Should().BeEmpty();
    }

    [Fact]
    public async Task Reports_a_warning_at_ninety_percent_used_memory()
    {
        var plugin = new RedisPlugin();
        var operations = Substitute.For<IRedisOperations>();
        operations.GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(OverviewAt(92));

        var problems = await plugin.GetDashboardProblemsAsyncForTesting(operations, Guid.NewGuid(), "secret");

        problems.Should().ContainSingle();
        problems[0].Severity.Should().Be("Warning");
        problems[0].LinkHref.Should().Be("/p/redis/overview");
    }

    [Fact]
    public async Task No_problem_when_maxmemory_is_unbounded()
    {
        var plugin = new RedisPlugin();
        var operations = Substitute.For<IRedisOperations>();
        operations.GetOverviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ServerOverview("7.2.4", "standalone", 0, 100, null, "noeviction", 1.0, 90, 100, 0, 0, 1, 0, []));

        var problems = await plugin.GetDashboardProblemsAsyncForTesting(operations, Guid.NewGuid(), "secret");

        problems.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisPluginDashboardTests"`
Expected: build failure — `GetDashboardProblemsAsyncForTesting` doesn't exist yet (a small internal seam so the test can inject a substitute `IRedisOperations` instead of the real one `GetDashboardProblemsAsync` constructs, matching RabbitMQ's `GetCachedSnapshotAsync` testability pattern — `docs/design.md` §6.8).

- [ ] **Step 3: Implement the dashboard hooks on `RedisPlugin`**

Add to `src/Plugins/SbConsole.Plugins.Redis/RedisPlugin.cs`:

```csharp
private const double MemoryProblemThresholdPercent = 90.0;

public async Task<IReadOnlyList<PluginDashboardMetric>> GetDashboardMetricsAsync(
    string connectionString, CancellationToken ct = default)
{
    var overview = await new RedisOperations().GetOverviewAsync(connectionString, ct);
    return
    [
        new PluginDashboardMetric("Used memory %", overview.MaxMemoryBytes is { } max && max > 0
            ? (int)(100.0 * overview.UsedMemoryBytes / max) : 0),
        new PluginDashboardMetric("Hit ratio %", (int)overview.HitRatioPercent),
    ];
}

public Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsync(
    Guid connectionId, string connectionString, IPluginStore store, CancellationToken ct = default) =>
    GetDashboardProblemsAsyncForTesting(new RedisOperations(), connectionId, connectionString, ct);

internal async Task<IReadOnlyList<PluginDashboardProblem>> GetDashboardProblemsAsyncForTesting(
    IRedisOperations operations, Guid connectionId, string connectionString, CancellationToken ct = default)
{
    var overview = await operations.GetOverviewAsync(connectionString, ct);
    if (overview.MaxMemoryBytes is not { } max || max <= 0)
    {
        return [];
    }

    var usedPercent = 100.0 * overview.UsedMemoryBytes / max;
    if (usedPercent < MemoryProblemThresholdPercent)
    {
        return [];
    }

    return
    [
        new PluginDashboardProblem(
            "Warning",
            "Memory usage is approaching the eviction threshold",
            $"{usedPercent:N0}% of maxmemory ({overview.MaxMemoryPolicy})",
            "/p/redis/overview"),
    ];
}
```

(`GetDashboardMetricsAsync`/`GetDashboardProblemsAsync` are SDK-default no-ops today — adding real bodies here overrides those defaults; no interface change needed since `IPlugin` already declares them.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Redis.Tests --filter "FullyQualifiedName~RedisPluginDashboardTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Add the Redis service to the local dev stack**

In `docker-compose.yml`, add (matching the RabbitMQ service's structure — healthcheck, profile gate, one-shot seeding):

```yaml
  redis:
    image: redis:7-alpine
    container_name: nervecenter-redis
    profiles: ["redis"]
    ports:
      - "${REDIS_PORT:-6379}:6379"
    command: ["redis-server", "--requirepass", "${REDIS_PASSWORD:-sbconsole}"]
    healthcheck:
      test: ["CMD", "redis-cli", "-a", "${REDIS_PASSWORD:-sbconsole}", "--no-auth-warning", "ping"]
      interval: 5s
      timeout: 5s
      retries: 12
      start_period: 10s

  redis-init:
    image: redis:7-alpine
    container_name: nervecenter-redis-init
    profiles: ["redis"]
    depends_on:
      redis:
        condition: service_healthy
    entrypoint: ["/bin/sh", "/init-keys.sh"]
    environment:
      REDIS_PASSWORD: "${REDIS_PASSWORD:-sbconsole}"
    volumes:
      - ./docker/redis/init-keys.sh:/init-keys.sh:ro
    restart: "no"
```

- [ ] **Step 6: Write the seed script**

```bash
#!/bin/sh
# docker/redis/init-keys.sh
set -e
R="redis-cli -h redis -a $REDIS_PASSWORD --no-auth-warning"

$R HSET session:u:77120 userId 77120 tenant acme-uk lastSeen "$(date -u +%FT%TZ)"
$R EXPIRE session:u:77120 1800

$R HSET session:u:77121 userId 77121 tenant acme-uk lastSeen "$(date -u +%FT%TZ)"

$R RPUSH jobs:email:retry '{"to":"a@acme.example","try":3}' '{"to":"k@acme.example","try":1}'

$R ZADD lb:weekly 9812 team-orion 8044 team-lyra 6970 team-vela

$R SADD flags:beta acme-uk northwind globex initech

echo "Redis sample data seeded."
```

- [ ] **Step 7: Document the new profile**

In `docker/README.md`, add a "## Redis (opt-in)" section following the RabbitMQ section's structure exactly: quick-start command (`docker compose --profile redis up -d`), a host-address table, what's seeded and why, a "Connect the app to it" subsection giving the literal secret string to paste (`standalone|0|localhost:6379,password=sbconsole`), and add `REDIS_PORT` to the overrides table and `--profile redis` to the reset command's profile list at the bottom of the file.

- [ ] **Step 8: Add the design.md addendum**

Append a new dated `## 6.9 Redis plugin` section to `docs/design.md` (following §6.8's structure — Shape, Decisions worth knowing, Deviations from the mockup, Host hooks, Local stack paragraphs), summarizing this plan: scaffold, Standalone/Sentinel connection handling via `RedisConnectionSettings`'s topology-prefixed secret, the pure-parser-vs-thin-wrapper split (`RedisInfoParser`/`RedisMemoryByPrefixSampler`/`RedisSlowLogParser` vs. `RedisOperations`), the Overview page, and the memory-threshold dashboard problem — with a pointer to `docs/superpowers/specs/2026-09-28-redis-plugin-design.md` for the full multi-phase design and a note that Keys/Streams/Pub-Sub follow as separate plans.

- [ ] **Step 9: Full solution build and test**

Run: `dotnet build -warnaserror && dotnet test`
Expected: everything green, including the pre-existing plugins (no regressions from the new `ProjectReference`/DI registration).

- [ ] **Step 10: Commit**

```bash
git add src/Plugins/SbConsole.Plugins.Redis tests/SbConsole.Plugins.Redis.Tests docker-compose.yml docker/redis docker/README.md docs/design.md
git commit -m "feat(redis): add dashboard hooks and the local dev stack"
```
