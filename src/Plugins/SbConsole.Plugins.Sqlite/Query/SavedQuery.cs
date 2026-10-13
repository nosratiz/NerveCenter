namespace SbConsole.Plugins.Sqlite.Query;

/// <summary>
/// One entry of the Query page's saved list (design spec §11.3). Global, not per connection --
/// saved SQL is usually portable across copies of the same file.
/// </summary>
public sealed record SavedQuery(Guid Id, string Name, string Sql, DateTimeOffset SavedAt)
{
    public const int MaxNameLength = 100;
    public const int MaxSaved = 200;
}
