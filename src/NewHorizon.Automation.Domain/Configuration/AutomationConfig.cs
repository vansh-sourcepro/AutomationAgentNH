using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Configuration;

/// <summary>
/// Per-module runtime behaviour, changed through the UI and read fresh at the start of each job —
/// never from the bootstrap file, and never cached across jobs, so a change takes effect from the
/// next run without a redeploy.
/// </summary>
/// <remarks>
/// One row per module, not per client: the agent is installed on a single client's server against
/// that client's ERP, so the whole installation belongs to one client by construction.
/// </remarks>
public sealed class AutomationConfig
{
    private AutomationConfig()
    {
        Module = string.Empty;
    }

    private AutomationConfig(Guid id, string module, DateTimeOffset nowUtc)
    {
        Id = id;
        Module = module;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The workflow family this row governs: SJO, OAF, MIL, CBOM, AutoShop.</summary>
    public string Module { get; private set; }

    /// <summary>Master switch. False ⇒ the agent does no work at all on this server.</summary>
    public bool EnableAgent { get; private set; } = true;

    /// <summary>Per-module switch, so one workflow can be paused without stopping the others.</summary>
    public bool EnableModule { get; private set; } = true;

    public AutomationMode Mode { get; private set; } = AutomationMode.Full;

    public int PollIntervalSeconds { get; private set; } = 30;

    public int ReconcileIntervalMinutes { get; private set; } = 5;

    /// <summary>Local time-of-day window outside which the reconciliation poll enqueues nothing.</summary>
    public TimeOnly? WorkingHoursStart { get; private set; }

    public TimeOnly? WorkingHoursEnd { get; private set; }

    public int RetryCount { get; private set; } = 3;

    public int ParallelWorkers { get; private set; } = 4;

    public string LoggingLevel { get; private set; } = "Information";

    /// <summary>Licence gate. Automation stays inert on an installation that has not subscribed.</summary>
    public bool IsLicensed { get; private set; } = true;

    public int PayloadRetentionDays { get; private set; } = 90;

    public int LogRetentionDays { get; private set; } = 90;

    public int ErrorRetentionDays { get; private set; } = 365;

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    /// <summary>True only when the licence, the agent and the module all permit work to start.</summary>
    public bool IsAutomationPermitted => IsLicensed && EnableAgent && EnableModule;

    public static AutomationConfig CreateDefault(string module, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return new AutomationConfig(Guid.NewGuid(), module.Trim(), nowUtc);
    }

    /// <summary>
    /// True when <paramref name="localNow"/> falls inside the configured window. An unset window
    /// means "always". A window whose end precedes its start crosses midnight.
    /// </summary>
    public bool IsWithinWorkingHours(TimeOnly localNow)
    {
        if (WorkingHoursStart is not { } start || WorkingHoursEnd is not { } end)
        {
            return true;
        }

        if (start == end)
        {
            return true;
        }

        return start < end
            ? localNow >= start && localNow < end
            : localNow >= start || localNow < end;
    }

    public void Update(AutomationConfigUpdate update, DateTimeOffset nowUtc, string? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(update);

        EnableAgent = update.EnableAgent ?? EnableAgent;
        EnableModule = update.EnableModule ?? EnableModule;
        Mode = update.Mode ?? Mode;
        IsLicensed = update.IsLicensed ?? IsLicensed;
        LoggingLevel = update.LoggingLevel ?? LoggingLevel;

        PollIntervalSeconds = Positive(update.PollIntervalSeconds, PollIntervalSeconds, nameof(PollIntervalSeconds));
        ReconcileIntervalMinutes = Positive(update.ReconcileIntervalMinutes, ReconcileIntervalMinutes, nameof(ReconcileIntervalMinutes));
        ParallelWorkers = Positive(update.ParallelWorkers, ParallelWorkers, nameof(ParallelWorkers));
        PayloadRetentionDays = Positive(update.PayloadRetentionDays, PayloadRetentionDays, nameof(PayloadRetentionDays));
        LogRetentionDays = Positive(update.LogRetentionDays, LogRetentionDays, nameof(LogRetentionDays));
        ErrorRetentionDays = Positive(update.ErrorRetentionDays, ErrorRetentionDays, nameof(ErrorRetentionDays));

        if (update.RetryCount is { } retryCount)
        {
            if (retryCount < 0)
            {
                throw new DomainException($"{nameof(RetryCount)} cannot be negative.");
            }

            RetryCount = retryCount;
        }

        if (update.WorkingHoursStart is not null || update.WorkingHoursEnd is not null)
        {
            WorkingHoursStart = update.WorkingHoursStart ?? WorkingHoursStart;
            WorkingHoursEnd = update.WorkingHoursEnd ?? WorkingHoursEnd;
        }

        if (update.ClearWorkingHours)
        {
            WorkingHoursStart = null;
            WorkingHoursEnd = null;
        }

        UpdatedAtUtc = nowUtc;
        UpdatedBy = updatedBy;
    }

    private static int Positive(int? candidate, int current, string name)
    {
        if (candidate is not { } value)
        {
            return current;
        }

        if (value <= 0)
        {
            throw new DomainException($"{name} must be greater than zero.");
        }

        return value;
    }
}
