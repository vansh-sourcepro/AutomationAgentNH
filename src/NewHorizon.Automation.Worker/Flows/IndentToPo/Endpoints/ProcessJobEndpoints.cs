using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Infrastructure.Persistence;
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
    internal static async Task<IResult> GetSummaryAsync(
        IProcessJobService processJobs,
        IServiceProvider sp,
        CancellationToken cancellationToken,
        string? module = null,
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
        var selected = (module ?? workflow)?.Trim().ToLowerInvariant();
        bool isGrnOnly = selected is "po-to-grn" or "potogrn" or "grn" or "po_to_grn";
        bool isIndentOnly = selected is "indent-to-po" or "indenttopo" or "indent" or "indenttopurchaseorder" or "indent_to_po";

        var dbContext = sp.GetService<AutomationDbContext>();

        // 1. PO → GRN only
        if (isGrnOnly)
        {
            if (dbContext is null)
            {
                return Results.Ok(new ProcessJobSummaryResponse(0, new Dictionary<string, int>(), 0, null, null, 0, 0, 0, 0));
            }

            var grnSummary = await ComputePoGrnSummaryAsync(dbContext, from, to, cancellationToken);
            return Results.Ok(grnSummary);
        }

        // 2. Indent → PO only
        if (isIndentOnly || dbContext is null)
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

        // 3. No parameter (or "all") → Unified combined KPIs across both flows
        var (defaultQuery, defaultError) = BuildQuery(
            search, null, status, company, trigger, stage, indentType, from, to, page: 1, pageSize: 1);

        if (defaultQuery is null)
        {
            return defaultError!;
        }

        var indentStats = ProcessJobMapper.ToResponse(await processJobs.GetSummaryAsync(defaultQuery, cancellationToken));

        var grnStats = await ComputePoGrnSummaryAsync(dbContext, from, to, cancellationToken);

        var combinedCounts = new Dictionary<string, int>(indentStats.CountsByStatus);
        foreach (var (k, v) in grnStats.CountsByStatus)
        {
            combinedCounts[k] = combinedCounts.GetValueOrDefault(k) + v;
        }

        int totalJobs = indentStats.TotalJobs + grnStats.TotalJobs;
        int successCount = indentStats.SuccessCount + grnStats.SuccessCount;
        double? successRate = totalJobs > 0 ? Math.Round((double)successCount / totalJobs, 4) : null;
        int totalTriggers = indentStats.TotalTriggerAttempts + grnStats.TotalTriggerAttempts;
        int emptyTriggers = indentStats.TriggerAttemptsWithoutEligibleIndent + grnStats.TriggerAttemptsWithoutEligibleIndent;
        int businessRefusals = indentStats.BusinessRefusalCount + grnStats.BusinessRefusalCount;
        int technicalFailures = indentStats.TechnicalFailureCount + grnStats.TechnicalFailureCount;

        double? avgDuration = null;
        if (indentStats.AverageDurationMs.HasValue && grnStats.AverageDurationMs.HasValue && totalTriggers > 0)
        {
            avgDuration = Math.Round(
                ((indentStats.AverageDurationMs.Value * indentStats.TotalTriggerAttempts) +
                 (grnStats.AverageDurationMs.Value * grnStats.TotalTriggerAttempts)) / totalTriggers, 1);
        }
        else
        {
            avgDuration = indentStats.AverageDurationMs ?? grnStats.AverageDurationMs;
        }

        return Results.Ok(new ProcessJobSummaryResponse(
            totalJobs,
            combinedCounts,
            successCount,
            successRate,
            avgDuration,
            totalTriggers,
            emptyTriggers,
            businessRefusals,
            technicalFailures));
    }

    private static async Task<ProcessJobSummaryResponse> ComputePoGrnSummaryAsync(
        AutomationDbContext dbContext,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? effectiveTo = to.HasValue ? (to.Value.TimeOfDay == TimeSpan.Zero ? to.Value.Date.AddDays(1).AddTicks(-1) : to.Value) : null;
        var runsQuery = dbContext.PoGrnRuns.AsNoTracking();
        if (from.HasValue) runsQuery = runsQuery.Where(r => r.StartedAtUtc >= from.Value);
        if (effectiveTo.HasValue) runsQuery = runsQuery.Where(r => r.StartedAtUtc <= effectiveTo.Value);

        var runs = await runsQuery.ToListAsync(cancellationToken);

        int totalTriggerAttempts = runs.Count;
        int emptyTriggers = runs.Count(r => r.PosExamined == 0);
        int totalPosExamined = runs.Sum(r => r.PosExamined);
        int totalGrnsCreated = runs.Sum(r => r.GrnsCreated);
        int totalPosSkipped = runs.Sum(r => r.PosSkipped);
        int totalPosFailed = runs.Sum(r => r.PosFailed);

        var completedRuns = runs.Where(r => r.CompletedAtUtc.HasValue).ToList();
        double? avgDuration = completedRuns.Count > 0
            ? Math.Round(completedRuns.Average(r => (r.CompletedAtUtc!.Value - r.StartedAtUtc).TotalMilliseconds), 1)
            : null;

        double? successRate = totalPosExamined > 0
            ? Math.Round((double)totalGrnsCreated / totalPosExamined, 4)
            : null;

        var counts = new Dictionary<string, int>
        {
            ["Completed"] = totalGrnsCreated,
            ["Skipped"] = totalPosSkipped,
            ["Failed"] = totalPosFailed,
        };

        return new ProcessJobSummaryResponse(
            TotalJobs: totalPosExamined,
            CountsByStatus: counts,
            SuccessCount: totalGrnsCreated,
            SuccessRate: successRate,
            AverageDurationMs: avgDuration,
            TotalTriggerAttempts: totalTriggerAttempts,
            TriggerAttemptsWithoutEligibleIndent: emptyTriggers,
            BusinessRefusalCount: totalPosSkipped,
            TechnicalFailureCount: totalPosFailed,
            TotalPosExamined: totalPosExamined,
            TotalGrnsCreated: totalGrnsCreated,
            TotalPosSkipped: totalPosSkipped,
            TotalPosFailed: totalPosFailed,
            TriggerAttemptsWithoutEligiblePo: emptyTriggers);
    }

    /// <summary>The two charts on the dashboard, one row per UTC day over the trailing window.</summary>
    internal static async Task<IResult> GetDailyStatsAsync(
        IProcessJobService processJobs,
        IServiceProvider sp,
        CancellationToken cancellationToken,
        string? module = null,
        string? workflow = null,
        int days = 30)
    {
        var selected = (module ?? workflow)?.Trim().ToLowerInvariant();
        bool isGrnOnly = selected is "po-to-grn" or "potogrn" or "grn" or "po_to_grn";
        bool isIndentOnly = selected is "indent-to-po" or "indenttopo" or "indent" or "indenttopurchaseorder" or "indent_to_po";

        var dbContext = sp.GetService<AutomationDbContext>();

        if (isGrnOnly)
        {
            if (dbContext is null)
            {
                return Results.Ok(new List<DailyConversionStatResponse>());
            }

            var grnDaily = await ComputePoGrnDailyStatsAsync(dbContext, days, cancellationToken);
            return Results.Ok(grnDaily);
        }

        if (isIndentOnly || dbContext is null)
        {
            var stats = await processJobs.GetDailyStatsAsync(days, cancellationToken);
            return Results.Ok(stats.Select(ProcessJobMapper.ToResponse).ToList());
        }

        var indentStats = (await processJobs.GetDailyStatsAsync(days, cancellationToken))
            .Select(ProcessJobMapper.ToResponse)
            .ToList();

        var grnDailyStats = await ComputePoGrnDailyStatsAsync(dbContext, days, cancellationToken);

        var byDate = new Dictionary<string, (int Created, int Converted, int Failed)>();
        foreach (var s in indentStats)
        {
            byDate[s.Date] = (s.PurchaseOrdersCreated, s.IndentsConverted, s.IndentsFailed);
        }
        foreach (var s in grnDailyStats)
        {
            if (byDate.TryGetValue(s.Date, out var existing))
            {
                byDate[s.Date] = (existing.Created + s.PurchaseOrdersCreated, existing.Converted + s.IndentsConverted, existing.Failed + s.IndentsFailed);
            }
            else
            {
                byDate[s.Date] = (s.PurchaseOrdersCreated, s.IndentsConverted, s.IndentsFailed);
            }
        }

        var result = byDate.OrderBy(kv => kv.Key)
            .Select(kv => new DailyConversionStatResponse(kv.Key, kv.Value.Created, kv.Value.Converted, kv.Value.Failed))
            .ToList();

        return Results.Ok(result);
    }

    private static async Task<List<DailyConversionStatResponse>> ComputePoGrnDailyStatsAsync(
        AutomationDbContext dbContext,
        int days,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.Date.AddDays(-days);
        var runs = await dbContext.PoGrnRuns.AsNoTracking()
            .Where(r => r.StartedAtUtc >= cutoff)
            .ToListAsync(cancellationToken);

        return runs
            .GroupBy(r => r.StartedAtUtc.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new DailyConversionStatResponse(
                g.Key,
                PurchaseOrdersCreated: g.Sum(r => r.GrnsCreated),
                IndentsConverted: g.Sum(r => r.PosExamined),
                IndentsFailed: g.Sum(r => r.PosFailed),
                GrnsCreated: g.Sum(r => r.GrnsCreated),
                PosExamined: g.Sum(r => r.PosExamined),
                PosFailed: g.Sum(r => r.PosFailed)))
            .ToList();
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
            ToUtc = to.HasValue ? (to.Value.TimeOfDay == TimeSpan.Zero ? to.Value.Date.AddDays(1).AddTicks(-1) : to.Value) : null,
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
    /// <summary>What one trigger did — including a trigger that converted nothing.</summary>
    internal static async Task<IResult> GetRunAsync(
        Guid runId,
        IProcessJobService processJobs,
        IServiceProvider sp,
        CancellationToken cancellationToken)
    {
        var detail = await processJobs.GetRunAsync(runId, cancellationToken);

        if (detail is not null)
        {
            return Results.Ok(ProcessJobMapper.ToResponse(detail));
        }

        var dbContext = sp.GetService<AutomationDbContext>();
        if (dbContext is not null)
        {
            var grnRun = await dbContext.PoGrnRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (grnRun is not null)
            {
                var receipts = await dbContext.PoGrnReceipts.AsNoTracking()
                    .Where(r => r.RunId == runId)
                    .ToListAsync(cancellationToken);

                return Results.Ok(new
                {
                    run = ProcessJobMapper.ToResponse(grnRun, receipts),
                    receipts
                });
            }
        }

        return Results.NotFound();
    }

    internal static async Task<IResult> ListRunsAsync(
        IProcessJobService processJobs,
        IServiceProvider sp,
        CancellationToken cancellationToken,
        string? module = null,
        string? workflow = null,
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

        var selected = (module ?? workflow)?.Trim().ToLowerInvariant();
        bool isGrnOnly = selected is "po-to-grn" or "potogrn" or "grn" or "po_to_grn";
        bool isIndentOnly = selected is "indent-to-po" or "indenttopo" or "indent" or "indenttopurchaseorder" or "indent_to_po";

        var dbContext = sp.GetService<AutomationDbContext>();
        DateTimeOffset? effectiveTo = to.HasValue ? (to.Value.TimeOfDay == TimeSpan.Zero ? to.Value.Date.AddDays(1).AddTicks(-1) : to.Value) : null;
        int clampedPage = Math.Max(1, page);
        int clampedPageSize = Math.Clamp(pageSize, 1, 200);

        // 1. PO → GRN only
        if (isGrnOnly)
        {
            if (dbContext is null)
            {
                return Results.Ok(new Application.Jobs.PagedResult<ProcessRunResponse>([], 0, clampedPage, clampedPageSize));
            }

            var grnQuery = dbContext.PoGrnRuns.AsNoTracking();
            if (triggerSource.HasValue) grnQuery = grnQuery.Where(r => r.Trigger == triggerSource.Value);
            if (runStatus.HasValue) grnQuery = grnQuery.Where(r => r.Status == runStatus.Value);
            if (from.HasValue) grnQuery = grnQuery.Where(r => r.StartedAtUtc >= from.Value);
            if (effectiveTo.HasValue) grnQuery = grnQuery.Where(r => r.StartedAtUtc <= effectiveTo.Value);

            var total = await grnQuery.CountAsync(cancellationToken);
            var items = await grnQuery.OrderByDescending(r => r.StartedAtUtc)
                .Skip((clampedPage - 1) * clampedPageSize)
                .Take(clampedPageSize)
                .ToListAsync(cancellationToken);

            var runIds = items.Select(r => r.Id).ToList();
            var receipts = runIds.Count > 0
                ? await dbContext.PoGrnReceipts.AsNoTracking()
                    .Where(r => runIds.Contains(r.RunId))
                    .ToListAsync(cancellationToken)
                : [];

            var receiptsByRun = receipts.GroupBy(r => r.RunId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<PoGrnReceipt>)g.ToList());

            return Results.Ok(new Application.Jobs.PagedResult<ProcessRunResponse>(
                [.. items.Select(run => ProcessJobMapper.ToResponse(run, receiptsByRun.GetValueOrDefault(run.Id)))],
                total,
                clampedPage,
                clampedPageSize));
        }

        // 2. Indent → PO only
        if (isIndentOnly || dbContext is null)
        {
            var result = await processJobs.ListRunsAsync(
                new ProcessRunQuery
                {
                    TriggerSource = triggerSource,
                    Status = runStatus,
                    FromUtc = from,
                    ToUtc = effectiveTo,
                    Page = clampedPage,
                    PageSize = clampedPageSize,
                },
                cancellationToken);

            return Results.Ok(new Application.Jobs.PagedResult<ProcessRunResponse>(
                [.. result.Items.Select(ProcessJobMapper.ToResponse)],
                result.TotalCount,
                result.Page,
                result.PageSize));
        }

        // 3. No parameter passed (or "all") → Merge both Indent → PO and PO → GRN runs
        var indentRunsResult = await processJobs.ListRunsAsync(
            new ProcessRunQuery
            {
                TriggerSource = triggerSource,
                Status = runStatus,
                FromUtc = from,
                ToUtc = effectiveTo,
                Page = 1,
                PageSize = Math.Max(clampedPageSize * clampedPage, 100),
            },
            cancellationToken);

        var allGrnQuery = dbContext.PoGrnRuns.AsNoTracking();
        if (triggerSource.HasValue) allGrnQuery = allGrnQuery.Where(r => r.Trigger == triggerSource.Value);
        if (runStatus.HasValue) allGrnQuery = allGrnQuery.Where(r => r.Status == runStatus.Value);
        if (from.HasValue) allGrnQuery = allGrnQuery.Where(r => r.StartedAtUtc >= from.Value);
        if (effectiveTo.HasValue) allGrnQuery = allGrnQuery.Where(r => r.StartedAtUtc <= effectiveTo.Value);

        var grnTotal = await allGrnQuery.CountAsync(cancellationToken);
        var grnItems = await allGrnQuery.OrderByDescending(r => r.StartedAtUtc)
            .Take(Math.Max(clampedPageSize * clampedPage, 100))
            .ToListAsync(cancellationToken);

        var grnRunIds = grnItems.Select(r => r.Id).ToList();
        var grnReceipts = grnRunIds.Count > 0
            ? await dbContext.PoGrnReceipts.AsNoTracking()
                .Where(r => grnRunIds.Contains(r.RunId))
                .ToListAsync(cancellationToken)
            : [];

        var grnReceiptsByRun = grnReceipts.GroupBy(r => r.RunId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<PoGrnReceipt>)g.ToList());

        var merged = indentRunsResult.Items.Select(ProcessJobMapper.ToResponse)
            .Concat(grnItems.Select(run => ProcessJobMapper.ToResponse(run, grnReceiptsByRun.GetValueOrDefault(run.Id))))
            .OrderByDescending(r => r.StartedAtUtc)
            .Skip((clampedPage - 1) * clampedPageSize)
            .Take(clampedPageSize)
            .ToList();

        var totalMergedCount = indentRunsResult.TotalCount + grnTotal;

        return Results.Ok(new Application.Jobs.PagedResult<ProcessRunResponse>(
            merged,
            totalMergedCount,
            clampedPage,
            clampedPageSize));
    }

    /// <summary>
    /// A refusal the caller can act on. Tracking being off is a 503, not a 404: the endpoint
    /// exists, the installation simply has no database behind it, and the message says so.
    /// </summary>
    private static IResult Refused(ProcessJobRefusal refusal) =>
        Results.Problem(title: refusal.Title, detail: refusal.Detail, statusCode: refusal.StatusCode);
}
