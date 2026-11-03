using System.Security.Cryptography;
using System.Text;
using EPTradeBot.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EPTradeBot.Api.Security;

/// <summary>Compares a supplied admin token with the configured one in constant time.</summary>
public sealed class AdminTokenCheck(IOptions<ManagementOptions> options)
{
    public bool Matches(string? supplied)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        var expected = Encoding.UTF8.GetBytes(options.Value.AdminToken);
        var actual = Encoding.UTF8.GetBytes(supplied);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(expected));
    }
}

/// <summary>Requires the management token in <c>X-Admin-Token</c>, compared in constant time.</summary>
internal sealed class AdminTokenFilter(AdminTokenCheck check) : IEndpointFilter
{
    public const string Header = "X-Admin-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!check.Matches(context.HttpContext.Request.Headers[Header].ToString()))
        {
            return TypedResults.Problem(
                title: "Sign-in required.",
                detail: "This action needs the admin token of this EP Trade bot instance.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
