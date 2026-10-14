using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SbConsole.Sdk;
using SbConsole.Plugins.Sqlite.Query;

namespace SbConsole.Plugins.Sqlite.Tests.Query;

public class SavedQueriesTests
{
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));

    private SaveQueryCommandHandler Save() => new(_store, _time, NullLogger<SaveQueryCommandHandler>.Instance);

    private Task<IReadOnlyList<SavedQuery>> List() => new ListSavedQueriesQueryHandler(_store).HandleAsync();

    [Fact]
    public async Task Nothing_saved_lists_empty()
    {
        (await List()).Should().BeEmpty();
    }

    [Fact]
    public async Task Saved_queries_round_trip_newest_first_under_one_global_key()
    {
        await Save().HandleAsync(new SaveQueryCommand("Old events", "SELECT 1"));
        _time.Advance(TimeSpan.FromMinutes(5));
        var saved = await Save().HandleAsync(new SaveQueryCommand("  Churned tenants  ", "DELETE FROM events WHERE tenant_id = 7"));

        saved.IsSuccess.Should().BeTrue();
        saved.Value!.Name.Should().Be("Churned tenants");
        saved.Value.SavedAt.Should().Be(new DateTimeOffset(2026, 10, 13, 9, 5, 0, TimeSpan.Zero));
        (await List()).Select(q => q.Name).Should().Equal("Churned tenants", "Old events");
        _store.Values.Keys.Should().Equal(SavedQueriesStore.Key);
        SavedQueriesStore.Key.Should().Be("saved-queries");
    }

    [Fact]
    public async Task Saving_under_an_existing_name_replaces_it_and_keeps_its_id()
    {
        var first = await Save().HandleAsync(new SaveQueryCommand("Daily", "SELECT 1"));
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await Save().HandleAsync(new SaveQueryCommand("daily", "SELECT 2"));

        var all = await List();
        all.Should().ContainSingle();
        all[0].Id.Should().Be(first.Value!.Id);
        all[0].Sql.Should().Be("SELECT 2");
        all[0].Name.Should().Be("daily");
        second.Value!.Id.Should().Be(first.Value.Id);
    }

    [Theory]
    [InlineData("", "SELECT 1", "Give the query a name.")]
    [InlineData("   ", "SELECT 1", "Give the query a name.")]
    [InlineData("x", "  ", "There is no SQL to save.")]
    public async Task Invalid_saves_are_refused(string name, string sql, string error)
    {
        var result = await Save().HandleAsync(new SaveQueryCommand(name, sql));

        result.Error.Should().Be(error);
        _store.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_over_the_limit_is_refused()
    {
        var result = await Save().HandleAsync(new SaveQueryCommand(new string('n', SavedQuery.MaxNameLength + 1), "SELECT 1"));

        result.Error.Should().Be($"Names are at most {SavedQuery.MaxNameLength} characters.");
    }

    [Fact]
    public async Task The_list_is_capped()
    {
        for (var i = 0; i < SavedQuery.MaxSaved; i++)
        {
            (await Save().HandleAsync(new SaveQueryCommand($"q{i}", "SELECT 1"))).IsSuccess.Should().BeTrue();
        }

        var over = await Save().HandleAsync(new SaveQueryCommand("one more", "SELECT 1"));
        var replace = await Save().HandleAsync(new SaveQueryCommand("q0", "SELECT 2"));

        over.Error.Should().Be($"At most {SavedQuery.MaxSaved} saved queries — delete some first.");
        replace.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_removes_one_and_is_idempotent()
    {
        var keep = await Save().HandleAsync(new SaveQueryCommand("keep", "SELECT 1"));
        var drop = await Save().HandleAsync(new SaveQueryCommand("drop", "SELECT 2"));
        var delete = new DeleteSavedQueryCommandHandler(_store, NullLogger<DeleteSavedQueryCommandHandler>.Instance);

        (await delete.HandleAsync(new DeleteSavedQueryCommand(drop.Value!.Id))).IsSuccess.Should().BeTrue();
        (await delete.HandleAsync(new DeleteSavedQueryCommand(drop.Value.Id))).IsSuccess.Should().BeTrue();

        (await List()).Select(q => q.Id).Should().Equal(keep.Value!.Id);
    }

    [Fact]
    public async Task A_store_failure_is_a_friendly_failed_result()
    {
        var store = Substitute.For<IPluginStore>();
        store.GetAsync(default!).ReturnsForAnyArgs<Task<string?>>(_ => throw new InvalidOperationException("db down"));

        var result = await new SaveQueryCommandHandler(store, _time, NullLogger<SaveQueryCommandHandler>.Instance)
            .HandleAsync(new SaveQueryCommand("x", "SELECT 1"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("db down");
    }

    [Fact]
    public async Task A_corrupt_stored_list_reads_as_empty_and_the_next_save_replaces_it()
    {
        _store.Values[SavedQueriesStore.Key] = "{not json";

        (await List()).Should().BeEmpty();
        (await Save().HandleAsync(new SaveQueryCommand("fresh", "SELECT 1"))).IsSuccess.Should().BeTrue();
        (await List()).Should().ContainSingle();
    }
}
