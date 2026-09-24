using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.ErpClient;

namespace NewHorizon.Automation.Worker.Services;

/// <summary>
/// Resolves the effective ERP form rights of the user behind the current request, by asking the ERP
/// with that user's own bearer token. The ERP is the single source of truth for role rights — the
/// same one the login menu is filtered from — and its JWT carries none of them, so the agent has to
/// ask.
/// </summary>
public interface IErpUserRightsService
{
    /// <summary>
    /// The right letters (e.g. <c>"EI"</c>) the current caller holds for <paramref name="formId"/>,
    /// or <see cref="string.Empty"/> when the role grants nothing — including when the ERP could not
    /// be reached, so the caller fails closed.
    /// </summary>
    Task<string> RightsForAsync(HttpContext httpContext, string formId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ErpUserRightsService : IErpUserRightsService
{
    /// <summary>Named <see cref="HttpClient"/> — no auth handler: it forwards the caller's token, not the agent's.</summary>
    public const string HttpClientName = "ErpFormRights";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly string _formRightsPath;
    private readonly string _companyId;
    private readonly ILogger<ErpUserRightsService> _logger;

    public ErpUserRightsService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptions<ErpEndpointOptions> endpoints,
        AutomationAgentOptions agentOptions,
        ILogger<ErpUserRightsService> logger)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(agentOptions);

        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _formRightsPath = endpoints.Value.FormRights;
        _companyId = agentOptions.ErpApi.CompanyId.ToString(CultureInfo.InvariantCulture);
        _logger = logger;
    }

    public async Task<string> RightsForAsync(HttpContext httpContext, string formId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var map = await GetRightsMapAsync(httpContext, cancellationToken);

        return map.TryGetValue(formId, out var letters) ? letters ?? string.Empty : string.Empty;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetRightsMapAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var cacheKey = "erp-form-rights::" + (
            user.FindFirst("uid")?.Value
            ?? user.FindFirst("id")?.Value
            ?? user.FindFirst("userName")?.Value
            ?? "unknown");

        if (_cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, string>? cached) && cached is not null)
        {
            return cached;
        }

        var map = await FetchAsync(httpContext, cancellationToken);

        // Cache the reachable-and-answered result only; a fail-closed empty from an ERP blip should
        // be retried on the next request, not held for a minute.
        if (map.Count > 0)
        {
            _cache.Set(cacheKey, map, CacheTtl);
        }

        return map;
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var authorization)
            || string.IsNullOrWhiteSpace(authorization.ToString()))
        {
            return None;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, _formRightsPath);
            request.Headers.TryAddWithoutValidation("Authorization", authorization.ToString());

            // Several ERP endpoints resolve which company database to open from this header alone —
            // the same one the ERP UI and the agent's own ErpAuthHandler attach to every call.
            request.Headers.TryAddWithoutValidation("CompanyId", _companyId);

            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "The ERP form-rights endpoint answered {StatusCode}; the PO Automation request is denied (fail closed).",
                    (int)response.StatusCode);

                return None;
            }

            var payload = await response.Content.ReadFromJsonAsync<FormRightsResponse>(cancellationToken);

            return payload?.Data is { Count: > 0 } data ? data : None;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(
                ex,
                "Could not read the caller's rights from the ERP; the PO Automation request is denied (fail closed).");

            return None;
        }
    }

    /// <summary>The <c>GenericResult&lt;Dictionary&lt;string,string&gt;&gt;</c> the ERP endpoint returns.</summary>
    private sealed record FormRightsResponse(Dictionary<string, string>? Data);
}
