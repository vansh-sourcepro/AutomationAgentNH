using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;
using NewHorizon.Automation.Worker.Contracts;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Services;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Services;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Endpoints;

/// <summary>
/// Exposes the Quotation to Sales Order automation API: <c>POST /api/automation/quotation-to-so</c>
/// and its process history, dashboard metrics, and open quotation queries.
/// Governed by the master on/off switch and run mode configured in <see cref="SoAutomationEndpoints"/>.
/// </summary>
public static class QuotationToSoEndpoints
{
    public const string Route = "/api/automation/quotation-to-so";

    public static IEndpointRouteBuilder MapQuotationToSoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(Route)
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        // Primary conversion trigger
        group.MapPost(string.Empty, ConvertAsync)
            .WithName("ConvertQuotationToSalesOrder");

        // Preview open quotations for customer and site without converting
        group.MapGet("/open", GetOpenQuotationsAsync)
            .WithName("GetOpenAuthorisedQuotations");

        // Dashboard composite (summary, dailyStats, lastCompletedJob, lastExecution, recentJobs)
        group.MapGet("/dashboard", GetQuotationDashboardAsync)
            .WithName("GetQuotationToSoDashboard");

        // Top recent conversion jobs for the dashboard grid (exact JSON array format)
        group.MapGet("/dashboard/recent", GetRecentJobsAsync)
            .WithName("GetQuotationToSoDashboardRecent");

        // Summary KPI statistics
        group.MapGet("/dashboard/summary", GetQuotationSummaryAsync)
            .WithName("GetQuotationToSoDashboardSummary");

        // Daily charts over trailing days
        group.MapGet("/dashboard/daily-summary", GetQuotationDailyStatsAsync)
            .WithName("GetQuotationToSoDashboardDailySummary");

        // Grid of quotation to SO conversions (newest first, exact JSON shape)
        group.MapGet("/history", ListQuotationHistoryAsync)
            .WithName("ListQuotationToSoHistory");

        // History summary
        group.MapGet("/history/summary", GetQuotationSummaryAsync)
            .WithName("GetQuotationToSoHistorySummary");

        // Execution detail with steps and checks
        group.MapGet("/history/{jobId:guid}", GetQuotationJobDetailAsync)
            .WithName("GetQuotationToSoHistoryDetail");

        // History by quotation document number
        group.MapGet("/history/document/{**documentNumber}", GetQuotationDocumentHistoryAsync)
            .WithName("GetQuotationToSoDocumentHistory");

        // Top-level summary & daily-summary shortcuts
        group.MapGet("/summary", GetQuotationSummaryAsync)
            .WithName("GetQuotationToSoSummary");

        group.MapGet("/daily-summary", GetQuotationDailyStatsAsync)
            .WithName("GetQuotationToSoDailyStats");

        // Runs list
        group.MapGet("/runs", ListQuotationRunsAsync)
            .WithName("ListQuotationToSoRuns");

        group.MapGet("/runs/{runId:guid}", GetQuotationRunAsync)
            .WithName("GetQuotationToSoRun");

        group.MapGet("/{jobId:guid}", GetQuotationJobDetailAsync)
            .WithName("GetQuotationToSoJob");

        // ---- Alias routes for /api/automation/so-automation/dashboard & history ----
        var soDashboardGroup = endpoints.MapGroup("/api/automation/so-automation/dashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        soDashboardGroup.MapGet(string.Empty, GetQuotationDashboardAsync).WithName("GetSoAutomationDashboard");
        soDashboardGroup.MapGet("/recent", GetRecentJobsAsync).WithName("GetSoAutomationDashboardRecent");
        soDashboardGroup.MapGet("/summary", GetQuotationSummaryAsync).WithName("GetSoAutomationDashboardSummary");
        soDashboardGroup.MapGet("/daily-summary", GetQuotationDailyStatsAsync).WithName("GetSoAutomationDashboardDailySummary");

        var soHistoryGroup = endpoints.MapGroup("/api/automation/so-automation/history")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        soHistoryGroup.MapGet(string.Empty, ListQuotationHistoryAsync).WithName("ListSoAutomationHistory");
        soHistoryGroup.MapGet("/summary", GetQuotationSummaryAsync).WithName("GetSoAutomationHistorySummary");
        soHistoryGroup.MapGet("/{jobId:guid}", GetQuotationJobDetailAsync).WithName("GetSoAutomationHistoryDetail");
        soHistoryGroup.MapGet("/document/{**documentNumber}", GetQuotationDocumentHistoryAsync).WithName("GetSoAutomationDocumentHistory");

        return endpoints;
    }

