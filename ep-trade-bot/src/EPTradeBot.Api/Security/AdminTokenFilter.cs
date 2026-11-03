using System.Security.Cryptography;
using System.Text;
using EPTradeBot.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Api.Security;

/// <summary>Requires the management token in <c>X-Admin-Token</c>, compared in constant time.</summary>
internal sealed class AdminTokenFilter(IOptions<ManagementOptions> options) : IEndpointFilter
{
    public const string Header = "X-Admin-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var supplied = context.HttpContext.Request.Headers[Header].ToString();
        var expected = Encoding.UTF8.GetBytes(options.Value.AdminToken);
        var actual = Encoding.UTF8.GetBytes(supplied);
        if (actual.Length == 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(expected)))
        {
            return TypedResults.Problem(
                title: "Sign-in required.",
                detail: "This action needs the admin token of this EP Trade bot instance.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
