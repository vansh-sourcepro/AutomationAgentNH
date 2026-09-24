using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// A complete workflow as data: an ordered list of stages, each an ordered list of operations.
/// Adding a workflow means adding one of these — the engine, queue, retry, logging and API
/// surface do not change.
/// </summary>
public sealed record WorkflowDefinition(string WorkflowType, IReadOnlyList<StageDefinition> Stages)
{
    /// <summary>
    /// The flat execution plan, in order. The job records exactly this list at creation, which is
    /// what lets the ERP show the whole timeline before anything has run.
    /// </summary>
    public IEnumerable<PlannedOperation> Plan() =>
        Stages.SelectMany(stage =>
            stage.Ordered()
                // Per-target operations are templates: a discovery step appends one real step per
                // target at run time, so they must not appear in the plan as bodiless placeholders.
                .Where(operation => operation.ContributesToInitialPlan)
                .Select(operation => new PlannedOperation(stage.Name, operation.Name, operation.Kind)));

    public OperationDefinition? Find(string stage, string operationName) =>
        Stages
            .FirstOrDefault(s => string.Equals(s.Name, stage, StringComparison.OrdinalIgnoreCase))
            ?.Operations
            .FirstOrDefault(o => string.Equals(o.Name, operationName, StringComparison.OrdinalIgnoreCase));
}
