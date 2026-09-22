using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;

namespace SbConsole.Core.Tests.Connections;

public class ListConnectionsQueryHandlerTests
{
    [Fact]
    public async Task Lists_metadata_without_secrets_filtered_by_kind()
    {
        using var testDb = new TestDb();
        var create = new CreateConnectionCommandHandler(
            testDb, new AesGcmSecretProtector(new byte[32]), Substitute.For<IAuditWriter>(), new FakeTimeProvider());
        await create.HandleAsync(new CreateConnectionCommand("bus-prod", "azure-servicebus", "s1", ["prod"], "admin"));
        await create.HandleAsync(new CreateConnectionCommand("other", "postgres", "s2", [], "admin"));

        var all = await new ListConnectionsQueryHandler(testDb).HandleAsync();
        var buses = await new ListConnectionsQueryHandler(testDb).HandleAsync("azure-servicebus");

        all.Should().HaveCount(2);
        buses.Should().ContainSingle(c => c.Name == "bus-prod" && c.IsProd);
    }

    [Fact]
    public async Task Includes_the_stored_summary()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                Name = "bus",
                Kind = "azure-servicebus",
                SecretCiphertext = [],
                SummaryJson = """{"Region":"eu-west-1"}""",
            });
            await db.SaveChangesAsync();
        }

        var listed = await new ListConnectionsQueryHandler(testDb).HandleAsync();

        listed.Should().ContainSingle(c => c.Summary.GetValueOrDefault("Region") == "eu-west-1");
    }
}
