namespace NewHorizon.Automation.Worker.Contracts;

/// <summary>One row of the jobs list.</summary>
public sealed record JobSummaryResponse(
    Guid Id,
    string CorrelationId,
    string WorkflowType,
    string DocumentType,
    string DocumentId,
    string Status,
    string Mode,
    string? CurrentStage,
    int Priority,
    int RetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int StepCount,
    int CompletedStepCount);

/// <summary>
/// One operation in the job's timeline. <paramref name="Target"/> is the Site ID for the per-site
/// stages, which is what stops the UI rendering a run of identical-looking rows.
/// </summary>
public sealed record JobStepResponse(
    Guid Id,
    string Stage,
    string OperationName,
    string? Target,
    int Sequence,
    string Kind,
    string Status,
    int RetryCount,
    string? ErpDocumentRef,
    string? Remarks,
    string? ApprovedBy,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

/// <summary>Header plus the full stage/operation timeline, for the job detail page.</summary>
public sealed record JobDetailResponse(JobSummaryResponse Job, IReadOnlyList<JobStepResponse> Steps);

/// <summary>
/// A failure, in both registers. The ERP UI shows <paramref name="LaymanMessage"/> by default and
/// reveals <paramref name="TechnicalMessage"/> to an administrator.
/// </summary>
public sealed record JobErrorResponse(
    Guid Id,
    Guid? StepId,
    string ErrorType,
    string LaymanMessage,
    string TechnicalMessage,
    string? ApiEndpoint,
    DateTimeOffset CreatedAtUtc);

/// <summary>Result of a control call, so the UI can report what actually happened.</summary>
public sealed record JobActionResponse(Guid JobId, string Status, string Message);

/// <summary>Result of asking for a cycle. Not started is the normal case when one is already live.</summary>
public sealed record RunNowResponse(bool Started, Guid? JobId, string Reason);

/// <summary>Counts by status plus the current cycle, for the dashboard.</summary>
public sealed record DashboardResponse(
    IReadOnlyDictionary<string, int> JobsByStatus,
    int TotalJobs,
    Guid? LiveCycleJobId,
    DateTimeOffset? LiveCycleStartedAtUtc);

/// <summary>Per-module runtime settings, as shown and edited in the ERP UI.</summary>
public sealed record AutomationConfigResponse(
    string Module,
    bool EnableAgent,
    bool EnableModule,
    string Mode,
    int PollIntervalSeconds,
    int ReconcileIntervalMinutes,
    TimeOnly? WorkingHoursStart,
    TimeOnly? WorkingHoursEnd,
    int RetryCount,
    int ParallelWorkers,
    string LoggingLevel,
    bool IsLicensed,
    int PayloadRetentionDays,
    int LogRetentionDays,
    int ErrorRetentionDays,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>
/// A settings change. Every field is optional: the UI sends only what the operator altered, and
/// anything omitted keeps its stored value.
/// </summary>
public sealed record UpdateConfigRequest(
    bool? EnableAgent,
    bool? EnableModule,
    string? Mode,
    int? PollIntervalSeconds,
    int? ReconcileIntervalMinutes,
    TimeOnly? WorkingHoursStart,
    TimeOnly? WorkingHoursEnd,
    bool ClearWorkingHours,
    int? RetryCount,
    int? ParallelWorkers,
    string? LoggingLevel,
    bool? IsLicensed,
    int? PayloadRetentionDays,
    int? LogRetentionDays,
    int? ErrorRetentionDays,
    string? UpdatedBy);

/// <summary>Who cancelled a job and why — recorded for audit, so both are required.</summary>
public sealed record CancelJobRequest(string CancelledBy, string Reason);
