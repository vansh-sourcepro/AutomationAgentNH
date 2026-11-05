using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NewHorizon.Automation.Application.Configuration;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// Passes when the caller presents <em>either</em> a valid ERP bearer token <em>or</em> the shared
/// inbound API key.
/// </summary>
/// <remarks>
/// The read endpoints the ERP frontend needs — the conversion history, the eligible-indent list,
/// the dashboard — were built for the API-key machine callers and are now also reached from a
/// browser carrying an ERP JWT. This keeps both working without splitting each endpoint in two.
/// Authentication middleware has already run, so a valid token shows up as
/// <see cref="HttpContext.User"/>; the API-key branch is the same fixed-time comparison
/// <see cref="ApiKeyFilter"/> uses.
/// </remarks>
public sealed class ErpUserOrApiKeyFilter : IEndpointFilter
{
    private readonly byte[] _expected;
    private readonly byte[] _signingKey;

    public ErpUserOrApiKeyFilter(AutomationAgentOptions options, InboundJwtOptions? inboundJwt = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _expected = Encoding.UTF8.GetBytes(options.Host.InboundApiKey ?? string.Empty);
        _signingKey = Encoding.ASCII.GetBytes(!string.IsNullOrWhiteSpace(inboundJwt?.SigningKey) ? inboundJwt.SigningKey : "sourcepro-secret-key");
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            return await next(context);
        }

        if (context.HttpContext.Request.Headers.TryGetValue(ApiKeyFilter.HeaderName, out var presented))
        {
            var candidate = Encoding.UTF8.GetBytes(presented.ToString());

            if (CryptographicOperations.FixedTimeEquals(candidate, _expected))
            {
                return await next(context);
            }
        }

        // Direct fallback: verify inbound bearer token against the ERP signing secret
        if (context.HttpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authStr.Substring(7).Trim();
                var parts = token.Split('.');
                if (parts.Length == 3)
                {
                    try
                    {
                        var provided = Base64UrlEncoder.DecodeBytes(parts[2]);
                        var computed = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));

                        if (CryptographicOperations.FixedTimeEquals(provided, computed))
                        {
                            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "ErpUser") }, "ErpBearer");
                            context.HttpContext.User = new ClaimsPrincipal(identity);
                            return await next(context);
                        }
                    }
                    catch
                    {
                        // Fall through to 401
                    }
                }
            }
        }

        return Results.Problem(
            $"A valid bearer token or the {ApiKeyFilter.HeaderName} header is required.",
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
