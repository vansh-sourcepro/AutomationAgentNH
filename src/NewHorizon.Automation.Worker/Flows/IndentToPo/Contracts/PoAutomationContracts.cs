namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;

/// <summary>
/// One indent type's automation settings, as the PO Automation screen reads and writes them.
/// </summary>
/// <param name="IndentType">"Regular", "Capital" or "Service" — the row's key.</param>
/// <param name="RunMode">"Disabled", "Api", "Timer" or "Both".</param>
/// <param name="ScheduleTime">
/// Local wall-clock time the daily run fires, "HH:mm:ss", or null when the row is not on a timer.
/// </param>
/// <param name="Sites">
/// Comma-separated site ids the run targets, or null to use <c>AutomationAgent:PurchaseOrder:Sites</c>.
/// </param>
/// <param name="IndentNumbers">
/// Comma-separated indent numbers this row converts, or null for "every eligible indent of this type".
/// </param>
/// <param name="LastRunReference">The <c>AutomationRun.Id</c> of the last run, for a straight link to it.</param>
public sealed record PoAutomationConfigResponse(
    string IndentType,
    string RunMode,
    TimeOnly? ScheduleTime,
    string? Sites,
    string? IndentNumbers,
    bool IsActive,
    bool DryRun,
    int? MaxIndentsPerRun,
    DateOnly? LastScheduledRunDate,
    DateTimeOffset? LastTriggeredAtUtc,
    string? LastRunStatus,
    Guid? LastRunReference,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>
/// A change to one indent type's automation settings. Every field is optional; an omitted field is
/// left as it was.
/// </summary>
/// <param name="RunMode">"Disabled", "Api", "Timer" or "Both".</param>
/// <param name="ScheduleTime">
/// "HH:mm" or "HH:mm:ss". Required when the row is active and the mode is Timer or Both.
/// </param>
/// <param name="ClearScheduleTime">Removes the schedule time; distinct from omitting <paramref name="ScheduleTime"/>.</param>
/// <param name="Sites">Comma-separated positive site ids, or "" to fall back to the configured list.</param>
/// <param name="IndentNumbers">
/// Comma-separated indent numbers (whole <c>26-27/PI/NF1/000162</c> or bare <c>000162</c>). Omitting
/// it leaves the list unchanged; <paramref name="ClearIndentNumbers"/> empties it.
/// </param>
/// <param name="ClearIndentNumbers">Removes the indent-number list, reverting to "every eligible indent of this type".</param>
/// <param name="ClearMaxIndentsPerRun">Removes the per-run ceiling.</param>
public sealed record UpdatePoAutomationRequest(
    string? RunMode = null,
    string? ScheduleTime = null,
    bool ClearScheduleTime = false,
    string? Sites = null,
    string? IndentNumbers = null,
    bool ClearIndentNumbers = false,
    bool? IsActive = null,
    bool? DryRun = null,
    int? MaxIndentsPerRun = null,
    bool ClearMaxIndentsPerRun = false);

/// <summary>
/// The PO Automation screen's master on/off toggle. <c>true</c> lets the scheduler and the "Run"
/// button convert; <c>false</c> stops every scheduled and manual conversion for all three indent
/// types. Persisted on the <c>IsActive</c> column of all three rows.
/// </summary>
public sealed record SetPoAutomationEnabledRequest(bool Enabled = false);
