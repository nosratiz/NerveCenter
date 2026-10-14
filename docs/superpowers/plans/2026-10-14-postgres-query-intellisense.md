# Postgres Query IntelliSense Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Completion popup in the Postgres Query editor offering keywords, built-in functions, schemas, tables/views/matviews, columns and aliases.

**Architecture:** Port of the SQLite plugin's §12 design. A pure C# `PgCompletionEngine` (server side) answers `(text, caret, schema)`; a small JS module (`postgres-completion.js`, adapted from `sqlite-completion.js`) renders the popup at the caret and calls the page's `[JSInvokable] GetCompletions`. The schema comes from one read-only catalog query, cached on the page per (connection, database).

**Tech Stack:** .NET 10, Blazor Interactive Server, MudBlazor, Npgsql, xUnit + FluentAssertions + NSubstitute + bUnit + Testcontainers (`postgres:16-alpine`).

**Spec:** `docs/superpowers/specs/2026-10-14-postgres-plugin-design.md` §16.

## Global Constraints

- `TreatWarningsAsErrors=true` — a warning fails the build. Gate before every commit: `dotnet build -warnaserror` and `dotnet test` green.
- TDD: write the failing test first, per task.
- Plugin references `SbConsole.Sdk` only; no new NuGet packages, no JS libraries, no JS build pipeline.
- Handlers are DI-registered by marker (`IScopedService`); queries never write audit rows.
- Theming only via `--mud-palette-*` tokens — no hex colours in CSS.
- Caps: 2,000 objects × 200 columns; engine returns at most 50 items.
- Excluded schemas: `pg_catalog`, `information_schema`, and anything `LIKE 'pg\_%'` (covers `pg_toast*`, `pg_temp*`).
- DDL that triggers a schema reload after a committed write: first keyword `CREATE`, `ALTER`, `DROP`, `COMMENT`, `TRUNCATE`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File map

| File | Responsibility |
|---|---|
| `src/Plugins/Databases/SbConsole.Plugins.Postgres/Client/PgCompletionModels.cs` (new) | `PgCompletionColumn`, `PgCompletionObject`, `PgCompletionSchema` (+ caps, `Empty`) |
| `.../Client/IPostgresOperations.cs` (modify) | add `GetCompletionSchemaAsync` |
| `.../Client/PostgresOperations.Completion.cs` (new) | the catalog query |
| `.../Query/GetCompletionSchemaQueryHandler.cs` (new) | query handler (not audited) |
| `.../Query/PgCompletionItems.cs` (new) | `PgCompletionKind`, `PgCompletionItem`, `PgCompletionResult` |
| `.../Query/PgCompletionVocabulary.cs` (new) | keywords, reserved words, built-in functions |
| `.../Query/PgCompletionEngine.cs` (new) | pure completion engine |
| `.../wwwroot/postgres-completion.js` (new) | popup, keys, caret positioning, insertion |
| `.../wwwroot/postgres.css` (modify) | popup styles |
| `.../Pages/Query.razor` (modify) | schema load/refresh, attach/detach, `GetCompletions` |
| `tests/SbConsole.Plugins.Postgres.Tests/Client/OperationsCompletionTests.cs` (new) | Testcontainer test of the catalog query |
| `tests/.../Query/GetCompletionSchemaQueryHandlerTests.cs` (new) | handler tests |
| `tests/.../Query/PgCompletionEngineTests.cs` (new) | engine tests |
| `tests/.../Pages/PgPageTestBase.cs` (modify) | register the new handler |
| `tests/.../Pages/QueryCompletionPageTests.cs` (new) | bUnit page tests |
| `docs/design.md` (modify) | §20 dated entry |

All `src/...` paths below are relative to the repo root; `PG` = `src/Plugins/Databases/SbConsole.Plugins.Postgres`, `PGT` = `tests/SbConsole.Plugins.Postgres.Tests`.

---

### Task 1: Completion schema source (models, operation, handler)

**Files:**
- Create: `PG/Client/PgCompletionModels.cs`
- Modify: `PG/Client/IPostgresOperations.cs`
- Create: `PG/Client/PostgresOperations.Completion.cs`
- Create: `PG/Query/GetCompletionSchemaQueryHandler.cs`
- Modify: `PGT/Pages/PgPageTestBase.cs` (`RegisterHandlers`)
- Test: `PGT/Client/OperationsCompletionTests.cs`, `PGT/Query/GetCompletionSchemaQueryHandlerTests.cs`

**Interfaces:**
- Produces:
  - `record PgCompletionColumn(string Name, string Type, bool PrimaryKey)`
  - `record PgCompletionObject(string Schema, string Name, PgObjectKind Kind, IReadOnlyList<PgCompletionColumn> Columns)`
  - `record PgCompletionSchema(IReadOnlyList<string> SearchPath, IReadOnlyList<string> Schemas, IReadOnlyList<PgCompletionObject> Objects)` with `const int MaxObjects = 2000`, `const int MaxColumns = 200`, `static PgCompletionSchema Empty`
  - `IPostgresOperations.GetCompletionSchemaAsync(string secret, string? database, CancellationToken ct = default) : Task<PgCompletionSchema>`
  - `GetCompletionSchemaQueryHandler.HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) : Task<PluginResult<PgCompletionSchema>>`

- [ ] **Step 1: Create the models (needed for the tests to compile)**

`PG/Client/PgCompletionModels.cs`:

```csharp
namespace SbConsole.Plugins.Postgres.Client;

/// <summary>One column for the Query editor's completion (design spec §16): name, format_type() text, part of the primary key.</summary>
public sealed record PgCompletionColumn(string Name, string Type, bool PrimaryKey);

/// <summary>A table, view, materialized view, partitioned or foreign table with its columns (attnum order, at most MaxColumns).</summary>
public sealed record PgCompletionObject(string Schema, string Name, PgObjectKind Kind, IReadOnlyList<PgCompletionColumn> Columns);

/// <summary>
/// What the completion engine knows about one database (design spec §16): the session's search path
/// (current_schemas(false)), the non-system schemas and up to MaxObjects relations, ordered by schema, name.
/// </summary>
public sealed record PgCompletionSchema(IReadOnlyList<string> SearchPath, IReadOnlyList<string> Schemas, IReadOnlyList<PgCompletionObject> Objects)
{
    public const int MaxObjects = 2000;
    public const int MaxColumns = 200;

    public static PgCompletionSchema Empty { get; } = new([], [], []);
}
```

Add to `PG/Client/IPostgresOperations.cs`, after `ListSchemaAsync`:

```csharp
    Task<PgCompletionSchema> GetCompletionSchemaAsync(string secret, string? database, CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing Testcontainer test**

`PGT/Client/OperationsCompletionTests.cs`:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsCompletionTests(PostgresFixture fx)
{
    [DockerFact]
    public async Task Completion_schema_has_search_path_schemas_objects_and_columns_without_system_schemas()
    {
        var schema = await fx.Operations.GetCompletionSchemaAsync(fx.Secret(), null);

        schema.SearchPath.Should().Equal("public");
        schema.Schemas.Should().Contain(["public", "reporting"])
            .And.NotContain(["pg_catalog", "information_schema", "pg_toast"]);
        schema.Objects.Should().NotContain(o => o.Schema == "pg_catalog" || o.Schema == "information_schema" || o.Schema.StartsWith("pg_"));

        var orders = schema.Objects.Single(o => o.Schema == "public" && o.Name == "orders");
        orders.Kind.Should().Be(PgObjectKind.Table);
        orders.Columns.Select(c => c.Name).Should().Equal("id", "customer_id", "status", "total_cents", "metadata", "created_at");
        orders.Columns[0].Should().Be(new PgCompletionColumn("id", "bigint", true));
        orders.Columns[1].PrimaryKey.Should().BeFalse();
        orders.Columns[5].Type.Should().Be("timestamp with time zone");

        schema.Objects.Single(o => o.Name == "v_order_totals").Kind.Should().Be(PgObjectKind.View);
        schema.Objects.Single(o => o.Name == "mv_country").Kind.Should().Be(PgObjectKind.MaterializedView);
        schema.Objects.Single(o => o.Schema == "reporting" && o.Name == "daily").Columns.Should().Equal(new PgCompletionColumn("d", "date", false));
        schema.Objects.Single(o => o.Name == "samples").Columns.Select(c => c.Name).Should().Contain("we\"ird");
    }

    [DockerFact]
    public async Task Completion_schema_reads_the_requested_database()
    {
        var schema = await fx.Operations.GetCompletionSchemaAsync(fx.Secret(), "analytics");

        schema.Objects.Should().NotContain(o => o.Name == "orders");
        schema.Schemas.Should().Contain("public");
    }
}
```

- [ ] **Step 3: Write the failing handler tests**

`PGT/Query/GetCompletionSchemaQueryHandlerTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Tests.Query;

public class GetCompletionSchemaQueryHandlerTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly IPostgresOperations _ops = Substitute.For<IPostgresOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    public GetCompletionSchemaQueryHandlerTests() => _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns("secret");

    private GetCompletionSchemaQueryHandler Sut() => new(_ops, _connections, NullLogger<GetCompletionSchemaQueryHandler>.Instance);

    [Fact]
    public async Task Missing_connection_fails_with_not_found()
    {
        _connections.GetSecretAsync(Id, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await Sut().HandleAsync(Id, "app");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(HandlerRunner.ConnectionNotFound);
    }

    [Fact]
    public async Task Returns_the_schema_for_the_database()
    {
        var schema = new PgCompletionSchema(["public"], ["public"], []);
        _ops.GetCompletionSchemaAsync("secret", "app", Arg.Any<CancellationToken>()).Returns(schema);

        var result = await Sut().HandleAsync(Id, "app");

        result.Value.Should().BeSameAs(schema);
    }

    [Fact]
    public async Task A_failure_becomes_a_friendly_error_and_writes_no_audit_row()
    {
        _ops.GetCompletionSchemaAsync("secret", "app", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Sut().HandleAsync(Id, "app");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }
}
```

(The handler takes no `IAuditScope`, so "no audit row" holds by construction.)

