namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>
/// Partial update of an <see cref="IndentPoAutomationConfig"/>. Every field is optional so the
/// management endpoint can change one setting without echoing the whole row back and risking a
/// value it never meant to touch — the same shape as <c>AutomationConfigUpdate</c>.
/// </summary>
public sealed record IndentPoAutomationConfigUpdate
{
    public PoAutomationRunMode? RunMode { get; init; }

    public TimeOnly? ScheduleTime { get; init; }

    /// <summary>Explicitly removes the schedule time; a null <see cref="ScheduleTime"/> alone means "unchanged".</summary>
    public bool ClearScheduleTime { get; init; }

    /// <summary>Comma-separated site ids, or empty/whitespace to fall back to the configured site list.</summary>
    public string? Sites { get; init; }

    /// <summary>Comma-separated indent numbers (whole or bare running number); null alone means "unchanged".</summary>
    public string? IndentNumbers { get; init; }

    /// <summary>Explicitly removes the indent-number list, so the run reverts to "every eligible indent of this type".</summary>
    public bool ClearIndentNumbers { get; init; }

    public bool? IsActive { get; init; }

    public bool? DryRun { get; init; }

    public int? MaxIndentsPerRun { get; init; }

    /// <summary>Explicitly removes the per-run ceiling; a null <see cref="MaxIndentsPerRun"/> alone means "unchanged".</summary>
    public bool ClearMaxIndentsPerRun { get; init; }
}
