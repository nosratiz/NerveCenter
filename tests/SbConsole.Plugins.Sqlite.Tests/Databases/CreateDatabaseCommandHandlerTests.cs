using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Plugins.Sqlite.Databases;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Databases;

/// <summary>Design spec §13 steps 1-5: the audited New database command.</summary>
public class CreateDatabaseCommandHandlerTests
{
    private const string Root = "/sqlite";
    private const string NewPath = "/sqlite/team/tasks.db";
    private readonly Guid _newId = Guid.NewGuid();
    private readonly ISqliteOperations _operations = Substitute.For<ISqliteOperations>();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();
    private readonly IConnectionCreator _creator = Substitute.For<IConnectionCreator>();
    private readonly IAuditScope _audit = Substitute.For<IAuditScope>();

    public CreateDatabaseCommandHandlerTests()
    {
        _connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).Returns(
            [new ConnectionInfo(Guid.NewGuid(), "sqlite-reports", "sqlite", [])]);
        _operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NewDatabaseCreated(NewPath, "/private/sqlite/team/tasks.db", "tasks.db", 3));
        _creator.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionCreateResult.Created(_newId));
    }

    private CreateDatabaseCommandHandler Handler() =>
        new(_operations, _connections, _creator, _audit, NullLogger<CreateDatabaseCommandHandler>.Instance);

    private static CreateDatabaseCommand Command(
        string name = "sqlite-tasks", NewDatabaseTemplate template = NewDatabaseTemplate.Starter, string? backupDir = null,
        SqliteAccessMode mode = SqliteAccessMode.ReadWrite, IReadOnlyList<string>? tags = null, string? script = null) =>
        new(Root, "team", "tasks", NewDatabaseJournal.Wal, template, script, name, mode, tags ?? ["prod"], backupDir);

    [Fact]
    public async Task Creates_the_file_then_the_connection_and_audits_path_template_and_journal()
    {
        var result = await Handler().HandleAsync(Command(backupDir: "/sqlite/team/backups"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(new DatabaseCreated(_newId, "sqlite-tasks", NewPath));
        await _operations.Received(1).CreateDatabaseAsync(
            new NewDatabaseRequest(Root, "team", "tasks", NewDatabaseJournal.Wal, NewDatabaseTemplate.Starter, null), Arg.Any<CancellationToken>());
        await _creator.Received(1).CreateAsync("sqlite-tasks", "sqlite",
            "Path=/sqlite/team/tasks.db;Mode=ReadWrite;BusyTimeoutMs=5000;StatementTimeoutS=30;BackupDir=/sqlite/team/backups",
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "prod" })), Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.database.create", "sqlite-tasks", ActionRisk.Mutating, true,
            "/sqlite/team/tasks.db · starter · WAL", Arg.Any<CancellationToken>());
        await _operations.DidNotReceiveWithAnyArgs().DeleteCreatedDatabaseAsync(default!, default);
    }

    [Fact]
    public async Task The_saved_secret_carries_the_chosen_mode_and_no_backup_dir_when_none_was_picked()
    {
        await Handler().HandleAsync(Command(mode: SqliteAccessMode.ReadOnly, tags: [" ", "team "]));

        await _creator.Received(1).CreateAsync("sqlite-tasks", "sqlite",
            "Path=/sqlite/team/tasks.db;Mode=ReadOnly;BusyTimeoutMs=5000;StatementTimeoutS=30",
            Arg.Is<IReadOnlyList<string>>(t => t.SequenceEqual(new[] { "team" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_taken_connection_name_is_refused_before_the_disk_is_touched_and_not_audited()
    {
        var result = await Handler().HandleAsync(Command(name: " sqlite-reports "));

        result.Error.Should().Be("A connection named 'sqlite-reports' already exists — pick another name.");
        await _operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);
        await _creator.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_connection_name_is_refused(string name)
    {
        var result = await Handler().HandleAsync(Command(name: name));

        result.Error.Should().Be("Type a connection name.");
        await _operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);
    }

    [Fact]
    public async Task A_backup_dir_with_a_semicolon_is_refused_before_the_disk_is_touched()
    {
        var result = await Handler().HandleAsync(Command(backupDir: "/sqlite/a;b"));

        result.IsSuccess.Should().BeFalse();
        await _operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);
    }

    [Fact]
    public async Task Input_the_operation_refuses_before_creating_anything_is_returned_and_not_audited()
    {
        _operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SqliteNewDatabaseRefusedException("tasks.db already exists in that folder — SbConsole never overwrites a file."));

        var result = await Handler().HandleAsync(Command());

        result.Error.Should().Be("tasks.db already exists in that folder — SbConsole never overwrites a file.");
        await _creator.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
        await _operations.DidNotReceiveWithAnyArgs().DeleteCreatedDatabaseAsync(default!, default);
    }

    [Fact]
    public async Task A_refused_script_is_returned_and_not_audited()
    {
        _operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SqliteScriptRefusedException("ATTACH is not allowed (statement 1) — the console only opens the connection's own file."));

        var result = await Handler().HandleAsync(Command(template: NewDatabaseTemplate.Script, script: "ATTACH 'x' AS y"));

        result.Error.Should().StartWith("ATTACH is not allowed");
        await _audit.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default, default, default, default);
    }

    [Fact]
    public async Task A_template_that_fails_after_the_file_existed_is_audited_as_failed()
    {
        _operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SqliteException("no such table: missing", 1));

        var result = await Handler().HandleAsync(Command(template: NewDatabaseTemplate.Script, script: "INSERT INTO missing VALUES (1)"));

        result.IsSuccess.Should().BeFalse();
        await _creator.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default!, default!, default);
        await _audit.Received(1).RecordAsync("sqlite.database.create", "sqlite-tasks", ActionRisk.Mutating, false,
            Arg.Is<string>(d => d.StartsWith("/sqlite/team/tasks.db · script · WAL · ")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_conflict_when_saving_the_connection_deletes_the_new_file_and_audits_the_failure()
    {
        _creator.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionCreateResult.Conflict());

        var result = await Handler().HandleAsync(Command());

        result.Error.Should().Be("A connection named 'sqlite-tasks' already exists — the new file was removed.");
        await _operations.Received(1).DeleteCreatedDatabaseAsync(NewPath, Arg.Any<CancellationToken>());
        await _audit.Received(1).RecordAsync("sqlite.database.create", "sqlite-tasks", ActionRisk.Mutating, false,
            Arg.Is<string>(d => d.StartsWith("/sqlite/team/tasks.db · starter · WAL · ")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_invalid_result_when_saving_the_connection_deletes_the_new_file()
    {
        _creator.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ConnectionCreateResult.Invalid("No plugin handles connection kind 'sqlite'."));

        var result = await Handler().HandleAsync(Command());

        result.Error.Should().Be("The connection could not be saved, so the new file was removed: No plugin handles connection kind 'sqlite'.");
        await _operations.Received(1).DeleteCreatedDatabaseAsync(NewPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_throwing_connection_save_deletes_the_new_file_and_never_escapes()
    {
        _creator.CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var result = await Handler().HandleAsync(Command());

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().StartWith("The connection could not be saved, so the new file was removed");
        await _operations.Received(1).DeleteCreatedDatabaseAsync(NewPath, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Once_the_file_exists_cancelling_does_not_leave_it_orphaned()
    {
        using var cts = new CancellationTokenSource();
        _operations.CreateDatabaseAsync(Arg.Any<NewDatabaseRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return new NewDatabaseCreated(NewPath, NewPath, "tasks.db", 0);
            });

        var result = await Handler().HandleAsync(Command(), cts.Token);

        result.IsSuccess.Should().BeTrue();
        await _creator.Received(1).CreateAsync("sqlite-tasks", "sqlite", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), CancellationToken.None);
    }

    [Fact]
    public async Task A_failing_connection_list_is_a_friendly_error()
    {
        _connections.ListAsync("sqlite", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Handler().HandleAsync(Command());

        result.IsSuccess.Should().BeFalse();
        await _operations.DidNotReceiveWithAnyArgs().CreateDatabaseAsync(default!, default);
    }

    [Fact]
    public async Task FindBackupsDir_asks_the_operations()
    {
        _operations.FindBackupsDirAsync(Root, "team", Arg.Any<CancellationToken>()).Returns("/sqlite/team/backups");

        var handler = new FindBackupsDirQueryHandler(_operations, NullLogger<FindBackupsDirQueryHandler>.Instance);

        (await handler.HandleAsync(Root, "team")).Should().Be("/sqlite/team/backups");
    }

    [Fact]
    public async Task FindBackupsDir_failing_offers_nothing()
    {
        _operations.FindBackupsDirAsync(Root, null, Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("denied"));

        var handler = new FindBackupsDirQueryHandler(_operations, NullLogger<FindBackupsDirQueryHandler>.Instance);

        (await handler.HandleAsync(Root, null)).Should().BeNull();
    }
}
