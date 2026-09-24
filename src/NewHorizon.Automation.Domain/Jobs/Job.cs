namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// One workflow run for one ERP document. Aggregate root: every status change and every step
/// mutation goes through here, so the transition rules cannot be bypassed by a repository or a
/// hosted service.
/// </summary>
public sealed class Job
{
    private readonly List<JobStep> _steps = [];

    // Materialisation constructor for EF Core.
    private Job()
    {
        WorkflowType = string.Empty;
        DocumentType = string.Empty;
        DocumentId = string.Empty;
        IdempotencyKey = string.Empty;
    }

    private Job(
        Guid id,
        string correlationId,
        string workflowType,
        string documentType,
        string documentId,
        AutomationMode mode,
        int priority,
        string idempotencyKey,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        CorrelationId = correlationId;
        WorkflowType = workflowType;
        DocumentType = documentType;
        DocumentId = documentId;
        Mode = mode;
        Priority = priority;
        IdempotencyKey = idempotencyKey;
        CreatedAtUtc = createdAtUtc;
        Status = JobStatus.Pending;
    }

    public Guid Id { get; private set; }

    /// <summary>Stamped on every log line of this run, and passed to the ERP for cross-system tracing.</summary>
    public string CorrelationId { get; private set; } = string.Empty;

    public string WorkflowType { get; private set; }

    public string DocumentType { get; private set; }

    public string DocumentId { get; private set; }

    /// <summary>
    /// Captured at creation from AutomationConfig and never re-read. A running job keeps the mode
    /// it started with, so a mid-run configuration change cannot make one run behave two ways.
    /// </summary>
    public AutomationMode Mode { get; private set; }

    /// <summary>Higher is claimed first. A manual retry bumps this above newly enqueued work.</summary>
    public int Priority { get; private set; }

    public JobStatus Status { get; private set; }

    public string? CurrentStage { get; private set; }

