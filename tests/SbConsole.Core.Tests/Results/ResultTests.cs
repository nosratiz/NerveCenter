using FluentAssertions;
using SbConsole.Core.Results;

namespace SbConsole.Core.Tests.Results;

public class ResultTests
{
    [Fact]
    public void Ok_result_has_value_and_no_error()
    {
        var result = Result<int>.Ok(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failed_result_carries_category_and_message()
    {
        var result = Result<int>.Fail(ErrorCategory.NotFound, "queue missing");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(new Error(ErrorCategory.NotFound, "queue missing"));
    }

    [Fact]
    public void Non_generic_result_works_for_commands()
    {
        Result.Ok().IsSuccess.Should().BeTrue();
        Result.Fail(ErrorCategory.Conflict, "exists").Error!.Category.Should().Be(ErrorCategory.Conflict);
    }
}
