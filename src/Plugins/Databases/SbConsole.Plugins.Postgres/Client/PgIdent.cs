namespace SbConsole.Plugins.Postgres.Client;

public static class PgIdent
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string Qualified(string schema, string name) => Quote(schema) + "." + Quote(name);
}
