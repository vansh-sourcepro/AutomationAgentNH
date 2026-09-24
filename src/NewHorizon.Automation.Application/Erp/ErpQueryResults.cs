namespace NewHorizon.Automation.Application.Erp;

/// <summary>
/// Answer to "has a document of this kind already been created for this source?" — the
/// query-before-create check that makes every operation safe to re-run.
/// </summary>
public sealed record ExistingDocumentResult(bool Exists, string? ErpDocumentRef);

/// <summary>Net requirement the ERP computed; zero means the operation has nothing to do.</summary>
public sealed record ShortageResult(decimal NetShortage, string? Detail)
{
    public bool HasShortage => NetShortage > 0m;
}

/// <summary>
/// Whether the children under a manufacturing item were allocated — the precondition that
/// decides if work-order generation may proceed.
/// </summary>
public sealed record AllocationStatusResult(bool ChildrenAllocated, string? Detail);

/// <summary>
/// Whether a transition the ERP automates internally (SO → OAF, SJO → CBOM) has completed for
/// this document. This is the confirmation call the agent makes instead of creating, for
/// operations the ERP owns.
/// </summary>
/// <param name="Completed">The ERP has finished this transition and produced the document.</param>
/// <param name="ErpDocumentRef">What it produced, recorded as the operation's checkpoint.</param>
/// <param name="ErpAutomationEnabled">
/// Whether the ERP-side flag for this transition is currently on. Lets the agent tell "the ERP is
/// still working on it" from "the ERP was never going to do it" — the difference between waiting
/// and taking over.
/// </param>
/// <param name="InProgress">The ERP has started but not finished; the agent should wait.</param>
public sealed record ErpAutomationOutcome(
    bool Completed,
    string? ErpDocumentRef,
    bool ErpAutomationEnabled,
    bool InProgress,
    string? Detail);

/// <summary>
/// A document the ERP believes should have an automation job. Fed to the reconciliation poll,
/// which enqueues only the ones the agent has no job for.
/// </summary>
public sealed record PendingDocument(
    string DocumentType,
    string DocumentId,
    string WorkflowType,
    DateTimeOffset DocumentDateUtc);
