using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

/// <summary>
/// Partial update of an <see cref="IssueToShopFloorAutomationConfig"/>.
/// Every field is optional so callers can change individual settings without affecting others.
/// </summary>
public sealed record IssueToShopFloorAutomationConfigUpdate
{
    public PoAutomationRunMode? RunMode { get; init; }

    public TimeOnly? ScheduleTime { get; init; }

    /// <summary>Explicitly removes the schedule time; null alone means "unchanged".</summary>
    public bool ClearScheduleTime { get; init; }

    /// <summary>Comma-separated site ids, or empty/whitespace to fall back to configured default.</summary>
    public string? Sites { get; init; }

    /// <summary>Comma-separated document numbers; null alone means "unchanged".</summary>
    public string? DocumentNumbers { get; init; }

    /// <summary>Explicitly removes document numbers, reverting to all eligible.</summary>
    public bool ClearDocumentNumbers { get; init; }

    public bool? IsActive { get; init; }

    public bool? DryRun { get; init; }

    public int? MaxDocumentsPerRun { get; init; }

    /// <summary>Explicitly removes per-run ceiling; null alone means "unchanged".</summary>
    public bool ClearMaxDocumentsPerRun { get; init; }
}
