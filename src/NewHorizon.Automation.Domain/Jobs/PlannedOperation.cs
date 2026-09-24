using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// One entry of the execution plan a job records at creation. Kept as a named type rather than a
/// tuple because the plan is stored and shown to users, and adding a field to it should not
/// silently change the meaning of positional arguments at every call site.
/// </summary>
/// <param name="Target">
/// What this step acts on when one operation repeats across many subjects — the Site ID, for the
/// per-site stages. Null for a step that runs once. Keeping it separate from
/// <paramref name="OperationName"/> means the definition lookup still finds the operation by its
/// stable name, while the timeline can still show which site each step covered.
/// </param>
public sealed record PlannedOperation(
    string Stage,
    string OperationName,
    OperationKind Kind = OperationKind.Execute,
    string? Target = null);