- [ ] **Step 4: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Postgres.Tests --filter "FullyQualifiedName~OperationsCompletionTests|FullyQualifiedName~GetCompletionSchemaQueryHandlerTests"`
Expected: build FAIL — `PostgresOperations` does not implement `GetCompletionSchemaAsync`, `GetCompletionSchemaQueryHandler` not found.

- [ ] **Step 5: Implement the operation**

`PG/Client/PostgresOperations.Completion.cs` (`SystemSchemas`, `ReadAsync`, `QueryAsync` and `KindOf` already exist on the partial class):

```csharp
using System.Globalization;

namespace SbConsole.Plugins.Postgres.Client;

public sealed partial class PostgresOperations
{
    // Partitions are left out as in ListSchemaAsync; the parent covers them.
    private static readonly string CompletionObjectsSql = $"""
        WITH objs AS (
            SELECT c.oid, n.nspname, c.relname, c.relkind
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind IN ('r','v','m','p','f') AND n.nspname NOT IN ({SystemSchemas}) AND n.nspname NOT LIKE 'pg\_%'
               AND NOT c.relispartition
             ORDER BY n.nspname, c.relname
             LIMIT {PgCompletionSchema.MaxObjects.ToString(CultureInfo.InvariantCulture)})
        SELECT o.nspname, o.relname, o.relkind, a.attname, format_type(a.atttypid, a.atttypmod),
               COALESCE(a.attnum = ANY (pk.indkey), false)
          FROM objs o
          LEFT JOIN pg_attribute a ON a.attrelid = o.oid AND a.attnum > 0 AND NOT a.attisdropped
          LEFT JOIN pg_index pk ON pk.indrelid = o.oid AND pk.indisprimary
         ORDER BY o.nspname, o.relname, a.attnum
        """;

