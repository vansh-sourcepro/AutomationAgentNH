namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// The registered workflows, keyed by their unique type name. Adding a workflow means adding one
/// definition to this catalog — the engine, queue, retry, logging and API surface do not change.
/// </summary>
public sealed class WorkflowCatalog : IWorkflowCatalog
{
    private readonly IReadOnlyDictionary<string, WorkflowDefinition> _definitions;

    public WorkflowCatalog(IEnumerable<WorkflowDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var byType = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions)
        {
            if (!byType.TryAdd(definition.WorkflowType, definition))
            {
                // Two definitions claiming one name would make job creation non-deterministic.
                throw new InvalidOperationException(
                    $"Workflow type '{definition.WorkflowType}' is registered more than once.");
            }
        }

        _definitions = byType;
    }

    public IReadOnlyCollection<string> KnownWorkflowTypes => (IReadOnlyCollection<string>)_definitions.Keys;

    public WorkflowDefinition Get(string workflowType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);

        return _definitions.TryGetValue(workflowType, out var definition)
            ? definition
            // An unknown workflow is a deployment mistake, not a job failure: enqueueing it should
            // be rejected at the API rather than leaving a job nobody can run.
            : throw new InvalidOperationException(
                $"Workflow type '{workflowType}' is not registered. Known types: "
                + $"{string.Join(", ", _definitions.Keys)}.");
    }

    public bool TryGet(string workflowType, out WorkflowDefinition? definition)
    {
        definition = null;

        return !string.IsNullOrWhiteSpace(workflowType)
            && _definitions.TryGetValue(workflowType, out definition);
    }
}
