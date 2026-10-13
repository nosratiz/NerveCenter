using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Overview;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests;

public class SqlitePluginDashboardTests
{
    private const string Secret = "Path=/data/reports.db";
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly InMemoryPluginStore _store = new();

    private static SqliteOverview Overview(long freelist) => new(
        "/data/reports.db", "3.53.3", 419_430_400, null, DateTimeOffset.UnixEpoch, 4096, 102_400, freelist, "WAL", "NORMAL", 1000, -2000, 5000);

    private Task<IReadOnlyList<PluginDashboardProblem>> Problems() =>
        SqlitePlugin.ComputeProblemsAsync(_operations, _connectionId, Secret, _store, CancellationToken.None);

    [Fact]
    public async Task Each_connection_counts_as_one_database()
    {
        (await new SqlitePlugin().GetDashboardMetricsAsync(Secret)).Should().Equal(new PluginDashboardMetric("Databases", 1));
    }

    [Fact]
    public async Task A_healthy_file_reports_no_problems()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 100));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, new IntegrityResult(true, ["ok"], 0, DateTimeOffset.UnixEpoch, TimeSpan.Zero));

        (await Problems()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, "1 problem found by the check started 2026-10-13 02:00 UTC")]
    [InlineData(2, "2 problems found by the check started 2026-10-13 02:00 UTC")]
    public async Task A_failed_integrity_check_is_an_error(int messageCount, string detail)
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        var messages = Enumerable.Range(0, messageCount).Select(i => $"m{i}").ToList();
        await IntegrityResultStore.WriteAsync(_store, _connectionId, new IntegrityResult(false, messages, 0,
            new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), TimeSpan.Zero));

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Error", "Integrity check failed", detail, "/p/sqlite/overview"));
    }

    [Theory]
    [InlineData(1, "1 row violates a foreign key")]
    [InlineData(3, "3 rows violate a foreign key")]
    public async Task Foreign_key_violations_are_a_warning(int violations, string detail)
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await IntegrityResultStore.WriteAsync(_store, _connectionId, new IntegrityResult(true, ["ok"], violations, DateTimeOffset.UnixEpoch, TimeSpan.Zero));

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning", "Foreign key violations", detail, "/p/sqlite/overview"));
    }

    [Fact]
    public async Task Many_free_pages_are_a_warning()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 25_600));

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning", "25% free pages", "VACUUM would reclaim 100 MB", "/p/sqlite/overview"));
    }

    [Fact]
    public async Task An_uninitialized_plugin_reports_nothing()
    {
        (await new SqlitePlugin().GetDashboardProblemsAsync(_connectionId, Secret, _store)).Should().BeEmpty();
    }
}
