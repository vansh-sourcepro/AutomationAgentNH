using System.ComponentModel.DataAnnotations;

namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// Where the ERP lives and how the agent signs in to it.
/// </summary>
/// <remarks>
/// The ERP has no client-credentials endpoint: it issues a token from the same
/// <c>/api/v1/auth/login</c> the UI uses, taking a user name, a password and the target database
/// connection string in the body. So the agent holds a real ERP user account. Everything here is
/// configuration on purpose — the agent is deployed on a private network onto the client's own
/// Windows server, and the API port in particular changes per installation, so an operator must be
/// able to correct any of it without a rebuild.
/// </remarks>
public sealed class ErpApiOptions
{
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string LoginPath { get; init; } = "/api/v1/auth/login";

    [Required(AllowEmptyStrings = false)]
    public string UserName { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// The <c>connStr</c> the login body carries: the ERP database the agent signs in against.
    /// This is the ERP's own database and the agent never opens it — only the ERP does, to resolve
    /// the login. It is not, and must never be, the automation database in
    /// <see cref="DatabaseOptions.ConnectionString"/>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string LoginConnectionString { get; init; } = string.Empty;

    /// <summary>Sent as <c>appID</c>; the ERP accepts an empty value.</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Sent as <c>userId</c>; the ERP accepts an empty value.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Sent as <c>isCEFlag</c>.</summary>
    public bool IsCeFlag { get; init; }

    /// <summary>
    /// How long a token is cached when the ERP's response carries no <c>validTo</c>. The ERP
    /// currently issues 24-hour tokens and states their expiry, so this is only a fallback.
    /// </summary>
    [Range(1, 168)]
    public int TokenTtlHours { get; init; } = 24;

    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Sent as the <c>CompanyId</c> header on every ERP call, not only login. Discovered
    /// 2026-08-18 against the live ERP: several read/write endpoints resolve which company
    /// database to open from this header alone and throw ("Connection String is not found") when
    /// it is absent — the same header the ERP UI's HTTP interceptor attaches to every request
    /// alongside the bearer token, sourced from the logged-in company. A single-company
    /// deployment (see single-tenant-deployment.md) has exactly one value here.
    /// </summary>
    public int CompanyId { get; init; } = 1;
}
