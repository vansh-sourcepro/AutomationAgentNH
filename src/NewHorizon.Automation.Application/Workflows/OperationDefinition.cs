using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// One operation of a workflow: the checkpoint unit. The engine persists this operation's result
/// and ERP document reference before it looks at the next one.
/// </summary>
/// <param name="Name">
/// Stable identifier — it is stored on the step and shown in the ERP timeline. Renaming one
/// breaks resume for jobs already in flight, which keep the plan they were created with.
/// </param>
/// <param name="Sequence">Order within the owning stage.</param>
/// <param name="Execute">
/// The ERP work. Must be safe to call twice: query before create. Not used by
/// <see cref="OperationKind.VerifyOnly"/> operations, which never create anything.
/// </param>
/// <param name="Precondition">Evaluated first; a false result skips the operation rather than failing it.</param>
/// <param name="RequiresApprovalInPartial">In Partial mode, pauses the job here for a human decision.</param>
/// <param name="Kind">Who performs the work — the agent, the ERP, or the ERP with the agent as fallback.</param>
/// <param name="ErpTransitionKind">
/// Which ERP-internal transition to confirm, for the two verifying kinds. e.g. "SO-to-OAF".
/// </param>
/// <param name="VerificationWaitBudget">
/// How long to keep waiting for ERP-side automation before giving up and asking a human. The ERP
/// may complete the transition asynchronously, so "not there yet" must not be mistaken for
/// "never going to happen" — but nor can a job wait forever.
/// </param>
public sealed record OperationDefinition(
    string Name,
    int Sequence,
    Func<OperationContext, IErpClient, CancellationToken, Task<OperationResult>>? Execute = null,
    Func<OperationContext, IErpClient, CancellationToken, Task<bool>>? Precondition = null,
    bool RequiresApprovalInPartial = false,
    OperationKind Kind = OperationKind.Execute,
    string? ErpTransitionKind = null,
    TimeSpan? VerificationWaitBudget = null,
    bool IsPerTarget = false)
{
    /// <summary>Default patience for an ERP-side transition before a human is asked to look.</summary>
    public static readonly TimeSpan DefaultVerificationWaitBudget = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A per-target operation is a template, not a step. It contributes nothing to the plan laid
    /// down at creation — a discovery operation appends one real step per target once the targets
    /// are known — but it stays resolvable by name so those appended steps find their body.
    /// </summary>
    public bool ContributesToInitialPlan => !IsPerTarget;

    /// <summary>True when the agent itself may create the ERP document for this operation.</summary>
    public bool CanExecute => Kind is OperationKind.Execute or OperationKind.VerifyThenExecute;

    /// <summary>True when the operation begins by asking the ERP what it has already done.</summary>
    public bool VerifiesFirst => Kind is OperationKind.VerifyOnly or OperationKind.VerifyThenExecute;

    public TimeSpan WaitBudget => VerificationWaitBudget ?? DefaultVerificationWaitBudget;

    /// <summary>
    /// Rejects a definition that cannot possibly run, at registration time rather than mid-job:
    /// an executing operation with no body, or a verifying one with nothing to ask about.
    /// </summary>
    public void Validate()
    {
        if (Kind is OperationKind.Execute && Execute is null)
        {
            throw new InvalidOperationException(
                $"Operation '{Name}' is an Execute operation but has no Execute delegate.");
        }

        if (Kind is OperationKind.VerifyThenExecute && Execute is null)
        {
            throw new InvalidOperationException(
                $"Operation '{Name}' falls back to executing but has no Execute delegate. "
                + $"Use {nameof(OperationKind)}.{nameof(OperationKind.VerifyOnly)} if the ERP always owns this step.");
        }

        if (VerifiesFirst && string.IsNullOrWhiteSpace(ErpTransitionKind))
        {
            throw new InvalidOperationException(
                $"Operation '{Name}' verifies ERP-side automation but names no {nameof(ErpTransitionKind)}.");
        }
    }
}
