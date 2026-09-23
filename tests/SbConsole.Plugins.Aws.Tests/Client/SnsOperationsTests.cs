using FluentAssertions;
using SbConsole.Plugins.Aws.Client;

namespace SbConsole.Plugins.Aws.Tests.Client;

public class SnsOperationsTests
{
    [Fact]
    public void TopicNameFromArn_returns_the_last_segment()
    {
        SnsOperations.TopicNameFromArn("arn:aws:sns:eu-west-1:123456789012:order-events-topic")
            .Should().Be("order-events-topic");
    }

    [Fact]
    public async Task ListTopicsAsync_against_an_unreachable_endpoint_throws_a_friendly_exception()
    {
        var ops = new SnsOperations();

        var act = () => ops.ListTopicsAsync("mode=access-keys;region=us-east-1;accessKeyId=AKIAFAKE;secretAccessKey=fake;endpoint=http://127.0.0.1:1");

        // Real network calls can't be unit-tested without a broker/emulator (same "light coverage
        // by necessity" limitation the SQS plan's SqsOperations accepted) -- this only pins that a
        // connection failure surfaces as SOME exception, not a hang or a silently-empty list.
        await act.Should().ThrowAsync<Exception>();
    }
}
