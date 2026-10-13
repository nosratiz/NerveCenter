using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Backups;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;
using static SbConsole.Plugins.Sqlite.Tests.Backups.BackupTestData;

namespace SbConsole.Plugins.Sqlite.Tests.Backups;

public sealed class SqliteBackupSchedulerTests : IDisposable
{
    private const string SecretA = "Path=/data/a.db;BackupDir=/data/backups";
    private const string SecretB = "Path=/data/b.db;BackupDir=/data/backups";
    private static readonly BackupPolicy Daily = new(Enabled: true, DailyAtUtc: "02:00", KeepDaily: 1, KeepWeekly: 0, KeepMonthly: 0);

    private readonly ConnectionInfo _a = new(Guid.NewGuid(), "sqlite-a", "sqlite", []);
    private readonly ConnectionInfo _b = new(Guid.NewGuid(), "sqlite-b", "sqlite", []);
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IPluginStoreFactory _storeFactory = Substitute.For<IPluginStoreFactory>();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _clock = new(Utc(2026, 10, 13, 2, 0));
    private readonly ServiceProvider _provider;

    public SqliteBackupSchedulerTests()
    {
        _connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns([_a, _b]);
        _connections.GetSecretAsync(_a.Id, Arg.Any<CancellationToken>()).Returns(SecretA);
        _connections.GetSecretAsync(_b.Id, Arg.Any<CancellationToken>()).Returns(SecretB);
        _storeFactory.For(SqlitePlugin.PluginId).Returns(_store);
        _operations.GetFileBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(4096L);
        _operations.CreateBackupAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => new BackupCreated($"{(call.Arg<string>().Contains("/a.db", StringComparison.Ordinal) ? "a" : "b")}-{call.Arg<DateTimeOffset>():yyyyMMdd-HHmmss}.db", 4096, TimeSpan.FromSeconds(1)));
        _operations.VerifyBackupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BackupVerification(true, ["ok"], new string('a', 64), new SqliteFileFingerprint(4096, 1, null, new string('f', 64), [])));

        // The plugin's own registrations (handlers by marker + the hosted service), with the host's
        // services and the operations substituted -- the same container shape the host builds.
        var services = new ServiceCollection();
        new SqlitePlugin().ConfigureServices(services);
        services.AddSingleton(_operations);
        services.AddSingleton(_connections);
        services.AddSingleton(_storeFactory);
        services.AddKeyedSingleton<IPluginStore>(SqlitePlugin.PluginId, _store);
        services.AddSingleton(_audit);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private SqliteBackupScheduler Scheduler() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<SqliteBackupScheduler>.Instance);

    private Task EnableAsync(ConnectionInfo connection, BackupPolicy? policy = null) =>
        BackupPolicyStore.WriteAsync(_store, connection.Id, policy ?? Daily);

    [Fact]
    public void The_plugin_registers_the_scheduler_as_a_hosted_service()
    {
        _provider.GetServices<IHostedService>().Should().ContainSingle().Which.Should().BeOfType<SqliteBackupScheduler>();
    }

    [Fact]
    public async Task A_due_backup_is_taken_by_system_verified_and_pruned()
    {
        await EnableAsync(_a);
        await BackupHistoryStore.AddAsync(_store, _a.Id, Ok(Utc(2026, 10, 12, 2), fileName: "a-old.db"));

        await Scheduler().TickAsync();

        var history = await BackupHistoryStore.ReadAsync(_store, _a.Id);
        history.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            FileName = "a-20261013-020000.db", Trigger = "schedule", Actor = "system", Status = "ok", Verify = "ok",
        });
        await _operations.Received(1).DeleteBackupFileAsync(SecretA, "a-old.db", Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.backup.create", "sqlite-a", ActionRisk.Safe, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.backup.prune", "sqlite-a", ActionRisk.Destructive, true, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _operations.DidNotReceive().CreateBackupAsync(SecretB, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_runs_before_the_slot()
    {
        await EnableAsync(_a, Daily with { DailyAtUtc = "02:01" });

        await Scheduler().TickAsync();

        await _operations.DidNotReceiveWithAnyArgs().CreateBackupAsync(default!, default, default);
    }

    [Fact]
    public async Task A_slot_already_served_today_is_not_run_again()
    {
        await EnableAsync(_a);
        var scheduler = Scheduler();

        await scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromHours(20));
        await scheduler.TickAsync();

        await _operations.Received(1).CreateBackupAsync(SecretA, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        _clock.SetUtcNow(Utc(2026, 10, 14, 2, 0));
        await scheduler.TickAsync();
        await _operations.Received(2).CreateBackupAsync(SecretA, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_backup_without_an_enabled_policy_or_a_backup_directory()
    {
        await EnableAsync(_a, Daily with { Enabled = false });
        await EnableAsync(_b);
        _connections.GetSecretAsync(_b.Id, Arg.Any<CancellationToken>()).Returns("Path=/data/b.db");

        await Scheduler().TickAsync();

        await _operations.DidNotReceiveWithAnyArgs().CreateBackupAsync(default!, default, default);
    }

    [Fact]
    public async Task A_failed_backup_is_recorded_and_the_next_connection_is_still_processed()
    {
        await EnableAsync(_a);
        await EnableAsync(_b);
        _operations.CreateBackupAsync(SecretA, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<BackupCreated>>(_ => throw new SqliteException("full", 13));

        await Scheduler().TickAsync();

        (await BackupHistoryStore.ReadAsync(_store, _a.Id)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Status = "failed", Error = "The disk or volume is full.", Trigger = "schedule", Actor = "system" });
        (await BackupHistoryStore.ReadAsync(_store, _b.Id)).Should().ContainSingle().Which.Status.Should().Be("ok");
        await _operations.DidNotReceive().DeleteBackupFileAsync(SecretA, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_backup_is_not_retried_every_minute()
    {
        await EnableAsync(_a);
        _operations.CreateBackupAsync(SecretA, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<BackupCreated>>(_ => throw new SqliteException("full", 13));
        var scheduler = Scheduler();

        await scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.TickAsync();

        await _operations.Received(1).CreateBackupAsync(SecretA, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_connection_whose_secret_cannot_be_read_does_not_stop_the_others()
    {
        await EnableAsync(_b);
        _connections.GetSecretAsync(_a.Id, Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new System.Security.Cryptography.CryptographicException("bad key"));

        await Scheduler().TickAsync();

        (await BackupHistoryStore.ReadAsync(_store, _b.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Growth_is_sampled_once_an_hour_per_connection()
    {
        var scheduler = Scheduler();

        await scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(59));
        await scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.TickAsync();

        (await GrowthStore.ReadAsync(_store, _a.Id)).Select(s => s.At).Should().Equal(Utc(2026, 10, 13, 2, 0), Utc(2026, 10, 13, 3, 0));
        (await GrowthStore.ReadAsync(_store, _b.Id)).Should().HaveCount(2);
        await _operations.Received(2).GetFileBytesAsync(SecretA, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_growth_sample_failure_does_not_stop_the_backup()
    {
        await EnableAsync(_a);
        _operations.GetFileBytesAsync(SecretA, Arg.Any<CancellationToken>()).Returns<Task<long?>>(_ => throw new SqlitePathRefusedException("outside the roots"));

        await Scheduler().TickAsync();

        (await BackupHistoryStore.ReadAsync(_store, _a.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_tick_does_not_stop_later_ticks()
    {
        var calls = 0;
        _connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<ConnectionInfo>>>(_ =>
        {
            calls++;
            return calls == 1 ? throw new InvalidOperationException("database is starting") : Task.FromResult<IReadOnlyList<ConnectionInfo>>([]);
        });
        using var scheduler = Scheduler();

        await scheduler.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => calls >= 1);
        // The delay is registered right after the failed tick; keep nudging the clock until the next tick ran.
        await WaitUntilAsync(() =>
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            return calls >= 2;
        });
        await scheduler.StopAsync(CancellationToken.None);

        calls.Should().BeGreaterThanOrEqualTo(2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within 5 s.");
            }

            await Task.Delay(10);
        }
    }
}
