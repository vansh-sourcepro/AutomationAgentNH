namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

/// <summary>
/// Quotation to Sales Order automation configuration response.
/// </summary>
public sealed record SoAutomationConfigResponse(
    bool IsActive,
    string RunMode,
    TimeOnly? ScheduleTime,
    string? QuotationNumbers,
    string? CustomerCode,
    string? SiteCode,
    bool DryRun,
    DateOnly? LastScheduledRunDate,
    DateTimeOffset? LastTriggeredAtUtc,
    string? LastRunStatus,
    string? LastRunMessage,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>
/// Update payload for Quotation to Sales Order automation configuration.
/// </summary>
public sealed record UpdateSoAutomationRequest(
    string? RunMode = null,
    string? ScheduleTime = null,
    bool ClearScheduleTime = false,
    string? QuotationNumbers = null,
    bool ClearQuotationNumbers = false,
    string? CustomerCode = null,
    bool ClearCustomerCode = false,
    string? SiteCode = null,
    bool ClearSiteCode = false,
    bool? IsActive = null,
    bool? DryRun = null);

/// <summary>
/// Master on/off toggle payload.
/// </summary>
public sealed record SetSoAutomationEnabledRequest(bool Enabled = false);
