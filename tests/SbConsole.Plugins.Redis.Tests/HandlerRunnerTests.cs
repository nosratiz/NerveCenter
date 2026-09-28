using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Redis.Tests;

public class HandlerRunnerTests
{
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly IConnectionProvider _connections = Substitute.For<IConnectionProvider>();

    [Fact]
    public async Task A_secret_decryption_failure_is_caught_and_returned_as_a_failed_result()
    {
        // Matches RabbitMQ's HandlerRunner.QueryAsync convention: GetSecretAsync is called INSIDE
        // the try block, because a decryption failure (e.g. a rotated SBC_DATA_KEY) must be caught
        // and converted to a friendly PluginResult.Fail, not propagate raw out of QueryAsync and
        // crash the calling page's OnInitializedAsync, which has no guard of its own.
        _connections.GetSecretAsync(_connectionId, Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new InvalidOperationException("decryption failed"));

        var result = await HandlerRunner.QueryAsync(
            _connections,
            NullLogger.Instance,
            _connectionId,
            "redis.test.query",
            _ => Task.FromResult("unused"),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }
}
