using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Query;

namespace SbConsole.Plugins.Sqlite.Tests.Query;

/// <summary>
/// Design spec §12: the pure completion engine. "|" in each input marks the caret; it is removed
/// before the engine sees the text.
/// </summary>
public class SqlCompletionEngineTests
{
    private static readonly CompletionSchema Schema = new(
    [
        new CompletionObject("events", SchemaObjectType.Table,
        [
            new CompletionColumn("id", "INTEGER", true),
            new CompletionColumn("tenant_id", "INTEGER", false),
            new CompletionColumn("name", "TEXT", false),
            new CompletionColumn("ts", "INTEGER", false),
        ]),
        new CompletionObject("my table", SchemaObjectType.Table,
        [
            new CompletionColumn("odd \"col\"", "TEXT", false),
            new CompletionColumn("order", "", false),
        ]),
        new CompletionObject("tenants", SchemaObjectType.Table,
        [
            new CompletionColumn("id", "INTEGER", true),
            new CompletionColumn("name", "TEXT", false),
            new CompletionColumn("status", "TEXT", false),
        ]),
        new CompletionObject("v_daily", SchemaObjectType.View,
        [
            new CompletionColumn("day", "", false),
            new CompletionColumn("n", "", false),
        ]),
    ], false);

    private static CompletionResult Complete(string marked, CompletionSchema? schema = null)
    {
        var caret = marked.IndexOf('|', StringComparison.Ordinal);
        caret.Should().BeGreaterThanOrEqualTo(0, "the test input marks the caret with |");
        return SqlCompletionEngine.Complete(marked.Remove(caret, 1), caret, schema ?? Schema);
    }

    private static List<string> Labels(CompletionResult result) => result.Items.Select(i => i.Label).ToList();

    // ---- qualifier. → columns ----

    [Fact]
    public void An_alias_from_FROM_lists_that_tables_columns_after_the_dot()
    {
        var result = Complete("SELECT e.| FROM events e");

        Labels(result).Should().Equal("id", "tenant_id", "name", "ts");
        result.Items.Should().OnlyContain(i => i.Kind == CompletionKind.Column);
        result.Items[0].Detail.Should().Be("INTEGER PK · events");
        result.Items[2].Detail.Should().Be("TEXT · events");
        result.ReplaceFrom.Should().Be(9);
        result.ReplaceTo.Should().Be(9);
    }

    [Fact]
    public void A_JOIN_AS_alias_resolves_and_the_typed_prefix_filters()
    {
        const string sql = "SELECT t.st| FROM events e JOIN tenants AS t ON t.id = e.tenant_id";

        var result = Complete(sql);

        Labels(result).Should().Equal("status");
        result.ReplaceFrom.Should().Be(9);
        result.ReplaceTo.Should().Be(11);
    }

    [Fact]
    public void UPDATE_and_INSERT_INTO_targets_resolve_as_qualifiers()
    {
        Labels(Complete("UPDATE tenants AS x SET status = 'a' WHERE x.|")).Should().Equal("id", "name", "status");
        Labels(Complete("INSERT INTO tenants AS x VALUES (1) ON CONFLICT DO UPDATE SET name = x.|")).Should().Equal("id", "name", "status");
    }

    [Fact]
    public void A_table_name_works_as_a_qualifier_without_an_alias_and_case_insensitively()
    {
        Labels(Complete("SELECT EVENTS.| FROM Events")).Should().Equal("id", "tenant_id", "name", "ts");
        Labels(Complete("SELECT E.| FROM events e")).Should().Equal("id", "tenant_id", "name", "ts");
        Labels(Complete("SELECT tenants.| FROM events")).Should().Equal("id", "name", "status");
    }

    [Fact]
    public void A_schema_qualified_table_still_registers_its_alias()
    {
        Labels(Complete("SELECT e.| FROM main.events e")).Should().Equal("id", "tenant_id", "name", "ts");
    }

