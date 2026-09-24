namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// The registry of known workflows. Keeping lookup behind a port means a new workflow is a
/// registration, not an engine change.
/// </summary>
public interface IWorkflowCatalog
{
    /// <summary>Throws when the type is unknown — an unrecognised workflow is a configuration bug, not a job failure.</summary>
    WorkflowDefinition Get(string workflowType);

    bool TryGet(string workflowType, out WorkflowDefinition? definition);

    IReadOnlyCollection<string> KnownWorkflowTypes { get; }
}
