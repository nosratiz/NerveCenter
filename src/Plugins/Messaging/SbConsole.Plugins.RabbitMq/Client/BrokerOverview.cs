namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// GET /api/overview, cluster-wide. Rates are null when the broker has not produced them yet (a
/// fresh node needs two stats samples) -- the UI renders null as "—", never 0. The churn counts are
/// cumulative since node start: the API exposes no hour window, so none is invented.
/// </summary>
public sealed record BrokerOverview(
    string ClusterName,
    string RabbitVersion,
    string? ErlangVersion,
    double? PublishRate,
    double? DeliverRate,
    double? AckRate,
    double? UnroutableRate,
    long Connections,
    long Channels,
    long Queues,
    long Exchanges,
    long Consumers,
    long MessagesReady,
    long MessagesUnacked,
    long ConnectionsOpened,
    long ConnectionsClosed,
    long ChannelsOpened);
