namespace SbConsole.Plugins.Redis.Client;

public sealed record SetPage(long Cardinality, IReadOnlyList<string> Members, long Cursor, bool Complete);
