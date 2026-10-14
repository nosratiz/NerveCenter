using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SbConsole.Plugins.Postgres.Query;

namespace SbConsole.Plugins.Postgres.Tests.Query;

public class QueryHistoryStoreTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private readonly InMemoryPluginStore _store = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    private QueryHistoryEntry Entry(string sql = "select 1", DateTimeOffset? at = null) =>
        new(sql, false, 1, 0.01, null, "nima", at ?? _clock.GetUtcNow(), "sha256:abc", null);

    [Fact]
    public async Task Read_with_nothing_stored_is_empty() =>
        (await QueryHistoryStore.ReadAsync(_store, Id, default)).Should().BeEmpty();

    [Fact]
    public async Task Append_keeps_newest_first()
    {
        await QueryHistoryStore.AppendAsync(_store, Id, Entry("a"), _clock.GetUtcNow(), default);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await QueryHistoryStore.AppendAsync(_store, Id, Entry("b"), _clock.GetUtcNow(), default);

        (await QueryHistoryStore.ReadAsync(_store, Id, default)).Select(e => e.Sql).Should().Equal("b", "a");
    }

    [Fact]
    public async Task Append_drops_entries_older_than_90_days()
    {
        await QueryHistoryStore.AppendAsync(_store, Id, Entry("old"), _clock.GetUtcNow(), default);
        _clock.Advance(TimeSpan.FromDays(91));
        await QueryHistoryStore.AppendAsync(_store, Id, Entry("new"), _clock.GetUtcNow(), default);

        (await QueryHistoryStore.ReadAsync(_store, Id, default)).Select(e => e.Sql).Should().Equal("new");
    }

    [Fact]
    public async Task Append_caps_at_500_entries()
    {
        for (var i = 0; i < 505; i++)
        {
            await QueryHistoryStore.AppendAsync(_store, Id, Entry($"q{i}"), _clock.GetUtcNow(), default);
        }

        var all = await QueryHistoryStore.ReadAsync(_store, Id, default);
        all.Should().HaveCount(500);
        all[0].Sql.Should().Be("q504");
    }

    [Fact]
    public async Task Append_truncates_long_sql()
    {
        await QueryHistoryStore.AppendAsync(_store, Id, Entry(new string('x', 5000)), _clock.GetUtcNow(), default);

        (await QueryHistoryStore.ReadAsync(_store, Id, default))[0].Sql.Length.Should().Be(QueryHistoryStore.MaxSqlLength);
    }

    [Fact]
    public async Task Concurrent_appends_lose_nothing()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            QueryHistoryStore.AppendAsync(_store, Id, Entry($"q{i}"), _clock.GetUtcNow(), default)));

        (await QueryHistoryStore.ReadAsync(_store, Id, default)).Should().HaveCount(20);
    }

    [Fact]
    public async Task Corrupt_json_reads_as_empty()
    {
        _store.Values[$"history:{Id}"] = "{nope";
        (await QueryHistoryStore.ReadAsync(_store, Id, default)).Should().BeEmpty();
    }
}