    [Fact]
    public void Quoted_qualifiers_of_every_kind_resolve()
    {
        var expected = new[] { "odd \"col\"", "order" };

        Labels(Complete("SELECT \"my table\".| FROM \"my table\"")).Should().Equal(expected);
        Labels(Complete("SELECT [my table].| FROM [my table]")).Should().Equal(expected);
        Labels(Complete("SELECT `my table`.| FROM `my table`")).Should().Equal(expected);
        Labels(Complete("SELECT m.| FROM \"my table\" m")).Should().Equal(expected);
    }

    [Fact]
    public void An_unknown_qualifier_lists_nothing()
    {
        Complete("SELECT x.| FROM events e").Items.Should().BeEmpty();
    }

    [Fact]
    public void main_dot_lists_tables_and_views()
    {
        Labels(Complete("SELECT * FROM main.|")).Should().Equal("events", "my table", "tenants", "v_daily");
    }

    [Fact]
    public void The_view_columns_complete_too()
    {
        Labels(Complete("SELECT d.| FROM v_daily d")).Should().Equal("day", "n");
        Complete("SELECT d.| FROM v_daily d").Items[0].Detail.Should().Be("v_daily");
    }

    // ---- after FROM / JOIN / INTO / UPDATE / TABLE / INDEX ON → tables and views ----

    [Theory]
    [InlineData("SELECT * FROM |")]
    [InlineData("SELECT * FROM events e JOIN |")]
    [InlineData("SELECT * FROM events e LEFT OUTER JOIN |")]
    [InlineData("INSERT INTO |")]
    [InlineData("INSERT OR REPLACE INTO |")]
    [InlineData("UPDATE |")]
    [InlineData("DELETE FROM |")]
    [InlineData("DROP TABLE |")]
    [InlineData("ALTER TABLE |")]
    [InlineData("CREATE INDEX ix ON |")]
    [InlineData("CREATE UNIQUE INDEX IF NOT EXISTS ix ON |")]
    [InlineData("SELECT * FROM events e, |")]
    public void Table_positions_list_only_tables_and_views(string sql)
    {
        var result = Complete(sql);

        Labels(result).Should().Equal("events", "my table", "tenants", "v_daily");
        result.Items.Select(i => i.Kind).Should().Equal(CompletionKind.Table, CompletionKind.Table, CompletionKind.Table, CompletionKind.View);
        result.Items[0].Detail.Should().Be("table · 4 columns");
        result.Items[3].Detail.Should().Be("view · 2 columns");
    }

    [Fact]
    public void After_FROM_the_prefix_filters_tables_case_insensitively()
    {
        var result = Complete("select * from EV|");

        Labels(result).Should().Equal("events");
        result.ReplaceFrom.Should().Be(14);
        result.ReplaceTo.Should().Be(16);
    }

    [Fact]
    public void A_join_condition_ON_is_not_a_table_position()
    {
        var result = Complete("SELECT * FROM events e JOIN tenants t ON |");

        result.Items.Should().Contain(i => i.Kind == CompletionKind.Keyword);
        result.Items[0].Kind.Should().Be(CompletionKind.Column);
    }

    [Fact]
    public void A_table_name_that_needs_quotes_is_inserted_quoted()
    {
        var item = Complete("SELECT * FROM my|").Items.Should().ContainSingle().Which;

        item.Label.Should().Be("my table");
        item.InsertText.Should().Be("\"my table\"");
    }

    // ---- general context ----

    [Fact]
    public void Ctrl_space_with_an_empty_prefix_lists_referenced_columns_first_then_aliases_keywords_functions_and_tables()
    {
        var result = Complete("SELECT | FROM events e JOIN tenants t ON t.id = e.tenant_id");

        result.Items.Should().HaveCount(SqlCompletionEngine.MaxItems);
        Labels(result).Take(7).Should().Equal("id", "tenant_id", "name", "ts", "id", "name", "status");
        result.Items[4].Detail.Should().Be("INTEGER PK · tenants");
        result.Items[7].Should().Be(new CompletionItem("e", "e", CompletionKind.Alias, "events"));
        result.Items[8].Should().Be(new CompletionItem("t", "t", CompletionKind.Alias, "tenants"));
        result.Items[9].Kind.Should().Be(CompletionKind.Keyword);
    }

