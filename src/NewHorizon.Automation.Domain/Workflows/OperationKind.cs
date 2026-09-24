namespace NewHorizon.Automation.Domain.Workflows;

/// <summary>
/// Who actually performs the work of an operation. Some transitions (SO → OAF, SJO → CBOM) are
/// already automated inside the ERP behind a configuration flag; for those the agent must not
/// create anything, or it would duplicate what the ERP just did on its own.
/// </summary>
public enum OperationKind
{
    /// <summary>
    /// The agent performs the work by calling an ERP create API. The default.
    /// </summary>
    Execute = 0,

    /// <summary>
    /// The ERP performs the work itself; the agent only confirms it happened and records the
    /// resulting document. The agent never creates in this mode — not even when the document is
    /// missing — because the ERP owns this transition and a create here would be a duplicate.
    /// A document that has not appeared yet is treated as "not finished", not as a failure.
    /// </summary>
    VerifyOnly = 1,

    /// <summary>
    /// Confirm first, and create only if the ERP has not. This is the safe default whenever the
    /// ERP-side automation flag may be either on or off: the agent adopts the ERP's document
    /// when the flag is on, and does the work itself when it is off, without having to read the
    /// flag or be redeployed when it changes.
    /// </summary>
    VerifyThenExecute = 2,
}
