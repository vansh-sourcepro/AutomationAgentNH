using System.Security.Cryptography;
using System.Text;
using NewHorizon.Automation.Application.Configuration;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// Rejects any management call that does not present the shared inbound key.
/// </summary>
/// <remarks>
/// The inner half of the ERP → Agent boundary; loopback binding is the outer half. Loopback alone
/// is not enough — it keeps other machines out, but every process on this server is on loopback
/// too, and these endpoints can start cycles and cancel jobs.
/// <para>
/// Health is exempt: a service monitor must be able to reach it, and it discloses no job data.
/// </para>
/// </remarks>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string HeaderName = "X-Automation-Api-Key";

    private readonly byte[] _expected;

    public ApiKeyFilter(AutomationAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _expected = Encoding.UTF8.GetBytes(options.Host.InboundApiKey ?? string.Empty);
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var presented))
        {
            return Results.Problem(
                $"The {HeaderName} header is required.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Fixed-time comparison: a length-or-prefix comparison leaks the key one byte at a time to
        // anything that can time the response.
        var candidate = Encoding.UTF8.GetBytes(presented.ToString());

        if (!CryptographicOperations.FixedTimeEquals(candidate, _expected))
        {
            return Results.Problem("The API key is not valid.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