    private static async Task<IResult> ConvertAsync(
        ConvertQuotationsRequest? request,
        IQuotationToSoService service,
        IQuotationSoAutomationConfigRepository configRepo,
        IQuotationToSoTracker tracker,
        IClock clock,
        ILogger<QuotationToSoService> logger,
        ClaimsPrincipal user,
        CancellationToken cancellationToken,
        string? trigger = null)
    {
        request ??= new ConvertQuotationsRequest();

        // 1. Determine trigger source
        TriggerSource triggerSource;
        if (string.IsNullOrWhiteSpace(trigger))
        {
            triggerSource = TriggerSource.Api;
        }
        else if (!Enum.TryParse(trigger.Trim(), ignoreCase: true, out triggerSource)
            || triggerSource is not (TriggerSource.Manual or TriggerSource.Timer or TriggerSource.Api))
        {
            return Results.Problem(
                title: "Unknown trigger.",
                detail: $"'{trigger}' is not a conversion trigger. Expected 'manual', 'timer' or 'api'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // 2. Read automation configuration
        var config = await configRepo.GetAsync(cancellationToken);

        // 3. Master toggle enforcement: if inactive, refuse all conversion attempts
        if (!config.IsActive)
        {
            logger.LogWarning("Quotation to Sales Order conversion rejected: automation master toggle is OFF.");
            return Results.Problem(
                title: "Quotation to Sales Order automation is turned off.",
                detail: "The master toggle is OFF. No quotations will be converted until automation is switched on.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // 4. Run mode permission check (Manual/Api only, Timer only, Both)
        if (!config.RunMode.Allows(triggerSource))
        {
            logger.LogWarning(
                "Quotation to Sales Order conversion rejected: run mode '{RunMode}' does not permit '{TriggerSource}' trigger.",
                config.RunMode,
                triggerSource);

            return Results.Problem(
                title: "Quotation to Sales Order automation trigger not permitted.",
                detail: $"The current automation run mode '{config.RunMode}' does not allow '{triggerSource}' runs.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // 5. Claim run start
        config.RecordRunStarted(clock.UtcNow, triggerSource == TriggerSource.Timer ? clock.LocalDate : null);
        try
        {
            await configRepo.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist run started state to config repository.");
        }

        var customerCode = !string.IsNullOrWhiteSpace(request.CustomerCode)
            ? request.CustomerCode.Trim()
            : config.CustomerCode;

        var siteCode = !string.IsNullOrWhiteSpace(request.SiteCode)
            ? request.SiteCode.Trim()
            : config.SiteCode;

        // Determine target quotation numbers (supports "1, 2, 3" in request.QuotationNumbers, request.QuotationNumber, or config.QuotationNumbers)
        var quotationNumbersString = !string.IsNullOrWhiteSpace(request.QuotationNumbers)
            ? request.QuotationNumbers
            : (!string.IsNullOrWhiteSpace(request.QuotationNumber) ? request.QuotationNumber : config.QuotationNumbers);

        var quotationList = quotationNumbersString?
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var triggeredBy = TriggeredByFor(triggerSource, user);

        await tracker.StartRunAsync(
            triggerSource,
            triggeredBy,
            triggerReference: null,
            quotationNumbersString,
            cancellationToken);

        try
        {
            // Case A: Single specific quotation number
            if (quotationList is { Count: 1 })
            {
                var qtnNo = quotationList[0];
                logger.LogInformation(
                    "Executing Sales Order creation for single Quotation {QuotationNumber} (Trigger: {Trigger})",
                    qtnNo,
                    triggerSource);

                var itemResult = await service.ConvertQuotationByNumberAsync(
                    qtnNo,
                    customerCode,
                    siteCode,
                    cancellationToken);

                var status = itemResult.Success ? "Success" : "Failed";
                var message = itemResult.Success
                    ? $"Quotation '{itemResult.QuotationNumber}' successfully converted to Sales Order '{itemResult.SalesOrderNumber}'."
                    : itemResult.ErrorMessage ?? $"Failed to convert quotation '{qtnNo}'.";

                config.RecordRunCompleted(status, message, clock.UtcNow);
                await SaveConfigSafeAsync(configRepo, config, logger, cancellationToken);

                await tracker.CompleteRunAsync(quotationsExamined: 1, cancellationToken);

                return Results.Ok(new ConvertQuotationsResponse
                {
                    Success = itemResult.Success,
                    Message = message,
                    SalesOrderNumber = itemResult.SalesOrderNumber,
                    Item = itemResult,
                });
            }

            // Case B: Multiple specific quotation numbers (e.g. from config or batch request)
            if (quotationList is { Count: > 1 })
            {
                logger.LogInformation(
                    "Executing Sales Order creation for {Count} configured quotations: {Quotations} (Trigger: {Trigger})",
                    quotationList.Count,
                    string.Join(", ", quotationList),
                    triggerSource);

                var items = new List<QuotationConversionItemResult>();
                var succeeded = 0;
                var failed = 0;

                foreach (var qtn in quotationList)
                {
                    var item = await service.ConvertQuotationByNumberAsync(
                        qtn,
                        customerCode,
                        siteCode,
                        cancellationToken);

                    items.Add(item);
                    if (item.Success)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                var batchResult = new QuotationToSoSweepResult
                {
                    CustomerCode = customerCode ?? "MULTIPLE",
                    SiteCode = siteCode ?? "CONFIGURED",
                    TotalFound = quotationList.Count,
                    Succeeded = succeeded,
                    Failed = failed,
                    Details = items,
                };

                var status = failed == 0 ? "Success" : succeeded > 0 ? "PartialSuccess" : "Failed";
                var batchMessage = $"Processed {batchResult.TotalFound} quotation(s): {batchResult.Succeeded} converted successfully, {batchResult.Failed} failed.";

                config.RecordRunCompleted(status, batchMessage, clock.UtcNow);
                await SaveConfigSafeAsync(configRepo, config, logger, cancellationToken);

                await tracker.CompleteRunAsync(quotationsExamined: quotationList.Count, cancellationToken);

                return Results.Ok(new ConvertQuotationsResponse
                {
                    Success = failed == 0,
                    Message = batchMessage,
                    Result = batchResult,
                });
            }

            // Case C: No quotation number specified -> Convert ALL eligible open authorised quotations
            logger.LogInformation(
                "Executing Quotation to Sales Order conversion for ALL eligible quotations (Customer: {CustomerCode}, Site: {SiteCode}, Trigger: {Trigger})",
                customerCode ?? "ALL",
                siteCode ?? "ALL",
                triggerSource);

            var sweepResult = await service.ConvertAllEligibleQuotationsAsync(
                customerCode,
                siteCode,
                cancellationToken);

            if (sweepResult.TotalFound == 0)
            {
                var scopeDesc = !string.IsNullOrWhiteSpace(customerCode)
                    ? $"Customer '{customerCode}'"
                    : "the system";

                var emptyMsg = $"No eligible open authorised quotations found for conversion in {scopeDesc}.";

                config.RecordRunCompleted("Success", emptyMsg, clock.UtcNow);
                await SaveConfigSafeAsync(configRepo, config, logger, cancellationToken);

                await tracker.CompleteRunAsync(quotationsExamined: 0, cancellationToken);

                return Results.Ok(new ConvertQuotationsResponse
                {
                    Success = true,
                    Message = emptyMsg,
                    Result = sweepResult,
                });
            }

            var sweepStatus = sweepResult.Failed == 0 ? "Success" : sweepResult.Succeeded > 0 ? "PartialSuccess" : "Failed";
            var sweepMsg = $"Processed {sweepResult.TotalFound} quotation(s): {sweepResult.Succeeded} converted successfully, {sweepResult.Failed} failed.";

            config.RecordRunCompleted(sweepStatus, sweepMsg, clock.UtcNow);
            await SaveConfigSafeAsync(configRepo, config, logger, cancellationToken);

            await tracker.CompleteRunAsync(quotationsExamined: sweepResult.TotalFound, cancellationToken);

            return Results.Ok(new ConvertQuotationsResponse
            {
                Success = sweepResult.Failed == 0,
                Message = sweepMsg,
                Result = sweepResult,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Error executing Quotation to Sales Order conversion for request {@Request}",
                request);

            config.RecordRunCompleted("Failed", ex.Message, clock.UtcNow);
            await SaveConfigSafeAsync(configRepo, config, logger, cancellationToken);

            await tracker.FailRunAsync(ex.Message, quotationsExamined: 0, cancellationToken);

            return Results.Problem(
                title: "Quotation conversion failed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetOpenQuotationsAsync(
        string customerCode,
        string siteCode,
        IQuotationToSoService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerCode) || string.IsNullOrWhiteSpace(siteCode))
        {
            return Results.Problem(
                title: "Missing parameters.",
                detail: "Query parameters 'customerCode' and 'siteCode' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var list = await service.GetOpenAuthorisedQuotationsAsync(customerCode, siteCode, cancellationToken);
        return Results.Ok(new { success = true, count = list.Count, data = list });
    }

    private static async Task<IResult> GetQuotationDashboardAsync(
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken,
        int days = 30,
        int recentCount = 10,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var result = await dashboardService.GetDashboardAsync(from, to, days, recentCount, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetRecentJobsAsync(
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken,
        int count = 10)
    {
        var result = await dashboardService.GetRecentJobsAsync(count, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ListQuotationHistoryAsync(
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? trigger = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 200) pageSize = 50;

        var result = await dashboardService.ListHistoryAsync(
            page,
            pageSize,
            search,
            status,
            trigger,
            from,
            to,
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetQuotationSummaryAsync(
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        var summary = await dashboardService.GetSummaryAsync(from, to, cancellationToken);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetQuotationDailyStatsAsync(
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var stats = await dashboardService.GetDailyStatsAsync(days, cancellationToken);
        return Results.Ok(stats);
    }

    private static async Task<IResult> GetQuotationJobDetailAsync(
        Guid jobId,
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        var detail = await dashboardService.GetJobDetailAsync(jobId, cancellationToken);
        return detail is null
            ? Results.NotFound(new { Message = $"No execution history found for job ID {jobId}." })
            : Results.Ok(detail);
    }

    private static async Task<IResult> GetQuotationDocumentHistoryAsync(
        string documentNumber,
        IQuotationToSoDashboardService dashboardService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(documentNumber))
        {
            return Results.Problem(title: "documentNumber is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var doc = System.Net.WebUtility.UrlDecode(documentNumber.Trim());
        var rows = await dashboardService.GetDocumentHistoryAsync(doc, cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListQuotationRunsAsync(
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
                WorkflowType = WorkflowNames.QuotationToSalesOrder,
                TriggerSource = triggerSource,
                Status = runStatus,
                FromUtc = from,
                ToUtc = to,
                Page = page,
                PageSize = pageSize,
            },
            cancellationToken);

        return Results.Ok(new PagedResult<ProcessRunResponse>(
            [.. result.Items.Select(ProcessJobMapper.ToResponse)],
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> GetQuotationRunAsync(
        Guid runId,
        IProcessJobService processJobs,
        CancellationToken cancellationToken)
    {
        var detail = await processJobs.GetRunAsync(runId, cancellationToken);
        return detail is null
            ? Results.NotFound()
            : Results.Ok(ProcessJobMapper.ToResponse(detail));
    }

    private static async Task<IResult> GetQuotationJobAsync(
        Guid jobId,
        IProcessJobService processJobs,
        CancellationToken cancellationToken)
    {
        var detail = await processJobs.GetExecutionAsync(jobId, cancellationToken);
        return detail is null
            ? Results.NotFound()
            : Results.Ok(ProcessJobMapper.ToResponse(detail));
    }

    private static (ProcessJobQuery? Query, IResult? Error) BuildQuotationQuery(
        string? search,
        string? status,
        string? company,
        string? trigger,
        string? stage,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize)
    {
        if (!TryParse<JobStatus>(status, "job status", out var jobStatus, out var statusError))
        {
            return (null, statusError);
        }

        if (!TryParse<TriggerSource>(trigger, "trigger", out var triggerSource, out var triggerError))
        {
            return (null, triggerError);
        }

        if (!string.IsNullOrWhiteSpace(stage)
            && !QuotationToSoStages.InOrder.Contains(stage.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return (null, Results.Problem(
                title: "Unknown stage.",
                detail: $"'{stage}' is not a stage for Quotation to Sales Order. Expected one of: "
                    + $"{string.Join(", ", QuotationToSoStages.InOrder)}.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        return (new ProcessJobQuery
        {
            Search = search,
            WorkflowType = WorkflowNames.QuotationToSalesOrder,
            Company = company,
            Status = jobStatus,
            TriggerSource = triggerSource,
            Stage = string.IsNullOrWhiteSpace(stage)
                ? null
                : QuotationToSoStages.InOrder.First(known =>
                    known.Equals(stage.Trim(), StringComparison.OrdinalIgnoreCase)),
            IndentKind = IndentKind.Regular,
            FromUtc = from,
            ToUtc = to,
            Page = page,
            PageSize = pageSize,
        }, null);
    }

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

    private static string? TriggeredByFor(TriggerSource trigger, ClaimsPrincipal? user) => trigger switch
    {
        TriggerSource.Manual => user?.FindFirstValue("userFullName")
            ?? user?.FindFirstValue("userName")
            ?? user?.FindFirstValue("id")
            ?? user?.Identity?.Name
            ?? "SUPER USER",
        TriggerSource.Timer => "Scheduler",
        TriggerSource.Api => user?.FindFirstValue("userFullName")
            ?? user?.FindFirstValue("userName")
            ?? user?.Identity?.Name
            ?? "API",
        _ => null,
    };

    private static async Task SaveConfigSafeAsync(
        IQuotationSoAutomationConfigRepository configRepo,
        QuotationSoAutomationConfig config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await configRepo.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist configuration state after run completed.");
        }
    }
}
