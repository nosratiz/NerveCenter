using FluentAssertions;
using SbConsole.Core.Plugins;

namespace SbConsole.Core.Tests.Plugins;

public class EfPluginStoreTests
{
    [Fact]
    public async Task Set_get_delete_roundtrip()
    {
        using var testDb = new TestDb();
        var store = new EfPluginStoreFactory(testDb).For("servicebus");

        (await store.GetAsync("prefs")).Should().BeNull();
        await store.SetAsync("prefs", """{"pageSize":50}""");
        (await store.GetAsync("prefs")).Should().Be("""{"pageSize":50}""");
        await store.SetAsync("prefs", """{"pageSize":100}""");
        (await store.GetAsync("prefs")).Should().Be("""{"pageSize":100}""");
        (await store.DeleteAsync("prefs")).Should().BeTrue();
        (await store.DeleteAsync("prefs")).Should().BeFalse();
    }

    [Fact]
    public async Task Stores_are_isolated_per_plugin()
    {
        using var testDb = new TestDb();
        var factory = new EfPluginStoreFactory(testDb);
        await factory.For("plugin-a").SetAsync("k", "a-value");

        (await factory.For("plugin-b").GetAsync("k")).Should().BeNull();
    }
}
