namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

/// <summary>
/// One issue source type's automation settings, as the Issue to Shop Floor configuration screen reads and writes them.
/// </summary>
/// <param name="IssueSource">"Sjo", "WorkOrder", or "SalesOaf" — the row's key.</param>
/// <param name="RunMode">"Disabled", "Api", "Timer" or "Both".</param>
/// <param name="ScheduleTime">
/// Local wall-clock time the daily run fires, "HH:mm:ss", or null when the row is not on a timer.
/// </param>
/// <param name="Sites">
/// Comma-separated site ids the run targets, or null to fall back to configured default.
/// </param>
/// <param name="DocumentNumbers">
/// Comma-separated document numbers this row converts, or null for "every eligible document of this source".
/// </param>
/// <param name="LastRunReference">The <c>AutomationRun.Id</c> of the last run, for a straight link to it.</param>
public sealed record IssueToShopFloorConfigResponse(
    string IssueSource,
    string RunMode,
    TimeOnly? ScheduleTime,
    string? Sites,
    string? DocumentNumbers,
    bool IsActive,
    bool DryRun,
    int? MaxDocumentsPerRun,
    DateOnly? LastScheduledRunDate,
    DateTimeOffset? LastTriggeredAtUtc,
    string? LastRunStatus,
    Guid? LastRunReference,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>
/// A change to one issue source's automation settings. Every field is optional; an omitted field is left as it was.
/// </summary>
/// <param name="RunMode">"Disabled", "Api", "Timer" or "Both".</param>
/// <param name="ScheduleTime">
/// "HH:mm" or "HH:mm:ss". Required when the row is active and the mode is Timer or Both.
/// </param>
/// <param name="ClearScheduleTime">Removes the schedule time; distinct from omitting <paramref name="ScheduleTime"/>.</param>
/// <param name="Sites">Comma-separated positive site ids, or "" to fall back to the configured list.</param>
/// <param name="DocumentNumbers">
/// Comma-separated document numbers. Omitting it leaves the list unchanged; <paramref name="ClearDocumentNumbers"/> empties it.
/// </param>
/// <param name="ClearDocumentNumbers">Removes the document numbers list, reverting to "every eligible document of this source".</param>
/// <param name="ClearMaxDocumentsPerRun">Removes the per-run ceiling.</param>
public sealed record UpdateIssueToShopFloorConfigRequest(
    string? RunMode = null,
    string? ScheduleTime = null,
    bool ClearScheduleTime = false,
    string? Sites = null,
    string? DocumentNumbers = null,
    bool ClearDocumentNumbers = false,
    bool? IsActive = null,
    bool? DryRun = null,
    int? MaxDocumentsPerRun = null,
    bool ClearMaxDocumentsPerRun = false);

/// <summary>
/// The Issue to Shop Floor screen's master on/off toggle. <c>true</c> lets the scheduler and manual triggers convert;
/// <c>false</c> stops conversions for all three issue source types. Persisted on the <c>IsActive</c> column of all three rows.
/// </summary>
public sealed record SetIssueToShopFloorEnabledRequest(bool Enabled = false);
