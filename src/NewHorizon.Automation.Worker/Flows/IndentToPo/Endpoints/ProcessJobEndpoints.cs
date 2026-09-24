using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Contracts;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Services;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Endpoints;

/// <summary>
/// The history of indent → purchase order conversions: which indent, under which trigger, how far
/// it got, and what came out.
/// </summary>
/// <remarks>
/// <para>
/// HTTP only. Every handler validates what it was sent, calls one service and shapes the answer;
/// there is no query and no rule here.
/// </para>
/// <para>
/// Reads that the generic job API already answers are not repeated: errors, cancel and the
/// job list live at <c>/api/automation/jobs</c> and work for these jobs like any other. What is
/// here is what needs the indent's shape to make sense.
/// </para>
/// </remarks>
public static class ProcessJobEndpoints
{
    public static IEndpointRouteBuilder MapProcessJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Applied to the group so an endpoint added here is authenticated by default rather than
        // by remembering to opt in. Accepts either the machine API key or an ERP bearer token: the
        // ERP frontend reads this history straight from the browser.
        //
        // A browser caller must also hold Role Management form 011172 ("PO Automation Run History")
        // at 'I' — the only right that form defines. The API-key machine callers carry no user
        // identity and are unaffected. (Should retry / tracked-start ever need their own right,
        // add 'E' to node 011172 in the ERP's ModuleInfo.xml and split it out here.)
        var group = endpoints.MapGroup("/api/process-jobs")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .RequireErpFormRight(ErpFormRightFilter.PoAutomationHistoryForm, ErpFormRightFilter.Inquiry);

        // The tracked trigger. The untracked one at /api/automation/indent-to-po still works and
        // still converts; this is the one that leaves a history.
        group.MapPost(string.Empty, StartAsync).WithName("StartProcessJob");

        // The grid: every conversion, newest first. Same path as the trigger, different verb —
        // GET lists what POST created.
        group.MapGet(string.Empty, ListAsync).WithName("ListProcessJobs");

        // Mapped ahead of "/{jobId:guid}" for readability; the route's :guid constraint already
        // makes the two unambiguous in either order — "summary" cannot parse as a guid.
        group.MapGet("/summary", GetSummaryAsync).WithName("GetProcessJobSummary");

        // The dashboard's charts: one row per day, oldest first, gaps filled with zero.
        group.MapGet("/daily-summary", GetDailyStatsAsync).WithName("GetProcessJobDailyStats");

        group.MapGet("/{jobId:guid}", GetExecutionAsync).WithName("GetProcessJob");
        group.MapGet("/indent/{indentId:long}", GetIndentHistoryAsync).WithName("GetIndentProcessHistory");
        group.MapPost("/{jobId:guid}/retry", RetryAsync).WithName("RetryProcessJob");

        group.MapGet("/runs", ListRunsAsync).WithName("ListProcessRuns");
        group.MapGet("/runs/{runId:guid}", GetRunAsync).WithName("GetProcessRun");

