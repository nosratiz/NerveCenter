using Microsoft.Data.Sqlite;
using NSubstitute;
using SbConsole.Plugins.Sqlite.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Sqlite.Tests.Client;

/// <summary>
/// A real SQLite file in its own temp directory, deleted on dispose. The one deliberate exception
/// to "plugin tests never touch a real engine" (design spec §9): a local file needs no Docker or
/// network, and it's the only honest way to test PRAGMA parsing, read-only mode and interrupts.
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    public TempDatabase(string ddl = "", string fileName = "test.db", bool wal = false)
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("sbc-sqlite-").FullName;
        Path = System.IO.Path.Combine(Directory, fileName);
        using var connection = OpenWriter();
        Run(connection, wal ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode=DELETE;");
        if (ddl.Length > 0)
        {
            Run(connection, ddl);
        }
    }

    public string Directory { get; }

    public string Path { get; }

    /// <summary>A read-write connection for arranging test data (the code under test never writes).</summary>
    public SqliteConnection OpenWriter()
    {
        var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Execute(string sql)
    {
        using var connection = OpenWriter();
        Run(connection, sql);
    }

    public string Secret(int statementTimeoutS = 30, int busyTimeoutMs = 5000, SqliteAccessMode mode = SqliteAccessMode.ReadOnly) =>
        new SqliteConnectionSettings(Path, mode, busyTimeoutMs, statementTimeoutS).ToSecretString();

    public SqliteOperations Operations() => new(AllowingPolicy());

    public static IHostPathPolicy AllowingPolicy()
    {
        var policy = Substitute.For<IHostPathPolicy>();
        policy.Check(Arg.Any<string>()).Returns(call => PathCheckResult.Allow(call.Arg<string>()));
        return policy;
    }

    public static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
