namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// One of the "main methods" — SJO, OAF, MIL, CBOM, AutoShop. Stages run strictly in sequence:
/// one finishes before the next begins.
/// </summary>
public sealed record StageDefinition(string Name, IReadOnlyList<OperationDefinition> Operations)
{
    /// <summary>Operations in declared execution order.</summary>
    public IEnumerable<OperationDefinition> Ordered() => Operations.OrderBy(operation => operation.Sequence);
}
