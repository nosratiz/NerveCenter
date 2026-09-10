using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Core.Data.Entities;
using SbConsole.Core.Plugins;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Plugins;

public class ListPluginsQueryHandlerTests
{
    private sealed class FakePlugin(string id, string kind) : IPlugin
    {
        public string Id => id;
        public string DisplayName => $"Plugin {id}";
        public string Version => "1.0.0";
        public IReadOnlyList<PluginNavItem> NavItems => [];
        public Type RootComponent => typeof(object);
        public string ConnectionKind => kind;
        public string ConnectionKindDisplayName => kind;
        public PluginContribution Contribution => new(PageCount: 2, ActionCount: 4);
        public void ConfigureServices(IServiceCollection services) { }
    }

    [Fact]
    public async Task Joins_plugin_metadata_with_live_connection_counts()
    {
        using var testDb = new TestDb();
        await using (var db = testDb.CreateDbContext())
        {
            db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "a", Kind = "servicebus", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
            db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "b", Kind = "servicebus", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
            db.Connections.Add(new Connection { Id = Guid.NewGuid(), Name = "c", Kind = "kafka", SecretCiphertext = [1], CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var plugins = new IPlugin[] { new FakePlugin("servicebus", "servicebus"), new FakePlugin("kafka", "kafka") };
        var result = await new ListPluginsQueryHandler(plugins, testDb).HandleAsync();

        result.Should().ContainSingle(p => p.Id == "servicebus" && p.ConnectionsInUse == 2);
        result.Should().ContainSingle(p => p.Id == "kafka" && p.ConnectionsInUse == 1);
    }

    [Fact]
    public async Task No_plugins_returns_empty_list()
    {
        using var testDb = new TestDb();

        var result = await new ListPluginsQueryHandler([], testDb).HandleAsync();

        result.Should().BeEmpty();
    }
}
