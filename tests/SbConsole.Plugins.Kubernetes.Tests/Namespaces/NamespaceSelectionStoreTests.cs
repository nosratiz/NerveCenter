using FluentAssertions;
using SbConsole.Plugins.Kubernetes.Namespaces;

namespace SbConsole.Plugins.Kubernetes.Tests.Namespaces;

public class NamespaceSelectionStoreTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly InMemoryPluginStore _store = new();

    [Fact]
    public async Task Nothing_stored_is_an_empty_selection()
    {
        (await NamespaceSelectionStore.GetAsync(_store, Id, "admin", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Round_trips_per_connection_and_user()
    {
        await NamespaceSelectionStore.SetAsync(_store, Id, "admin", ["checkout", "payments"], default);

        _store.Values.Should().ContainKey($"ns:{Id}:admin");
        (await NamespaceSelectionStore.GetAsync(_store, Id, "admin", default)).Should().Equal("checkout", "payments");
        (await NamespaceSelectionStore.GetAsync(_store, Id, "someone-else", default)).Should().BeEmpty();
        (await NamespaceSelectionStore.GetAsync(_store, Guid.NewGuid(), "admin", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Bad_json_is_an_empty_selection()
    {
        _store.Values[NamespaceSelectionStore.Key(Id, "admin")] = "{not json";

        (await NamespaceSelectionStore.GetAsync(_store, Id, "admin", default)).Should().BeEmpty();
    }
}
