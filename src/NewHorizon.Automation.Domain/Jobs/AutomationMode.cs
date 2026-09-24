namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// Per-module automation depth. Captured on the job at creation and never changed
/// mid-run, so a configuration change cannot make one job behave two ways.
/// </summary>
public enum AutomationMode
{
    /// <summary>Every operation runs unattended.</summary>
    Full = 0,

    /// <summary>Operations flagged RequiresApprovalInPartial pause the job for a human decision.</summary>
    Partial = 1,
}
