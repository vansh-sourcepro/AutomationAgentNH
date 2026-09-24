using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// Fluent authoring of a <see cref="WorkflowDefinition"/>. Sequence numbers are assigned as
/// operations are declared, so inserting, reordering or deleting a step is a one-line edit with
/// no numbers to renumber by hand — which is the whole point of keeping workflows as data.
/// </summary>
/// <example>
/// <code>
/// var sjo = WorkflowBuilder.For("SJO")
///     .Stage("SJO", stage => stage
///         .Execute("DeAllocation", (ctx, erp, ct) => ...)
///         .Execute("Allocation", (ctx, erp, ct) => ...)
///         .VerifyOnly("CbomGeneration", erpTransitionKind: "SJO-to-CBOM"))
///     .Build();
/// </code>
/// </example>
public sealed class WorkflowBuilder
{
    private readonly string _workflowType;
    private readonly List<StageDefinition> _stages = [];

    private WorkflowBuilder(string workflowType) => _workflowType = workflowType;

    public static WorkflowBuilder For(string workflowType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);
        return new WorkflowBuilder(workflowType);
    }

    /// <summary>Appends a stage. Stages run strictly in the order declared.</summary>
    public WorkflowBuilder Stage(string name, Action<StageBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var stageBuilder = new StageBuilder(name);
        configure(stageBuilder);
        _stages.Add(stageBuilder.Build());

        return this;
    }

    public WorkflowDefinition Build()
    {
        if (_stages.Count == 0)
        {
            throw new InvalidOperationException($"Workflow '{_workflowType}' declares no stages.");
        }

        var duplicateStage = _stages
            .GroupBy(stage => stage.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateStage is not null)
        {
            // Stage names key the timeline and the step rows; two of the same would make resume
            // and the UI ambiguous.
            throw new InvalidOperationException(
                $"Workflow '{_workflowType}' declares stage '{duplicateStage.Key}' more than once.");
        }

        return new WorkflowDefinition(_workflowType, _stages);
    }

    /// <summary>Declares the operations of one stage, in execution order.</summary>
    public sealed class StageBuilder
    {
        private readonly string _stageName;
        private readonly List<OperationDefinition> _operations = [];

        internal StageBuilder(string stageName) => _stageName = stageName;

        /// <summary>
        /// The agent performs the work by calling ERP APIs. The body must be safe to run twice —
        /// query before create — because a resumed job may re-enter it.
        /// </summary>
        public StageBuilder Execute(
            string name,
            Func<OperationContext, IErpClient, CancellationToken, Task<OperationResult>> execute,
            Func<OperationContext, IErpClient, CancellationToken, Task<bool>>? precondition = null,
            bool requiresApprovalInPartial = false)
        {
            ArgumentNullException.ThrowIfNull(execute);

            return Add(new OperationDefinition(
                name,
                _operations.Count,
                execute,
                precondition,
                requiresApprovalInPartial,
                OperationKind.Execute));
        }

        /// <summary>
        /// Declares an operation that runs once per target discovered at run time — once per Site
        /// ID, in practice. It contributes no step to the initial plan; an earlier discovery
        /// operation appends one real, individually checkpointed step per target.
        /// </summary>
        public StageBuilder PerTarget(
            string name,
            Func<OperationContext, IErpClient, CancellationToken, Task<OperationResult>> execute,
            Func<OperationContext, IErpClient, CancellationToken, Task<bool>>? precondition = null,
            bool requiresApprovalInPartial = false)
        {
            ArgumentNullException.ThrowIfNull(execute);

            return Add(new OperationDefinition(
                name,
                _operations.Count,
                execute,
                precondition,
                requiresApprovalInPartial,
                OperationKind.Execute,
                ErpTransitionKind: null,
                VerificationWaitBudget: null,
                IsPerTarget: true));
        }

        /// <summary>
        /// The ERP already performs this transition itself behind its own flag, so the agent only
        /// confirms it happened and records the document. Nothing is created here — that is what
        /// keeps the agent from duplicating the ERP's own automation.
        /// </summary>
        public StageBuilder VerifyOnly(
            string name,
            string erpTransitionKind,
            TimeSpan? waitBudget = null,
            Func<OperationContext, IErpClient, CancellationToken, Task<bool>>? precondition = null,
            bool requiresApprovalInPartial = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(erpTransitionKind);

            return Add(new OperationDefinition(
                name,
                _operations.Count,
                Execute: null,
                precondition,
                requiresApprovalInPartial,
                OperationKind.VerifyOnly,
                erpTransitionKind,
                waitBudget));
        }

        /// <summary>
        /// Confirm what the ERP did, and do the work only if it did not. Use this wherever the
        /// ERP-side flag might be on or off: the agent adapts without reading the flag and
        /// without a redeploy when it is toggled.
        /// </summary>
        public StageBuilder VerifyThenExecute(
            string name,
            string erpTransitionKind,
            Func<OperationContext, IErpClient, CancellationToken, Task<OperationResult>> execute,
            TimeSpan? waitBudget = null,
            Func<OperationContext, IErpClient, CancellationToken, Task<bool>>? precondition = null,
            bool requiresApprovalInPartial = false)
        {
            ArgumentNullException.ThrowIfNull(execute);
            ArgumentException.ThrowIfNullOrWhiteSpace(erpTransitionKind);

            return Add(new OperationDefinition(
                name,
                _operations.Count,
                execute,
                precondition,
                requiresApprovalInPartial,
                OperationKind.VerifyThenExecute,
                erpTransitionKind,
                waitBudget));
        }

        private StageBuilder Add(OperationDefinition operation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
            operation.Validate();

            if (_operations.Exists(existing =>
                    string.Equals(existing.Name, operation.Name, StringComparison.OrdinalIgnoreCase)))
            {
                // Operation names are the resume key within a stage; duplicates would make
                // "first operation not yet completed" ambiguous.
                throw new InvalidOperationException(
                    $"Stage '{_stageName}' declares operation '{operation.Name}' more than once.");
            }

            _operations.Add(operation);

            return this;
        }

        internal StageDefinition Build()
        {
            if (_operations.Count == 0)
            {
                throw new InvalidOperationException($"Stage '{_stageName}' declares no operations.");
            }

            return new StageDefinition(_stageName, _operations);
        }
    }
}
