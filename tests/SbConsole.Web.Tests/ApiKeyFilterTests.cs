using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using SbConsole.Web.Auth;

namespace SbConsole.Web.Tests;

public class ApiKeyFilterTests
{
    private static EndpointFilterInvocationContext Context(string? headerValue)
    {
        var http = new DefaultHttpContext();
        if (headerValue is not null)
        {
            http.Request.Headers["X-Api-Key"] = headerValue;
        }

        return new DefaultEndpointFilterInvocationContext(http);
    }

    private static readonly EndpointFilterDelegate Next = _ => ValueTask.FromResult<object?>(Results.Ok("through"));

    [Fact]
    public async Task Correct_key_passes_through()
    {
        var result = await new ApiKeyFilter("expected").InvokeAsync(Context("expected"), Next);

        result.Should().NotBeOfType<UnauthorizedHttpResult>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("")]
    public async Task Missing_or_wrong_key_is_unauthorized(string? provided)
    {
        var result = await new ApiKeyFilter("expected").InvokeAsync(Context(provided), Next);

        result.Should().BeOfType<UnauthorizedHttpResult>();
    }
}
