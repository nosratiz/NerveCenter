namespace SbConsole.Sdk;

/// <summary>
/// Saves a new connection on the plugin's behalf (SQLite plugin design spec §13: a plugin that has
/// just made the thing a connection points at, e.g. a new database file). Implemented by the host,
/// which runs it through the same create path as the Connections page: the secret is encrypted,
/// the plugin's GetConnectionSummary is stored, and a "connection.create" audit row is written as
/// the signed-in user. A plugin uses it only for its own <see cref="IPlugin.ConnectionKind"/>; the
/// host refuses a kind no registered plugin handles.
/// </summary>
public interface IConnectionCreator
{
    Task<ConnectionCreateResult> CreateAsync(
        string name, string kind, string secret, IReadOnlyList<string> tags, CancellationToken ct = default);
}

/// <summary>Created, a name that is already taken, or input the host refused (with a message safe to show).</summary>
public abstract record ConnectionCreateResult
{
    private ConnectionCreateResult()
    {
    }

    public sealed record Created(Guid Id) : ConnectionCreateResult;

    public sealed record Conflict : ConnectionCreateResult;

    public sealed record Invalid(string Error) : ConnectionCreateResult;
}