    public Task<PgCompletionSchema> GetCompletionSchemaAsync(string secret, string? database, CancellationToken ct = default) =>
        ReadAsync(secret, database, async (conn, tx) =>
        {
            string[] searchPath;
            await using (var q = await QueryAsync(conn, tx, "SELECT current_schemas(false)", ct))
            {
                await q.Reader.ReadAsync(ct);
                searchPath = q.Reader.GetFieldValue<string[]>(0);
            }

            var schemas = new List<string>();
            await using (var q = await QueryAsync(conn, tx,
                $"SELECT nspname FROM pg_namespace WHERE nspname NOT IN ({SystemSchemas}) AND nspname NOT LIKE 'pg\\_%' ORDER BY nspname", ct))
            {
                while (await q.Reader.ReadAsync(ct))
                {
                    schemas.Add(q.Reader.GetString(0));
                }
            }

            var objects = new List<PgCompletionObject>();
            await using (var q = await QueryAsync(conn, tx, CompletionObjectsSql, ct))
            {
                var r = q.Reader;
                List<PgCompletionColumn>? columns = null;
                string? lastSchema = null;
                string? lastName = null;
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(0);
                    var name = r.GetString(1);
                    if (columns is null || schema != lastSchema || name != lastName)
                    {
                        columns = [];
                        objects.Add(new PgCompletionObject(schema, name, KindOf(r.GetChar(2)), columns));
                        lastSchema = schema;
                        lastName = name;
                    }

                    if (!r.IsDBNull(3) && columns.Count < PgCompletionSchema.MaxColumns)
                    {
                        columns.Add(new PgCompletionColumn(r.GetString(3), r.GetString(4), r.GetBoolean(5)));
                    }
                }
            }

            return new PgCompletionSchema(searchPath, schemas, objects);
        }, ct);
}
```

If `KindOf` is declared `private static PgObjectKind KindOf(char)` in `PostgresOperations.Schema.cs`, it is reachable here (same partial class). If the build reports it missing, `grep -n "KindOf" PG/Client/*.cs` and use the existing name.

- [ ] **Step 6: Implement the handler**

`PG/Query/GetCompletionSchemaQueryHandler.cs`:

```csharp
using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>The Query editor's completion schema (design spec §16) for one database. Not audited.</summary>
public sealed class GetCompletionSchemaQueryHandler(
    IPostgresOperations operations, IConnectionProvider connections, ILogger<GetCompletionSchemaQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<PgCompletionSchema>> HandleAsync(Guid connectionId, string? database, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "postgres.query.completion-schema",
            secret => operations.GetCompletionSchemaAsync(secret, database, ct), ct);
}
```

In `PGT/Pages/PgPageTestBase.cs` `RegisterHandlers()`, after the `ExplainQueryQueryHandler` line, add:

```csharp
        Services.AddSingleton(new GetCompletionSchemaQueryHandler(Operations, Connections, NullLogger<GetCompletionSchemaQueryHandler>.Instance));
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Postgres.Tests --filter "FullyQualifiedName~OperationsCompletionTests|FullyQualifiedName~GetCompletionSchemaQueryHandlerTests"`
Expected: PASS (the two `[DockerFact]`s are skipped if Docker isn't running — start Docker Desktop to actually exercise them).

- [ ] **Step 8: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Postgres.Tests`
Expected: build succeeded, 0 warnings; tests PASS (includes `ProgramDiRegistrationTests` in the Web tests when the whole solution runs — run `dotnet test` once before committing).

```bash
git add src/Plugins/Databases/SbConsole.Plugins.Postgres/Client/PgCompletionModels.cs src/Plugins/Databases/SbConsole.Plugins.Postgres/Client/IPostgresOperations.cs src/Plugins/Databases/SbConsole.Plugins.Postgres/Client/PostgresOperations.Completion.cs src/Plugins/Databases/SbConsole.Plugins.Postgres/Query/GetCompletionSchemaQueryHandler.cs tests/SbConsole.Plugins.Postgres.Tests/Client/OperationsCompletionTests.cs tests/SbConsole.Plugins.Postgres.Tests/Query/GetCompletionSchemaQueryHandlerTests.cs tests/SbConsole.Plugins.Postgres.Tests/Pages/PgPageTestBase.cs
git commit -m "feat(postgres): completion schema operation and query handler

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Completion engine

**Files:**
- Create: `PG/Query/PgCompletionItems.cs`
- Create: `PG/Query/PgCompletionVocabulary.cs`
- Create: `PG/Query/PgCompletionEngine.cs`
- Test: `PGT/Query/PgCompletionEngineTests.cs`

**Interfaces:**
- Consumes: `PgCompletionSchema`, `PgCompletionObject`, `PgCompletionColumn` (Task 1); `PgObjectKind` and `PgIdent.Quote(string)` (existing, `SbConsole.Plugins.Postgres.Client`).
- Produces:
  - `enum PgCompletionKind { Keyword, Function, Schema, Table, View, Column, Alias }` (serialised by name)
  - `record PgCompletionItem(string Label, string InsertText, PgCompletionKind Kind, string? Detail)`
  - `record PgCompletionResult(int ReplaceFrom, int ReplaceTo, IReadOnlyList<PgCompletionItem> Items)`
  - `static PgCompletionResult PgCompletionEngine.Complete(string text, int caret, PgCompletionSchema schema)`; `const int MaxItems = 50`

- [ ] **Step 1: Create the result models (needed for tests to compile)**

`PG/Query/PgCompletionItems.cs`:

```csharp
using System.Text.Json.Serialization;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>What a completion item is; serialised by name so postgres-completion.js can pick a badge.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PgCompletionKind>))]
public enum PgCompletionKind
{
    Keyword,
    Function,
    Schema,
    Table,
    View,
    Column,
    Alias,
}

/// <summary>One suggestion: Label is shown and filtered on, InsertText replaces the range (quoted / qualified / upper-cased as needed), Detail is the muted hint.</summary>
public sealed record PgCompletionItem(string Label, string InsertText, PgCompletionKind Kind, string? Detail);

/// <summary>Accepting an item replaces text[ReplaceFrom..ReplaceTo) with its InsertText (UTF-16 offsets, as the textarea counts).</summary>
public sealed record PgCompletionResult(int ReplaceFrom, int ReplaceTo, IReadOnlyList<PgCompletionItem> Items);
```

- [ ] **Step 2: Write the failing engine tests**

`PGT/Query/PgCompletionEngineTests.cs` — `|` marks the caret and is removed before calling the engine:

```csharp
using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;

namespace SbConsole.Plugins.Postgres.Tests.Query;

public class PgCompletionEngineTests
{
    private static PgCompletionColumn Col(string name, string type = "text", bool pk = false) => new(name, type, pk);

    private static readonly PgCompletionSchema Schema = new(
        ["public"],
        ["public", "reporting", "Sales"],
        [
            new("public", "orders", PgObjectKind.Table,
                [Col("id", "bigint", true), Col("customer_id", "bigint"), Col("status"), Col("created_at", "timestamp with time zone")]),
            new("public", "customers", PgObjectKind.Table, [Col("id", "bigint", true), Col("email"), Col("Country Code"), Col("user")]),
            new("public", "v_order_totals", PgObjectKind.View, [Col("customer_id", "bigint"), Col("total", "bigint")]),
            new("public", "mv_country", PgObjectKind.MaterializedView, [Col("country"), Col("customers", "bigint")]),
            new("reporting", "daily", PgObjectKind.Table, [Col("d", "date")]),
            new("Sales", "Leads", PgObjectKind.Table, [Col("id", "integer", true)]),
        ]);

    private static PgCompletionResult Complete(string marked, PgCompletionSchema? schema = null)
    {
        var caret = marked.IndexOf('|', StringComparison.Ordinal);
        caret.Should().BeGreaterThanOrEqualTo(0, "the test text must mark the caret with |");
        return PgCompletionEngine.Complete(marked.Remove(caret, 1), caret, schema ?? Schema);
    }

    private static IEnumerable<string> Labels(string marked, PgCompletionSchema? schema = null) =>
        Complete(marked, schema).Items.Select(i => i.Label);

    // ---- keywords, functions, ranking ----

    [Fact]
    public void Keywords_complete_upper_case_from_any_case_prefix_with_an_empty_schema()
    {
        var result = Complete("sel|", PgCompletionSchema.Empty);

        result.ReplaceFrom.Should().Be(0);
        result.ReplaceTo.Should().Be(3);
        result.Items.Should().ContainEquivalentOf(new PgCompletionItem("SELECT", "SELECT", PgCompletionKind.Keyword, null));
    }

    [Fact]
    public void Functions_insert_an_open_paren_or_empty_parens_and_show_the_signature()
    {
        Complete("SELECT coal|").Items.Should().ContainEquivalentOf(
            new PgCompletionItem("coalesce", "coalesce(", PgCompletionKind.Function, "coalesce(value, …)"));
        Complete("SELECT no|").Items.Should().Contain(i => i.Label == "now" && i.InsertText == "now()");
    }

    [Fact]
    public void Prefix_matches_come_before_substring_matches()
    {
        var labels = Labels("SELECT st| FROM orders").ToList();

        labels[0].Should().Be("status");
        labels.Should().Contain("customer_id");
        labels.IndexOf("status").Should().BeLessThan(labels.IndexOf("customer_id"));
    }

    [Fact]
    public void At_most_fifty_items_are_returned()
    {
        Complete("SELECT |", PgCompletionSchema.Empty).Items.Should().HaveCount(PgCompletionEngine.MaxItems);
    }

    [Fact]
    public void An_item_equal_to_the_typed_word_ignoring_case_is_left_out()
    {
        Labels("select|").Should().NotContain("SELECT");
        Labels("SELECT * FROM orders|").Should().NotContain("orders");
    }

    // ---- suppression ----

    [Theory]
    [InlineData("SELECT 'fr|")]
    [InlineData("SELECT 'it''s fr|")]
    [InlineData("SELECT E'it\\'s fr|")]
    [InlineData("SELECT $$ fro|")]
    [InlineData("SELECT $fn$ fro|")]
    [InlineData("-- sel|")]
    [InlineData("/* a /* b */ sel|")]
    [InlineData("SELECT \"Coun|")]
    [InlineData("SELECT id::|")]
    [InlineData("SELECT id::te|")]
    [InlineData("SELECT 12|")]
    public void Nothing_is_offered_inside_strings_comments_quoted_names_numbers_or_after_a_cast(string text)
    {
        Complete(text).Items.Should().BeEmpty();
    }

    [Fact]
    public void A_closed_nested_comment_and_a_closed_dollar_quote_do_not_suppress()
    {
        Labels("/* a /* b */ c */ sel|").Should().Contain("SELECT");
        Labels("DO $$ BEGIN END $$; SELECT * FROM |").Should().Contain("orders");
    }

    [Fact]
    public void A_positional_parameter_is_not_a_dollar_quote()
    {
        Labels("SELECT * FROM orders WHERE id = $1 AND st|").Should().Contain("status");
    }

    // ---- table positions ----

    [Fact]
    public void After_from_tables_then_schemas_are_offered_and_off_path_tables_are_qualified()
    {
        var items = Complete("SELECT * FROM |").Items;

        items.Select(i => i.Label).Should().Equal(
            "orders", "customers", "v_order_totals", "mv_country", "reporting.daily", "Sales.Leads", "public", "reporting", "Sales");
        items.Single(i => i.Label == "orders").Should().Be(new PgCompletionItem("orders", "orders", PgCompletionKind.Table, "table · public · 4 columns"));
        items.Single(i => i.Label == "mv_country").Should().Be(
            new PgCompletionItem("mv_country", "mv_country", PgCompletionKind.View, "materialized view · public · 2 columns"));
        items.Single(i => i.Label == "reporting.daily").InsertText.Should().Be("reporting.daily");
        items.Single(i => i.Label == "Sales.Leads").InsertText.Should().Be("\"Sales\".\"Leads\"");
        items.Single(i => i.Label == "Sales").Should().Be(new PgCompletionItem("Sales", "\"Sales\"", PgCompletionKind.Schema, "schema"));
    }

    [Theory]
    [InlineData("SELECT * FROM orders o JOIN |")]
    [InlineData("INSERT INTO |")]
    [InlineData("UPDATE |")]
    [InlineData("DELETE FROM |")]
    [InlineData("SELECT * FROM ONLY |")]
    [InlineData("SELECT * FROM orders, |")]
    [InlineData("SELECT * FROM orders o, customers c, |")]
    [InlineData("CREATE INDEX ix ON |")]
    [InlineData("ALTER TABLE |")]
    public void Table_positions_offer_tables_and_no_keywords(string text)
    {
        var labels = Labels(text).ToList();

        labels.Should().Contain("orders");
        labels.Should().NotContain("SELECT");
    }

    [Theory]
    [InlineData("SELECT id, |")]
    [InlineData("SELECT * FROM orders o JOIN customers c ON |")]
    public void A_select_list_comma_and_a_join_on_are_not_table_positions(string text)
    {
        Complete(text).Items.Should().Contain(i => i.Kind == PgCompletionKind.Keyword);
    }

    // ---- columns and qualifiers ----

    [Fact]
    public void Columns_of_the_statements_tables_come_first()
    {
        Labels("SELECT | FROM orders").Take(4).Should().Equal("id", "customer_id", "status", "created_at");
        Complete("SELECT | FROM orders").Items[0].Should().Be(
            new PgCompletionItem("id", "id", PgCompletionKind.Column, "bigint PK · orders"));
    }

    [Theory]
    [InlineData("SELECT o.| FROM orders o")]
    [InlineData("SELECT x.| FROM public.orders AS x")]
    [InlineData("SELECT orders.| FROM orders")]
    [InlineData("SELECT o.st|atus FROM orders o")]
    public void A_qualifier_naming_an_alias_or_table_lists_its_columns(string text)
    {
        Labels(text).Should().BeSubsetOf(["id", "customer_id", "status", "created_at"]).And.Contain("status");
    }

    [Fact]
    public void A_word_after_the_caret_is_part_of_the_replaced_range()
    {
        var result = Complete("SELECT o.st|atus FROM orders o");

        result.ReplaceFrom.Should().Be(9);
        result.ReplaceTo.Should().Be(15);
    }

    [Fact]
    public void A_schema_qualifier_lists_that_schemas_tables_unqualified()
    {
        Complete("SELECT * FROM reporting.|").Items.Should().Equal(
            new PgCompletionItem("daily", "daily", PgCompletionKind.Table, "table · reporting · 1 column"));
        Complete("SELECT * FROM \"Sales\".|").Items.Single().InsertText.Should().Be("\"Leads\"");
    }

    [Fact]
    public void Schema_dot_table_dot_lists_that_tables_columns()
    {
        Labels("SELECT reporting.daily.| FROM reporting.daily").Should().Equal("d");
    }

    [Fact]
    public void An_alias_wins_over_a_schema_with_the_same_name()
    {
        Labels("SELECT reporting.| FROM orders reporting").Should().Contain("customer_id").And.NotContain("daily");
    }

    [Fact]
    public void An_unknown_qualifier_offers_nothing()
    {
        Complete("SELECT zz.| FROM orders").Items.Should().BeEmpty();
    }

    [Fact]
    public void Aliases_are_offered_in_the_general_list()
    {
        Complete("SELECT | FROM orders o").Items.Should().ContainEquivalentOf(
            new PgCompletionItem("o", "o", PgCompletionKind.Alias, "orders"));
    }

    [Fact]
    public void Identifiers_are_quoted_only_when_needed()
    {
        var items = Complete("SELECT | FROM customers").Items;

        items.Single(i => i.Label == "email").InsertText.Should().Be("email");
        items.Single(i => i.Label == "Country Code").InsertText.Should().Be("\"Country Code\"");
        items.Single(i => i.Label == "user").InsertText.Should().Be("\"user\"");
    }

    // ---- statements and search path ----

    [Fact]
    public void Only_the_statement_holding_the_caret_counts()
    {
        Complete("SELECT * FROM orders o; SELECT o.|").Items.Should().BeEmpty();
        Labels("SELECT ';', o.| FROM orders o").Should().Contain("status");
        Labels("SELECT $$;$$, o.| FROM orders o").Should().Contain("status");
        Labels("SELECT E'a\\'b', o.| FROM orders o").Should().Contain("status");
    }

    [Fact]
    public void Unqualified_names_resolve_through_the_search_path_in_order()
    {
        PgCompletionSchema Path(params string[] path) => new(path, ["public", "reporting"],
        [
            new("public", "orders", PgObjectKind.Table, [Col("id")]),
            new("reporting", "orders", PgObjectKind.Table, [Col("day")]),
        ]);

        Labels("SELECT orders.| FROM orders", Path("reporting", "public")).Should().Equal("day");
        Labels("SELECT orders.| FROM orders", Path("public", "reporting")).Should().Equal("id");
        Labels("SELECT * FROM |", Path("public")).Should().Contain("reporting.orders");
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Postgres.Tests --filter "FullyQualifiedName~PgCompletionEngineTests"`
Expected: build FAIL — `PgCompletionEngine` does not exist.

- [ ] **Step 4: Create the vocabulary**

`PG/Query/PgCompletionVocabulary.cs`:

```csharp
namespace SbConsole.Plugins.Postgres.Query;

/// <summary>
/// PostgreSQL 16 keywords worth suggesting (reserved plus common non-reserved), the reserved words an
/// identifier must be quoted to use, and the built-in functions offered (design spec §16). Operators
/// (->, ->>, @>) are not functions and are not listed; types are out of scope.
/// </summary>
internal static class PgCompletionVocabulary
{
    public static readonly string[] Keywords =
    [
        "ADD", "ALL", "ALTER", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION", "BEGIN", "BETWEEN",
        "BOTH", "BY", "CASCADE", "CASE", "CAST", "CHECK", "CLUSTER", "COLLATE", "COLUMN", "COMMENT", "COMMIT", "CONCURRENTLY",
        "CONFLICT", "CONSTRAINT", "COPY", "CREATE", "CROSS", "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME",
        "CURRENT_TIMESTAMP", "CURRENT_USER", "DATABASE", "DEFAULT", "DEFERRABLE", "DELETE", "DESC", "DISTINCT", "DO", "DROP",
        "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXISTS", "EXPLAIN", "EXTENSION", "FALSE", "FETCH", "FILTER", "FIRST",
        "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL", "FUNCTION", "GRANT", "GROUP", "GROUPS", "HAVING", "IF", "ILIKE", "IN",
        "INDEX", "INNER", "INSERT", "INTERSECT", "INTERVAL", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST", "LATERAL", "LEADING",
        "LEFT", "LIKE", "LIMIT", "LOCAL", "LOCALTIME", "LOCALTIMESTAMP", "LOCK", "MATERIALIZED", "NATURAL", "NEXT", "NO", "NOT",
        "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OTHERS", "OUTER", "OVER", "OVERLAPS",
        "OWNER", "PARTITION", "PRECEDING", "PRIMARY", "PROCEDURE", "RANGE", "RECURSIVE", "REFERENCES", "REFRESH", "REINDEX",
        "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RETURNS", "REVOKE", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT",
        "SCHEMA", "SELECT", "SEQUENCE", "SESSION_USER", "SET", "SHOW", "SIMILAR", "SOME", "SYMMETRIC", "TABLE", "TABLESAMPLE",
        "TEMP", "TEMPORARY", "THEN", "TIES", "TO", "TRAILING", "TRANSACTION", "TRIGGER", "TRUE", "TRUNCATE", "TYPE", "UNBOUNDED",
        "UNION", "UNIQUE", "UPDATE", "USER", "USING", "VACUUM", "VALUES", "VARIADIC", "VERBOSE", "VIEW", "WHEN", "WHERE",
        "WINDOW", "WITH", "WITHIN", "WITHOUT",
    ];

    public static readonly HashSet<string> KeywordSet = new(Keywords, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reserved (and reserved-can-be-function-or-type) words: an identifier spelled like one must be quoted.</summary>
    public static readonly HashSet<string> ReservedSet = new(
    [
        "ALL", "ANALYSE", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "AUTHORIZATION", "BINARY", "BOTH", "CASE",
        "CAST", "CHECK", "COLLATE", "COLLATION", "COLUMN", "CONCURRENTLY", "CONSTRAINT", "CREATE", "CROSS", "CURRENT_CATALOG",
        "CURRENT_DATE", "CURRENT_ROLE", "CURRENT_SCHEMA", "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT",
        "DEFERRABLE", "DESC", "DISTINCT", "DO", "ELSE", "END", "EXCEPT", "FALSE", "FETCH", "FOR", "FOREIGN", "FREEZE", "FROM",
        "FULL", "GRANT", "GROUP", "HAVING", "ILIKE", "IN", "INITIALLY", "INNER", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN",
        "LATERAL", "LEADING", "LEFT", "LIKE", "LIMIT", "LOCALTIME", "LOCALTIMESTAMP", "NATURAL", "NOT", "NOTNULL", "NULL",
        "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OVERLAPS", "PLACING", "PRIMARY", "REFERENCES", "RETURNING", "RIGHT",
        "SELECT", "SESSION_USER", "SIMILAR", "SOME", "SYMMETRIC", "SYSTEM_USER", "TABLE", "TABLESAMPLE", "THEN", "TO",
        "TRAILING", "TRUE", "UNION", "UNIQUE", "USER", "USING", "VARIADIC", "VERBOSE", "WHEN", "WHERE", "WINDOW", "WITH",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Name and signature; a signature ending in "()" takes no arguments.</summary>
    public static readonly (string Name, string Signature)[] Functions =
    [
        // aggregate
        ("avg", "avg(expr)"), ("count", "count(expr)"), ("sum", "sum(expr)"), ("min", "min(expr)"), ("max", "max(expr)"),
        ("array_agg", "array_agg(expr)"), ("string_agg", "string_agg(expr, delimiter)"), ("bool_and", "bool_and(expr)"),
        ("bool_or", "bool_or(expr)"), ("every", "every(expr)"), ("json_agg", "json_agg(expr)"), ("jsonb_agg", "jsonb_agg(expr)"),
        ("json_object_agg", "json_object_agg(key, value)"), ("jsonb_object_agg", "jsonb_object_agg(key, value)"),
        ("percentile_cont", "percentile_cont(fraction)"), ("percentile_disc", "percentile_disc(fraction)"), ("mode", "mode()"),
        ("stddev", "stddev(expr)"), ("variance", "variance(expr)"),
        // window
        ("row_number", "row_number()"), ("rank", "rank()"), ("dense_rank", "dense_rank()"), ("percent_rank", "percent_rank()"),
        ("cume_dist", "cume_dist()"), ("ntile", "ntile(n)"), ("lag", "lag(value, offset, default)"),
        ("lead", "lead(value, offset, default)"), ("first_value", "first_value(value)"), ("last_value", "last_value(value)"),
        ("nth_value", "nth_value(value, n)"),
        // conditional
        ("coalesce", "coalesce(value, …)"), ("nullif", "nullif(value1, value2)"), ("greatest", "greatest(value, …)"),
        ("least", "least(value, …)"),
        // string
        ("length", "length(text)"), ("char_length", "char_length(text)"), ("lower", "lower(text)"), ("upper", "upper(text)"),
        ("initcap", "initcap(text)"), ("trim", "trim(text)"), ("btrim", "btrim(text, characters)"),
        ("ltrim", "ltrim(text, characters)"), ("rtrim", "rtrim(text, characters)"), ("lpad", "lpad(text, length, fill)"),
        ("rpad", "rpad(text, length, fill)"), ("concat", "concat(value, …)"), ("concat_ws", "concat_ws(sep, value, …)"),
        ("format", "format(formatstr, …)"), ("left", "left(text, n)"), ("right", "right(text, n)"),
        ("substring", "substring(text, start, count)"), ("substr", "substr(text, start, count)"),
        ("position", "position(substring in text)"), ("strpos", "strpos(text, substring)"), ("replace", "replace(text, from, to)"),
        ("split_part", "split_part(text, delimiter, n)"), ("string_to_array", "string_to_array(text, delimiter)"),
        ("regexp_replace", "regexp_replace(text, pattern, replacement, flags)"), ("regexp_match", "regexp_match(text, pattern)"),
        ("regexp_matches", "regexp_matches(text, pattern, flags)"), ("regexp_split_to_table", "regexp_split_to_table(text, pattern)"),
        ("repeat", "repeat(text, n)"), ("reverse", "reverse(text)"), ("md5", "md5(text)"), ("encode", "encode(bytes, format)"),
        ("decode", "decode(text, format)"), ("to_hex", "to_hex(n)"), ("quote_ident", "quote_ident(text)"),
        ("quote_literal", "quote_literal(text)"), ("starts_with", "starts_with(text, prefix)"),
        // math
        ("abs", "abs(x)"), ("ceil", "ceil(x)"), ("floor", "floor(x)"), ("round", "round(x, s)"), ("trunc", "trunc(x, s)"),
        ("mod", "mod(y, x)"), ("div", "div(y, x)"), ("power", "power(a, b)"), ("sqrt", "sqrt(x)"), ("exp", "exp(x)"),
        ("ln", "ln(x)"), ("log", "log(x)"), ("sign", "sign(x)"), ("random", "random()"),
        // date and time
        ("now", "now()"), ("clock_timestamp", "clock_timestamp()"), ("statement_timestamp", "statement_timestamp()"),
        ("transaction_timestamp", "transaction_timestamp()"), ("date_trunc", "date_trunc(field, source)"),
        ("date_part", "date_part(field, source)"), ("extract", "extract(field from source)"), ("age", "age(timestamp, timestamp)"),
        ("to_char", "to_char(value, format)"), ("to_date", "to_date(text, format)"), ("to_timestamp", "to_timestamp(text, format)"),
        ("make_date", "make_date(year, month, day)"), ("make_interval", "make_interval(years, months, weeks, days, hours, mins, secs)"),
        ("date_bin", "date_bin(stride, source, origin)"), ("justify_interval", "justify_interval(interval)"),
        // json
        ("to_json", "to_json(value)"), ("to_jsonb", "to_jsonb(value)"), ("row_to_json", "row_to_json(record)"),
        ("json_build_object", "json_build_object(key, value, …)"), ("jsonb_build_object", "jsonb_build_object(key, value, …)"),
        ("json_build_array", "json_build_array(value, …)"), ("jsonb_build_array", "jsonb_build_array(value, …)"),
        ("jsonb_set", "jsonb_set(target, path, new_value, create_missing)"), ("jsonb_insert", "jsonb_insert(target, path, new_value)"),
        ("jsonb_strip_nulls", "jsonb_strip_nulls(jsonb)"), ("jsonb_pretty", "jsonb_pretty(jsonb)"), ("jsonb_typeof", "jsonb_typeof(jsonb)"),
        ("json_typeof", "json_typeof(json)"), ("jsonb_array_length", "jsonb_array_length(jsonb)"),
        ("json_array_length", "json_array_length(json)"), ("jsonb_array_elements", "jsonb_array_elements(jsonb)"),
        ("jsonb_array_elements_text", "jsonb_array_elements_text(jsonb)"), ("json_array_elements", "json_array_elements(json)"),
        ("jsonb_each", "jsonb_each(jsonb)"), ("jsonb_each_text", "jsonb_each_text(jsonb)"), ("jsonb_object_keys", "jsonb_object_keys(jsonb)"),
        ("jsonb_extract_path", "jsonb_extract_path(from_json, path, …)"),
        ("jsonb_extract_path_text", "jsonb_extract_path_text(from_json, path, …)"),
        ("jsonb_path_query", "jsonb_path_query(target, path)"), ("jsonb_path_exists", "jsonb_path_exists(target, path)"),
        // array
        ("array_length", "array_length(array, dim)"), ("array_append", "array_append(array, element)"),
        ("array_prepend", "array_prepend(element, array)"), ("array_cat", "array_cat(array1, array2)"),
        ("array_remove", "array_remove(array, element)"), ("array_position", "array_position(array, element)"),
        ("array_to_string", "array_to_string(array, delimiter)"), ("cardinality", "cardinality(array)"), ("unnest", "unnest(array)"),
        // set-returning and system
        ("generate_series", "generate_series(start, stop, step)"), ("gen_random_uuid", "gen_random_uuid()"),
        ("current_setting", "current_setting(name)"), ("pg_typeof", "pg_typeof(value)"), ("pg_size_pretty", "pg_size_pretty(bytes)"),
        ("pg_total_relation_size", "pg_total_relation_size(relation)"), ("pg_relation_size", "pg_relation_size(relation)"),
        ("version", "version()"), ("current_database", "current_database()"), ("pg_backend_pid", "pg_backend_pid()"),
        ("txid_current", "txid_current()"), ("pg_sleep", "pg_sleep(seconds)"),
    ];
}
```

- [ ] **Step 5: Implement the engine**

`PG/Query/PgCompletionEngine.cs`:

```csharp
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Query;

/// <summary>
/// Query editor completion (design spec §16). Pure: (text, caret, schema) → the range to replace and
/// up to MaxItems suggestions. A small tokenizer with PgSqlLexer's quoting rules ('…' with '' escapes,
/// E'…' with backslash escapes, $tag$…$tag$, "…" names, -- and nested /* */ comments) finds the
/// statement holding the caret (split on top-level ';'), then the context decides what is offered:
/// <list type="bullet">
/// <item>inside a string, dollar-quoted body, quoted name or comment, in a number, or right after <c>::</c> → nothing;</item>
/// <item>after <c>s.t.</c> → that table's columns; after <c>q.</c> → the columns of the alias/table q names, else the
/// tables of schema q, else the columns of q resolved through the search path;</item>
/// <item>after FROM, JOIN, INTO, UPDATE, TABLE, ONLY, a FROM-list comma, or CREATE INDEX … ON → tables/views, then schemas;</item>
/// <item>otherwise → the statement's tables' columns, its aliases, keywords, functions, tables/views, schemas.</item>
/// </list>
/// Unquoted names match case-insensitively, quoted ones exactly. The identifier before the caret
/// filters: prefix matches first, then substring matches, both case-insensitive, kind order kept.
/// </summary>
public static class PgCompletionEngine
{
    public const int MaxItems = 50;

    private static readonly HashSet<string> TableKeywords = new(["FROM", "JOIN", "INTO", "UPDATE", "TABLE", "ONLY"], StringComparer.OrdinalIgnoreCase);

    // Words that open a clause: scanning back from a ',' to the nearest one tells a FROM list from a SELECT list.
    private static readonly HashSet<string> ClauseKeywords = new(
        ["SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "SET", "VALUES", "ON", "USING", "JOIN", "RETURNING", "WINDOW", "BY"],
        StringComparer.OrdinalIgnoreCase);

    private enum TokenKind
    {
        Word,
        Quoted,
        String,
        Comment,
        Number,
        Punct,
    }

    private readonly record struct Token(TokenKind Kind, int Start, int End, bool Terminated);

    /// <summary>A name as written: a quoted one matches exactly, a bare one case-insensitively.</summary>
    private readonly record struct Ident(string Value, bool Quoted)
    {
        public bool Matches(string actual) =>
            string.Equals(actual, Value, Quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TableRef(PgCompletionObject Object, Ident? Alias);

    public static PgCompletionResult Complete(string text, int caret, PgCompletionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(schema);
        caret = Math.Clamp(caret, 0, text.Length);
        var none = new PgCompletionResult(caret, caret, []);
        var tokens = Tokenize(text);

        // Inside a string, quoted name or comment: an unterminated one runs to the end of the text.
        foreach (var t in tokens)
        {
            if (t.Kind is TokenKind.String or TokenKind.Quoted or TokenKind.Comment
                && t.Start < caret && (caret < t.End || (caret == t.End && !t.Terminated)))
            {
                return none;
            }
        }

        var statement = CurrentStatement(text, tokens, caret);
        var replaceFrom = caret;
        var replaceTo = caret;
        foreach (var t in statement)
        {
            if (t.Start < caret && caret <= t.End)
            {
                if (t.Kind == TokenKind.Number)
                {
                    return none;
                }

                if (t.Kind == TokenKind.Word)
                {
                    replaceFrom = t.Start;
                    replaceTo = t.End;
                }
            }
        }

        var prefix = text[replaceFrom..caret];
        var code = statement.Where(t => t.Kind != TokenKind.Comment).ToList();
        var before = code.Where(t => t.End <= replaceFrom).ToList();
        var refs = TableRefs(text, code, schema);

        var candidates = Candidates(text, before, code, refs, schema);
        if (candidates is null)
        {
            return new PgCompletionResult(replaceFrom, replaceTo, []);
        }

        // Accepting an item equal to the word just typed would change nothing (or only its case), and
        // would eat the Enter meant as a newline -- leave it out.
        var typed = caret == replaceTo ? text[replaceFrom..replaceTo] : null;
        var items = candidates
            .Select(item => (Item: item, Rank: Rank(item.Label, prefix)))
            .Where(x => x.Rank >= 0 && !string.Equals(x.Item.InsertText, typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Rank)
            .Take(MaxItems)
            .Select(x => x.Item)
            .ToList();
        return new PgCompletionResult(replaceFrom, replaceTo, items);
    }

    /// <summary>The ordered candidates for the context, or null when nothing applies (an unknown qualifier).</summary>
    private static List<PgCompletionItem>? Candidates(
        string text, List<Token> before, List<Token> code, List<TableRef> refs, PgCompletionSchema schema)
    {
        var previous = before.Count > 0 ? before[^1] : (Token?)null;

        if (previous is { Kind: TokenKind.Punct } cast && Slice(text, cast) == "::")
        {
            return []; // a type name goes here; types are out of scope
        }

        if (previous is { Kind: TokenKind.Punct } dot && text[dot.Start] == '.')
        {
            return QualifiedCandidates(text, before, refs, schema);
        }

        if (previous is { } prev && IsTablePosition(text, prev, before, code))
        {
            return [.. TableItems(schema, schema.Objects, qualify: true), .. SchemaItems(schema)];
        }

        var items = new List<PgCompletionItem>();
        foreach (var obj in refs.Select(r => r.Object).Distinct())
        {
            items.AddRange(ColumnItems(obj));
        }

        items.AddRange(refs.Where(r => r.Alias is not null)
            .Select(r => (Alias: r.Alias!.Value.Value, r.Object))
            .DistinctBy(r => r.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PgCompletionItem(r.Alias, Identifier(r.Alias), PgCompletionKind.Alias, r.Object.Name)));
        items.AddRange(PgCompletionVocabulary.Keywords.Select(k => new PgCompletionItem(k, k, PgCompletionKind.Keyword, null)));
        items.AddRange(PgCompletionVocabulary.Functions.Select(f => new PgCompletionItem(
            f.Name, f.Signature.EndsWith("()", StringComparison.Ordinal) ? f.Name + "()" : f.Name + "(", PgCompletionKind.Function, f.Signature)));
        items.AddRange(TableItems(schema, schema.Objects, qualify: true));
        items.AddRange(SchemaItems(schema));
        return items;
    }

    /// <summary>Candidates after <c>q.</c> (before[^1] is the dot).</summary>
    private static List<PgCompletionItem>? QualifiedCandidates(string text, List<Token> before, List<TableRef> refs, PgCompletionSchema schema)
    {
        if (before.Count < 2 || Name(text, before[^2]) is not { } qualifier)
        {
            return null;
        }

        // schema.table.
        if (before.Count >= 4 && IsPunct(text, before[^3], '.') && Name(text, before[^4]) is { } schemaName)
        {
            var obj = schema.Objects.FirstOrDefault(o => schemaName.Matches(o.Schema) && qualifier.Matches(o.Name));
            return obj is null ? null : [.. ColumnItems(obj)];
        }

        // An alias wins over a table of the statement, which wins over a schema of the same name.
        var target = refs.FirstOrDefault(r => r.Alias is { } alias && qualifier.Matches(alias.Value))?.Object
            ?? refs.FirstOrDefault(r => qualifier.Matches(r.Object.Name))?.Object;
        if (target is not null)
        {
            return [.. ColumnItems(target)];
        }

        var schemaMatch = schema.Schemas.FirstOrDefault(s => qualifier.Matches(s));
        if (schemaMatch is not null)
        {
            return [.. TableItems(schema, schema.Objects.Where(o => o.Schema == schemaMatch), qualify: false)];
        }

        return Resolve(schema, qualifier) is { } resolved ? [.. ColumnItems(resolved)] : null;
    }

    private static bool IsTablePosition(string text, Token previous, List<Token> before, List<Token> code)
    {
        if (previous.Kind == TokenKind.Word)
        {
            var word = Slice(text, previous);
            if (TableKeywords.Contains(word))
            {
                return true;
            }

            // CREATE [UNIQUE] INDEX … ON <table>, never a join's ON.
            return word.Equals("ON", StringComparison.OrdinalIgnoreCase)
                && code.Count > 0 && IsWord(text, code[0], "CREATE")
                && before.Any(t => IsWord(text, t, "INDEX"));
        }

        if (previous.Kind == TokenKind.Punct && text[previous.Start] == ',')
        {
            var depth = 0;
            for (var i = before.Count - 2; i >= 0; i--)
            {
                var t = before[i];
                if (t.Kind == TokenKind.Punct)
                {
                    depth += text[t.Start] switch { ')' => 1, '(' => -1, _ => 0 };
                    if (depth < 0)
                    {
                        return false;
                    }
                }
                else if (depth == 0 && t.Kind == TokenKind.Word && ClauseKeywords.Contains(Slice(text, t)))
                {
                    return Slice(text, t).Equals("FROM", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return false;
    }

    /// <summary>Tables named after FROM / JOIN / UPDATE / INTO (and further FROM-list entries) that exist in the schema, with any alias.</summary>
    private static List<TableRef> TableRefs(string text, List<Token> code, PgCompletionSchema schema)
    {
        var refs = new List<TableRef>();
        for (var i = 0; i < code.Count; i++)
        {
            var keyword = code[i].Kind == TokenKind.Word ? Slice(text, code[i]).ToUpperInvariant() : "";
            if (keyword is not ("FROM" or "JOIN" or "UPDATE" or "INTO"))
            {
                continue;
            }

            var j = i + 1;
            while (j < code.Count)
            {
                if (IsWord(text, code[j], "ONLY"))
                {
                    j++;
                    if (j >= code.Count)
                    {
                        break;
                    }
                }

                var next = ReadRef(text, code, j, schema, refs);
                if (next < 0 || keyword != "FROM" || next >= code.Count || !IsPunct(text, code[next], ','))
                {
                    break;
                }

                j = next + 1;
            }
        }

        return refs;
    }

    /// <summary>Reads <c>[schema.]name [[AS] alias]</c> at <paramref name="j"/>; returns the index after it, or -1 when no name is there.</summary>
    private static int ReadRef(string text, List<Token> code, int j, PgCompletionSchema schema, List<TableRef> refs)
    {
        if (Name(text, code[j]) is not { } first)
        {
            return -1;
        }

        PgCompletionObject? obj;
        if (j + 2 < code.Count && IsPunct(text, code[j + 1], '.') && Name(text, code[j + 2]) is { } second)
        {
            obj = schema.Objects.FirstOrDefault(o => first.Matches(o.Schema) && second.Matches(o.Name));
            j += 2;
        }
        else
        {
            obj = Resolve(schema, first);
        }

        j++;
        Ident? alias = null;
        var k = j < code.Count && IsWord(text, code[j], "AS") ? j + 1 : j;
        if (k < code.Count
            && ((code[k].Kind == TokenKind.Quoted && code[k].Terminated)
                || (code[k].Kind == TokenKind.Word && !PgCompletionVocabulary.KeywordSet.Contains(Slice(text, code[k])))))
        {
            alias = Name(text, code[k]);
            j = k + 1;
        }

        if (obj is not null)
        {
            refs.Add(new TableRef(obj, alias));
        }

        return j;
    }

    /// <summary>An unqualified relation name: the first search-path schema (in path order) that has it.</summary>
    private static PgCompletionObject? Resolve(PgCompletionSchema schema, Ident name)
    {
        foreach (var path in schema.SearchPath)
        {
            var obj = schema.Objects.FirstOrDefault(o => o.Schema == path && name.Matches(o.Name));
            if (obj is not null)
            {
                return obj;
            }
        }

        return null;
    }

    private static IEnumerable<PgCompletionItem> ColumnItems(PgCompletionObject obj) =>
        obj.Columns.Select(c => new PgCompletionItem(
            c.Name, Identifier(c.Name), PgCompletionKind.Column, $"{c.Type}{(c.PrimaryKey ? " PK" : "")} · {obj.Name}"));

    /// <summary>qualify: a relation whose schema is not on the search path is shown and inserted as schema.name.</summary>
    private static IEnumerable<PgCompletionItem> TableItems(PgCompletionSchema schema, IEnumerable<PgCompletionObject> objects, bool qualify) =>
        objects.Select(o =>
        {
            var offPath = qualify && !schema.SearchPath.Contains(o.Schema);
            var label = offPath ? $"{o.Schema}.{o.Name}" : o.Name;
            var insert = offPath ? $"{Identifier(o.Schema)}.{Identifier(o.Name)}" : Identifier(o.Name);
            var view = o.Kind is PgObjectKind.View or PgObjectKind.MaterializedView;
            var columns = o.Columns.Count == 1 ? "1 column" : $"{o.Columns.Count} columns";
            return new PgCompletionItem(label, insert, view ? PgCompletionKind.View : PgCompletionKind.Table, $"{KindText(o.Kind)} · {o.Schema} · {columns}");
        });

    private static IEnumerable<PgCompletionItem> SchemaItems(PgCompletionSchema schema) =>
        schema.Schemas.Select(s => new PgCompletionItem(s, Identifier(s), PgCompletionKind.Schema, "schema"));

    private static string KindText(PgObjectKind kind) => kind switch
    {
        PgObjectKind.View => "view",
        PgObjectKind.MaterializedView => "materialized view",
        PgObjectKind.PartitionedTable => "partitioned table",
        PgObjectKind.ForeignTable => "foreign table",
        _ => "table",
    };

    /// <summary>Bare when PostgreSQL would read it back unchanged (lower-case, not reserved), else quoted.</summary>
    private static string Identifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetterLower(name[0]) || name[0] == '_')
            && name.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch is '_' or '$')
            && !PgCompletionVocabulary.ReservedSet.Contains(name)
            ? name
            : PgIdent.Quote(name);

    private static int Rank(string label, string prefix) =>
        prefix.Length == 0 || label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0
        : label.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1
        : -1;

    /// <summary>The tokens of the statement holding the caret; strings and comments are whole tokens, so every ';' token is top-level.</summary>
    private static List<Token> CurrentStatement(string text, List<Token> tokens, int caret)
    {
        var startIndex = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Punct || text[t.Start] != ';')
            {
                continue;
            }

            if (caret <= t.Start)
            {
                return tokens[startIndex..i];
            }

            startIndex = i + 1;
        }

        return tokens[startIndex..];
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            Token token;
            if (ch == '-' && Next(text, i) == '-')
            {
                var newline = text.IndexOf('\n', i + 2);
                token = newline < 0 ? new(TokenKind.Comment, i, text.Length, false) : new(TokenKind.Comment, i, newline + 1, true);
            }
            else if (ch == '/' && Next(text, i) == '*')
            {
                token = BlockComment(text, i);
            }
            else if (ch is 'E' or 'e' && Next(text, i) == '\'')
            {
                token = Quote(text, i, i + 1, TokenKind.String, backslash: true);
            }
            else if (ch == '\'')
            {
                token = Quote(text, i, i, TokenKind.String, backslash: false);
            }
            else if (ch == '"')
            {
                token = Quote(text, i, i, TokenKind.Quoted, backslash: false);
            }
            else if (ch == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                token = close < 0 ? new(TokenKind.String, i, text.Length, false) : new(TokenKind.String, i, close + tag.Length, true);
            }
            else if (ch == ':' && Next(text, i) == ':')
            {
                token = new(TokenKind.Punct, i, i + 2, true);
            }
            else if (char.IsAsciiDigit(ch) || (ch == '.' && Next(text, i) is { } digit && char.IsAsciiDigit(digit)))
            {
                var j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '.'))
                {
                    j++;
                }

                token = new(TokenKind.Number, i, j, true);
            }
            else if (char.IsLetter(ch) || ch == '_')
            {
                var j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '$'))
                {
                    j++;
                }

                token = new(TokenKind.Word, i, j, true);
            }
            else
            {
                token = new(TokenKind.Punct, i, i + 1, true);
            }

            tokens.Add(token);
            i = token.End;
        }

        return tokens;
    }

    /// <summary>A quoted section starting at <paramref name="open"/> (a doubled quote escapes; with backslash, \x escapes too).</summary>
    private static Token Quote(string text, int start, int open, TokenKind kind, bool backslash)
    {
        var quote = text[open];
        var i = open + 1;
        while (i < text.Length)
        {
            if (backslash && text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return new Token(kind, start, i + 1, true);
            }

            i++;
        }

        return new Token(kind, start, text.Length, false);
    }

    /// <summary>/* … */ with nesting, as PostgreSQL reads it.</summary>
    private static Token BlockComment(string text, int start)
    {
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            if (text[i] == '/' && Next(text, i) == '*')
            {
                depth++;
                i += 2;
            }
            else if (text[i] == '*' && Next(text, i) == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                {
                    return new Token(TokenKind.Comment, start, i, true);
                }
            }
            else
            {
                i++;
            }
        }

        return new Token(TokenKind.Comment, start, text.Length, false);
    }

    /// <summary>"$$" or "$tag$" at <paramref name="i"/> (tag: a letter or _ then letters, digits, _); null otherwise -- "$1" is a parameter.</summary>
    private static string? DollarTag(string text, int i)
    {
        var j = i + 1;
        if (j < text.Length && (char.IsLetter(text[j]) || text[j] == '_'))
        {
            while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
            {
                j++;
            }
        }

        return j < text.Length && text[j] == '$' ? text[i..(j + 1)] : null;
    }

    /// <summary>The name a Word or terminated quoted identifier stands for; null for anything else.</summary>
    private static Ident? Name(string text, Token t)
    {
        if (t.Kind == TokenKind.Word)
        {
            return new Ident(Slice(text, t), false);
        }

        if (t.Kind != TokenKind.Quoted || !t.Terminated || t.End - t.Start < 2)
        {
            return null;
        }

        return new Ident(text[(t.Start + 1)..(t.End - 1)].Replace("\"\"", "\"", StringComparison.Ordinal), true);
    }

    private static char? Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : null;

    private static string Slice(string text, Token t) => text[t.Start..t.End];

    private static bool IsWord(string text, Token t, string word) =>
        t.Kind == TokenKind.Word && Slice(text, t).Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsPunct(string text, Token t, char ch) => t.Kind == TokenKind.Punct && text[t.Start] == ch;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/SbConsole.Plugins.Postgres.Tests --filter "FullyQualifiedName~PgCompletionEngineTests"`
Expected: PASS. If a single expectation fails, fix the engine (not the test) unless the test contradicts spec §16 — then stop and ask.

- [ ] **Step 7: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test tests/SbConsole.Plugins.Postgres.Tests`
Expected: 0 warnings, all PASS.

```bash
git add src/Plugins/Databases/SbConsole.Plugins.Postgres/Query/PgCompletionItems.cs src/Plugins/Databases/SbConsole.Plugins.Postgres/Query/PgCompletionVocabulary.cs src/Plugins/Databases/SbConsole.Plugins.Postgres/Query/PgCompletionEngine.cs tests/SbConsole.Plugins.Postgres.Tests/Query/PgCompletionEngineTests.cs
git commit -m "feat(postgres): query completion engine

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Popup module, styles and Query page wiring

**Files:**
- Create: `PG/wwwroot/postgres-completion.js`
- Modify: `PG/wwwroot/postgres.css` (append)
- Modify: `PG/Pages/Query.razor`
- Test: `PGT/Pages/QueryCompletionPageTests.cs`

**Interfaces:**
- Consumes: `GetCompletionSchemaQueryHandler.HandleAsync(Guid, string?, CancellationToken)` (Task 1), `PgCompletionEngine.Complete` / `PgCompletionResult` / `PgCompletionSchema.Empty` (Tasks 1–2), `PgSqlLexer.Split(string)` → statements with `FirstKeyword` (existing).
- Produces: JS module exports `attach(element, dotnetRef)` / `detach(element)` — `element` is the textarea or any ancestor of it; the page's `[JSInvokable] PgCompletionResult GetCompletions(string text, int caret)`.

- [ ] **Step 1: Write the failing page tests**

`PGT/Pages/QueryCompletionPageTests.cs`:

```csharp
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Query;
using QueryPage = SbConsole.Plugins.Postgres.Pages.Query;

namespace SbConsole.Plugins.Postgres.Tests.Pages;

/// <summary>Query IntelliSense on the page (design spec §16): schema per database, the JS attach, the JSInvokable, DDL refresh.</summary>
public class QueryCompletionPageTests : PgPageTestBase
{
    private const string CompletionModulePath = "./_content/SbConsole.Plugins.Postgres/postgres-completion.js";

    private static readonly PgCompletionSchema Schema = new(["public"], ["public"],
        [new("public", "orders", PgObjectKind.Table, [new("id", "bigint", true), new("status", "text", false)])]);

    private readonly BunitJSModuleInterop _module;

    public QueryCompletionPageTests()
    {
        Operations.ListDatabasesAsync(Secret, Arg.Any<CancellationToken>()).Returns(["app"]);
        Operations.GetCompletionSchemaAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Schema);
        Operations.RunQueryAsync(Secret, "app", Arg.Any<PgQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PgQueryResult([], [], false, 0, TimeSpan.FromSeconds(1), 1, "OK"));
        _module = JSInterop.SetupModule(CompletionModulePath);
        _module.SetupVoid("attach", _ => true).SetVoidResult();
        _module.SetupVoid("detach", _ => true).SetVoidResult();
    }

    private IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderQuery()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/p/postgres/query?connectionId={ConnectionId}");
        var cut = RenderWithPopovers(b =>
        {
            b.OpenComponent<QueryPage>(0);
            b.CloseComponent();
        });
        cut.WaitForElement(".pg-editor");
        return cut;
    }

    private DotNetObjectReference<QueryPage> AttachedReference(IRenderedComponent<Bunit.Rendering.ContainerFragment> cut)
    {
        cut.WaitForAssertion(() => _module.VerifyInvoke("attach"));
        return (DotNetObjectReference<QueryPage>)_module.VerifyInvoke("attach").Arguments[1]!;
    }

    [Fact]
    public void The_schema_loads_for_the_selected_database()
    {
        var cut = RenderQuery();

        cut.WaitForAssertion(() => _ = Operations.Received(1).GetCompletionSchemaAsync(Secret, "app", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void The_completion_module_is_attached_to_the_editor_with_a_reference_to_the_page()
    {
        var cut = RenderQuery();

        var reference = AttachedReference(cut);

        _module.VerifyInvoke("attach").Arguments[0].Should().BeOfType<ElementReference>();
        reference.Value.Should().BeSameAs(cut.FindComponent<QueryPage>().Instance);
    }

    [Fact]
    public void The_js_invokable_returns_the_engine_result_for_the_loaded_schema()
    {
        var cut = RenderQuery();
        var page = AttachedReference(cut).Value;
        const string text = "SELECT o. FROM orders o";

        cut.WaitForAssertion(() => page.GetCompletions(text, 9).Items.Should().NotBeEmpty());

        page.GetCompletions(text, 9).Should().BeEquivalentTo(PgCompletionEngine.Complete(text, 9, Schema));
        page.GetCompletions(text, 9).Items.Select(i => i.Label).Should().Equal("id", "status");
    }

    [Fact]
    public void A_failed_schema_load_leaves_keywords_working_and_shows_no_error()
    {
        Operations.GetCompletionSchemaAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PgCompletionSchema>>(_ => throw new InvalidOperationException("nope"));
        var cut = RenderQuery();
        var page = AttachedReference(cut).Value;
        cut.WaitForAssertion(() => _ = Operations.Received(1).GetCompletionSchemaAsync(Secret, "app", Arg.Any<CancellationToken>()));

        cut.FindAll(".pg-query-error").Should().BeEmpty();
        page.GetCompletions("SEL", 3).Items.Select(i => i.Label).Should().Contain("SELECT");
        page.GetCompletions("SELECT * FROM ", 14).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CREATE TABLE scratch (a int)", 2)]
    [InlineData("alter table orders add column note text", 2)]
    [InlineData("UPDATE orders SET status = 'x'", 1)]
    public void The_schema_reloads_after_a_committed_write_only_when_it_ran_ddl(string sql, int loads)
    {
        Confirmation.ConfirmAsync("Enter write mode on", ConnectionName, true, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(true);
        var cut = RenderQuery();
        cut.WaitForAssertion(() => _ = Operations.Received(1).GetCompletionSchemaAsync(Secret, "app", Arg.Any<CancellationToken>()));

        cut.FindAll(".pg-mode-toggle .mud-toggle-item").First(i => i.TextContent.Contains("Write")).Click();
        cut.WaitForElement(".pg-write-banner");
        cut.Find(".pg-editor textarea").Input(sql);
        cut.WaitForState(() => !cut.Find(".pg-run").HasAttribute("disabled"));
        cut.Find(".pg-run").Click();
        cut.WaitForElement(".pg-status").TextContent.Should().Contain("committed");

        cut.WaitForAssertion(() => _ = Operations.Received(loads).GetCompletionSchemaAsync(Secret, "app", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task The_module_is_detached_on_dispose()
    {
        var cut = RenderQuery();
        AttachedReference(cut);

        await DisposeComponentsAsync();

        _module.VerifyInvoke("detach");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/SbConsole.Plugins.Postgres.Tests --filter "FullyQualifiedName~QueryCompletionPageTests"`
Expected: build FAIL — `Query` has no `GetCompletions`.

- [ ] **Step 3: Create the JS module from the SQLite one**

```bash
cp src/Plugins/Databases/SbConsole.Plugins.Sqlite/wwwroot/sqlite-completion.js src/Plugins/Databases/SbConsole.Plugins.Postgres/wwwroot/postgres-completion.js
sed -i '' -e 's/sql-complete/pg-complete/g' -e 's/sql-is-active/pg-is-active/g' src/Plugins/Databases/SbConsole.Plugins.Postgres/wwwroot/postgres-completion.js
```

Then edit `postgres-completion.js`:

Replace the header comment (everything above `const DEBOUNCE_MS`) with:

```js
// Query editor completion popup (design spec §16; adapted from the SQLite plugin's sqlite-completion.js).
// The suggestions come from the server: the Query page's [JSInvokable] GetCompletions runs
// PgCompletionEngine. This module only asks for them (debounced, stale answers dropped), renders the
// popup at the caret (mirror-div technique), handles the keys and inserts the accepted text. No editor
// library -- MudTextField's <textarea> stays.
//
// Keys while the popup is open: ↑/↓ move, Enter or Tab accept, Esc closes. Ctrl/⌘+Enter is left
// alone (after closing the popup) so the page's run shortcut on the editor wrapper still runs the
// query; plain Enter is swallowed only when it accepts a suggestion. ARIA: the textarea keeps its
// textbox role (aria-autocomplete, aria-controls, aria-activedescendant are valid on it); the popup
// is a listbox of options.
```

Replace the `BADGES` line with:

```js
const BADGES = { Keyword: "kw", Function: "fn", Schema: "S", Table: "T", View: "V", Column: "C", Alias: "A" };
```

Replace the `attach` and `detach` functions with:

```js
// The Query page passes the editor's wrapper; MudTextField renders the <textarea> inside it.
function textareaOf(element) {
    return element && (element.matches("textarea") ? element : element.querySelector("textarea"));
}

export function attach(element, dotnet) {
    const textarea = textareaOf(element);
    if (textarea && !controllers.has(textarea)) {
        controllers.set(textarea, new Completion(textarea, dotnet));
    }
}

export function detach(element) {
    const textarea = textareaOf(element);
    const controller = textarea && controllers.get(textarea);
    if (controller) {
        controller.dispose();
        controllers.delete(textarea);
    }
}
```

Verify no SQLite leftovers: `grep -n "sql-\|sqlite\|Sqlite" src/Plugins/Databases/SbConsole.Plugins.Postgres/wwwroot/postgres-completion.js` → only the "adapted from … sqlite-completion.js" header line.

- [ ] **Step 4: Append the popup styles**

Append to `PG/wwwroot/postgres.css`:

```css
/* ---- Query completion popup (design spec §16) ----
   postgres-completion.js appends it to <body> (outside .pg-page), so it uses --mud-palette-* directly. */
.pg-complete {
    position: fixed; z-index: 1500; min-width: 240px; max-width: min(520px, calc(100vw - 16px)); max-height: 264px;
    overflow-y: auto; padding: 4px; box-sizing: border-box; border-radius: 8px;
    background: var(--mud-palette-surface); color: var(--mud-palette-text-primary);
    border: 1px solid var(--mud-palette-lines-default);
    box-shadow: 0 8px 24px color-mix(in srgb, var(--mud-palette-black) 30%, transparent);
    font-family: var(--mud-typography-mono-family, ui-monospace, SFMono-Regular, Menlo, monospace); font-size: 12.5px; line-height: 1.5;
}
.pg-complete[hidden] { display: none; }
.pg-complete-item {
    display: flex; align-items: baseline; gap: 8px; padding: 3px 8px; border-radius: 5px; cursor: pointer; white-space: nowrap;
}
.pg-complete-item:hover { background: var(--mud-palette-action-default-hover); }
.pg-complete-item.pg-is-active { background: color-mix(in srgb, var(--mud-palette-primary) 20%, transparent); }
.pg-complete-kind {
    --pg-kind: var(--mud-palette-text-secondary);
    flex: none; min-width: 22px; box-sizing: border-box; padding: 0 4px; border-radius: 4px; text-align: center;
    font-size: 10.5px; font-weight: 500; color: var(--pg-kind); background: color-mix(in srgb, var(--pg-kind) 16%, transparent);
}
.pg-complete-kind-table, .pg-complete-kind-view { --pg-kind: var(--mud-palette-primary); }
.pg-complete-kind-schema { --pg-kind: var(--mud-palette-secondary); }
.pg-complete-kind-column { --pg-kind: var(--mud-palette-info); }
.pg-complete-kind-alias { --pg-kind: var(--mud-palette-warning); }
.pg-complete-kind-function { --pg-kind: var(--mud-palette-success); }
.pg-complete-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.pg-complete-detail {
    margin-left: auto; padding-left: 16px; min-width: 0; overflow: hidden; text-overflow: ellipsis;
    color: var(--mud-palette-text-secondary); font-size: 11px;
}
```

- [ ] **Step 5: Wire the page**

In `PG/Pages/Query.razor`:

5a. After `@inject ListQueryHistoryQueryHandler ListHistory` add:

```razor
@inject GetCompletionSchemaQueryHandler CompletionSchemaQuery
```

5b. After `private const string QueryPath = "/p/postgres/query";` add:

```csharp
    private const string CompletionModulePath = "./_content/SbConsole.Plugins.Postgres/postgres-completion.js";

    // A committed write whose statements start with one of these may have changed the schema (§16).
    private static readonly HashSet<string> DdlKeywords = new(["CREATE", "ALTER", "DROP", "COMMENT", "TRUNCATE"], StringComparer.Ordinal);
```

5c. After `private DotNetObjectReference<Query>? _self;` add:

```csharp
    private IJSObjectReference? _completionModule;
    private DotNetObjectReference<Query>? _completionRef;
    private bool _completionAttached;
    private bool _attachingCompletion;
    private PgCompletionSchema _schema = PgCompletionSchema.Empty;
    private int _schemaGen;
```

5d. Replace the whole existing `OnAfterRenderAsync` method with the following (the shortcut body is unchanged, only moved into `RegisterShortcutAsync`):

```csharp
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _connection is null)
        {
            return;
        }

        await RegisterShortcutAsync();
        await AttachCompletionAsync();
    }

    private async Task RegisterShortcutAsync()
    {
        if (_shortcutRegistered || _registering || _disposed)
        {
            return;
        }

        _registering = true;
        DotNetObjectReference<Query>? self = null;
        try
        {
            var module = _module ?? await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            if (_disposed)
            {
                // Disposed while the import was in flight: DisposeAsync never saw this module.
                if (!ReferenceEquals(module, _module))
                {
                    await module.DisposeAsync();
                }

                return;
            }

            _module = module;
            self = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("registerRunShortcut", _editorWrap, self);
            if (_disposed)
            {
                self.Dispose();
                return;
            }

            _self = self;
            self = null;
            _shortcutRegistered = true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            // The shortcut is a convenience; the Run button still works. (TaskCanceledException is an OperationCanceledException.)
        }
        finally
        {
            self?.Dispose();
            _registering = false;
        }
    }

    // ---- completion (§16) ------------------------------------------------------------------------------------

    private async Task AttachCompletionAsync()
    {
        if (_completionAttached || _attachingCompletion || _disposed)
        {
            return;
        }

        _attachingCompletion = true;
        DotNetObjectReference<Query>? reference = null;
        try
        {
            var module = _completionModule ?? await JS.InvokeAsync<IJSObjectReference>("import", CompletionModulePath);
            if (_disposed)
            {
                if (!ReferenceEquals(module, _completionModule))
                {
                    await module.DisposeAsync();
                }

                return;
            }

            _completionModule = module;
            reference = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("attach", _editorWrap, reference);
            if (_disposed)
            {
                return;
            }

            _completionRef = reference;
            reference = null;
            _completionAttached = true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            // No completion popup (script failed to load, or the page is already gone): the editor still works.
        }
        finally
        {
            reference?.Dispose();
            _attachingCompletion = false;
        }
    }

    /// <summary>Called by postgres-completion.js for every (debounced) request; the engine is pure and fast.</summary>
    [JSInvokable]
    public PgCompletionResult GetCompletions(string text, int caret) => PgCompletionEngine.Complete(text ?? "", caret, _schema);

    /// <summary>
    /// Loads the selected database's completion schema. Never shows an error (the handler logs it):
    /// completion just works without tables. keepCurrent keeps the old schema until the new one arrives (DDL refresh).
    /// </summary>
    private async Task LoadSchemaAsync(bool keepCurrent)
    {
        if (!keepCurrent)
        {
            _schema = PgCompletionSchema.Empty;
        }

        if (_connection is not { } connection || _disposed)
        {
            return;
        }

        var generation = ++_schemaGen;
        var token = _cts.Token;
        try
        {
            var result = await CompletionSchemaQuery.HandleAsync(connection.Id, DatabaseArg, token);
            if (generation == _schemaGen && !_disposed && result is { IsSuccess: true, Value: { } schema })
            {
                _schema = schema;
            }
        }
        catch (OperationCanceledException)
        {
            // Switched connection/database or left the page: a newer load (or nothing) takes over.
        }
    }
```

5e. In `OnConnectionChangedAsync`, inside the `if (_connection is not null && _connection.Id != connection.Id)` block, after `_history = [];` add:

```csharp
            _schema = PgCompletionSchema.Empty;
            _schemaGen++;
```

5f. In `OnDatabaseChangedAsync`, after the final `UpdateUrl();` add:

```csharp
        await LoadSchemaAsync(keepCurrent: false);
```

5g. In `RunCoreAsync`, replace the success tail

```csharp
        _result = run.Result;
        _status = BuildStatus(run, write);
        await LoadHistoryAsync();
    }
```

with

```csharp
        _result = run.Result;
        _status = BuildStatus(run, write);
        if (write && PgSqlLexer.Split(sql).Any(s => DdlKeywords.Contains(s.FirstKeyword)))
        {
            // Not awaited: Run is released now; the popup picks the new tables up when the load lands.
            _ = LoadSchemaAsync(keepCurrent: true);
        }

        await LoadHistoryAsync();
    }
```

5h. In `DisposeAsync`, replace the final `_self?.Dispose();` with:

```csharp
        if (_completionModule is not null)
        {
            try
            {
                if (_completionAttached)
                {
                    await _completionModule.InvokeVoidAsync("detach", _editorWrap);
                }

                await _completionModule.DisposeAsync();
            }
            catch (Exception ex) when (ex is JSDisconnectedException or JSException or InvalidOperationException)
            {
                // The circuit is already gone; nothing to release.
            }
        }

        _self?.Dispose();
        _completionRef?.Dispose();
```

5i. Update the comment on line 1 of `Query.razor` to mention completion:

```razor
@* Mockup 1c (read-only run with history, and Write on PROD with a marked error line). Completion (§16): postgres-completion.js asks GetCompletions (PgCompletionEngine) against the per-database schema. *@
```

- [ ] **Step 6: Run the page tests**

Run: `dotnet test tests/SbConsole.Plugins.Postgres.Tests --filter "FullyQualifiedName~QueryCompletionPageTests|FullyQualifiedName~QueryPageTests"`
Expected: PASS — including the existing `QueryPageTests` (notably `The_run_shortcut_is_registered_on_render_and_unregistered_on_dispose`).

- [ ] **Step 7: Gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings; whole solution PASS.

```bash
git add src/Plugins/Databases/SbConsole.Plugins.Postgres/wwwroot/postgres-completion.js src/Plugins/Databases/SbConsole.Plugins.Postgres/wwwroot/postgres.css src/Plugins/Databases/SbConsole.Plugins.Postgres/Pages/Query.razor tests/SbConsole.Plugins.Postgres.Tests/Pages/QueryCompletionPageTests.cs
git commit -m "feat(postgres): query editor completion popup

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Verify in the running app and record the decision

**Files:**
- Modify: `docs/design.md` (append §20)

- [ ] **Step 1: Run the app against the local Postgres**

```bash
docker compose --profile postgres up -d
```

Start the app with the `.claude/launch.json` `sbconsole-web` config (or `dotnet run --project src/SbConsole.Web --launch-profile http`), with a Postgres connection to `localhost:5433` (see `docker/README.md`). Open `/p/postgres/query`.

- [ ] **Step 2: Exercise the popup by hand and confirm each behaviour**

1. Type `sel` → popup shows `SELECT`; Enter inserts `SELECT`.
2. `SELECT * FROM ` → tables, then schemas; off-search-path tables appear as `schema.table`.
3. `SELECT o. FROM orders o` with the caret after `o.` → that table's columns.
4. Ctrl+Space on an empty spot opens the popup; ↑/↓ move; Tab accepts; Esc closes.
5. With the popup open, Ctrl/⌘+Enter runs the query (popup closes).
6. Typing inside `'…'`, `$$…$$` or `-- …` shows no popup.
7. Switch database in the picker → `FROM ` offers the new database's tables.
8. Toggle the app theme light/dark → popup colours follow.
9. In Write mode run `CREATE TABLE zz_tmp (a int)`; then `SELECT * FROM zz` offers `zz_tmp`. Drop it afterwards (`DROP TABLE zz_tmp`).

If any step fails, fix it (with a regression test where the behaviour lives in C#) before continuing.

- [ ] **Step 3: Append the design-doc entry**

Append to `docs/design.md`:

```markdown

## 20. PostgreSQL query IntelliSense (2026-10-14)

Completion in the Postgres Query editor (spec: `docs/superpowers/specs/2026-10-14-postgres-plugin-design.md`
§16; plan: `docs/superpowers/plans/2026-10-14-postgres-query-intellisense.md`), reversing that spec's
§15 non-goal. Same shape as the SQLite plugin's (§15 / its spec §12): a pure C# `PgCompletionEngine`
answers the page's `[JSInvokable] GetCompletions`, and `postgres-completion.js` only renders the
popup — no editor library. The engine and popup are a **port, not shared code**: plugins reference
only the Sdk, and the dialects differ (dollar quoting, `E''`, schemas, search path, `::`). Hoist a
shared engine/popup into the Sdk or host only when a third plugin needs completion. The schema is
one read-only catalog query per (connection, database) (`GetCompletionSchemaQueryHandler`, not
audited, capped at 2,000 objects × 200 columns), reloaded after a committed write that ran DDL.
Suggestions: keywords, built-in functions, schemas, tables/views/matviews, columns, aliases;
user-defined functions, types and enum labels are out of scope.
```

- [ ] **Step 4: Final gate and commit**

Run: `dotnet build -warnaserror && dotnet test`
Expected: 0 warnings; all PASS.

```bash
git add docs/design.md
git commit -m "docs: record Postgres query IntelliSense (§20)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
