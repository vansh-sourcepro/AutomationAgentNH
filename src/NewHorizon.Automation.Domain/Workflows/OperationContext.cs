using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Workflows;

/// <summary>
/// Everything an operation is allowed to know about the run it belongs to. Deliberately a
/// read-only projection of the job rather than the aggregate itself, so an operation body cannot
/// mutate job state behind the engine's back.
/// </summary>
public sealed class OperationContext
{
    public OperationContext(
        Guid jobId,
        string correlationId,
        string workflowType,
        string documentType,
        string documentId,
        AutomationMode mode,
        string stage,
        string operationName,
        string? target,
        IReadOnlyDictionary<string, string> completedDocumentRefs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(completedDocumentRefs);

        JobId = jobId;
        CorrelationId = correlationId;
        WorkflowType = workflowType;
        DocumentType = documentType;
        DocumentId = documentId;
        Mode = mode;
        Stage = stage;
        OperationName = operationName;
        Target = target;
        CompletedDocumentRefs = completedDocumentRefs;
    }

    public Guid JobId { get; }

    public string CorrelationId { get; }

    public string WorkflowType { get; }

    public string DocumentType { get; }

    /// <summary>The ERP document being processed — the sales order number, typically.</summary>
    public string DocumentId { get; }

    public AutomationMode Mode { get; }

    public string Stage { get; }

    public string OperationName { get; }

    /// <summary>What this step acts on — the Site ID for a per-site operation; null otherwise.</summary>
    public string? Target { get; }

    /// <summary>
    /// ERP documents produced by earlier operations of this run, keyed "Stage/Operation".
    /// A labour requisition reads the work order number it must reference from here.
    /// </summary>
    public IReadOnlyDictionary<string, string> CompletedDocumentRefs { get; }

    public string? DocumentRefFor(string stage, string operationName, string? target = null) =>
        CompletedDocumentRefs.GetValueOrDefault(Job.DocumentRefKey(stage, operationName, target));

    public static OperationContext ForStep(Job job, JobStep step)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(step);

        return new OperationContext(
            job.Id,
            job.CorrelationId,
            job.WorkflowType,
            job.DocumentType,
            job.DocumentId,
            job.Mode,
            step.Stage,
            step.OperationName,
            step.Target,
            job.CompletedDocumentRefs());
    }
}
