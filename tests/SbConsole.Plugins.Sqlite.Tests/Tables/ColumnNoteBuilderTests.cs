using FluentAssertions;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Tables;

namespace SbConsole.Plugins.Sqlite.Tests.Tables;

public class ColumnNoteBuilderTests
{
    private static TableDetail Events(bool withoutRowId = false) => new(
        "events", SchemaObjectType.Table, "CREATE TABLE events(...)", withoutRowId, 41_200_000,
        [
            new ColumnInfo("id", "INTEGER", false, null, 1, false, false),
            new ColumnInfo("session_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("tenant_id", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("props", "TEXT", false, null, 0, false, false),
            new ColumnInfo("ts", "INTEGER", true, null, 0, false, false),
            new ColumnInfo("source", "TEXT", true, "'web'", 0, false, false),
        ],
        [new IndexInfo("idx_events_tenant_ts", false, "c", false, ["tenant_id", "ts"])],
        [new ForeignKeyInfo(0, "session_id", "sessions", "id", "NO ACTION", "CASCADE")]);

    [Fact]
    public void Schema_notes_appear_without_samples()
    {
        var notes = ColumnNoteBuilder.Build(Events(), null);

        notes["id"].Should().Equal("alias of rowid");
        notes["session_id"].Should().Equal("→ sessions.id · ON DELETE CASCADE");
        notes["tenant_id"].Should().Equal("indexed · idx_events_tenant_ts");
        notes["ts"].Should().BeEmpty("it's the second column of the index, not its leading one");
    }

    [Fact]
    public void An_integer_pk_on_a_without_rowid_table_is_not_a_rowid_alias()
    {
        ColumnNoteBuilder.Build(Events(withoutRowId: true), null)["id"].Should().BeEmpty();
    }

    [Fact]
    public void Sample_notes_cover_json_epoch_ms_and_distinct_counts()
    {
        ColumnSample[] samples =
        [
            new("props", 1000, 992, 992, 992, 984, 0, 0),
            new("ts", 1000, 1000, 1000, 0, 0, 1000, 1000),
            new("source", 1000, 1000, 4, 1000, 0, 0, 0),
        ];

        var notes = ColumnNoteBuilder.Build(Events(), samples);

        notes["props"].Should().Equal("JSON in 99.2% of sampled rows");
        notes["ts"].Should().Equal(ColumnNoteBuilder.UnixMsNote);
        notes["source"].Should().Equal("4 distinct ≈ (sampled)");
    }

    [Fact]
    public void Integers_outside_the_epoch_ms_range_get_no_date_hint()
    {
        var notes = ColumnNoteBuilder.Build(Events(), [new ColumnSample("ts", 10, 10, 10, 0, 0, 10, 9)]);

        notes["ts"].Should().BeEmpty();
    }
}
