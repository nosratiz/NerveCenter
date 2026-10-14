using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
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
        SqlitePlugin.ComputeProblemsAsync(_operations, _connectionId, Secret, _store, Now, CancellationToken.None);

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

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

    private static BackupRecord Record(DateTimeOffset at, bool ok, string? error = null) =>
        new(Guid.NewGuid(), ok ? "reports.db" : null, at, 10, 1, BackupRecord.TriggerSchedule, "system", null,
            ok ? BackupRecord.StatusOk : BackupRecord.StatusFailed, error);

    [Fact]
    public async Task A_failed_newest_backup_is_a_warning_linking_to_backups()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await BackupHistoryStore.WriteAsync(_store, _connectionId,
            [Record(Now.AddHours(-10), ok: false, "The disk or volume is full."), Record(Now.AddHours(-20), ok: true)]);

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning", "Last backup failed", "The disk or volume is full. (2026-10-13 02:00 UTC)", "/p/sqlite/backups"));
    }

    [Fact]
    public async Task An_older_failure_followed_by_a_success_is_not_a_problem()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [Record(Now.AddHours(-2), ok: true), Record(Now.AddHours(-20), ok: false, "x")]);

        (await Problems()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_enabled_policy_with_no_recent_success_is_overdue()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [Record(Now.AddHours(-30), ok: true)]);

        (await Problems()).Should().ContainSingle().Which.Should().Be(new PluginDashboardProblem(
            "Warning", "Backup overdue", "Last successful backup 30 h ago", "/p/sqlite/backups"));
    }

    [Fact]
    public async Task An_enabled_policy_that_never_succeeded_is_overdue_and_the_failure_shows_too()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [Record(Now.AddHours(-1), ok: false, "boom")]);

        (await Problems()).Select(p => (p.Title, p.Detail)).Should().Equal(
            ("Last backup failed", "boom (2026-10-13 11:00 UTC)"),
            ("Backup overdue", "No successful backup yet"));
    }

    [Fact]
    public async Task No_overdue_warning_without_an_enabled_policy_or_within_26_hours()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [Record(Now.AddHours(-25), ok: true)]);
        var other = Guid.NewGuid();
        await BackupPolicyStore.WriteAsync(_store, other, new BackupPolicy(Enabled: false));

        (await Problems()).Should().BeEmpty();
        (await SqlitePlugin.ComputeProblemsAsync(_operations, other, Secret, _store, Now, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_initialized_plugin_uses_its_clock_for_backup_problems()
    {
        _operations.GetOverviewAsync(Secret, Arg.Any<CancellationToken>()).Returns(Overview(freelist: 0));
        await BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(Enabled: true));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [Record(Now.AddHours(-2), ok: true)]);
        var plugin = new SqlitePlugin();
        plugin.InitializeForTesting(_operations, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now.AddHours(30)));

        (await plugin.GetDashboardProblemsAsync(_connectionId, Secret, _store)).Should().ContainSingle()
            .Which.Title.Should().Be("Backup overdue");
    }
}
