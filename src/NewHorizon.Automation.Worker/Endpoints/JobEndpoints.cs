using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Contracts;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// What the ERP needs to see and steer the agent. Every endpoint either reads or controls — none
/// performs ERP work, which only the engine does.
/// </summary>
/// <remarks>
/// Approve and reject are deliberately absent. Nothing in this workflow sets
/// <c>RequiresApprovalInPartial</c>, so no job can reach an approval gate; adding the endpoints
/// would offer the ERP UI a control that could never fire. They arrive with the first operation
/// that needs a Partial-mode gate (design doc §18.5).
/// </remarks>
public static class JobEndpoints
{
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Applied to the group so a new endpoint added here is authenticated by default rather
        // than by remembering to opt in.
        var group = endpoints.MapGroup("/api/automation").AddEndpointFilter<ApiKeyFilter>();

        group.MapGet("/jobs", ListJobsAsync).WithName("ListJobs");
        group.MapGet("/jobs/{jobId:guid}", GetJobAsync).WithName("GetJob");
        group.MapGet("/jobs/{jobId:guid}/errors", GetJobErrorsAsync).WithName("GetJobErrors");

        group.MapPost("/jobs/{jobId:guid}/retry", RetryJobAsync).WithName("RetryJob");
        group.MapPost("/jobs/{jobId:guid}/resume", ResumeJobAsync).WithName("ResumeJob");
        group.MapPost("/jobs/{jobId:guid}/cancel", CancelJobAsync).WithName("CancelJob");

        group.MapPost("/run-now", RunNowAsync).WithName("RunCycleNow");

        // The ERP frontend shows the dashboard counts, so this one accepts a bearer token as well
        // as the API key. Mapped outside the group to escape its API-key-only filter — the rest of
        // the group is controls, not a read the UI needs.
        endpoints.MapGet("/api/automation/dashboard", GetDashboardAsync)
            .WithName("GetDashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        return endpoints;
    }

