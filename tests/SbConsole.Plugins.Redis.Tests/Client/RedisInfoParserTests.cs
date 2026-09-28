using FluentAssertions;
using SbConsole.Plugins.Redis.Client;

namespace SbConsole.Plugins.Redis.Tests.Client;

public class RedisInfoParserTests
{
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SampleSections() =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["Server"] = new Dictionary<string, string> { ["redis_version"] = "7.2.4", ["redis_mode"] = "standalone" },
            ["Clients"] = new Dictionary<string, string> { ["connected_clients"] = "412", ["blocked_clients"] = "3" },
            ["Memory"] = new Dictionary<string, string>
            {
                ["used_memory"] = "7405032140",
                ["maxmemory"] = "8589934592",
                ["maxmemory_policy"] = "allkeys-lru",
                ["mem_fragmentation_ratio"] = "1.12",
            },
            ["Stats"] = new Dictionary<string, string>
            {
                ["instantaneous_ops_per_sec"] = "41800",
                ["evicted_keys"] = "1240000",
                ["expired_keys"] = "98213",
                ["keyspace_hits"] = "9482013",
                ["keyspace_misses"] = "583021",
            },
            ["Replication"] = new Dictionary<string, string> { ["role"] = "master", ["connected_slaves"] = "2" },
            ["Keyspace"] = new Dictionary<string, string>
            {
                ["db0"] = "keys=2410000,expires=1820000,avg_ttl=0",
                ["db1"] = "keys=18200,expires=0,avg_ttl=0",
            },
        };

    [Fact]
    public void Parses_server_identity_and_replica_count()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.RedisVersion.Should().Be("7.2.4");
        overview.Mode.Should().Be("standalone");
        overview.ConnectedReplicas.Should().Be(2);
    }

    [Fact]
    public void Parses_memory_fields()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.UsedMemoryBytes.Should().Be(7405032140);
        overview.MaxMemoryBytes.Should().Be(8589934592);
        overview.MaxMemoryPolicy.Should().Be("allkeys-lru");
        overview.MemoryFragmentationRatio.Should().Be(1.12);
    }

    [Fact]
    public void Treats_maxmemory_zero_as_unbounded()
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(SampleSections())
        {
            ["Memory"] = new Dictionary<string, string>
            {
                ["used_memory"] = "100", ["maxmemory"] = "0", ["maxmemory_policy"] = "noeviction", ["mem_fragmentation_ratio"] = "1.0",
            },
        };

        RedisInfoParser.Parse(sections).MaxMemoryBytes.Should().BeNull();
    }

    [Fact]
    public void Computes_hit_ratio_percent_from_hits_and_misses()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        // 9482013 / (9482013 + 583021) * 100
        overview.HitRatioPercent.Should().BeApproximately(94.2, 0.1);
    }

    [Fact]
    public void Parses_ops_evicted_expired_and_client_counts()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.OpsPerSecond.Should().Be(41800);
        overview.EvictedKeysTotal.Should().Be(1240000);
        overview.ExpiredKeysTotal.Should().Be(98213);
        overview.ConnectedClients.Should().Be(412);
        overview.BlockedClients.Should().Be(3);
    }

    [Fact]
    public void Parses_one_keyspace_entry_per_database_sorted_by_index()
    {
        var overview = RedisInfoParser.Parse(SampleSections());

        overview.Databases.Should().Equal(
            new KeyspaceDatabaseInfo(0, 2410000, 1820000),
            new KeyspaceDatabaseInfo(1, 18200, 0));
    }

    [Fact]
    public void Missing_keyspace_section_yields_no_databases()
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(SampleSections());
        sections.Remove("Keyspace");

        RedisInfoParser.Parse(sections).Databases.Should().BeEmpty();
    }
}
