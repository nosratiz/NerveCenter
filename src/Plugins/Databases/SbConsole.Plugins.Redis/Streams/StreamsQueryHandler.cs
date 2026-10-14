using Microsoft.Extensions.Logging;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Streams;

/// <summary>The Streams page's single read-side handler -- same shape as KeysQueryHandler (design spec §9): one method per read shape, all thin HandlerRunner.QueryAsync wrappers, never auditing (queries never write).</summary>
public sealed class StreamsQueryHandler(
    IRedisOperations operations, IConnectionProvider connections, ILogger<StreamsQueryHandler> logger) : IScopedService
{
    public Task<PluginResult<StreamScanPage>> ScanAsync(Guid connectionId, ScanStreamsRequest request, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.scan",
            secret => operations.ScanStreamsAsync(secret, request, ct), ct);

    public Task<PluginResult<StreamOverview>> GetOverviewAsync(Guid connectionId, int database, string key, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.overview",
            secret => operations.GetStreamOverviewAsync(secret, database, key, ct), ct);

    public Task<PluginResult<IReadOnlyList<StreamConsumerGroupSummary>>> GetConsumerGroupsAsync(Guid connectionId, int database, string key, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.groups",
            secret => operations.GetStreamConsumerGroupsAsync(secret, database, key, ct), ct);

    public Task<PluginResult<IReadOnlyList<StreamConsumer>>> GetConsumersAsync(Guid connectionId, int database, string key, string group, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.consumers",
            secret => operations.GetStreamConsumersAsync(secret, database, key, group, ct), ct);

    public Task<PluginResult<IReadOnlyList<StreamPendingEntry>>> GetPendingEntriesAsync(Guid connectionId, int database, string key, string group, int count, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.pending",
            secret => operations.GetStreamPendingEntriesAsync(secret, database, key, group, count, ct), ct);

    public Task<PluginResult<IReadOnlyList<StreamEntry>>> GetEntriesAsync(Guid connectionId, int database, string key, string? beforeId, int count, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.entries",
            secret => operations.GetStreamEntriesAsync(secret, database, key, beforeId, count, ct), ct);

    public Task<PluginResult<StreamEntry?>> GetEntryAsync(Guid connectionId, int database, string key, string entryId, CancellationToken ct = default) =>
        HandlerRunner.QueryAsync(connections, logger, connectionId, "redis.stream.entry",
            secret => operations.GetStreamEntryAsync(secret, database, key, entryId, ct), ct);
}
