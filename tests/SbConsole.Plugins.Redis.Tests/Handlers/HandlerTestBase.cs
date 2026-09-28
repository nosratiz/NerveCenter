using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Plugins.Redis.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests.Handlers;

public abstract class HandlerTestBase
{
    protected const string Secret = "standalone|0|localhost:6379,password=hunter2";
    protected const int Database = 3;
    protected readonly Guid ConnectionId = Guid.NewGuid();
    protected readonly IRedisOperations Operations = Substitute.For<IRedisOperations>();
    protected readonly IConnectionProvider Connections = Substitute.For<IConnectionProvider>();
    protected readonly IAuditScope Audit = Substitute.For<IAuditScope>();

    protected HandlerTestBase()
    {
        Connections.GetSecretAsync(ConnectionId, Arg.Any<CancellationToken>()).Returns(Secret);
    }

    protected static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    protected static readonly TimeoutException Timeout = new("Redis command timed out");

    protected static string TimeoutText => FriendlyRedisError.From(Timeout);
}
