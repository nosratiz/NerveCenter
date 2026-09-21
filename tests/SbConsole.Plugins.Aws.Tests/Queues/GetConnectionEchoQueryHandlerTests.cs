using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Aws.Queues;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Queues;

public class GetConnectionEchoQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_safe_echo_of_the_connections_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns("mode=access-keys;region=eu-west-1;accessKeyId=AKIA123;secretAccessKey=s3cr3t");

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("region=eu-west-1 · mode=access-keys");
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }
}
