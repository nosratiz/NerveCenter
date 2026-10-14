using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Pages;

/// <summary>Query IntelliSense on the page (design spec §12): schema per connection/backup, the JS attach, the JSInvokable.</summary>
public class QueryCompletionPageTests : SqlitePageTestBase
{
    private const string ModulePath = "./_content/SbConsole.Plugins.Sqlite/sqlite-completion.js";
    private const string BackupFile = "reports-20261012-020000.db";

    private static readonly CompletionSchema Schema = new(
    [
        new CompletionObject("events", SchemaObjectType.Table,
            [new CompletionColumn("id", "INTEGER", true), new CompletionColumn("name", "TEXT", false)]),
    ], false);

    private static readonly CompletionSchema SchemaAfterDdl = new(
    [
        new CompletionObject("events", SchemaObjectType.Table, [new CompletionColumn("id", "INTEGER", true)]),
        new CompletionObject("scratch", SchemaObjectType.Table, [new CompletionColumn("a", "", false)]),
    ], false);

    private readonly BunitJSModuleInterop _module;

    public QueryCompletionPageTests()
    {
        Operations.GetCompletionSchemaAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Schema);
        _module = JSInterop.SetupModule(ModulePath);
        _module.SetupVoid("attach", _ => true).SetVoidResult();
        _module.SetupVoid("detach", _ => true).SetVoidResult();
    }

    private IRenderedComponent<SbConsole.Plugins.Sqlite.Pages.Query> RenderPage()
    {
        var cut = Render<SbConsole.Plugins.Sqlite.Pages.Query>();
        cut.WaitForElement(".sqlite-query-editor");
        cut.WaitForAssertion(() => cut.Find(".sqlite-query-meta").TextContent.Should().Contain("statement timeout"));
        return cut;
    }

    private DotNetObjectReference<SbConsole.Plugins.Sqlite.Pages.Query> AttachedReference() =>
        (DotNetObjectReference<SbConsole.Plugins.Sqlite.Pages.Query>)_module.VerifyInvoke("attach").Arguments[1]!;

    [Fact]
    public async Task The_schema_loads_from_the_live_file_when_the_connection_is_selected()
    {
        RenderPage();

        await Operations.Received(1).GetCompletionSchemaAsync(Secret, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task On_a_backup_the_schema_comes_from_the_backup_file()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(
            $"/p/sqlite/query?connectionId={ConnectionId}&backup={Uri.EscapeDataString(BackupFile)}");

        var cut = RenderPage();
        cut.WaitForElement(".sqlite-query-backup-banner");

        await Operations.Received(1).GetCompletionSchemaAsync(Secret, BackupFile, Arg.Any<CancellationToken>());
        await Operations.DidNotReceive().GetCompletionSchemaAsync(Secret, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_completion_module_is_attached_to_the_editor_with_a_reference_to_the_page()
    {
        var cut = RenderPage();

        var invocation = _module.VerifyInvoke("attach");
        invocation.Arguments[0].Should().BeOfType<ElementReference>();
        AttachedReference().Value.Should().BeSameAs(cut.Instance);
    }

    [Fact]
    public void The_js_invokable_returns_the_engine_result_for_the_loaded_schema()
    {
        RenderPage();
        const string text = "SELECT e. FROM events e";

        var result = AttachedReference().Value.GetCompletions(text, 9);

        result.Should().BeEquivalentTo(SqlCompletionEngine.Complete(text, 9, Schema));
        result.Items.Select(i => i.Label).Should().Equal("id", "name");
    }

    [Fact]
    public void Before_the_schema_arrives_completion_still_answers_with_keywords()
    {
        Operations.GetCompletionSchemaAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<CompletionSchema>().Task);
        RenderPage();

        var result = AttachedReference().Value.GetCompletions("SEL", 3);

        result.Items.Select(i => i.Label).Should().Contain("SELECT");
        AttachedReference().Value.GetCompletions("SELECT * FROM ", 14).Items.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_schema_load_leaves_completion_working_without_tables_and_shows_no_error()
    {
        Operations.GetCompletionSchemaAsync(Secret, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<CompletionSchema>>(_ => throw new SqliteBackupException("nope"));

        var cut = RenderPage();

        cut.FindAll(".sqlite-query-error").Should().BeEmpty();
        AttachedReference().Value.GetCompletions("SELECT * FROM ", 14).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CREATE TABLE scratch(a)", "CREATE", 2)]
    [InlineData("DROP TABLE scratch", "DROP", 2)]
    [InlineData("ALTER TABLE events ADD COLUMN b", "ALTER", 2)]
    [InlineData("DELETE FROM events", "DELETE", 1)]
    [InlineData("UPDATE events SET name = 'x'", "UPDATE", 1)]
    public async Task The_schema_reloads_after_a_committed_write_only_when_it_ran_ddl(string sql, string keyword, int loads)
    {
        Connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
        [
            new ConnectionInfo(ConnectionId, ConnectionName, "sqlite", [],
                Summary: new Dictionary<string, string> { ["File"] = "reports.db", ["Mode"] = "read-write" }),
        ]);
        var statements = new[] { new WriteStatementResult(keyword, sql, 0, 0) };
        Operations.DryRunWriteAsync(Secret, sql, Arg.Any<CancellationToken>()).Returns(new WriteScriptResult(false, statements, TimeSpan.Zero, null));
        Operations.RunWriteAsync(Secret, sql, Arg.Any<CancellationToken>()).Returns(new WriteScriptResult(true, statements, TimeSpan.Zero, new WriteFileState(null, 0)));
        Operations.GetCompletionSchemaAsync(Secret, null, Arg.Any<CancellationToken>()).Returns(Schema, SchemaAfterDdl);
        var cut = RenderPage();
        cut.Find(".sqlite-query-mode-write").Click();
        cut.Find(".sqlite-query-editor").Input(sql);
        cut.Find(".sqlite-query-dryrun").Click();
        cut.WaitForElement(".sqlite-query-dryrun-panel");

        cut.Find(".sqlite-query-runwrite").Click();
        cut.WaitForElement(".sqlite-query-write-result");

        await Operations.Received(loads).GetCompletionSchemaAsync(Secret, null, Arg.Any<CancellationToken>());
        var tables = AttachedReference().Value.GetCompletions("SELECT * FROM ", 14).Items.Select(i => i.Label);
        tables.Should().Equal(loads == 2 ? ["events", "scratch"] : ["events"]);
    }

    [Fact]
    public async Task Disposing_the_page_detaches_the_editor_and_releases_the_reference()
    {
        RenderPage();
        var reference = AttachedReference();

        await DisposeComponentsAsync();

        _module.VerifyInvoke("detach").Arguments[0].Should().BeOfType<ElementReference>();
        reference.Invoking(r => _ = r.Value).Should().Throw<ObjectDisposedException>();
    }
}
