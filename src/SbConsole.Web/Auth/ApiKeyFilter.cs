using System.Security.Cryptography;
using System.Text;

namespace SbConsole.Web.Auth;

public sealed class ApiKeyFilter(string expectedKey) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var provided)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided.ToString()),
                Encoding.UTF8.GetBytes(expectedKey)))
        {
            return TypedResults.Unauthorized();
        }

        return await next(context);
    }
}
