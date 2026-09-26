using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.RabbitMq.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.RabbitMq.Tests.Handlers;

public abstract class HandlerTestBase
{
    protected const string Secret = "host=rabbit;vhost=%2Forders;username=u;password=p";
    protected const string Vhost = "/orders";
    protected readonly Guid ConnectionId = Guid.NewGuid();
    protected readonly IRabbitOperations Operations = Substitute.For<IRabbitOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();

    protected HandlerTestBase()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    protected static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    protected static readonly ManagementApiException Forbidden = new(403, "GET", "/api/queues/%2Forders", "Access refused.");

    protected static string ForbiddenText => FriendlyRabbitError.From(Forbidden);

    protected static QueueSummary Queue(string name, long ready = 0) => new(
        Vhost, name, "classic", true, false, false, ready, 0, 0, null, null, null, null, null, null, null, null, false, null, null, 0, null, null,
        new Dictionary<string, object?>(), new Dictionary<string, object?>());

    protected static BindingInfo Binding(string source, string destination, string key = "") =>
        new(source, destination, "queue", key, new Dictionary<string, object?>(), key);
}
