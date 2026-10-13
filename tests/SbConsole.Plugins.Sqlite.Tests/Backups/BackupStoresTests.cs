using FluentAssertions;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using static SbConsole.Plugins.Sqlite.Tests.Backups.BackupTestData;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

public class BackupStoresTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly InMemoryPluginStore _store = new();

    [Fact]
    public async Task History_is_kept_per_connection_newest_first()
    {
        var older = Ok(Utc(2026, 10, 12));
        var newer = Failed(Utc(2026, 10, 13));

        await BackupHistoryStore.AddAsync(_store, _connectionId, older);
        await BackupHistoryStore.AddAsync(_store, _connectionId, newer);

        _store.Values.Keys.Should().Equal($"backups:{_connectionId}");
        (await BackupHistoryStore.ReadAsync(_store, _connectionId)).Should().Equal(newer, older);
        (await BackupHistoryStore.ReadAsync(_store, Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task History_is_capped_at_200_records_dropping_the_oldest()
    {
        var start = Utc(2026, 1, 1);
        await BackupHistoryStore.WriteAsync(_store, _connectionId, Enumerable.Range(0, 200).Select(i => Ok(start.AddHours(i))));

        await BackupHistoryStore.AddAsync(_store, _connectionId, Ok(start.AddHours(500)));

        var records = await BackupHistoryStore.ReadAsync(_store, _connectionId);
        records.Should().HaveCount(BackupHistoryStore.MaxRecords);
        records[0].TakenAt.Should().Be(start.AddHours(500));
        records[^1].TakenAt.Should().Be(start.AddHours(1));
    }

    [Fact]
    public async Task Update_replaces_by_id_and_reports_a_missing_record()
    {
        var record = Ok(Utc(2026, 10, 13));
        await BackupHistoryStore.AddAsync(_store, _connectionId, record);

        (await BackupHistoryStore.UpdateAsync(_store, _connectionId, record with { Verify = BackupRecord.VerifyOk })).Should().BeTrue();
        (await BackupHistoryStore.UpdateAsync(_store, _connectionId, Ok(Utc(2026, 10, 1)))).Should().BeFalse();

        (await BackupHistoryStore.ReadAsync(_store, _connectionId)).Should().ContainSingle().Which.Verify.Should().Be(BackupRecord.VerifyOk);
    }

    [Fact]
    public async Task Remove_drops_the_given_records()
    {
        var a = Ok(Utc(2026, 10, 13));
        var b = Ok(Utc(2026, 10, 12));
        var c = Ok(Utc(2026, 10, 11));
        await BackupHistoryStore.WriteAsync(_store, _connectionId, [a, b, c]);

        await BackupHistoryStore.RemoveAsync(_store, _connectionId, [a.Id, c.Id]);

        (await BackupHistoryStore.ReadAsync(_store, _connectionId)).Should().Equal(b);
    }

    [Fact]
    public async Task Corrupt_history_reads_as_empty()
    {
        _store.Values[BackupHistoryStore.Key(_connectionId)] = "{nope";

        (await BackupHistoryStore.ReadAsync(_store, _connectionId)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_policy_round_trips_and_defaults_match_the_spec()
    {
        var policy = new BackupPolicy(Enabled: true, DailyAtUtc: "03:30", KeepDaily: 3, KeepWeekly: 0, KeepMonthly: 12);

        await BackupPolicyStore.WriteAsync(_store, _connectionId, policy);

        (await BackupPolicyStore.ReadAsync(_store, _connectionId)).Should().Be(policy);
        _store.Values.Keys.Should().Equal($"backup-policy:{_connectionId}");
        BackupPolicy.Default.Should().Be(new BackupPolicy(false, "02:00", 7, 4, 3));
    }

    [Theory]
    [InlineData("{nope")]
    [InlineData("""{"Enabled":true,"DailyAtUtc":"25:00","KeepDaily":7,"KeepWeekly":4,"KeepMonthly":3}""")]
    public async Task A_corrupt_or_invalid_stored_policy_reads_as_none(string json)
    {
        _store.Values[BackupPolicyStore.Key(_connectionId)] = json;

        (await BackupPolicyStore.ReadAsync(_store, _connectionId)).Should().BeNull();
    }

    [Theory]
    [InlineData("02:00", 7, 4, 3, null)]
    [InlineData("23:59", 1, 0, 0, null)]
    [InlineData("2:00", 7, 4, 3, "Daily time must be HH:mm (UTC), e.g. 02:00.")]
    [InlineData("24:00", 7, 4, 3, "Daily time must be HH:mm (UTC), e.g. 02:00.")]
    [InlineData("", 7, 4, 3, "Daily time must be HH:mm (UTC), e.g. 02:00.")]
    [InlineData("02:00", 0, 4, 3, "Keep at least 1 daily backup.")]
    [InlineData("02:00", 7, -1, 3, "Weekly and monthly counts can't be negative.")]
    [InlineData("02:00", 7, 4, -1, "Weekly and monthly counts can't be negative.")]
    public void Policies_are_validated(string dailyAt, int keepDaily, int keepWeekly, int keepMonthly, string? problem)
    {
        new BackupPolicy(true, dailyAt, keepDaily, keepWeekly, keepMonthly).Validate().Should().Be(problem);
    }

    [Fact]
    public async Task Writing_an_invalid_policy_throws()
    {
        var act = () => BackupPolicyStore.WriteAsync(_store, _connectionId, new BackupPolicy(KeepDaily: 0));

        await act.Should().ThrowAsync<ArgumentException>();
        _store.Values.Should().BeEmpty();
    }

    [Fact]
    public void A_policy_describes_itself()
    {
        new BackupPolicy(Enabled: true).Describe().Should().Be("daily 02:00 UTC · 7d · 4w · 3m");
        new BackupPolicy().Describe().Should().Be("disabled · 7d · 4w · 3m");
    }

    [Fact]
    public async Task Growth_samples_at_most_once_an_hour()
    {
        var at = Utc(2026, 10, 13, 12);

        (await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(at, 100))).Should().BeTrue();
        (await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(at.AddMinutes(59), 110))).Should().BeFalse();
        (await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(at.AddHours(1), 120))).Should().BeTrue();

        (await GrowthStore.ReadAsync(_store, _connectionId)).Should().Equal(new GrowthSample(at, 100), new GrowthSample(at.AddHours(1), 120));
        _store.Values.Keys.Should().Equal($"growth:{_connectionId}");
    }

    [Fact]
    public async Task Growth_samples_older_than_30_days_are_pruned()
    {
        var at = Utc(2026, 10, 13, 12);
        await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(at.AddDays(-31), 1));
        await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(at.AddDays(-30), 2));

        await GrowthStore.AppendAsync(_store, _connectionId, new GrowthSample(at, 3));

        (await GrowthStore.ReadAsync(_store, _connectionId)).Select(s => s.Bytes).Should().Equal(2, 3);
    }

    [Fact]
    public async Task Corrupt_growth_reads_as_empty()
    {
        _store.Values[GrowthStore.Key(_connectionId)] = "[{";

        (await GrowthStore.ReadAsync(_store, _connectionId)).Should().BeEmpty();
    }

    [Fact]
    public void Growth_is_collecting_with_fewer_than_two_samples_or_under_an_hour()
    {
        var at = Utc(2026, 10, 13, 12);

        GrowthSummary.From([], null).Collecting.Should().BeTrue();
        GrowthSummary.From([new GrowthSample(at, 1)], null).Should().Be(new GrowthSummary(new GrowthSample(at, 1), new GrowthSample(at, 1), 0, null, null));
        GrowthSummary.From([new GrowthSample(at, 1), new GrowthSample(at.AddMinutes(30), 5)], null).Collecting.Should().BeTrue();
    }

    [Fact]
    public void Growth_rate_and_days_until_the_volume_is_full()
    {
        var at = Utc(2026, 10, 1);
        GrowthSample[] samples = [new(at.AddDays(10), 3_000), new(at, 1_000), new(at.AddDays(5), 2_000)];

        var summary = GrowthSummary.From(samples, new VolumeInfo(1_000_000, 10_000));

        summary.Collecting.Should().BeFalse();
        summary.First.Should().Be(new GrowthSample(at, 1_000));
        summary.Last.Should().Be(new GrowthSample(at.AddDays(10), 3_000));
        summary.DeltaBytes.Should().Be(2_000);
        summary.BytesPerDay.Should().Be(200);
        summary.DaysUntilFull.Should().Be(50);
    }

    [Fact]
    public void A_shrinking_file_or_unknown_volume_has_no_days_until_full()
    {
        var at = Utc(2026, 10, 1);

        var shrinking = GrowthSummary.From([new GrowthSample(at, 3_000), new GrowthSample(at.AddDays(1), 1_000)], new VolumeInfo(10, 10));
        var unknown = GrowthSummary.From([new GrowthSample(at, 1_000), new GrowthSample(at.AddDays(1), 3_000)], null);

        shrinking.BytesPerDay.Should().Be(-2_000);
        shrinking.DaysUntilFull.Should().BeNull();
        unknown.DaysUntilFull.Should().BeNull();
    }
}
