using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Core.Audit;
using SbConsole.Core.Connections;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Security;

namespace SbConsole.Core.Tests.Connections;

public class EfConnectionProviderTests
{
    [Fact]
    public async Task Get_secret_decrypts_just_in_time_and_returns_null_for_unknown()
    {
        using var testDb = new TestDb();
        var protector = new AesGcmSecretProtector(new byte[32]);
        var create = new CreateConnectionCommandHandler(testDb, protector, Substitute.For<IAuditWriter>(), new FakeTimeProvider(), []);
        var created = await create.HandleAsync(new CreateConnectionCommand("bus", "azure-servicebus", "Endpoint=sb://real", [], "admin"));
        var provider = new EfConnectionProvider(testDb, protector);

        (await provider.GetSecretAsync(created.Value)).Should().Be("Endpoint=sb://real");
        (await provider.GetSecretAsync(Guid.NewGuid())).Should().BeNull();
        (await provider.ListAsync("azure-servicebus")).Should().ContainSingle(c => c.Name == "bus");
    }

    [Fact]
    public async Task List_includes_the_stored_summary()
    {
        using var testDb = new TestDb();
        var protector = new AesGcmSecretProtector(new byte[32]);
        await using (var db = testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                Name = "bus",
                Kind = "azure-servicebus",
                SecretCiphertext = protector.Protect("Endpoint=sb://real"),
                SummaryJson = """{"Region":"eu-west-1"}""",
            });
            await db.SaveChangesAsync();
        }

        var provider = new EfConnectionProvider(testDb, protector);
        var listed = await provider.ListAsync("azure-servicebus");

        listed.Should().ContainSingle(c => c.Summary.GetValueOrDefault("Region") == "eu-west-1");
    }
}
