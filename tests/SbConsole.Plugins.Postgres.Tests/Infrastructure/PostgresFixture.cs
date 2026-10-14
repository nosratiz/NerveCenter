using Npgsql;
using SbConsole.Plugins.Postgres.Client;
using Testcontainers.PostgreSql;
using Microsoft.Extensions.Logging.Abstractions;

namespace SbConsole.Plugins.Postgres.Tests.Infrastructure;

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>One postgres:16-alpine container per test run, seeded with a small "app" database.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public PostgresOperations Operations { get; } = new(NullLogger<PostgresOperations>.Instance);

    public string Secret(PgAccessMode mode = PgAccessMode.ReadOnly, string database = "app") =>
        Settings(mode, database).ToSecretString();

    public string SecretWithPassword(string password) =>
        (Settings(PgAccessMode.ReadOnly, "app") with { Password = password }).ToSecretString();

    public string SecretForRole(string user, string password) =>
        (Settings(PgAccessMode.ReadOnly, "app") with { Username = user, Password = password }).ToSecretString();

    public string AdminConnectionString(string database = "app") =>
        new NpgsqlConnectionStringBuilder(Container.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString;

    private PostgreSqlContainer Container => _container ?? throw new InvalidOperationException("Docker is not available.");

    private PostgresConnectionSettings Settings(PgAccessMode mode, string database) =>
        new(Container.Hostname, Container.GetMappedPublicPort(5432), database, "postgres", "postgres",
            SslMode: "Disable", Mode: mode);

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();

        await Exec("postgres", "CREATE DATABASE app; CREATE DATABASE analytics;");
        await Exec("app", Seed);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task Exec(string database, string sql)
    {
        await using var conn = new NpgsqlConnection(AdminConnectionString(database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string Seed = """
        CREATE TABLE customers (id bigserial PRIMARY KEY, email text UNIQUE, country text);
        CREATE TABLE orders (
            id bigserial PRIMARY KEY,
            customer_id bigint REFERENCES customers,
            status text NOT NULL DEFAULT 'pending',
            total_cents int,
            metadata jsonb,
            created_at timestamptz DEFAULT now());
        CREATE INDEX idx_orders_status ON orders (status);
        -- idx_orders_legacy must stay unused — never query orders by total_cents in tests.
        -- It is wider than idx_orders_status so it is deterministically the largest unused index.
        CREATE INDEX idx_orders_legacy ON orders (total_cents, created_at, customer_id);
        INSERT INTO customers (email, country)
            SELECT 'user' || g || '@example.com', (ARRAY['US','DE','FR','JP'])[1 + g % 4] FROM generate_series(1, 200) g;
        INSERT INTO orders (customer_id, status, total_cents, metadata)
            SELECT 1 + g % 200, (ARRAY['pending','paid','shipped'])[1 + g % 3], 100 + g,
                   jsonb_build_object('n', g, 'tags', jsonb_build_array('a', 'b'))
              FROM generate_series(1, 2000) g;
        CREATE VIEW v_order_totals AS SELECT customer_id, sum(total_cents) AS total FROM orders GROUP BY customer_id;
        CREATE MATERIALIZED VIEW mv_country AS SELECT country, count(*) AS customers FROM customers GROUP BY country;
        CREATE FUNCTION order_count() RETURNS bigint LANGUAGE sql AS 'SELECT count(*) FROM orders';
        CREATE SCHEMA reporting;
        CREATE TABLE reporting.daily (d date);
        ANALYZE;
        """;
}
