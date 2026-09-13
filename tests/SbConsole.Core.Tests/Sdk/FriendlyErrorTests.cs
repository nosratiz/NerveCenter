using FluentAssertions;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class FriendlyErrorTests
{
    [Fact]
    public void Short_message_passes_through_unchanged()
    {
        FriendlyError.From(new InvalidOperationException("queue already exists"))
            .Should().Be("queue already exists");
    }

    [Fact]
    public void Long_message_is_truncated_and_marked()
    {
        var result = FriendlyError.From(new InvalidOperationException(new string('x', 3_000)));

        result.Should().HaveLength(FriendlyError.MaxLength + FriendlyError.TruncationMarker.Length);
        result.Should().StartWith(new string('x', FriendlyError.MaxLength));
        result.Should().EndWith(FriendlyError.TruncationMarker);
    }

    [Fact]
    public void Exactly_max_length_is_not_marked_as_truncated()
    {
        FriendlyError.From(new InvalidOperationException(new string('x', FriendlyError.MaxLength)))
            .Should().NotContain(FriendlyError.TruncationMarker);
    }

    [Fact]
    public void Newlines_are_collapsed_so_a_table_cell_cannot_wrap()
    {
        FriendlyError.From(new InvalidOperationException("first line\n  second line\r\n\tthird"))
            .Should().Be("first line second line third");
    }

    [Fact]
    public void Uses_only_the_exceptions_own_message_so_no_stack_trace_can_leak()
    {
        // Message vs ToString() is the whole point: a thrown exception's ToString() carries the
        // stack trace (live testing produced a 2,608-character snackbar full of
        // "at Microsoft.Azure.Amqp..." frames), and Message never does.
        Exception thrown;
        try
        {
            throw new InvalidOperationException("broker said no");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.ToString().Should().Contain("FriendlyErrorTests"); // the frame is genuinely present
        FriendlyError.From(thrown).Should().Be("broker said no");
        FriendlyError.From(thrown).Should().NotContain("   at ");
    }

    [Fact]
    public void An_aggregate_exceptions_repeated_inner_messages_are_capped()
    {
        // The live "namespace unreachable" shape: Azure.Core's RetryPolicy throws an
        // AggregateException whose Message appends every inner message, one per attempt.
        var inner = new Exception(new string('y', 400));
        var aggregate = new AggregateException("Retry failed after 4 tries.", inner, inner, inner, inner);

        var result = FriendlyError.From(aggregate);

        result.Length.Should().BeLessThan(aggregate.Message.Length);
        result.Should().EndWith(FriendlyError.TruncationMarker);
    }

    [Fact]
    public void An_exception_with_no_message_still_yields_something_readable()
    {
        FriendlyError.From(new OperationCanceledException(""))
            .Should().Be("OperationCanceledException" + FriendlyError.TruncationMarker);
    }

    [Fact]
    public void Truncate_leaves_a_null_message_null()
    {
        // A successful ConnectionTestResult has no ErrorMessage; truncation must not invent one.
        FriendlyError.Truncate(null).Should().BeNull();
    }
}
