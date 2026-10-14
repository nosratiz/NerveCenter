namespace SbConsole.Plugins.RabbitMq.Client;

/// <summary>
/// GET /api/queues/{vhost}/{name} plus GET /api/queues/{vhost}/{name}/bindings. Bindings include
/// the implicit default-exchange binding (Source ""), which the UI hides because it can't be
/// removed.
/// </summary>
public sealed record QueueDetails(
    QueueSummary Summary,
    IReadOnlyList<ConsumerInfo> Consumers,
    IReadOnlyList<BindingInfo> Bindings);
