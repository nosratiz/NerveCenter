using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;
using static SbConsole.Plugins.Sqlite.Tests.Backups.BackupTestData;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

public class BackupQueryHandlerTests
{
    private const string Secret = "Path=/data/reports.db;BackupDir=/data/backups";
    private static readonly DateTimeOffset Now = Utc(2026, 10, 13, 12);
    private static readonly SqliteFileFingerprint Live = new(4096, 3, null, new string('f', 64), []);

    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _unknownId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(Now);

    public BackupQueryHandlerTests()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns(Secret);
        _connections.GetSecretAsync(_unknownId, Arg.Any<CancellationToken>()).Returns((string?)null);
        _operations.GetLiveFingerprintAsync(Secret, Arg.Any<CancellationToken>()).Returns(Live);
    }

    private GetBackupsQueryHandler Backups() => new(_operations, _connections, _store, _time, NullLogger<GetBackupsQueryHandler>.Instance);

    private GetGrowthQueryHandler Growth() => new(_operations, _connections, _store, _time, NullLogger<GetGrowthQueryHandler>.Instance);

    [Fact]
    public async Task The_backups_page_gets_history_policy_live_file_prune_preview_and_schedule_in_one_load()
    {
        var policy = new BackupPolicy(Enabled: true, DailyAtUtc: "02:00", KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 0);
        await BackupPolicyStore.WriteAsync(_store, _connectionId, policy);
        var newest = Ok(Utc(2026, 10, 12, 2)) with { Verify = BackupRecord.VerifyOk, Bytes = 400 };
        var older = Ok(Utc(2026, 10, 11, 2)) with { Bytes = 300 };
        var failed = Failed(Utc(2026, 10, 13, 2));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [newest, older, failed]);

        var result = await Backups().HandleAsync(_connectionId);

        var overview = result.Value!;
        overview.BackupDir.Should().Be("/data/backups");
        overview.History.Should().Equal(failed, newest, older);
        overview.Policy.Should().Be(policy);
        overview.PolicySaved.Should().BeTrue();
        overview.Live.Should().Be(Live);
        overview.LiveError.Should().BeNull();
        overview.PrunePreview.Should().Equal(older);
        overview.PruneBytes.Should().Be(300);
        overview.Kept.Should().Be(2);
        overview.TotalBytes.Should().Be(700);
        overview.VerifiedOk.Should().Be(1);
        overview.Schedule.Should().Be(new BackupScheduleStatus(newest, 34, Overdue: true, Due: false, Utc(2026, 10, 14, 2), MissedSlots: 5));
    }

    [Fact]
    public async Task Without_a_policy_the_defaults_show_and_nothing_is_due_or_overdue()
    {
        var result = await Backups().HandleAsync(_connectionId);

        result.Value!.Policy.Should().Be(BackupPolicy.Default);
        result.Value.PolicySaved.Should().BeFalse();
        result.Value.History.Should().BeEmpty();
        result.Value.Schedule.Should().Be(new BackupScheduleStatus(null, null, false, false, null, 0));
    }

    [Fact]
    public async Task A_connection_without_a_backup_directory_still_loads()
    {
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>()).Returns("Path=/data/reports.db");
        _operations.GetLiveFingerprintAsync("Path=/data/reports.db", Arg.Any<CancellationToken>()).Returns(Live);

        (await Backups().HandleAsync(_connectionId)).Value!.BackupDir.Should().BeNull();
    }

    [Fact]
    public async Task An_unreadable_live_file_still_shows_the_history_with_the_reason()
    {
        await BackupHistoryStore.AddAsync(_store, _connectionId, Ok(Utc(2026, 10, 12)));
        _operations.GetLiveFingerprintAsync(Secret, Arg.Any<CancellationToken>()).Returns<Task<SqliteFileFingerprint>>(_ => throw new SqliteException("raw", 14));

        var result = await Backups().HandleAsync(_connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Live.Should().BeNull();
        result.Value.LiveError.Should().Be("File not found, or not readable by the SbConsole container user.");
        result.Value.History.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_backups_load_of_an_unknown_connection_fails()
    {
        (await Backups().HandleAsync(_unknownId)).Error.Should().Be(HandlerRunner.ConnectionNotFound);
    }

    [Fact]
    public async Task A_cancelled_backups_load_rethrows()
    {
        using var cts = new CancellationTokenSource();
        _operations.GetLiveFingerprintAsync(Secret, Arg.Any<CancellationToken>()).Returns<Task<SqliteFileFingerprint>>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        var act = () => Backups().HandleAsync(_connectionId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Loading_growth_takes_a_sample_and_summarises_with_the_volume()
    {
        await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(Now.AddDays(-2), 1_000));
        _operations.GetFileBytesAsync(Secret, Arg.Any<CancellationToken>()).Returns(3_000L);
        _operations.GetVolumeAsync(Secret, Arg.Any<CancellationToken>()).Returns(new VolumeInfo(1_000_000, 50_000));

        var result = await Growth().HandleAsync(_connectionId);

        result.Value!.Samples.Should().Equal(new GrowthSample(Now.AddDays(-2), 1_000), new GrowthSample(Now, 3_000));
        result.Value.Summary.BytesPerDay.Should().Be(1_000);
        result.Value.Summary.DaysUntilFull.Should().Be(50);
        result.Value.Volume.Should().Be(new VolumeInfo(1_000_000, 50_000));
    }

    [Fact]
    public async Task Growth_is_sampled_at_most_once_an_hour_across_loads()
    {
        _operations.GetFileBytesAsync(Secret, Arg.Any<CancellationToken>()).Returns(3_000L);

        await Growth().HandleAsync(_connectionId);
        _time.Advance(TimeSpan.FromMinutes(30));
        var result = await Growth().HandleAsync(_connectionId);

        result.Value!.Samples.Should().ContainSingle();
        result.Value.Summary.Collecting.Should().BeTrue();
        await _operations.Received(1).GetFileBytesAsync(Secret, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sampling_and_volume_failures_still_return_the_stored_samples()
    {
        await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(Now.AddDays(-1), 1_000));
        _operations.GetFileBytesAsync(Secret, Arg.Any<CancellationToken>()).Returns<Task<long?>>(_ => throw new SqlitePathRefusedException("outside the roots"));
        _operations.GetVolumeAsync(Secret, Arg.Any<CancellationToken>()).Returns<Task<VolumeInfo?>>(_ => throw new IOException("statvfs"));

        var result = await Growth().HandleAsync(_connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Samples.Should().ContainSingle();
        result.Value.Volume.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_file_takes_no_sample()
    {
        _operations.GetFileBytesAsync(Secret, Arg.Any<CancellationToken>()).Returns((long?)null);

        (await Growth().HandleAsync(_connectionId)).Value!.Samples.Should().BeEmpty();
    }

    [Fact]
    public async Task Growth_of_an_unknown_connection_fails()
    {
        (await Growth().HandleAsync(_unknownId)).Error.Should().Be(HandlerRunner.ConnectionNotFound);
    }
}
