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

    [Fact]
    public async Task Concurrent_SetAsync_for_same_key_completes_successfully()
    {
        using var testDb = new TestDb();
        var factory = new EfPluginStoreFactory(testDb);
        var store = factory.For("plugin-concurrent-test");
        const string key = "concurrent-key";
        const string value1 = """{"data":"value1"}""";
        const string value2 = """{"data":"value2"}""";

        // Race two concurrent SetAsync calls for the same key
        var task1 = store.SetAsync(key, value1);
        var task2 = store.SetAsync(key, value2);

        // Both calls should complete without exception
        var ex = await Record.ExceptionAsync(() => Task.WhenAll(task1, task2));
        ex.Should().BeNull();

        // The final value should be one of the two written values (consistent state)
        var finalValue = await store.GetAsync(key);
        finalValue.Should().BeOneOf(value1, value2);
    }
}
