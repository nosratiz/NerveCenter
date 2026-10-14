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

    [DockerFact]
    public async Task Completion_objects_list_search_path_schemas_first()
    {
        var schema = await fx.Operations.GetCompletionSchemaAsync(fx.Secret(), null);

        var firstReporting = schema.Objects.ToList().FindIndex(o => o.Schema == "reporting");
        var lastPublic = schema.Objects.ToList().FindLastIndex(o => o.Schema == "public");
        firstReporting.Should().BeGreaterThanOrEqualTo(0);
        lastPublic.Should().BeLessThan(firstReporting);
    }
}
