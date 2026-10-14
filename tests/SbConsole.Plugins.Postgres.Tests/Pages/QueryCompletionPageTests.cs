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