    private static async Task<IResult> ListJobsAsync(
        IJobRepository jobs,
        CancellationToken cancellationToken,
        string? status = null,
        string? workflowType = null,
        string? documentId = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50)
    {
        if (status is not null && !Enum.TryParse<JobStatus>(status, ignoreCase: true, out _))
        {
            return Results.Problem(
                $"'{status}' is not a job status. Expected one of: {string.Join(", ", Enum.GetNames<JobStatus>())}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var query = new JobQuery
        {
            Status = status is null ? null : Enum.Parse<JobStatus>(status, ignoreCase: true),
            WorkflowType = workflowType,
            DocumentId = documentId,
            CreatedFromUtc = from,
            CreatedToUtc = to,
            Page = Math.Max(page, 1),

            // Capped: the ERP UI is paged, and an unbounded page size would let one call pull the
            // whole job history into memory.
            PageSize = Math.Clamp(pageSize, 1, 200),
        };

        var result = await jobs.ListAsync(query, cancellationToken);

        return Results.Ok(new PagedResult<JobSummaryResponse>(
            result.Items.Select(ToSummary).ToList(),
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> GetJobAsync(
        Guid jobId,
        IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);

        if (job is null)
        {
            return Results.NotFound();
        }

        var steps = job.Steps
            .OrderBy(step => step.Sequence)
            .Select(step => new JobStepResponse(
                step.Id,
                step.Stage,
                step.OperationName,
                step.Target,
                step.Sequence,
                step.Kind.ToString(),
                step.Status.ToString(),
                step.RetryCount,
                step.ErpDocumentRef,
                step.Remarks,
                step.ApprovedBy,
                step.StartedAtUtc,
                step.CompletedAtUtc))
            .ToList();

        return Results.Ok(new JobDetailResponse(ToSummary(job), steps));
    }

    private static async Task<IResult> GetJobErrorsAsync(
        Guid jobId,
        IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);

        if (job is null)
        {
            return Results.NotFound();
        }

        var errors = await jobs.GetErrorsAsync(jobId, cancellationToken);

        return Results.Ok(errors
            .Select(error => new JobErrorResponse(
                error.Id,
                error.StepId,
                error.ErrorType.ToString(),
                error.LaymanMessage,
                error.TechnicalMessage,
                error.ApiEndpoint,
                error.CreatedAtUtc))
            .ToList());
    }

    /// <summary>
    /// Re-queues a failed job at elevated priority, so an operator who has just fixed the cause
    /// does not wait behind the routine backlog.
    /// </summary>
    private static Task<IResult> RetryJobAsync(
        Guid jobId,
        IJobRepository jobs,
        CancellationToken cancellationToken) =>
        RequeueAsync(jobId, jobs, priorityBoost: 100, "Job re-queued for retry.", cancellationToken);

    /// <summary>
    /// The same recovery at ordinary priority. Both restart at the first operation that is not
    /// Completed — neither repeats work the ERP has already done.
    /// </summary>
    private static Task<IResult> ResumeJobAsync(
        Guid jobId,
        IJobRepository jobs,
        CancellationToken cancellationToken) =>
        RequeueAsync(jobId, jobs, priorityBoost: 0, "Job re-queued to resume.", cancellationToken);

    private static async Task<IResult> RequeueAsync(
        Guid jobId,
        IJobRepository jobs,
        int priorityBoost,
        string message,
        CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken);

        if (job is null)
        {
            return Results.NotFound();
        }

        if (job.ConversionId is not null)
        {
            // An indent conversion is run inline by whoever triggered it, not walked by the
            // engine. Re-queueing one would put a Pending job in front of a dispatcher that has no
            // workflow definition for it, and the job would fail for a reason nobody could act on.
            return Results.Problem(
                title: "An indent conversion cannot be re-queued here.",
                detail: "It is not run by the job engine, so there is nothing to resume. "
                    + $"POST /api/process-jobs/{jobId}/retry converts the same indent again as a "
                    + "new execution.",
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            job.RequeueForRetry(priorityBoost);
        }
        catch (InvalidJobTransitionException ex)
        {
            // Asking to retry a job that is running or already finished is an operator mistake,
            // not a server fault — say so rather than returning 500.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await jobs.SaveAsync(job, cancellationToken);

        return Results.Ok(new JobActionResponse(job.Id, job.Status.ToString(), message));
    }

    private static async Task<IResult> CancelJobAsync(
        Guid jobId,
        CancelJobRequest request,
        IJobRepository jobs,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.CancelledBy) || string.IsNullOrWhiteSpace(request.Reason))
        {
            // Both are audit fields: a cancellation with no attributable actor or reason is worse
            // than no cancellation record at all.
            return Results.Problem(
                "cancelledBy and reason are both required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var job = await jobs.GetAsync(jobId, cancellationToken);

        if (job is null)
        {
            return Results.NotFound();
        }

        try
        {
            job.Cancel(request.CancelledBy, request.Reason, clock.UtcNow);
        }
        catch (InvalidJobTransitionException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await jobs.SaveAsync(job, cancellationToken);

        return Results.Ok(new JobActionResponse(job.Id, job.Status.ToString(), "Job cancelled."));
    }

    /// <summary>
    /// Starts a cycle immediately instead of waiting for the timer. Bypasses the working-hours
    /// window because a person has asked for it explicitly; the one-live-cycle rule still holds.
    /// </summary>
    private static async Task<IResult> RunNowAsync(
        ICycleEnqueueService enqueue,
        CancellationToken cancellationToken)
    {
        var outcome = await enqueue.EnqueueAsync(
            WorkflowNames.AutoShopCycle,
            respectSchedule: false,
            priority: 100,
            cancellationToken);

        return Results.Ok(new RunNowResponse(outcome.Started, outcome.JobId, outcome.Reason));
    }

    private static async Task<IResult> GetDashboardAsync(
        IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        var counts = await jobs.GetStatusCountsAsync(cancellationToken);

        var live = await jobs.ListAsync(
            new JobQuery { WorkflowType = WorkflowNames.AutoShopCycle, PageSize = 1, Status = JobStatus.Running },
            cancellationToken);

        var runningCycle = live.Items.FirstOrDefault();

        return Results.Ok(new DashboardResponse(
            counts.ToDictionary(entry => entry.Key.ToString(), entry => entry.Value),
            counts.Values.Sum(),
            runningCycle?.Id,
            runningCycle?.StartedAtUtc));
    }

    private static JobSummaryResponse ToSummary(Job job) => new(
        job.Id,
        job.CorrelationId,
        job.WorkflowType,
        job.DocumentType,
        job.DocumentId,
        job.Status.ToString(),
        job.Mode.ToString(),
        job.CurrentStage,
        job.Priority,
        job.RetryCount,
        job.CreatedAtUtc,
        job.StartedAtUtc,
        job.CompletedAtUtc,
        job.Steps.Count,
        job.Steps.Count(step => step.Status is StepStatus.Completed or StepStatus.Skipped));
}
