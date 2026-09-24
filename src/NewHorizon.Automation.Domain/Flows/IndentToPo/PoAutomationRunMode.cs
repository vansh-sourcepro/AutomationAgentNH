using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>
/// How an indent type's automation may be started. It gates <em>triggers</em>, never <em>how many</em>
/// orders a run makes: the allow-list on the run request still decides that.
/// </summary>
/// <remarks>
/// A person clicking "Run now" in the ERP UI is <see cref="TriggerSource.Manual"/> and is never
/// gated here — the mode only governs the two unattended ways in, the daily scheduler
/// (<see cref="TriggerSource.Timer"/>) and an external call to the conversion API
/// (<see cref="TriggerSource.Api"/>).
/// </remarks>
public enum PoAutomationRunMode
{
    /// <summary>Neither the scheduler nor the API may start this type. The default for a fresh install.</summary>
    Disabled = 0,

    /// <summary>An external call to <c>/api/automation/indent-to-po</c> may convert this type; the scheduler may not.</summary>
    Api = 1,

    /// <summary>The daily scheduler may convert this type; an external API call may not.</summary>
    Timer = 2,

    /// <summary>Both the scheduler and an external API call may convert this type.</summary>
    Both = 3,
}

/// <summary>
/// Whether a run mode permits a given unattended trigger. <see cref="TriggerSource.Manual"/> — the
/// UI's "Run now" — is always allowed by its callers and is never asked about here.
/// </summary>
public static class PoAutomationRunModeExtensions
{
    public static bool Allows(this PoAutomationRunMode mode, TriggerSource trigger) => trigger switch
    {
        TriggerSource.Timer => mode is PoAutomationRunMode.Timer or PoAutomationRunMode.Both,
        TriggerSource.Api => mode is PoAutomationRunMode.Api or PoAutomationRunMode.Both,
        _ => true,
    };
}