    [Fact]
    public void Kinds_come_in_order_columns_keywords_functions_tables()
    {
        var result = Complete("SELECT * FROM events WHERE t|");

        var kinds = result.Items.Select(i => i.Kind).ToList();
        kinds.IndexOf(CompletionKind.Column).Should().BeLessThan(kinds.IndexOf(CompletionKind.Keyword));
        kinds.IndexOf(CompletionKind.Keyword).Should().BeLessThan(kinds.IndexOf(CompletionKind.Function));
        kinds.IndexOf(CompletionKind.Function).Should().BeLessThan(kinds.IndexOf(CompletionKind.Table));
    }

    [Fact]
    public void Prefix_matches_rank_before_substring_matches()
    {
        var labels = Labels(Complete("SELECT na| FROM events"));

        labels.Should().Contain(["name", "NATURAL", "tenant_id"]);
        labels.IndexOf("name").Should().BeLessThan(labels.IndexOf("NATURAL"), "columns before keywords among prefix matches");
        labels.IndexOf("NATURAL").Should().BeLessThan(labels.IndexOf("tenant_id"), "tenant_id only contains 'na'");
    }

    [Fact]
    public void Keywords_are_inserted_upper_case()
    {
        var item = Complete("sel|").Items.Should().ContainSingle(i => i.Label == "SELECT").Which;

        item.InsertText.Should().Be("SELECT");
        item.Kind.Should().Be(CompletionKind.Keyword);
    }

    [Fact]
    public void Functions_carry_their_signature_and_insert_an_open_paren()
    {
        var count = Complete("SELECT coun|").Items.Should().ContainSingle(i => i.Label == "count").Which;
        var random = Complete("SELECT rand|").Items.Should().Contain(i => i.Label == "random").Which;

        count.Kind.Should().Be(CompletionKind.Function);
        count.Detail.Should().Be("count(X)");
        count.InsertText.Should().Be("count(");
        random.InsertText.Should().Be("random()");
        Labels(Complete("SELECT json_ex|")).Should().Contain("json_extract");
        Complete("SELECT strf|").Items.Should().Contain(i => i.Label == "strftime");
        Complete("SELECT row_n|").Items.Should().Contain(i => i.Label == "row_number");
    }

    [Fact]
    public void Columns_that_need_quotes_are_inserted_quoted()
    {
        var result = Complete("SELECT m.| FROM \"my table\" m");

        result.Items.Select(i => i.InsertText).Should().Equal("\"odd \"\"col\"\"\"", "\"order\"");
    }

    [Fact]
    public void Update_SET_and_INSERT_column_lists_offer_the_target_tables_columns_first()
    {
        Complete("UPDATE tenants SET st|").Items[0].Label.Should().Be("status");
        Complete("INSERT INTO tenants (st|").Items[0].Label.Should().Be("status");
    }

    [Fact]
    public void At_most_fifty_items()
    {
        Complete("|").Items.Should().HaveCount(SqlCompletionEngine.MaxItems);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Labels(Complete("SELECT NA| FROM events")).Should().StartWith("name");
        Labels(Complete("SELECT * FROM events WHERE TeNaNt|")).Should().StartWith("tenant_id");
    }

    [Fact]
    public void An_item_identical_to_the_typed_word_is_dropped_so_Enter_stays_a_newline()
    {
        Labels(Complete("SELECT * FROM events|")).Should().NotContain("events");
        Labels(Complete("SELECT * FROM events WHERE name|")).Should().NotContain("name");
    }

    // ---- replace range ----

    [Fact]
    public void A_caret_mid_identifier_replaces_the_whole_word()
    {
        var result = Complete("SELECT na|me FROM events");

        result.ReplaceFrom.Should().Be(7);
        result.ReplaceTo.Should().Be(11);
        result.Items[0].Label.Should().Be("name");
    }

