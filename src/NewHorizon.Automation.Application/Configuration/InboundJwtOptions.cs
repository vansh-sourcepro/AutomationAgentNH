namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// How the agent validates the bearer token the ERP frontend (WebApp2) presents on its management
/// calls. The values mirror the ERP API's own JWT settings so a token the ERP issued at login is
/// accepted here without a second sign-in.
/// </summary>
/// <remarks>
/// Optional. When <see cref="SigningKey"/> is blank the JWT scheme is not registered and the
/// browser-facing endpoints are unavailable — the agent then serves only the machine-to-machine
/// API-key callers, exactly as it did before this feature. The real key belongs in user-secrets or
/// an environment variable on the server, never in <c>appsettings.json</c>.
/// </remarks>
public sealed class InboundJwtOptions
{
    public const string SectionName = "AutomationAgent:InboundJwt";

    public string Issuer { get; init; } = "sourcepro.issuer";

    public string Audience { get; init; } = "sourcepro.audience";

    /// <summary>The ERP's symmetric signing secret. Blank disables browser-facing JWT auth.</summary>
    public string SigningKey { get; init; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey);
}

/// <summary>
/// Browser origins allowed to call the agent's management API. Empty means no cross-origin call is
/// permitted — set it to the WebApp2 origin(s) for the environment.
/// </summary>
public sealed class AgentCorsOptions
{
    public const string SectionName = "AutomationAgent:Cors";

    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}
