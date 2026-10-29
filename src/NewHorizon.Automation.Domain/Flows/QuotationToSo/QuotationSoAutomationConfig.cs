using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.QuotationToSo;

/// <summary>
/// Automation configuration for Quotation to Sales Order conversion.
/// Stores master toggle (IsActive), timer schedule time, target quotation numbers, and run mode.
/// </summary>
public sealed class QuotationSoAutomationConfig
{
    private QuotationSoAutomationConfig()
    {
    }

    private QuotationSoAutomationConfig(Guid id, DateTimeOffset nowUtc)
    {
        Id = id;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Master on/off toggle. When false, manual conversions are rejected and timer runs are skipped.
    /// </summary>
    public bool IsActive { get; private set; } = false;

    /// <summary>
    /// Run mode: Api/Manual only, Timer only, or Both.
    /// </summary>
    public SoAutomationRunMode RunMode { get; private set; } = SoAutomationRunMode.Both;

    /// <summary>
    /// Daily scheduled time (local wall clock time) when timer conversion runs.
    /// </summary>
    public TimeOnly? ScheduleTime { get; private set; }

    /// <summary>
    /// Comma-separated quotation numbers to convert (e.g. "26-27/TE//000008, 000009").
    /// If null or empty, the automation processes all eligible open authorised quotations.
    /// </summary>
    public string? QuotationNumbers { get; private set; }

    /// <summary>
    /// Optional customer code filter.
    /// </summary>
    public string? CustomerCode { get; private set; }

    /// <summary>
    /// Optional site code filter.
    /// </summary>
    public string? SiteCode { get; private set; }

    /// <summary>
    /// When true, conversion reports what would be created without inserting records into ERP.
    /// </summary>
    public bool DryRun { get; private set; } = false;

    /// <summary>
    /// The date the scheduler last ran this configuration, preventing multiple runs on the same day slot.
    /// </summary>
    public DateOnly? LastScheduledRunDate { get; private set; }

    /// <summary>
    /// When any run (timer or manual) was last triggered.
    /// </summary>
    public DateTimeOffset? LastTriggeredAtUtc { get; private set; }

    /// <summary>
    /// Outcome status of the last run ("Success", "Failed", "PartialSuccess", "Skipped").
    /// </summary>
    public string? LastRunStatus { get; private set; }

    /// <summary>
    /// Detailed message or summary of the last run.
    /// </summary>
    public string? LastRunMessage { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public static QuotationSoAutomationConfig CreateDefault(DateTimeOffset nowUtc) =>
        new(Guid.NewGuid(), nowUtc)
        {
            IsActive = true,
            RunMode = SoAutomationRunMode.Both,
        };

    public static QuotationSoAutomationConfig FromSnapshot(
        Guid id,
        bool isActive,
        SoAutomationRunMode runMode,
        TimeOnly? scheduleTime,
        string? quotationNumbers,
        string? customerCode,
        string? siteCode,
        bool dryRun,
        DateOnly? lastScheduledRunDate,
        DateTimeOffset? lastTriggeredAtUtc,
        string? lastRunStatus,
        string? lastRunMessage,
        DateTimeOffset updatedAtUtc,
        string? updatedBy) =>
        new(id, updatedAtUtc)
        {
            IsActive = isActive,
            RunMode = runMode,
            ScheduleTime = scheduleTime,
            QuotationNumbers = quotationNumbers,
            CustomerCode = customerCode,
            SiteCode = siteCode,
            DryRun = dryRun,
            LastScheduledRunDate = lastScheduledRunDate,
            LastTriggeredAtUtc = lastTriggeredAtUtc,
            LastRunStatus = lastRunStatus,
            LastRunMessage = lastRunMessage,
            UpdatedBy = updatedBy,
        };

    /// <summary>
    /// Checks whether the scheduled timer should run right now.
    /// </summary>
    public bool ShouldRunOnSchedule(TimeOnly localNow, DateOnly localToday) =>
        IsActive
        && RunMode.Allows(TriggerSource.Timer)
        && ScheduleTime is { } slot
        && localNow >= slot
        && (LastScheduledRunDate is null || LastScheduledRunDate < localToday);

    /// <summary>
    /// Applies configuration updates.
    /// </summary>
    public void Update(QuotationSoAutomationConfigUpdate update, DateTimeOffset nowUtc, string? updatedBy)
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

        // If schedule time changed, allow it to fire for the new slot today if applicable
        if (ScheduleTime is not null && ScheduleTime != scheduleBefore)
        {
            LastScheduledRunDate = null;
        }

        if (update.QuotationNumbers is not null)
        {
            QuotationNumbers = NormaliseList(update.QuotationNumbers);
        }

        if (update.ClearQuotationNumbers)
        {
            QuotationNumbers = null;
        }

        if (update.CustomerCode is not null)
        {
            CustomerCode = string.IsNullOrWhiteSpace(update.CustomerCode) ? null : update.CustomerCode.Trim();
        }

        if (update.ClearCustomerCode)
        {
            CustomerCode = null;
        }

        if (update.SiteCode is not null)
        {
            SiteCode = string.IsNullOrWhiteSpace(update.SiteCode) ? null : update.SiteCode.Trim();
        }

        if (update.ClearSiteCode)
        {
            SiteCode = null;
        }

        UpdatedAtUtc = nowUtc;
        UpdatedBy = updatedBy;
    }

    /// <summary>
    /// Directly updates the master toggle (active/inactive).
    /// </summary>
    public void SetActive(bool active, DateTimeOffset nowUtc, string? updatedBy)
    {
        IsActive = active;
        UpdatedAtUtc = nowUtc;
        UpdatedBy = updatedBy;
    }

    /// <summary>
    /// Records that a run has started.
    /// </summary>
    public void RecordRunStarted(DateTimeOffset nowUtc, DateOnly? scheduledDate)
    {
        LastTriggeredAtUtc = nowUtc;
        if (scheduledDate.HasValue)
        {
            LastScheduledRunDate = scheduledDate.Value;
        }
    }

    /// <summary>
    /// Records the outcome of a run.
    /// </summary>
    public void RecordRunCompleted(string status, string message, DateTimeOffset nowUtc)
    {
        LastRunStatus = status;
        LastRunMessage = message;
        UpdatedAtUtc = nowUtc;
    }

    private static string? NormaliseList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var tokens = input
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var joined = string.Join(", ", tokens);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }
}
