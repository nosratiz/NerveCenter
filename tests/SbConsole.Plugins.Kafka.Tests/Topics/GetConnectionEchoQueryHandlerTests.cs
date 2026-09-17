using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using NSubstitute;
using SbConsole.Plugins.Kafka.Topics;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Kafka.Tests.Topics;

public class GetConnectionEchoQueryHandlerTests
{
    [Fact]
    public async Task Returns_the_safe_echo_for_the_connections_decrypted_secret()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns("bootstrap.servers=real:9092;security.protocol=SASL_SSL");

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("bootstrap.servers=real:9092 · security.protocol=SASL_SSL");
    }

    [Fact]
    public async Task Unknown_connection_returns_a_failure_not_an_empty_string()
    {
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task The_decrypted_secrets_password_is_never_exposed_through_the_result_value()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IConnectionProvider>();
        connections.GetSecretAsync(connectionId, Arg.Any<CancellationToken>())
            .Returns("bootstrap.servers=real:9092;sasl.username=alice;sasl.password=s3cr3tPW");

        var result = await new GetConnectionEchoQueryHandler(connections, NullLogger<GetConnectionEchoQueryHandler>.Instance).HandleAsync(connectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("bootstrap.servers=real:9092");
        result.Value.Should().NotContain("s3cr3tPW");
        result.Value.Should().NotContain("alice");
    }
}
