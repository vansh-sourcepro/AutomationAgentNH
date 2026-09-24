namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// One invocation of a trigger: who asked, what they were allowed to touch, and what came of it.
/// </summary>
/// <remarks>
/// <para>
/// A run exists so that the trigger, the mode and the selection are recorded once rather than
/// repeated on every job the run creates — and, more importantly, so that a run which created no
/// jobs at all is still visible. A sweep that examined forty indents and converted none is the
/// case worth keeping: without a row of its own it leaves nothing behind but a log line.
/// </para>
/// <para>
/// Deliberately not indent-specific. <see cref="WorkflowType"/> says what kind of run it was, so
/// the AutoShop cycle can adopt this table later without a second one being invented for it.
/// </para>
/// </remarks>
public sealed class AutomationRun
{
    // Materialisation constructor for EF Core.
    private AutomationRun()
    {
        CorrelationId = string.Empty;
        WorkflowType = string.Empty;
        RequestedIndentTypes = string.Empty;
    }

    private AutomationRun(
        Guid id,
        string correlationId,
        string workflowType,
        TriggerSource triggerSource,
        string? triggeredBy,
        string? triggerReference,
        AutomationMode mode,
        string requestedIndentTypes,
        string? requestedSites,
        int? maxIndents,
        DateTimeOffset startedAtUtc)
    {
        Id = id;
        CorrelationId = correlationId;
        WorkflowType = workflowType;
        TriggerSource = triggerSource;
        TriggeredBy = triggeredBy;
        TriggerReference = triggerReference;
        Mode = mode;
        RequestedIndentTypes = requestedIndentTypes;
        RequestedSites = requestedSites;
        MaxIndents = maxIndents;
        StartedAtUtc = startedAtUtc;
        Status = RunStatus.Running;
    }

    public Guid Id { get; private set; }

    /// <summary>Shared by every job, log line and ERP call this run produces.</summary>
    public string CorrelationId { get; private set; }

    public string WorkflowType { get; private set; }

    public TriggerSource TriggerSource { get; private set; }

    /// <summary>User id, service account or bot name. Null when the caller did not say.</summary>
    public string? TriggeredBy { get; private set; }

    /// <summary>Chat session, ERP request id, HTTP request id — the way back to what asked.</summary>
    public string? TriggerReference { get; private set; }

    /// <summary>Read from <c>AutomationConfig</c> when the run opened, and never re-read.</summary>
    public AutomationMode Mode { get; private set; }

    /// <summary>
    /// The indent types this run was allowed to convert, as the agent understood them — an absent
    /// filter is stored expanded rather than blank. This is the difference between "found no
    /// Capital indents" and "was never allowed to look for Capital indents".
    /// </summary>
    public string RequestedIndentTypes { get; private set; }

    /// <summary>Sites the caller named. Null means the configured list was used.</summary>
    public string? RequestedSites { get; private set; }

    public int? MaxIndents { get; private set; }

    public RunStatus Status { get; private set; }

    public int IndentsExamined { get; private set; }

    public int JobsCreated { get; private set; }

    public int PurchaseOrdersCreated { get; private set; }

    /// <summary>Why the run as a whole failed. Per-indent failures live on their own jobs.</summary>
    public string? FailureReason { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>
    /// Computed by the database from the two timestamps above, so it can never disagree with them.
    /// Null while the run is still going.
    /// </summary>
    public long? DurationMs { get; private set; }

    public bool IsTerminal => Status is not RunStatus.Running;

    public static AutomationRun Start(
        string workflowType,
        TriggerSource triggerSource,
        AutomationMode mode,
        string requestedIndentTypes,
        DateTimeOffset nowUtc,
        string? triggeredBy = null,
        string? triggerReference = null,
        string? requestedSites = null,
        int? maxIndents = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowType);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedIndentTypes);

        return new AutomationRun(
            Guid.NewGuid(),
            correlationId ?? Guid.NewGuid().ToString("N"),
            workflowType.Trim(),
            triggerSource,
            Trim(triggeredBy),
            Trim(triggerReference),
            mode,
            requestedIndentTypes.Trim(),
            Trim(requestedSites),
            maxIndents,
            nowUtc);
    }

    /// <summary>
    /// Records what the run got through. Called as the run proceeds rather than only at the end,
    /// so a run killed mid-flight still shows how far it had come.
    /// </summary>
    public void RecordProgress(int indentsExamined, int jobsCreated, int purchaseOrdersCreated)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indentsExamined);
        ArgumentOutOfRangeException.ThrowIfNegative(jobsCreated);
        ArgumentOutOfRangeException.ThrowIfNegative(purchaseOrdersCreated);

        IndentsExamined = indentsExamined;
        JobsCreated = jobsCreated;
        PurchaseOrdersCreated = purchaseOrdersCreated;
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        EnsureLive(RunStatus.Completed);

        Status = RunStatus.Completed;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>
    /// The run itself broke — discovery refused, the ERP was unreachable. An indent that simply
    /// produced no order is not this; that is a completed run with an outcome that says why.
    /// </summary>
    public void Fail(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureLive(RunStatus.Failed);

        Status = RunStatus.Failed;
        FailureReason = Truncate(reason);
        CompletedAtUtc = nowUtc;
    }

    public void Cancel(string reason, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureLive(RunStatus.Cancelled);

        Status = RunStatus.Cancelled;
        FailureReason = Truncate(reason);
        CompletedAtUtc = nowUtc;
    }

    private void EnsureLive(RunStatus target)
    {
        if (IsTerminal)
        {
            throw new DomainException(
                $"Run {Id} has already finished ({Status}) and cannot become {target}.");
        }
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Matches the column width, so an over-long ERP message cannot fail the save.</summary>
    private static string Truncate(string value) =>
        value.Length <= FieldLengths.Message ? value : value[..FieldLengths.Message];
}
