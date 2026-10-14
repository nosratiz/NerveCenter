using FluentAssertions;
using SbConsole.Plugins.Postgres.Client;

namespace SbConsole.Plugins.Postgres.Tests.Client;

public class PgIdentTests
{
    [Fact]
    public void Quote_doubles_embedded_quotes() => PgIdent.Quote("a\"b").Should().Be("\"a\"\"b\"");

    [Fact]
    public void Qualified_quotes_both_parts() => PgIdent.Qualified("public", "My Table").Should().Be("\"public\".\"My Table\"");
}
