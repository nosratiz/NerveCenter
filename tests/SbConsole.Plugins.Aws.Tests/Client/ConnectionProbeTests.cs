using Amazon.SimpleNotificationService;
using FluentAssertions;
using SbConsole.Plugins.Aws.Client;
using SbConsole.Sdk;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class ConnectionProbeTests
{
    [Fact]
    public async Task A_successful_probe_is_a_Passed_check_carrying_the_probe_detail()
    {
        var check = await ConnectionProbe.RunAsync("Topics visible", _ => Task.FromResult("3"), CancellationToken.None);

        check.Should().Be(new ConnectionCheck("Topics visible", ConnectionCheckStatus.Passed, "3"));
    }

    [Fact]
    public async Task A_denied_probe_is_a_Failed_check_with_a_friendly_message_and_never_throws()
    {
        var check = await ConnectionProbe.RunAsync(
            "Topics visible",
            _ => throw new AmazonSimpleNotificationServiceException("User is not authorized to perform: sns:ListTopics") { ErrorCode = "AuthorizationError" },
            CancellationToken.None);

        check.Label.Should().Be("Topics visible");
        check.Status.Should().Be(ConnectionCheckStatus.Failed);
        check.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_becoming_a_Failed_check()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => ConnectionProbe.RunAsync("Topics visible", ct => Task.FromCanceled<string>(ct), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
