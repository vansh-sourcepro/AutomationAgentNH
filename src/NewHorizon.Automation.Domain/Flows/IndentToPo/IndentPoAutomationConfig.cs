using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>
/// One indent type's automation settings: whether it may run unattended, when the daily run fires,
/// and what it targets. Exactly three rows exist — Regular, Capital, Service — seeded by migration
/// and never created or deleted through the API.
/// </summary>
/// <remarks>
/// A sibling of <c>AutomationConfig</c> rather than a column on it: <c>AutomationConfig</c> is the
/// master licence / enable / retention switch keyed by module name, while this is the per-type
/// schedule the buyer maintains. Read fresh at the start of each scheduler tick, never cached.
/// </remarks>
public sealed class IndentPoAutomationConfig
{
    // Materialisation constructor for EF Core.
    private IndentPoAutomationConfig()
    {
    }

    private IndentPoAutomationConfig(Guid id, IndentKind indentKind, DateTimeOffset nowUtc)
    {
        Id = id;
        IndentKind = indentKind;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The indent family this row governs. The natural key — one row per value.</summary>
    public IndentKind IndentKind { get; private set; }

    /// <summary>
    /// "Indent-based" (<see cref="PoAutomationRunMode.Api"/>) — Run button only; "Timer-based"
    /// (<see cref="PoAutomationRunMode.Timer"/>) — scheduler only; <see cref="PoAutomationRunMode.Both"/>.
    /// (<see cref="PoAutomationRunMode.Disabled"/> is legacy and no longer offered by the screen.)
    /// </summary>
    public PoAutomationRunMode RunMode { get; private set; } = PoAutomationRunMode.Api;

    /// <summary>Local wall-clock time the daily run fires. Required once the row is active on a timer.</summary>
    public TimeOnly? ScheduleTime { get; private set; }

    /// <summary>Comma-separated site ids. Null means "use <c>AutomationAgent:PurchaseOrder:Sites</c>".</summary>
    public string? Sites { get; private set; }

    /// <summary>
    /// Comma-separated indent numbers this row converts — either the whole document number
    /// (<c>26-27/PI/NF1/000162</c>) or the bare running number (<c>000162</c>). Null / empty means
    /// "every eligible authorised indent of <see cref="IndentKind"/>", which is what the conversion
    /// endpoint does when no numbers are named.
    /// </summary>
    public string? IndentNumbers { get; private set; }

    /// <summary>
    /// The master on/off switch, driven by the PO Automation screen's toggle (set on all three rows
    /// together via <c>PUT /api/automation/po-automation/enabled</c>). False ⇒ the scheduler skips
    /// this type (<see cref="ShouldRunOnSchedule"/>) and a manual or timer <c>/convert</c> is refused.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>When true, a run reports what it would create and creates nothing.</summary>
    public bool DryRun { get; private set; }

    /// <summary>Ceiling on indents converted per run. Null lets the sweep keep its own default.</summary>
    public int? MaxIndentsPerRun { get; private set; }

    /// <summary>The local date the scheduler last fired this row, so a missed slot runs once and no more.</summary>
    public DateOnly? LastScheduledRunDate { get; private set; }

    /// <summary>When any run (scheduled or manual) was last started for this type.</summary>
    public DateTimeOffset? LastTriggeredAtUtc { get; private set; }

    /// <summary><see cref="RunStatus"/> of the last run, as text, for the UI's "last result" column.</summary>
    public string? LastRunStatus { get; private set; }

    /// <summary>The <c>AutomationRun.Id</c> of the last run, so the UI can link straight to it.</summary>
    public Guid? LastRunReference { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public static IndentPoAutomationConfig CreateDefault(IndentKind indentKind, DateTimeOffset nowUtc) =>
        new(Guid.NewGuid(), indentKind, nowUtc);

    /// <summary>
    /// True when the scheduler should start a run for this type right now: PO Automation is switched
    /// on (<see cref="IsActive"/> — the master toggle the PO Automation screen persists), the mode
    /// admits a timer (<c>Timer</c> or <c>Both</c>), a schedule time is set, its slot has passed
    /// today, and it has not already run today. An <c>Api</c> ("Indent-based") row never runs
    /// automatically — only the screen's "Run" button fires it — and nothing runs while the master
    /// toggle is off.
    /// </summary>
    public bool ShouldRunOnSchedule(TimeOnly localNow, DateOnly localToday) =>
        IsActive
        && RunMode.Allows(TriggerSource.Timer)
        && ScheduleTime is { } slot
        && localNow >= slot
        && (LastScheduledRunDate is null || LastScheduledRunDate < localToday);

    /// <summary>Applies a partial update, validating what the combination requires.</summary>
    public void Update(IndentPoAutomationConfigUpdate update, DateTimeOffset nowUtc, string? updatedBy)
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

        // A new or changed schedule time gets a fresh slot for today: without this, a row that
        // already ran (or was merely stamped) earlier today could not run again at the new time
        // until tomorrow, and nothing tells the operator why. Clearing the time needs no reset —
        // ShouldRunOnSchedule short-circuits on a null slot anyway.
        if (ScheduleTime is not null && ScheduleTime != scheduleBefore)
        {
            LastScheduledRunDate = null;
        }

        if (update.Sites is not null)
        {
            Sites = NormaliseSites(update.Sites);
        }

        if (update.IndentNumbers is not null)
        {
            IndentNumbers = NormaliseIndentNumbers(update.IndentNumbers);
        }

        if (update.ClearIndentNumbers)
        {
            IndentNumbers = null;
        }

        if (update.MaxIndentsPerRun is { } ceiling)
        {
            if (ceiling <= 0)
            {
                throw new DomainException($"{nameof(MaxIndentsPerRun)} must be greater than zero.");
            }

            MaxIndentsPerRun = ceiling;
        }

        if (update.ClearMaxIndentsPerRun)
        {
            MaxIndentsPerRun = null;
        }

        // Keep the stored shape consistent with the mode, whatever the caller sent:
        //   Api   ("Indent-based") — named indent numbers, no schedule.
        //   Timer ("Timer-based")  — a schedule, no numbers (converts every eligible indent of the type).
        //   Both                   — both.
        switch (RunMode)
        {
            case PoAutomationRunMode.Api:
                ScheduleTime = null;
                break;
            case PoAutomationRunMode.Timer:
                IndentNumbers = null;
                break;
        }

        UpdatedAtUtc = nowUtc;
        UpdatedBy = updatedBy;
    }

    /// <summary>Records that the scheduler fired this row today, with the run it opened (null for a dry run).</summary>
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

    /// <summary>
    /// Trims to a canonical "1, 2, 4" form, rejects anything that is not a positive integer, and
    /// collapses an empty list back to null so the run falls through to the configured site list.
    /// </summary>
    private static string? NormaliseSites(string sites)
    {
        if (string.IsNullOrWhiteSpace(sites))
        {
            return null;
        }

        var parsed = new List<int>();

        foreach (var part in sites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var siteId) || siteId <= 0)
            {
                throw new DomainException($"'{part}' is not a valid site id. Give a comma-separated list of positive numbers.");
            }

            if (!parsed.Contains(siteId))
            {
                parsed.Add(siteId);
            }
        }

        return parsed.Count == 0 ? null : string.Join(", ", parsed);
    }

    /// <summary>The configured sites as integers, or an empty list when the row defers to configuration.</summary>
    public IReadOnlyList<int> SiteIds() =>
        string.IsNullOrWhiteSpace(Sites)
            ? []
            : [.. Sites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)];

    /// <summary>
    /// The configured indent numbers, or an empty list when the row names none — in which case the
    /// run converts every eligible authorised indent of <see cref="IndentKind"/>.
    /// </summary>
    public IReadOnlyList<string> IndentNumberList() =>
        string.IsNullOrWhiteSpace(IndentNumbers)
            ? []
            : [.. IndentNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// Trims each entry, drops blanks, removes case-insensitive duplicates, and collapses an empty
    /// list back to null so the run falls through to "every eligible indent of this type".
    /// </summary>
    private static string? NormaliseIndentNumbers(string indentNumbers)
    {
        if (string.IsNullOrWhiteSpace(indentNumbers))
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();

        foreach (var part in indentNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(part))
            {
                kept.Add(part);
            }
        }

        return kept.Count == 0 ? null : string.Join(", ", kept);
    }
}
