using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;
using SbConsole.Plugins.Postgres.Tests.Infrastructure;

namespace SbConsole.Plugins.Postgres.Tests.Client;

[Collection("postgres")]
public sealed class OperationsSchemaTests(PostgresFixture fx)
{
    private async Task<PgObjectNode> NodeAsync(string schema, string name)
    {
        var tree = await fx.Operations.ListSchemaAsync(fx.Secret(), null);
        return tree.Schemas.Single(s => s.Name == schema)
            .Tables.Concat(tree.Schemas.Single(s => s.Name == schema).Views)
            .Concat(tree.Schemas.Single(s => s.Name == schema).MaterializedViews)
            .Concat(tree.Schemas.Single(s => s.Name == schema).Functions)
            .Single(o => o.Name == name);
    }

    [DockerFact]
    public async Task Schema_tree_lists_tables_views_matviews_functions_and_empty_schemas()
    {
        var tree = await fx.Operations.ListSchemaAsync(fx.Secret(), null);

        tree.Database.Should().Be("app");
        tree.Schemas.Select(s => s.Name).Should().Contain(["public", "reporting"])
            .And.NotContain(["pg_catalog", "information_schema"]);
        var pub = tree.Schemas.Single(s => s.Name == "public");
        pub.Tables.Select(t => t.Name).Should().Contain(["customers", "orders"]);
        pub.Tables.Single(t => t.Name == "orders").Kind.Should().Be(PgObjectKind.Table);
        pub.Tables.Single(t => t.Name == "orders").RowEstimate.Should().BeGreaterThan(0);
        pub.Views.Select(t => t.Name).Should().Contain("v_order_totals");
        pub.Views.Single().Kind.Should().Be(PgObjectKind.View);
        pub.MaterializedViews.Select(t => t.Name).Should().Contain("mv_country");
        pub.MaterializedViews.Single().Kind.Should().Be(PgObjectKind.MaterializedView);
        pub.Functions.Select(t => t.Name).Should().Contain("order_count");
        pub.Functions.Single().Kind.Should().Be(PgObjectKind.Function);
        tree.Schemas.Single(s => s.Name == "reporting").Tables.Select(t => t.Name).Should().Contain("daily");
    }

    [DockerFact]
    public async Task Detail_of_orders_has_columns_with_pk_fk_default_and_sizes()
    {
        var node = await NodeAsync("public", "orders");
        var d = await fx.Operations.GetObjectDetailAsync(fx.Secret(), null, node.Oid);

        d.Should().NotBeNull();
        d!.Schema.Should().Be("public");
        d.Name.Should().Be("orders");
        d.Kind.Should().Be(PgObjectKind.Table);
        d.Owner.Should().Be("postgres");
        d.HeapBytes.Should().BeGreaterThan(0);
        d.IndexBytes.Should().BeGreaterThan(0);
        d.Columns.Select(c => c.Name).Should().Equal("id", "customer_id", "status", "total_cents", "metadata", "created_at");
        d.Columns.Select(c => c.Ordinal).Should().Equal(1, 2, 3, 4, 5, 6);
        var id = d.Columns[0];
        id.IsPrimaryKey.Should().BeTrue();
        id.Nullable.Should().BeFalse();
        id.Type.Should().Be("bigint");
        id.Default.Should().Contain("nextval");
        d.Columns[1].References.Should().Be("customers(id)");
        d.Columns[1].IsPrimaryKey.Should().BeFalse();
        var status = d.Columns[2];
        status.Nullable.Should().BeFalse();
        status.Default.Should().Contain("pending");
        d.Columns[4].Type.Should().Be("jsonb");
        d.Columns[5].Type.Should().Be("timestamp with time zone");
    }

    [DockerFact]
    public async Task Detail_lists_indexes_with_flags_and_constraints()
    {
        var node = await NodeAsync("public", "orders");
        var d = (await fx.Operations.GetObjectDetailAsync(fx.Secret(), null, node.Oid))!;

        d.Indexes.Select(i => i.Name).Should().Contain(["orders_pkey", "idx_orders_status", "idx_orders_legacy"]);
        var pk = d.Indexes.Single(i => i.Name == "orders_pkey");
        pk.IsPrimary.Should().BeTrue();
        pk.IsUnique.Should().BeTrue();
        pk.BacksConstraint.Should().BeTrue();
        pk.Method.Should().Be("btree");
        pk.Bytes.Should().BeGreaterThan(0);
        var status = d.Indexes.Single(i => i.Name == "idx_orders_status");
        status.IsPrimary.Should().BeFalse();
        status.BacksConstraint.Should().BeFalse();
        status.IsValid.Should().BeTrue();
        status.IsPartial.Should().BeFalse();
        status.ColumnsText.Should().Be("(status)");
        status.Definition.Should().StartWith("CREATE INDEX idx_orders_status");
        d.Indexes.Single(i => i.Name == "idx_orders_legacy").Scans.Should().Be(0);

        d.Constraints.Select(c => (c.Type, c.Name)).Should().Contain(("primary key", "orders_pkey"));
        d.Constraints.Single(c => c.Type == "foreign key").Definition.Should().Contain("REFERENCES customers(id)");
        d.StatsResetAt.Should().BeNull("a fresh container never reset its stats");
    }

