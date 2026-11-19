using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

/// <summary>
/// One issue source type's automation settings: whether it may run unattended, when the daily run fires,
/// and what it targets. Exactly three rows exist — Sjo, WorkOrder, SalesOaf.
/// </summary>
public sealed class IssueToShopFloorAutomationConfig
{
    // Materialisation constructor for EF Core.
    private IssueToShopFloorAutomationConfig()
    {
    }

    private IssueToShopFloorAutomationConfig(Guid id, IssueToShopFloorSource source, DateTimeOffset nowUtc)
    {
        Id = id;
        Source = source;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The source document family this row governs: Sjo, WorkOrder, or SalesOaf.</summary>
    public IssueToShopFloorSource Source { get; private set; }

    /// <summary>
    /// Run mode: Api (Document-based / manual trigger), Timer (Timer-based scheduler), Both, or Disabled.
    /// </summary>
    public PoAutomationRunMode RunMode { get; private set; } = PoAutomationRunMode.Api;

    /// <summary>Local wall-clock time the daily run fires, "HH:mm:ss", or null when not on a timer.</summary>
    public TimeOnly? ScheduleTime { get; private set; }

    /// <summary>Comma-separated site ids. Null means "use configured default".</summary>
    public string? Sites { get; private set; }

    /// <summary>
    /// Comma-separated document numbers this row converts. Null/empty means "every eligible document of this source".
    /// </summary>
    public string? DocumentNumbers { get; private set; }

    /// <summary>
    /// The master switch / per-source active toggle. False stops scheduled and manual conversions.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>When true, a run reports what it would create and creates nothing.</summary>
    public bool DryRun { get; private set; }

    /// <summary>Ceiling on documents converted per run. Null means no ceiling.</summary>
    public int? MaxDocumentsPerRun { get; private set; }

    /// <summary>The local date the scheduler last fired this row, so a missed slot runs once and no more.</summary>
    public DateOnly? LastScheduledRunDate { get; private set; }

    /// <summary>When any run was last started for this source.</summary>
    public DateTimeOffset? LastTriggeredAtUtc { get; private set; }

    /// <summary><see cref="RunStatus"/> of the last run, as text, for UI status display.</summary>
    public string? LastRunStatus { get; private set; }

    /// <summary>The <c>AutomationRun.Id</c> of the last run, so the UI can link straight to it.</summary>
    public Guid? LastRunReference { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public static IssueToShopFloorAutomationConfig CreateDefault(IssueToShopFloorSource source, DateTimeOffset nowUtc) =>
        new(Guid.NewGuid(), source, nowUtc);

    /// <summary>
    /// True when the scheduler should start a run for this source right now.
    /// </summary>
    public bool ShouldRunOnSchedule(TimeOnly localNow, DateOnly localToday) =>
        IsActive
        && RunMode.Allows(TriggerSource.Timer)
        && ScheduleTime is { } slot
        && localNow >= slot
        && (LastScheduledRunDate is null || LastScheduledRunDate < localToday);

    /// <summary>Applies a partial update, validating what the combination requires.</summary>
    public void Update(IssueToShopFloorAutomationConfigUpdate update, DateTimeOffset nowUtc, string? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(update);

        var scheduleBefore = ScheduleTime;

        RunMode = update.RunMode ?? RunMode;
        IsActive = update.IsActive ?? IsActive;
        DryRun = update.DryRun ?? DryRun;

        if (update.ScheduleTime is not null)
        {
            ScheduleTime = update.ScheduleTime;
        }

        if (update.ClearScheduleTime)
        {
            ScheduleTime = null;
        }

        if (ScheduleTime is not null && ScheduleTime != scheduleBefore)
        {
            LastScheduledRunDate = null;
        }

        if (update.Sites is not null)
        {
            Sites = NormaliseSites(update.Sites);
        }

        if (update.DocumentNumbers is not null)
        {
            DocumentNumbers = NormaliseDocumentNumbers(update.DocumentNumbers);
        }

        if (update.ClearDocumentNumbers)
        {
            DocumentNumbers = null;
        }

        if (update.MaxDocumentsPerRun is { } ceiling)
        {
            if (ceiling <= 0)
            {
                throw new DomainException($"{nameof(MaxDocumentsPerRun)} must be greater than zero.");
            }

            MaxDocumentsPerRun = ceiling;
        }

        if (update.ClearMaxDocumentsPerRun)
        {
            MaxDocumentsPerRun = null;
        }

        switch (RunMode)
        {
            case PoAutomationRunMode.Disabled:
                ScheduleTime = null;
                IsActive = false;
                break;
            case PoAutomationRunMode.Api:
                ScheduleTime = null;
                break;
            case PoAutomationRunMode.Timer:
                DocumentNumbers = null;
                break;
        }

        UpdatedAtUtc = nowUtc;
        UpdatedBy = updatedBy;
    }

    /// <summary>Records that the scheduler fired this row today, with the run it opened.</summary>
    public void MarkScheduledRun(DateOnly localToday, DateTimeOffset nowUtc, RunStatus status, Guid? runId)
    {
        LastScheduledRunDate = localToday;
        RecordRun(nowUtc, status, runId);
    }

    /// <summary>Records a manual "Run now" or an API-triggered run, which does not touch the scheduler's date.</summary>
    public void MarkManualRun(DateTimeOffset nowUtc, RunStatus status, Guid? runId) =>
        RecordRun(nowUtc, status, runId);

    private void RecordRun(DateTimeOffset nowUtc, RunStatus status, Guid? runId)
    {
        LastTriggeredAtUtc = nowUtc;
        LastRunStatus = status.ToString();
        LastRunReference = runId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Records that the scheduler fired this row today, with the run it opened.</summary>
    public void RecordScheduledRun(DateOnly localToday, Guid? runId, DateTimeOffset nowUtc)
    {
        LastScheduledRunDate = localToday;
        RecordRun(nowUtc, RunStatus.Running, runId);
    }

    /// <summary>Records that a manual or API run was started for this row.</summary>
    public void RecordTriggered(Guid? runId, DateTimeOffset nowUtc) =>
        RecordRun(nowUtc, RunStatus.Running, runId);

    /// <summary>Records the terminal status of the run that was started for this row.</summary>
    public void RecordRunCompleted(RunStatus status, DateTimeOffset nowUtc)
    {
        LastRunStatus = status.ToString();
        UpdatedAtUtc = nowUtc;
    }

    private static string? NormaliseSites(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) && id > 0 ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        return parsed.Count == 0 ? null : string.Join(',', parsed);
    }

    private static string? NormaliseDocumentNumbers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parsed.Count == 0 ? null : string.Join(',', parsed);
    }

    /// <summary>The configured sites as integers, or an empty list when the row defers to configuration.</summary>
    public IReadOnlyList<int> SiteIds() =>
        string.IsNullOrWhiteSpace(Sites)
            ? []
            : [.. Sites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)];

    /// <summary>
    /// The configured document numbers, or an empty list when the row names none — in which case the
    /// run converts every eligible document of <see cref="Source"/>.
    /// </summary>
    public IReadOnlyList<string> DocumentNumberList() =>
        string.IsNullOrWhiteSpace(DocumentNumbers)
            ? []
            : [.. DocumentNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
