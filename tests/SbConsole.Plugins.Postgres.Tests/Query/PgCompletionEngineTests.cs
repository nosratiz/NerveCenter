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
        Complete("SELECT now|").Items.Should().Contain(i => i.Label == "now" && i.InsertText == "now()");
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
    public void A_finished_word_equal_to_a_candidate_ignoring_case_offers_nothing()
    {
        Labels("select|").Should().BeEmpty();
        Labels("SELECT * FROM orders|").Should().BeEmpty();
    }

    [Fact]
    public void A_finished_word_does_not_fall_through_to_a_longer_prefix_match()
    {
        var withArchive = new PgCompletionSchema(["public"], ["public"],
        [
            new("public", "orders", PgObjectKind.Table, [Col("id")]),
            new("public", "orders_archive", PgObjectKind.Table, [Col("id")]),
        ]);
        Labels("SELECT * FROM orders|", withArchive).Should().BeEmpty();
        Labels("SELECT * FROM orders_|", withArchive).Should().Contain("orders_archive");
        Labels("SELECT * FROM x WHERE a = 1 OR|").Should().BeEmpty();
    }

    [Fact]
    public void OVERRIDING_after_a_table_is_a_keyword_not_an_alias()
    {
        Labels("SELECT | FROM orders OVERRIDING").Should().NotContain("OVERRIDING");
        Complete("SELECT | FROM orders OVERRIDING").Items
            .Should().NotContain(i => i.Kind == PgCompletionKind.Alias && i.Label == "OVERRIDING");
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
    public void Aliases_are_inserted_as_written()
    {
        Complete("SELECT | FROM orders O").Items.Should().ContainEquivalentOf(
            new PgCompletionItem("O", "O", PgCompletionKind.Alias, "orders"));
        Complete("SELECT | FROM orders \"My O\"").Items.Should().ContainEquivalentOf(
            new PgCompletionItem("My O", "\"My O\"", PgCompletionKind.Alias, "orders"));
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
