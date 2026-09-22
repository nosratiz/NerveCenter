using FluentAssertions;
using SbConsole.Core.Data.Entities;

namespace SbConsole.Core.Tests.Data.Entities;

public class ConnectionTests
{
    [Fact]
    public void Summary_is_empty_when_SummaryJson_is_null()
    {
        var connection = new Connection { Name = "x", Kind = "k", SecretCiphertext = [] };

        connection.Summary.Should().BeEmpty();
    }

    [Fact]
    public void Summary_deserializes_the_stored_json()
    {
        var connection = new Connection
        {
            Name = "x", Kind = "k", SecretCiphertext = [],
            SummaryJson = """{"Region":"eu-west-1"}""",
        };

        connection.Summary.Should().ContainSingle(kv => kv.Key == "Region" && kv.Value == "eu-west-1");
    }
}