    [Fact]
    public void Right_after_a_dot_the_range_is_empty_at_the_caret()
    {
        var result = Complete("SELECT e.|, 1 FROM events e");

        result.ReplaceFrom.Should().Be(9);
        result.ReplaceTo.Should().Be(9);
    }

    [Fact]
    public void A_caret_beyond_the_text_is_clamped()
    {
        var act = () => SqlCompletionEngine.Complete("SELECT", 99, Schema);
        var negative = () => SqlCompletionEngine.Complete("SELECT", -3, Schema);

        act.Should().NotThrow().Which.ReplaceTo.Should().Be(6);
        negative.Should().NotThrow().Which.ReplaceFrom.Should().Be(0);
    }

    // ---- statements, strings, comments ----

    [Fact]
    public void Only_the_current_statement_is_used()
    {
        Complete("SELECT * FROM tenants t; SELECT t.| FROM events").Items.Should().BeEmpty();
        Labels(Complete("SELECT e.| FROM events e; SELECT * FROM tenants e")).Should().Equal("id", "tenant_id", "name", "ts");
        Labels(Complete("SELECT * FROM events e;\nSELECT e.| FROM tenants e")).Should().Equal("id", "name", "status");
    }

    [Fact]
    public void A_semicolon_inside_a_string_or_trigger_body_does_not_split()
    {
        Labels(Complete("SELECT ';', e.| FROM events e")).Should().Equal("id", "tenant_id", "name", "ts");
        Labels(Complete("CREATE TRIGGER trg AFTER INSERT ON events BEGIN UPDATE tenants SET name = 'x'; DELETE FROM |")).Should().Contain("events");
    }

    [Theory]
    [InlineData("SELECT 'e.|' FROM events e")]
    [InlineData("SELECT 'it''s e.|' FROM events e")]
    [InlineData("SELECT 'unterminated e.|")]
    [InlineData("SELECT \"e.|\" FROM events e")]
    [InlineData("SELECT \"unterminated|")]
    [InlineData("SELECT `e.|` FROM events e")]
    [InlineData("SELECT [e.|] FROM events e")]
    [InlineData("SELECT 1 -- e.|\nFROM events e")]
    [InlineData("SELECT 1 -- e.|")]
    [InlineData("SELECT /* e.| */ 1 FROM events e")]
    [InlineData("SELECT /* unterminated e.|")]
    public void Inside_a_string_quoted_name_or_comment_nothing_is_offered(string sql)
    {
        Complete(sql).Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("SELECT 'a', e.| FROM events e")]
    [InlineData("SELECT /* note */ e.| FROM events e")]
    [InlineData("-- header\nSELECT e.| FROM events e")]
    public void Right_after_a_closed_string_or_comment_completion_works(string sql)
    {
        Labels(Complete(sql)).Should().Equal("id", "tenant_id", "name", "ts");
    }

    [Fact]
    public void A_comment_between_FROM_and_the_caret_is_skipped()
    {
        Labels(Complete("SELECT * FROM /* pick one */ |")).Should().Equal("events", "my table", "tenants", "v_daily");
    }

    [Fact]
    public void Typing_a_number_offers_nothing()
    {
        Complete("SELECT 12|").Items.Should().BeEmpty();
        Complete("SELECT 1.5|").Items.Should().BeEmpty();
    }

    // ---- empty schema ----

    [Fact]
    public void An_empty_schema_still_offers_keywords_and_functions_but_no_tables()
    {
        Complete("SELECT * FROM |", CompletionSchema.Empty).Items.Should().BeEmpty();
        Complete("SELECT e.| FROM events e", CompletionSchema.Empty).Items.Should().BeEmpty();
        Labels(Complete("SEL|", CompletionSchema.Empty)).Should().Contain("SELECT");
        Complete("|", CompletionSchema.Empty).Items.Should().NotBeEmpty();
    }
}
