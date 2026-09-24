using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// One Operation of one job — the unit at which the agent checkpoints. Its
/// <see cref="ErpDocumentRef"/> is what makes a re-run safe: a completed step is skipped and its
/// stored ERP document is reused instead of creating a second one.
/// </summary>
public sealed class JobStep
{
    // Materialisation constructor for EF Core.
    private JobStep()
    {
        Stage = string.Empty;
        OperationName = string.Empty;
    }

    private JobStep(Guid id, Guid jobId, string stage, string operationName, int sequence, OperationKind kind, string? target)
    {
        Id = id;
        JobId = jobId;
        Stage = stage;
        OperationName = operationName;
        Sequence = sequence;
        Kind = kind;
        Target = target;
        Status = StepStatus.Pending;
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    /// <summary>The "main method" this operation belongs to: SJO, OAF, MIL, CBOM, AutoShop.</summary>
    public string Stage { get; private set; }

    public string OperationName { get; private set; }

    /// <summary>Global execution order across the whole workflow, not just within the stage.</summary>
    public int Sequence { get; private set; }

    /// <summary>
    /// Who performed this operation. Stored on the row rather than looked up from the current
    /// definition so the timeline stays historically accurate: a step run when the ERP owned the
    /// transition must keep saying so even after the workflow is later redefined.
    /// </summary>
    public OperationKind Kind { get; private set; }

    /// <summary>
    /// What this step acted on when one operation repeats across many subjects — the Site ID for
    /// the per-site stages. Null for a step that runs once.
    /// </summary>
    public string? Target { get; private set; }

    public StepStatus Status { get; private set; }

    public int RetryCount { get; private set; }

    public string? RequestPayload { get; private set; }

    public string? ResponsePayload { get; private set; }

    /// <summary>
    /// Identifier of the ERP document this operation produced (work order no., PR no., ...).
    /// Present on every completed operation that created something.
    /// </summary>
    public string? ErpDocumentRef { get; private set; }

    /// <summary>Why the step was skipped, or why it is waiting for approval. Not an error.</summary>
    public string? Remarks { get; private set; }

    /// <summary>Set when this step is the Partial-mode gate that a human released.</summary>
    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public static JobStep Create(
        Guid jobId,
        string stage,
        string operationName,
        int sequence,
        OperationKind kind = OperationKind.Execute,
        string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        return new JobStep(Guid.NewGuid(), jobId, stage, operationName, sequence, kind, target);
    }

    /// <summary>How this step is named in logs and in the ERP timeline.</summary>
    public string DisplayName => Target is null ? OperationName : $"{OperationName} / {Target}";

    public bool IsTerminal => Status is StepStatus.Completed or StepStatus.Skipped;

    public void Start(DateTimeOffset nowUtc)
    {
        if (Status is StepStatus.Completed)
        {
            // The invariant that keeps duplicate work orders and purchase requisitions impossible.
            throw new DomainException(
                $"Operation '{OperationName}' of job {JobId} is already completed and must never re-run.");
        }

        if (Status is StepStatus.Skipped)
        {
            throw new DomainException(
                $"Operation '{OperationName}' of job {JobId} was skipped and must not be started.");
        }

        Status = StepStatus.Running;
        StartedAtUtc ??= nowUtc;
        CompletedAtUtc = null;
    }

    public void Complete(DateTimeOffset nowUtc, string? erpDocumentRef, string? requestPayload, string? responsePayload)
    {
        if (Status is not StepStatus.Running)
        {
            throw new DomainException(
                $"Operation '{OperationName}' of job {JobId} cannot complete from {Status}.");
        }

        Status = StepStatus.Completed;
        ErpDocumentRef = erpDocumentRef;
        RequestPayload = requestPayload;
        ResponsePayload = responsePayload;
        CompletedAtUtc = nowUtc;
    }

    public void Fail(DateTimeOffset nowUtc, string? requestPayload, string? responsePayload)
    {
        if (Status is StepStatus.Completed or StepStatus.Skipped)
        {
            throw new DomainException(
                $"Operation '{OperationName}' of job {JobId} cannot fail from {Status}.");
        }

        Status = StepStatus.Failed;
        RequestPayload = requestPayload ?? RequestPayload;
        ResponsePayload = responsePayload ?? ResponsePayload;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Precondition not met. Terminal and successful — the workflow moves on.</summary>
    public void Skip(DateTimeOffset nowUtc, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is StepStatus.Completed)
        {
            throw new DomainException(
                $"Operation '{OperationName}' of job {JobId} is completed and cannot be skipped.");
        }

        Status = StepStatus.Skipped;
        Remarks = reason;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Returns the step to the queue for another attempt after a transient failure.</summary>
    public void PrepareForRetry()
    {
        if (Status is StepStatus.Completed or StepStatus.Skipped)
        {
            throw new DomainException(
                $"Operation '{OperationName}' of job {JobId} is terminal ({Status}) and cannot be retried.");
        }

        RetryCount++;
        Status = StepStatus.Pending;
        CompletedAtUtc = null;
    }

    /// <summary>Records the human decision that released this operation's Partial-mode gate.</summary>
    public void RecordApproval(string approvedBy, DateTimeOffset nowUtc, string? remarks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        ApprovedBy = approvedBy;
        ApprovedAtUtc = nowUtc;

        if (!string.IsNullOrWhiteSpace(remarks))
        {
            Remarks = remarks;
        }
    }

    public void SetRemarks(string? remarks) => Remarks = remarks;
}