    [DockerFact]
    public async Task Detail_of_a_view_has_columns_and_no_indexes()
    {
        var node = await NodeAsync("public", "v_order_totals");
        var d = (await fx.Operations.GetObjectDetailAsync(fx.Secret(), null, node.Oid))!;

        d.Kind.Should().Be(PgObjectKind.View);
        d.Columns.Select(c => c.Name).Should().Equal("customer_id", "total");
        d.Indexes.Should().BeEmpty();
        d.Constraints.Should().BeEmpty();
        d.HeapBytes.Should().Be(0);
    }

    [DockerFact]
    public async Task Detail_of_a_function_names_it_with_empty_lists()
    {
        var node = await NodeAsync("public", "order_count");
        var d = (await fx.Operations.GetObjectDetailAsync(fx.Secret(), null, node.Oid))!;

        d.Kind.Should().Be(PgObjectKind.Function);
        d.Name.Should().Be("order_count");
        d.Owner.Should().Be("postgres");
        d.Columns.Should().BeEmpty();
        d.Indexes.Should().BeEmpty();
        d.Constraints.Should().BeEmpty();
        d.HeapBytes.Should().Be(0);
    }

    [DockerFact]
    public async Task Detail_of_an_unknown_oid_is_null() =>
        (await fx.Operations.GetObjectDetailAsync(fx.Secret(), null, 4_000_000_000)).Should().BeNull();

    [DockerFact]
    public async Task Table_ddl_has_create_constraints_indexes_and_comments()
    {
        var node = await NodeAsync("public", "orders");
        var ddl = await fx.Operations.GetDdlAsync(fx.Secret(), null, node.Oid);

        ddl.Should().Contain("CREATE TABLE \"public\".\"orders\" (");
        ddl.Should().Contain("\"status\" text DEFAULT 'pending'::text NOT NULL");
        ddl.Should().Contain("ALTER TABLE \"public\".\"orders\" ADD CONSTRAINT \"orders_pkey\" PRIMARY KEY (id);");
        ddl.Should().Contain("ADD CONSTRAINT \"orders_customer_id_fkey\" FOREIGN KEY (customer_id) REFERENCES customers(id);");
        ddl.Should().Contain("CREATE INDEX idx_orders_status ON public.orders USING btree (status);");
        ddl.Should().NotContain("CREATE UNIQUE INDEX orders_pkey");

        var sample = await NodeAsync("public", "samples");
        (await fx.Operations.GetDdlAsync(fx.Secret(), null, sample.Oid))
            .Should().Contain("COMMENT ON COLUMN \"public\".\"samples\".\"label\" IS 'it''s a label';")
            .And.Contain("\"we\"\"ird\" text");
    }

    [DockerFact]
    public async Task View_matview_and_function_ddl()
    {
        var view = await fx.Operations.GetDdlAsync(fx.Secret(), null, (await NodeAsync("public", "v_order_totals")).Oid);
        view.Should().StartWith("CREATE VIEW \"public\".\"v_order_totals\" AS").And.Contain("sum(");

        var mat = await fx.Operations.GetDdlAsync(fx.Secret(), null, (await NodeAsync("public", "mv_country")).Oid);
        mat.Should().StartWith("CREATE MATERIALIZED VIEW \"public\".\"mv_country\" AS");

        var fn = await fx.Operations.GetDdlAsync(fx.Secret(), null, (await NodeAsync("public", "order_count")).Oid);
        fn.Should().Contain("CREATE OR REPLACE FUNCTION public.order_count()");
    }

    [DockerFact]
    public async Task Ddl_of_an_unknown_oid_is_null() =>
        (await fx.Operations.GetDdlAsync(fx.Secret(), null, 4_000_000_000)).Should().BeNull();

    [DockerFact]
    public async Task Generated_and_identity_columns_are_written_as_such_in_ddl()
    {
        var ddl = await fx.Operations.GetDdlAsync(fx.Secret(), null, (await NodeAsync("public", "oddities")).Oid);
        ddl.Should().Contain("\"gen\" integer GENERATED ALWAYS AS ((id * 2)) STORED");
        ddl.Should().Contain("\"ident\" integer GENERATED BY DEFAULT AS IDENTITY");
        ddl.Should().Contain("\"ident_always\" integer GENERATED ALWAYS AS IDENTITY");
        ddl.Should().NotContain("\"gen\" integer DEFAULT").And.NotContain("ident integer DEFAULT").And.NotContain("nextval");
    }

    [DockerFact]
    public async Task Index_columns_text_survives_an_index_name_containing_using()
    {
        var detail = await fx.Operations.GetObjectDetailAsync(fx.Secret(), null, (await NodeAsync("public", "oddities")).Oid);
        detail!.Indexes.Single(i => i.Name == "ix USING x").ColumnsText.Should().Be("(code)");
    }
}
