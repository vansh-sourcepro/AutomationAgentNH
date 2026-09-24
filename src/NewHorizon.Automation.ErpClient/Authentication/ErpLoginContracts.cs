using System.Text.Json.Serialization;

namespace NewHorizon.Automation.ErpClient.Authentication;

/// <summary>
/// Body posted to the ERP login endpoint. The property names are the ERP's, not ours — it is the
/// same contract the ERP UI posts, so the casing (<c>appID</c>, <c>isCEFlag</c>) is copied exactly.
/// </summary>
public sealed record ErpLoginRequest
{
    [JsonPropertyName("userName")]
    public required string UserName { get; init; }

    [JsonPropertyName("password")]
    public required string Password { get; init; }

    /// <summary>The ERP database the login resolves against.</summary>
    [JsonPropertyName("connStr")]
    public required string ConnectionString { get; init; }

    [JsonPropertyName("isCEFlag")]
    public bool IsCeFlag { get; init; }

    [JsonPropertyName("appID")]
    public string AppId { get; init; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;
}

/// <summary>
/// The ERP's standard response envelope. A refusal arrives as HTTP 400 with
/// <c>success = false</c> and a message key, so the status code alone never decides the outcome.
/// </summary>
public sealed record ErpLoginResponse
{
    [JsonPropertyName("data")]
    public ErpLoginData? Data { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    /// <summary>Whichever explanation the ERP filled in, for the technical log line.</summary>
    public string? Reason => Message ?? ErrorMessage;
}

public sealed record ErpLoginData
{
    [JsonPropertyName("token")]
    public ErpLoginToken? Token { get; init; }

    /// <summary>
    /// A per-session GUID, not a user id — verified against the live ERP on 2026-08-18, which
    /// answered with <c>70327653-4662-4818-9b59-f77caa0f3ab2</c>. Kept for correlation only; the
    /// numeric id documents are stamped with is <see cref="ErpLoginUser.Id"/>.
    /// </summary>
    [JsonPropertyName("uid")]
    public string? Uid { get; init; }

    [JsonPropertyName("user")]
    public ErpLoginUser? User { get; init; }
}

/// <summary>
/// Who the agent signed in as. The ERP sends a great deal more here — the whole menu tree, among
/// other things — but only the identity matters to the agent.
/// </summary>
public sealed record ErpLoginUser
{
    /// <summary>
    /// The numeric ERP user id, the value documents record as their creator. It also appears as the
    /// JWT's <c>id</c> claim, but reading it from the response body avoids decoding the token.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("userName")]
    public string? UserName { get; init; }
}

public sealed record ErpLoginToken
{
    /// <summary>The bearer token itself.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>
    /// Absolute expiry stated by the ERP (currently issue time + 24 hours). Preferred over any
    /// configured lifetime: only the ERP knows when it stops honouring the token.
    /// </summary>
    [JsonPropertyName("validTo")]
    public DateTimeOffset? ValidTo { get; init; }
}

/// <summary>
/// Body posted to <c>ErpEndpointOptions.AddUserSession</c> right after login. Property names and
/// casing are the ERP's own (<c>MConWebUserModel</c>) — this is the same call the ERP UI's
/// <c>myProfileService.addSession</c> makes immediately after <c>auth/login</c>, to create the
/// database-backed session row a later pipeline middleware requires on every other call.
/// </summary>
public sealed record ErpAddUserSessionRequest
{
    [JsonPropertyName("mcnuserid")]
    public required string UserName { get; init; }

    [JsonPropertyName("mcncompcode")]
    public string CompanyCode { get; init; } = string.Empty;

    [JsonPropertyName("mcndefloccode")]
    public string DefaultLocationCode { get; init; } = string.Empty;

    /// <summary>The login response's <c>data.uid</c> — what the session-gate middleware matches on.</summary>
    [JsonPropertyName("mcnsessionid")]
    public required string SessionId { get; init; }

    [JsonPropertyName("mcnhostip")]
    public string HostIp { get; init; } = "127.0.0.1";

    [JsonPropertyName("mcnhostnm")]
    public string HostName { get; init; } = Environment.MachineName;

    [JsonPropertyName("mcnhostbrowser")]
    public string HostBrowser { get; init; } = "NewHorizon.Automation.Agent";

    [JsonPropertyName("mcnhostos")]
    public string HostOs { get; init; } = "Windows Service";

    [JsonPropertyName("mcnhostdevice")]
    public string HostDevice { get; init; } = "AutomationAgent";
}

/// <summary>The ERP's generic response envelope, for calls whose payload the agent does not need.</summary>
public sealed record ErpGenericResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    public string? Reason => Message ?? ErrorMessage;
}
