using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Configuration;

/// <summary>
/// Partial update of an <see cref="AutomationConfig"/>. Every field is optional so the config
/// endpoint can change one flag without the caller having to echo back the whole row and risk
/// clobbering a setting it never intended to touch.
/// </summary>
public sealed record AutomationConfigUpdate
{
    public bool? EnableAgent { get; init; }

    public bool? EnableModule { get; init; }

    public AutomationMode? Mode { get; init; }

    public bool? IsLicensed { get; init; }

    public int? PollIntervalSeconds { get; init; }

    public int? ReconcileIntervalMinutes { get; init; }

    public TimeOnly? WorkingHoursStart { get; init; }

    public TimeOnly? WorkingHoursEnd { get; init; }

    /// <summary>Explicitly removes the working-hours window; null values alone mean "unchanged".</summary>
    public bool ClearWorkingHours { get; init; }

    public int? RetryCount { get; init; }

    public int? ParallelWorkers { get; init; }

    public string? LoggingLevel { get; init; }

    public int? PayloadRetentionDays { get; init; }

    public int? LogRetentionDays { get; init; }

    public int? ErrorRetentionDays { get; init; }
}