        return endpoints;
    }

    /// <summary>Converts the indents this request allows, recording the whole thing.</summary>
    private static async Task<IResult> StartAsync(
        StartProcessJobRequest? request,
        IProcessJobOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        request ??= new StartProcessJobRequest();

        var (result, refusal) = await orchestrator.StartAsync(request, cancellationToken);

        return result is null ? Refused(refusal!) : Results.Ok(result);
    }

    /// <summary>
    /// Every conversion as a grid line: job, document, workflow, trigger, mode, stage, status and
    /// duration. Filters narrow together; all are optional.
    /// </summary>
    private static async Task<IResult> ListAsync(
        IProcessJobService processJobs,
        IOptions<AutomationAgentOptions> options,
        CancellationToken cancellationToken,
        string? search = null,
        string? workflow = null,
        string? status = null,
        string? company = null,
        string? trigger = null,
        string? stage = null,
        string? indentType = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50)
    {
        var (query, error) = BuildQuery(
            search, workflow, status, company, trigger, stage, indentType, from, to, page, pageSize);

        if (query is null)
        {
            return error!;
        }

        var result = await processJobs.ListExecutionsAsync(query, cancellationToken);

        // The company the agent authenticates and posts under, the same on every row. Read from
        // configuration rather than the database, because there is no company column and this
        // installation serves exactly one client's ERP.
        var companyId = options.Value.ErpApi.CompanyId;

        return Results.Ok(new Application.Jobs.PagedResult<ProcessJobRowResponse>(
            [.. result.Items.Select(row => ProcessJobMapper.ToResponse(row, companyId))],
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

    /// <summary>
    /// The grid collapsed to totals, for the same filter <see cref="ListAsync"/> takes (its
    /// <c>page</c>/<c>pageSize</c> have nothing to collapse and are not accepted here).
    /// </summary>
    private static async Task<IResult> GetSummaryAsync(
        IProcessJobService processJobs,
        CancellationToken cancellationToken,
        string? search = null,
        string? workflow = null,
        string? status = null,
        string? company = null,
        string? trigger = null,
        string? stage = null,
        string? indentType = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var (query, error) = BuildQuery(
            search, workflow, status, company, trigger, stage, indentType, from, to, page: 1, pageSize: 1);

        if (query is null)
        {
            return error!;
        }

        var summary = await processJobs.GetSummaryAsync(query, cancellationToken);

        return Results.Ok(ProcessJobMapper.ToResponse(summary));
    }

    /// <summary>The two charts on the dashboard, one row per UTC day over the trailing window.</summary>
    private static async Task<IResult> GetDailyStatsAsync(
        IProcessJobService processJobs,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var stats = await processJobs.GetDailyStatsAsync(days, cancellationToken);

        return Results.Ok(stats.Select(ProcessJobMapper.ToResponse).ToList());
    }

    /// <summary>
    /// Validates and shapes the filter <see cref="ListAsync"/> and <see cref="GetSummaryAsync"/>
    /// both take, so a query string that means something to one means the same thing to the other —
    /// and a rule that changes here (a new workflow, a renamed stage) changes for both at once.
    /// </summary>
    private static (ProcessJobQuery? Query, IResult? Error) BuildQuery(
        string? search,
        string? workflow,
        string? status,
        string? company,
        string? trigger,
        string? stage,
        string? indentType,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize)
    {
        if (!TryParse<JobStatus>(status, "job status", out var jobStatus, out var statusError))
        {
            return (null, statusError);
        }

        // Validated against the real workflow names for the same reason as stage: an unknown one
        // is a mistake worth naming. "IndentToPO" is a plausible guess and matches nothing — the
        // value this grid actually carries is "IndentToPurchaseOrder".
        if (!string.IsNullOrWhiteSpace(workflow)
            && !WorkflowNames.All.Contains(workflow.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return (null, Results.Problem(
                title: "Unknown workflow.",
                detail: $"'{workflow}' is not a workflow. Expected one of: "
                    + $"{string.Join(", ", WorkflowNames.All)}.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        if (!TryParse<TriggerSource>(trigger, "trigger", out var triggerSource, out var triggerError))
        {
            return (null, triggerError);
        }

        if (!TryParse<IndentKind>(indentType, "indent type", out var indentKind, out var kindError))
        {
            return (null, kindError);
        }

        // Validated against the five real stages rather than passed through: a typo would
        // otherwise return an empty grid, which reads as "nothing happened" instead of "you asked
        // for a stage that does not exist".
        if (!string.IsNullOrWhiteSpace(stage)
            && !IndentPoStages.InOrder.Contains(stage.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return (null, Results.Problem(
                title: "Unknown stage.",
                detail: $"'{stage}' is not a stage. Expected one of: "
                    + $"{string.Join(", ", IndentPoStages.InOrder)}.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        return (new ProcessJobQuery
        {
            Search = search,
            WorkflowType = workflow,
            Company = company,
            Status = jobStatus,
            TriggerSource = triggerSource,
            Stage = string.IsNullOrWhiteSpace(stage)
                ? null
                : IndentPoStages.InOrder.First(known =>
                    known.Equals(stage.Trim(), StringComparison.OrdinalIgnoreCase)),
            IndentKind = indentKind,
            FromUtc = from,
            ToUtc = to,
            Page = page,
            PageSize = pageSize,
        }, null);
    }

    /// <summary>
    /// Parses an optional enum filter, or hands back the refusal that names the offender and lists
    /// what would have been accepted — the convention every other filter here follows.
    /// </summary>
    private static bool TryParse<TEnum>(string? value, string label, out TEnum? parsed, out IResult? error)
        where TEnum : struct, Enum
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var result))
        {
            parsed = result;
            return true;
        }

        error = Results.Problem(
            title: $"Unknown {label}.",
            detail: $"'{value}' is not a {label}. Expected one of: "
                + $"{string.Join(", ", Enum.GetNames<TEnum>())}.",
            statusCode: StatusCodes.Status400BadRequest);

        return false;
    }

    /// <summary>One execution: its indent, stage timeline, outcomes and failures.</summary>
    private static async Task<IResult> GetExecutionAsync(
        Guid jobId,
        IProcessJobService processJobs,
        CancellationToken cancellationToken)
    {
        var detail = await processJobs.GetExecutionAsync(jobId, cancellationToken);

        return detail is null
            ? Results.NotFound()
            : Results.Ok(ProcessJobMapper.ToResponse(detail));
    }

    /// <summary>Every attempt at one indent, oldest first — the history and retry view.</summary>
    private static async Task<IResult> GetIndentHistoryAsync(
        long indentId,
        IProcessJobService processJobs,
        CancellationToken cancellationToken,
        string? indentType = null)
    {
        IndentKind? kind = null;

        if (!string.IsNullOrWhiteSpace(indentType))
        {
            if (!Enum.TryParse<IndentKind>(indentType.Trim(), ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    title: "Unknown indent type.",
                    detail: $"'{indentType}' is not an indent type. Expected one of: "
                        + $"{string.Join(", ", Enum.GetNames<IndentKind>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            kind = parsed;
        }

        var history = await processJobs.GetExecutionsByIndentAsync(indentId, kind, cancellationToken);

        if (history.Conversions.Count == 0)
        {
            return Results.NotFound();
        }

        // Ambiguous rather than guessed: the same number can be a live XINDID and a live
        // XINDAUTOID, and they are different indents in different modules.
        return history.Ambiguous
            ? Results.Problem(
                title: "That indent id belongs to more than one indent.",
                detail: "It exists as both a material and a service indent. Re-send with "
                    + "?indentType=regular, ?indentType=capital or ?indentType=service.",
                statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(ProcessJobMapper.ToResponse(indentId, history));
    }

    /// <summary>Attempts the same indent again, as a new execution.</summary>
    private static async Task<IResult> RetryAsync(
        Guid jobId,
        IProcessJobOrchestrator orchestrator,
        CancellationToken cancellationToken,
        string? triggeredBy = null)
    {
        var (result, refusal) = await orchestrator.RetryAsync(jobId, triggeredBy, cancellationToken);

        return result is null ? Refused(refusal!) : Results.Ok(result);
    }

    /// <summary>What one trigger did — including a trigger that converted nothing.</summary>
    private static async Task<IResult> GetRunAsync(
        Guid runId,
        IProcessJobService processJobs,
        CancellationToken cancellationToken)
    {
        var detail = await processJobs.GetRunAsync(runId, cancellationToken);

        return detail is null
            ? Results.NotFound()
            : Results.Ok(ProcessJobMapper.ToResponse(detail));
    }

    private static async Task<IResult> ListRunsAsync(
        IProcessJobService processJobs,
        CancellationToken cancellationToken,
        string? trigger = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50)
    {
        TriggerSource? triggerSource = null;
        if (!string.IsNullOrWhiteSpace(trigger))
        {
            if (!Enum.TryParse<TriggerSource>(trigger.Trim(), ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    title: "Unknown trigger.",
                    detail: $"'{trigger}' is not a trigger. Expected one of: "
                        + $"{string.Join(", ", Enum.GetNames<TriggerSource>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            triggerSource = parsed;
        }

        RunStatus? runStatus = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<RunStatus>(status.Trim(), ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    title: "Unknown run status.",
                    detail: $"'{status}' is not a run status. Expected one of: "
                        + $"{string.Join(", ", Enum.GetNames<RunStatus>())}.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            runStatus = parsed;
        }

        var result = await processJobs.ListRunsAsync(
            new ProcessRunQuery
            {
                TriggerSource = triggerSource,
                Status = runStatus,
                FromUtc = from,
                ToUtc = to,
                Page = page,
                PageSize = pageSize,
            },
            cancellationToken);

        return Results.Ok(new Application.Jobs.PagedResult<ProcessRunResponse>(
            [.. result.Items.Select(ProcessJobMapper.ToResponse)],
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

    /// <summary>
    /// A refusal the caller can act on. Tracking being off is a 503, not a 404: the endpoint
    /// exists, the installation simply has no database behind it, and the message says so.
    /// </summary>
    private static IResult Refused(ProcessJobRefusal refusal) =>
        Results.Problem(title: refusal.Title, detail: refusal.Detail, statusCode: refusal.StatusCode);
}