    public int RetryCount { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public string? CancelledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>
    /// Earliest moment this job may be claimed again. Set when an operation fails transiently or
    /// is waiting on ERP-side automation: without it a Pending job would be re-claimed on the very
    /// next poll, turning "retry with backoff" into a hot loop against an ERP that is already unwell.
    /// </summary>
    public DateTimeOffset? NotBeforeUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>
    /// Computed by the database from <see cref="StartedAtUtc"/> and <see cref="CompletedAtUtc"/>,
    /// so the total can never disagree with the timestamps it comes from. Null until the job ends.
    /// </summary>
    public long? DurationMs { get; private set; }

    /// <summary>
    /// The indent this job is an attempt at converting, when it is one. Null for every other kind
    /// of job — an AutoShop cycle has no conversion — and it is that nullability the two filtered
    /// uniqueness rules key off: a conversion may be re-attempted once the last attempt finished,
    /// a document job may not.
    /// </summary>
    public Guid? ConversionId { get; private set; }

    /// <summary>
    /// The trigger invocation that created this job. Nullable because a job enqueued outside a run
    /// — an ERP push, a manual resume — must still be creatable.
    /// </summary>
    public Guid? RunId { get; private set; }

    /// <summary>Optimistic concurrency token; two workers can never write the same job.</summary>
    public byte[]? RowVersion { get; private set; }

    public IReadOnlyList<JobStep> Steps => _steps;

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Cancelled or JobStatus.Skipped;

    public static Job Create(
        string workflowType,
        string documentType,
        string documentId,
        AutomationMode mode,
        DateTimeOffset nowUtc,
        int priority = 0,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        // Fully qualified: the string property below shadows the value-object type name.
        var key = Jobs.IdempotencyKey.For(documentType, documentId, workflowType);

        return new Job(
            Guid.NewGuid(),
            correlationId ?? Guid.NewGuid().ToString("N"),
            workflowType.Trim(),
            documentType.Trim(),
            documentId.Trim(),
            mode,
            priority,
            key.Value,
            nowUtc);
    }

    /// <summary>
    /// Lays down the full operation list at creation time so the ERP UI can show the whole
    /// Stage → Operation timeline before anything runs, and so resume has a fixed plan to walk.
    /// </summary>
    public void PlanSteps(IEnumerable<PlannedOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (_steps.Count > 0)
        {
            throw new DomainException($"Job {Id} already has a plan; steps are laid down once.");
        }

        AppendSteps(operations);

        if (_steps.Count == 0)
        {
            throw new DomainException($"Workflow '{WorkflowType}' produced no operations for job {Id}.");
        }
    }

    /// <summary>
    /// Appends steps discovered while the job was running — one per Site ID, once the site list has
    /// been fetched. Some work simply cannot be planned up front: the sites exist in the ERP, not in
    /// the workflow definition.
    /// </summary>
    /// <remarks>
    /// Append-only, and therefore safe: existing steps keep their sequence numbers, so a resumed
    /// job's notion of "first operation not yet completed" cannot shift underneath it. Expansion is
    /// persisted in the same save as the discovering step's completion, so a crash either keeps
    /// both or neither — a half-expanded plan is not reachable.
    /// </remarks>
    public void ExpandPlan(IEnumerable<PlannedOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (_steps.Count == 0)
        {
            throw new DomainException($"Job {Id} has no plan to expand; call {nameof(PlanSteps)} first.");
        }

        if (IsTerminal)
        {
            throw new DomainException($"Job {Id} has finished ({Status}) and cannot be expanded.");
        }

        AppendSteps(operations);
    }

    private void AppendSteps(IEnumerable<PlannedOperation> operations)
    {
        var sequence = _steps.Count == 0 ? 0 : _steps.Max(step => step.Sequence) + 1;

        foreach (var operation in operations)
        {
            _steps.Add(JobStep.Create(
                Id,
                operation.Stage,
                operation.OperationName,
                sequence++,
                operation.Kind,
                operation.Target));
        }
    }

    /// <summary>The operation the engine should run next: the first one not already terminal.</summary>
    public JobStep? NextStep() =>
        _steps.OrderBy(step => step.Sequence).FirstOrDefault(step => !step.IsTerminal);

    public JobStep StepAt(int sequence) =>
        _steps.SingleOrDefault(step => step.Sequence == sequence)
        ?? throw new DomainException($"Job {Id} has no operation at sequence {sequence}.");

    /// <summary>
    /// Every ERP document produced so far, keyed by "Stage/Operation", so a later operation can
    /// reference the work order or requisition an earlier one created.
    /// </summary>
    public IReadOnlyDictionary<string, string> CompletedDocumentRefs() =>
        _steps
            .Where(step => step.Status is StepStatus.Completed && step.ErpDocumentRef is not null)
            // Keyed including the target, because a per-site operation contributes one entry per
            // site: without it, several sites would collide on a single Stage/Operation key.
            .ToDictionary(DocumentRefKey, step => step.ErpDocumentRef!);

    /// <summary>
    /// The key an earlier operation's ERP document is published under, so a later operation can
    /// find it — "SJO/WorkOrderGeneration", or "SjoSequence/SequenceSite/SITE-01".
    /// </summary>
    public static string DocumentRefKey(string stage, string operationName, string? target = null) =>
        target is null ? $"{stage}/{operationName}" : $"{stage}/{operationName}/{target}";

    private static string DocumentRefKey(JobStep step) =>
        DocumentRefKey(step.Stage, step.OperationName, step.Target);

    /// <summary>
    /// Ties this job to the indent it converts and the run that asked for it. Called once, before
    /// the job is enqueued: after that the job is a historical record of one attempt and its
    /// provenance does not move.
    /// </summary>
    public void LinkToConversion(Guid conversionId, Guid? runId)
    {
        if (conversionId == Guid.Empty)
        {
            throw new DomainException($"Job {Id} cannot be linked to an empty conversion id.");
        }

        if (ConversionId is not null)
        {
            throw new DomainException(
                $"Job {Id} already belongs to conversion {ConversionId}; provenance is set once.");
        }

        ConversionId = conversionId;
        RunId = runId;
    }

    // ---- transitions -------------------------------------------------------

    /// <summary>Pending → Running. Only a pending job can be claimed.</summary>
    public void Claim(DateTimeOffset nowUtc)
    {
        if (Status is not JobStatus.Pending)
        {
            throw new InvalidJobTransitionException(Id, Status, JobStatus.Running, "Only a Pending job can be claimed.");
        }

        Status = JobStatus.Running;
        StartedAtUtc ??= nowUtc;
        NotBeforeUtc = null;
    }

    /// <summary>Running → AwaitingApproval, at a Partial-mode gate.</summary>
    public void PauseForApproval(string stage, string operationName)
    {
        if (Status is not JobStatus.Running)
        {
            throw new InvalidJobTransitionException(Id, Status, JobStatus.AwaitingApproval);
        }

        if (Mode is not AutomationMode.Partial)
        {
            throw new DomainException($"Job {Id} runs in {Mode} mode and has no approval gates.");
        }

        Status = JobStatus.AwaitingApproval;
        CurrentStage = stage;
        _ = operationName;
    }

    /// <summary>
    /// AwaitingApproval → Pending. A business decision, recorded against the job and the gated
    /// operation. Distinct from <see cref="RequeueForRetry"/>, which is failure recovery.
    /// </summary>
    public void Approve(string approvedBy, DateTimeOffset nowUtc, string? remarks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        if (Status is not JobStatus.AwaitingApproval)
        {
            throw new InvalidJobTransitionException(
                Id, Status, JobStatus.Pending, "Only a job waiting at an approval gate can be approved.");
        }

        var gatedStep = NextStep()
            ?? throw new DomainException($"Job {Id} is awaiting approval but has no pending operation.");

        gatedStep.RecordApproval(approvedBy, nowUtc, remarks);

        ApprovedBy = approvedBy;
        ApprovedAtUtc = nowUtc;
        Status = JobStatus.Pending;
    }

    /// <summary>AwaitingApproval → Cancelled, with the rejector and reason kept for audit.</summary>
    public void Reject(string rejectedBy, string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is not JobStatus.AwaitingApproval)
        {
            throw new InvalidJobTransitionException(
                Id, Status, JobStatus.Cancelled, "Only a job waiting at an approval gate can be rejected.");
        }

        NextStep()?.SetRemarks(reason);

        CancelledBy = rejectedBy;
        CancellationReason = reason;
        Status = JobStatus.Cancelled;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Running → Failed, after a technical/system error or exhausted retries.</summary>
    public void Fail(DateTimeOffset nowUtc)
    {
        if (Status is not JobStatus.Running)
        {
            throw new InvalidJobTransitionException(Id, Status, JobStatus.Failed);
        }

        Status = JobStatus.Failed;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>Running → Skipped, after a business-rule refusal. Terminal; never auto-retried.</summary>
    public void Skip(DateTimeOffset nowUtc)
    {
        if (Status is not JobStatus.Running)
        {
            throw new InvalidJobTransitionException(Id, Status, JobStatus.Skipped);
        }

        Status = JobStatus.Skipped;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>
    /// Failed → Pending, at raised priority. Re-queueing (rather than jumping straight to
    /// Running) keeps one claiming path, so a retried job still respects worker limits and is
    /// picked up ahead of new work by the priority ordering.
    /// </summary>
    public void RequeueForRetry(int priorityBoost = 100)
    {
        if (Status is not JobStatus.Failed)
        {
            throw new InvalidJobTransitionException(
                Id, Status, JobStatus.Pending, "Only a Failed job can be retried.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(priorityBoost);

        var failedStep = _steps.FirstOrDefault(step => step.Status is StepStatus.Failed);
        failedStep?.PrepareForRetry();

        RetryCount++;
        Priority += priorityBoost;
        // A human pressing Retry means "now", so any pending backoff delay is discarded.
        NotBeforeUtc = null;
        Status = JobStatus.Pending;
        CompletedAtUtc = null;
    }

    /// <summary>
    /// Running → Pending, after a transient failure or while waiting for the ERP to finish a
    /// transition it automates itself. Deliberately distinct from <see cref="RequeueForRetry"/>:
    /// this is the engine retrying on its own, so priority is untouched — only a human pressing
    /// Retry earns a place at the front of the queue.
    /// </summary>
    public void RequeueAfterTransientFailure(DateTimeOffset nowUtc, TimeSpan delay)
    {
        if (Status is not JobStatus.Running)
        {
            throw new InvalidJobTransitionException(
                Id, Status, JobStatus.Pending, "Only a Running job can be re-queued by the engine.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        var attemptedStep = _steps.Find(step => step.Status is StepStatus.Running or StepStatus.Failed);
        attemptedStep?.PrepareForRetry();

        Status = JobStatus.Pending;
        NotBeforeUtc = nowUtc + delay;
    }

    /// <summary>
    /// Running → Pending, for a job orphaned by a dead process. The engine restarts it at the
    /// first non-terminal operation; completed operations are skipped by their stored
    /// ErpDocumentRef, so recovery never duplicates ERP documents.
    /// </summary>
    public void MarkResumable()
    {
        if (Status is not JobStatus.Running)
        {
            throw new InvalidJobTransitionException(
                Id, Status, JobStatus.Pending, "Only a Running job can be reclaimed after a crash.");
        }

        NotBeforeUtc = null;
        var interruptedStep = _steps.FirstOrDefault(step => step.Status is StepStatus.Running);
        interruptedStep?.PrepareForRetry();

        Status = JobStatus.Pending;
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        if (Status is not JobStatus.Running)
        {
            throw new InvalidJobTransitionException(Id, Status, JobStatus.Completed);
        }

        if (_steps.Exists(step => !step.IsTerminal))
        {
            throw new DomainException(
                $"Job {Id} cannot complete: operation '{NextStep()!.OperationName}' has not finished.");
        }

        Status = JobStatus.Completed;
        CompletedAtUtc = nowUtc;
    }

    public void Cancel(string cancelledBy, string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelledBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (IsTerminal)
        {
            throw new InvalidJobTransitionException(
                Id, Status, JobStatus.Cancelled, "The job has already finished.");
        }

        CancelledBy = cancelledBy;
        CancellationReason = reason;
        Status = JobStatus.Cancelled;
        CompletedAtUtc = nowUtc;
    }

    public void EnterStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        CurrentStage = stage;
    }
}
