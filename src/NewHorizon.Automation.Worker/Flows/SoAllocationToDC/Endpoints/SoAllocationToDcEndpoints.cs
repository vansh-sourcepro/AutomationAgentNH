using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Contracts;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.History;
using NewHorizon.Automation.Application.Abstractions;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Endpoints;

/// <summary>
/// Automation endpoints for Sales Allocation -> Delivery Challan conversion.
/// </summary>
public static class SoAllocationToDcEndpoints
{
    public static IEndpointRouteBuilder MapSoAllocationToDcEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // API-key / ERP token protected — same security model as /api/automation/indent-to-po.
        endpoints
            .MapPost(
                "/api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallansFromAllocations",
                CreateDeliveryChallansFromAllAllocationsAsync)
            .WithName("CreateDeliveryChallansFromAllocations")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapPost(
                "/api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallanFromAllocation",
                CreateDeliveryChallanAsync)
            .WithName("CreateDeliveryChallanFromAllocation")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/PendingAllocations",
                GetPendingAllocationsAsync)
            .WithName("GetPendingAllocationsForDc")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/AutomationConfig",
                GetAutomationConfigAsync)
            .WithName("GetSoAutomationConfig")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapPost(
                "/api/v1/sales/SalesAllocationDeAllocation/AutomationConfig",
                UpdateAutomationConfigAsync)
            .WithName("UpdateSoAutomationConfig")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapPut(
                "/api/v1/sales/SalesAllocationDeAllocation/AutomationConfig",
                UpdateAutomationConfigAsync)
            .WithName("PutSoAutomationConfig")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        // Dashboard & Metrics endpoints
        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/Dashboard",
                GetDashboardAsync)
            .WithName("GetSoAllocationToDcDashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/automation/so-allocation-to-dc/dashboard",
                GetDashboardAsync)
            .WithName("GetSoAllocationToDcDashboardAlt")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/summary",
                GetSummaryEndpointAsync)
            .WithName("GetSoAllocationToDcSummary")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/daily-summary",
                GetDailyStatsEndpointAsync)
            .WithName("GetSoAllocationToDcDailyStats")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        // History endpoints
        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/History",
                GetHistoryAsync)
            .WithName("GetSoAllocationToDcHistory")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/automation/so-allocation-to-dc/history",
                GetHistoryAsync)
            .WithName("GetSoAllocationToDcHistoryAlt")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/History/{jobId:guid}",
                GetExecutionDetailAsync)
            .WithName("GetSoAllocationToDcExecutionDetail")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        return endpoints;
    }

    private static async Task<IResult> GetAutomationConfigAsync(
        ISoAllocationToDcConfigRepository repo,
        CancellationToken cancellationToken)
    {
        var config = await repo.GetConfigAsync(cancellationToken);
        return Results.Ok(new SoAllocationToDcAutomationConfigDto
        {
            AutomationEnabled = config.AutomationEnabled,
            AutomationTime = config.AutomationTime,
            OafNumber = config.OafNumbers?.FirstOrDefault(),
            AllocationNumber = config.OafNumbers?.FirstOrDefault(),
            AutomationMode = config.AutomationMode,
            OafNumbers = config.OafNumbers,
            UpdatedAtUtc = config.UpdatedAtUtc,
            LastScheduledRunDate = config.LastScheduledRunDate?.ToString("yyyy-MM-dd"),
            LastScheduledRunStatus = config.LastScheduledRunStatus,
            LastCreatedDcCount = config.LastCreatedDcCount,
            LastScheduledRunMessage = config.LastScheduledRunMessage,
            LastScheduledRunUtc = config.LastScheduledRunUtc
        });
    }

    private static async Task<IResult> UpdateAutomationConfigAsync(
        SoAllocationToDcAutomationConfigDto dto,
        ISoAllocationToDcConfigRepository repo,
        IClock clock,
        CancellationToken cancellationToken)
    {
        string? normalizedTime = null;
        if (!string.IsNullOrWhiteSpace(dto.AutomationTime))
        {
            string[] formats = { "HH:mm", "H:mm", "HH:mm:ss", "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt" };
            if (!DateTime.TryParseExact(dto.AutomationTime.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedTime))
            {
                return Results.BadRequest("Invalid AutomationTime format. Please provide time in a standard 24-hour (HH:mm) or 12-hour (hh:mm PM) format.");
            }
            normalizedTime = parsedTime.ToString("HH:mm");
        }

        var oafList = dto.OafNumbers?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (oafList == null || oafList.Count == 0)
        {
            var fallback = dto.OafNumber ?? dto.AllocationNumber;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                oafList = fallback.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        if (oafList != null && oafList.Count == 0)
        {
            oafList = null;
        }

        var config = await repo.GetConfigAsync(cancellationToken);
        bool timeChanged = config.AutomationTime != normalizedTime;

        var mode = dto.AutomationMode?.ToUpperInvariant();
        if (!dto.AutomationEnabled)
        {
            mode = null;
            normalizedTime = null;
            oafList = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                if (oafList != null && oafList.Count > 0 && !string.IsNullOrWhiteSpace(normalizedTime))
                {
                    mode = "BOTH";
                }
                else if (oafList != null && oafList.Count > 0)
                {
                    mode = "INDIVIDUAL";
                }
                else
                {
                    mode = "TIMER";
                }
            }
        }

        var modeChanged = config.AutomationMode != mode;
        var enabledChanged = config.AutomationEnabled != dto.AutomationEnabled;

        config.AutomationEnabled = dto.AutomationEnabled;
        config.AutomationTime = normalizedTime;
        config.OafNumbers = oafList;
        config.AutomationMode = mode;
        config.UpdatedAtUtc = clock.UtcNow;

        if (timeChanged || modeChanged || enabledChanged)
        {
            if (!string.IsNullOrWhiteSpace(normalizedTime) && TimeOnly.TryParse(normalizedTime, out var slot))
            {
                // If the scheduled time for today has already passed, mark it as run for today so it doesn't trigger immediately
                if (clock.LocalTimeOfDay >= slot)
                {
                    config.LastScheduledRunDate = clock.LocalDate;
                }
                else
                {
                    config.LastScheduledRunDate = null;
                }
            }
            else
            {
                config.LastScheduledRunDate = null;
            }
        }

        await repo.SaveConfigAsync(config, cancellationToken);

        return Results.Ok(new SoAllocationToDcAutomationConfigDto
        {
            AutomationEnabled = config.AutomationEnabled,
            AutomationTime = config.AutomationTime,
            OafNumber = config.OafNumbers?.FirstOrDefault(),
            AllocationNumber = config.OafNumbers?.FirstOrDefault(),
            AutomationMode = config.AutomationMode,
            OafNumbers = config.OafNumbers,
            UpdatedAtUtc = config.UpdatedAtUtc,
            LastScheduledRunDate = config.LastScheduledRunDate?.ToString("yyyy-MM-dd"),
            LastScheduledRunStatus = config.LastScheduledRunStatus,
            LastCreatedDcCount = config.LastCreatedDcCount,
            LastScheduledRunMessage = config.LastScheduledRunMessage,
            LastScheduledRunUtc = config.LastScheduledRunUtc
        });
    }

    private static async Task<IResult> GetPendingAllocationsAsync(
        string? customerCode,
        ISoAllocationToDcService service,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("SoAllocationToDcEndpoints");
        try
        {
            var data = await service.GetPendingAllocationsAsync(customerCode, cancellationToken);

            return Results.Ok(new PendingAllocationsResponse(
                Success: true,
                Message: $"Found {data.Count} OAF allocation candidate(s).",
                Data: data,
                ErrorMessage: null));
        }
        catch (DcValidationException ex)
        {
            logger.LogWarning(ex, "GetPendingAllocations validation warning: {Message}", ex.Message);
            return Results.Ok(new PendingAllocationsResponse(
                Success: false,
                Message: null,
                Data: Array.Empty<PendingOafSummary>(),
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            logger.LogWarning(ex, "GetPendingAllocations transient ERP error: {Message}", ex.Message);
            return Results.Ok(new PendingAllocationsResponse(
                Success: false,
                Message: null,
                Data: Array.Empty<PendingOafSummary>(),
                ErrorMessage: ex.LaymanMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetPendingAllocations unexpected error: {Message}", ex.Message);
            return Results.Ok(new PendingAllocationsResponse(
                Success: false,
                Message: null,
                Data: Array.Empty<PendingOafSummary>(),
                ErrorMessage: ex.Message));
        }
    }

    private static async Task<IResult> CreateDeliveryChallanAsync(
        CreateDcFromAllocationRequest? request,
        ISoAllocationToDcService service,
        ISoAllocationToDcHistoryRepository historyRepo,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (request is null || !request.HasAnyIdentifier)
        {
            return Results.BadRequest(new CreateDcFromAllocationResponse(
                Success: false,
                Message: null,
                Data: null,
                Errors:
                [
                    new DcValidationErrorResponse(
                        "Identifier",
                        "At least one identifier ('oafId', 'oafNo', or 'intOrdNo') is required.")
                ],
                ErrorMessage: "Invalid request. Please provide 'oafNo' and/or 'intOrdNo' (or 'oafId')."));
        }

        var startedAt = clock.UtcNow;
        var runId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var trigger = ResolveTrigger(context);
        var triggeredBy = ResolveTriggeredBy(context);

        try
        {
            var result = await service.CreateDeliveryChallanFromAllocationAsync(
                request.OafId,
                request.ResolvedOafNo,
                request.ResolvedIntOrdNo,
                request.CustomerCode,
                cancellationToken);

            var completedAt = clock.UtcNow;
            var durationMs = (long)(completedAt - startedAt).TotalMilliseconds;

            var execRecord = new SoAllocationToDcExecutionRecord
            {
                JobId = jobId,
                RunId = runId,
                AllocationId = request.OafId ?? 0,
                OafId = result.OafId,
                OafNo = result.OafNo ?? request.ResolvedOafNo,
                IntOrdNo = result.IntOrdNo ?? request.ResolvedIntOrdNo,
                CustomerCode = request.CustomerCode,
                Status = "Success",
                DcCreated = true,
                DcNumber = result.DcNumber,
                DcId = result.DcId,
                DcStatus = "Open",
                LineCount = result.DcItemCount,
                AllocatedQty = result.TotalAllocatedQty,
                DcQty = result.TotalDcQty,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs
            };

            var runRecord = new SoAllocationToDcRunRecord
            {
                RunId = runId,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs,
                Status = "Success",
                TotalAllocations = 1,
                ProcessedAllocations = 1,
                SuccessfulAllocations = 1,
                CreatedDcCount = 1,
                TotalAllocatedQty = result.TotalAllocatedQty,
                TotalDcQty = result.TotalDcQty
            };

            await historyRepo.RecordRunAsync(runRecord, new List<SoAllocationToDcExecutionRecord> { execRecord }, cancellationToken);

            return Results.Ok(new CreateDcFromAllocationResponse(
                Success: true,
                Message: "Delivery Challan created successfully. Authorization is pending.",
                Data: new CreateDcFromAllocationData(
                    result.DcNumber,
                    result.DcId,
                    result.OafId,
                    result.OafNo,
                    result.IntOrdNo,
                    result.AllocatedItemCount,
                    result.DcItemCount,
                    result.TotalAllocatedQty,
                    result.TotalDcQty,
                    Status: "Open",
                    Authorized: false),
                Errors: null,
                ErrorMessage: null));
        }
        catch (DcValidationException ex)
        {
            var completedAt = clock.UtcNow;
            var durationMs = (long)(completedAt - startedAt).TotalMilliseconds;
            var isDuplicate = ex.Message.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase);
            var status = isDuplicate ? "Skipped" : "Failed";

            var execRecord = new SoAllocationToDcExecutionRecord
            {
                JobId = jobId,
                RunId = runId,
                AllocationId = request.OafId ?? 0,
                OafId = request.OafId ?? 0,
                OafNo = request.ResolvedOafNo,
                IntOrdNo = request.ResolvedIntOrdNo,
                CustomerCode = request.CustomerCode,
                Status = status,
                DcCreated = false,
                FailureReason = ex.Message,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs
            };

            var runRecord = new SoAllocationToDcRunRecord
            {
                RunId = runId,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs,
                Status = status,
                TotalAllocations = 1,
                ProcessedAllocations = 1,
                SuccessfulAllocations = 0,
                FailedAllocations = status == "Failed" ? 1 : 0,
                SkippedAllocations = status == "Skipped" ? 1 : 0,
                FailureReason = ex.Message
            };

            await historyRepo.RecordRunAsync(runRecord, new List<SoAllocationToDcExecutionRecord> { execRecord }, cancellationToken);

            return Results.UnprocessableEntity(new CreateDcFromAllocationResponse(
                Success: false,
                Message: null,
                Data: null,
                Errors: ex.Errors
                    .Select(e => new DcValidationErrorResponse(e.Field, e.Message))
                    .ToList(),
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            var completedAt = clock.UtcNow;
            var durationMs = (long)(completedAt - startedAt).TotalMilliseconds;

            var execRecord = new SoAllocationToDcExecutionRecord
            {
                JobId = jobId,
                RunId = runId,
                AllocationId = request.OafId ?? 0,
                OafId = request.OafId ?? 0,
                OafNo = request.ResolvedOafNo,
                IntOrdNo = request.ResolvedIntOrdNo,
                CustomerCode = request.CustomerCode,
                Status = "Failed",
                DcCreated = false,
                FailureReason = ex.LaymanMessage,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs
            };

            var runRecord = new SoAllocationToDcRunRecord
            {
                RunId = runId,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs,
                Status = "Failed",
                TotalAllocations = 1,
                ProcessedAllocations = 1,
                FailedAllocations = 1,
                FailureReason = ex.LaymanMessage
            };

            await historyRepo.RecordRunAsync(runRecord, new List<SoAllocationToDcExecutionRecord> { execRecord }, cancellationToken);

            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CreateDeliveryChallansFromAllAllocationsAsync(
        BatchDcCreationRequestDto? requestDto,
        ISoAllocationToDcService service,
        ISoAllocationToDcConfigRepository configRepo,
        ISoAllocationToDcHistoryRepository historyRepo,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
        var runId = Guid.NewGuid();
        var trigger = ResolveTrigger(context);
        var triggeredBy = ResolveTriggeredBy(context);

        try
        {
            var config = await configRepo.GetConfigAsync(cancellationToken);
            if (!config.AutomationEnabled)
            {
                return Results.Ok(new BatchDcCreationResponse(
                    Success: true,
                    Message: "Automation is disabled. No allocations were processed.",
                    Data: new BatchDcCreationData(
                        TotalAllocations: 0,
                        ProcessedAllocations: 0,
                        SuccessfulAllocations: 0,
                        PartialAllocations: 0,
                        FailedAllocations: 0,
                        TotalItems: 0,
                        SuccessfulItems: 0,
                        FailedItems: 0,
                        CreatedDCCount: 0,
                        Results: new List<AllocationDcItemResult>()),
                    ErrorMessage: null));
            }

            var request = requestDto?.ToRequest() ?? new BatchDcCreationRequest();

            if (request.OafNumbers == null || request.OafNumbers.Count == 0)
            {
                if (config.AutomationMode == "INDIVIDUAL" || config.AutomationMode == "TIMER" || config.AutomationMode == "BOTH")
                {
                    request = request with { OafNumbers = null };
                }
            }

            var batchResult = await service.CreateDeliveryChallansFromAllocationsAsync(
                request, cancellationToken);

            var completedAt = clock.UtcNow;
            var durationMs = (long)(completedAt - startedAt).TotalMilliseconds;

            var executionRecords = new List<SoAllocationToDcExecutionRecord>();

            if (batchResult.Data?.Results != null)
            {
                foreach (var item in batchResult.Data.Results)
                {
                    var isSuccess = item.Status == "SUCCESS" || (!string.IsNullOrEmpty(item.DcNumber) && item.Status == "PARTIAL_SUCCESS") || (item.Items != null && item.Items.Any(i => i.DcCreated));
                    var isDuplicateOrSkipped = !isSuccess && (
                        item.FailedField == "Duplicate" ||
                        item.FailedField == "NotFound" ||
                        (item.Items != null && item.Items.Any(i => i.FailedField == "Duplicate" || i.FailedField == "NotFound")) ||
                        (item.Message != null && item.Message.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase)) ||
                        (item.Message != null && item.Message.Contains("No pending dispatchable allocation", StringComparison.OrdinalIgnoreCase)));

                    string execStatus;
                    if (isSuccess)
                    {
                        execStatus = "Success";
                    }
                    else if (isDuplicateOrSkipped)
                    {
                        execStatus = "Skipped";
                    }
                    else
                    {
                        execStatus = "Failed";
                    }

                    var execRecord = new SoAllocationToDcExecutionRecord
                    {
                        JobId = Guid.NewGuid(),
                        RunId = runId,
                        AllocationId = item.AllocationId,
                        OafId = item.OafId,
                        OafNo = item.OafNo,
                        IntOrdNo = item.IntOrdNo,
                        CustomerCode = item.CustomerCode,
                        ShipmentCode = item.ShipmentCode,
                        LineCount = item.ItemCount,
                        AllocatedQty = item.AllocatedQuantity,
                        DcQty = item.DcQuantity,
                        Trigger = trigger,
                        TriggeredBy = triggeredBy,
                        StartedAtUtc = startedAt,
                        CompletedAtUtc = completedAt,
                        DurationMs = durationMs,
                        Status = execStatus,
                        DcCreated = isSuccess || !string.IsNullOrEmpty(item.DcNumber) || (item.Items != null && item.Items.Any(i => i.DcCreated)),
                        DcNumber = item.DcNumber ?? item.Items?.FirstOrDefault(i => !string.IsNullOrEmpty(i.DcNumber))?.DcNumber,
                        DcId = item.DcId ?? item.Items?.FirstOrDefault(i => i.DcId.HasValue)?.DcId,
                        DcStatus = item.DcStatus ?? (isSuccess ? "Open" : null),
                        FailureReason = isSuccess ? null : item.Message,
                        FailedField = isSuccess ? null : item.FailedField,
                        FailedApi = isSuccess ? null : item.FailedApi,
                        Items = item.Items?.Select(i => new DcItemResultRecord
                        {
                            ItemCode = i.ItemCode,
                            Status = i.Status,
                            DcCreated = i.DcCreated,
                            DcId = i.DcId,
                            DcNumber = i.DcNumber,
                            AllocatedQuantity = i.AllocatedQuantity,
                            DcQuantity = i.DcQuantity,
                            Message = i.Message,
                            FailedField = i.FailedField,
                            FailedApi = i.FailedApi
                        }).ToList() ?? new()
                    };

                    executionRecords.Add(execRecord);
                }
            }

            var totalAlloc = Math.Max(batchResult.Data?.TotalAllocations ?? 0, executionRecords.Count);
            var successAlloc = executionRecords.Count(e => e.Status == "Success" || e.DcCreated);
            var skippedAlloc = executionRecords.Count(e => e.Status == "Skipped");
            var failedAlloc = executionRecords.Count(e => e.Status == "Failed" && !e.DcCreated);
            var createdDcCount = executionRecords.Count(e => e.DcCreated);

            string runStatus;
            if (successAlloc > 0 && failedAlloc == 0)
            {
                runStatus = "Success";
            }
            else if (successAlloc > 0 && failedAlloc > 0)
            {
                runStatus = "Partial";
            }
            else if (skippedAlloc > 0 && failedAlloc == 0)
            {
                runStatus = "Skipped";
            }
            else if (failedAlloc > 0)
            {
                runStatus = "Failed";
            }
            else
            {
                runStatus = batchResult.Success ? "Success" : "Failed";
            }

            var runRecord = new SoAllocationToDcRunRecord
            {
                RunId = runId,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs,
                Status = runStatus,
                TotalAllocations = totalAlloc,
                ProcessedAllocations = executionRecords.Count,
                SuccessfulAllocations = successAlloc,
                SkippedAllocations = skippedAlloc,
                FailedAllocations = failedAlloc,
                CreatedDcCount = createdDcCount,
                TotalAllocatedQty = executionRecords.Sum(e => e.AllocatedQty),
                TotalDcQty = executionRecords.Sum(e => e.DcQty),
                FailureReason = runStatus == "Success" ? null : batchResult.Message
            };

            await historyRepo.RecordRunAsync(runRecord, executionRecords, cancellationToken);

            return Results.Ok(new BatchDcCreationResponse(
                Success: batchResult.Success,
                Message: batchResult.Message,
                Data: batchResult.Data,
                ErrorMessage: null));
        }
        catch (DcValidationException ex)
        {
            var completedAt = clock.UtcNow;
            var durationMs = (long)(completedAt - startedAt).TotalMilliseconds;
            var isDuplicate = ex.Message.Contains("already has a Delivery Challan", StringComparison.OrdinalIgnoreCase);
            var status = isDuplicate ? "Skipped" : "Failed";
            var failedRun = new SoAllocationToDcRunRecord
            {
                RunId = runId,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs,
                Status = status,
                FailedAllocations = status == "Failed" ? 1 : 0,
                SkippedAllocations = status == "Skipped" ? 1 : 0,
                FailureReason = ex.Message
            };
            await historyRepo.RecordRunAsync(failedRun, new List<SoAllocationToDcExecutionRecord>(), cancellationToken);

            return Results.UnprocessableEntity(new BatchDcCreationResponse(
                Success: false,
                Message: ex.Message,
                Data: null,
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            var completedAt = clock.UtcNow;
            var durationMs = (long)(completedAt - startedAt).TotalMilliseconds;
            var failedRun = new SoAllocationToDcRunRecord
            {
                RunId = runId,
                Trigger = trigger,
                TriggeredBy = triggeredBy,
                StartedAtUtc = startedAt,
                CompletedAtUtc = completedAt,
                DurationMs = durationMs,
                Status = "Failed",
                FailureReason = ex.LaymanMessage
            };
            await historyRepo.RecordRunAsync(failedRun, new List<SoAllocationToDcExecutionRecord>(), cancellationToken);

            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> GetDashboardAsync(
        int? days,
        int? recentCount,
        ISoAllocationToDcHistoryRepository historyRepo,
        CancellationToken cancellationToken)
    {
        var dashboard = await historyRepo.GetDashboardAsync(days ?? 30, recentCount ?? 10, cancellationToken);
        return Results.Ok(dashboard);
    }

    private static async Task<IResult> GetHistoryAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? status = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        ISoAllocationToDcHistoryRepository historyRepo = null!,
        CancellationToken cancellationToken = default)
    {
        var history = await historyRepo.GetHistoryAsync(page, pageSize, search, status, from, to, cancellationToken);
        return Results.Ok(history);
    }

    private static async Task<IResult> GetExecutionDetailAsync(
        Guid jobId,
        ISoAllocationToDcHistoryRepository historyRepo,
        CancellationToken cancellationToken)
    {
        var record = await historyRepo.GetExecutionByIdAsync(jobId, cancellationToken);
        return record != null ? Results.Ok(record) : Results.NotFound();
    }

    private static async Task<IResult> GetSummaryEndpointAsync(
        ISoAllocationToDcHistoryRepository historyRepo,
        CancellationToken cancellationToken)
    {
        var summary = await historyRepo.GetSummaryAsync(cancellationToken);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetDailyStatsEndpointAsync(
        int days = 30,
        ISoAllocationToDcHistoryRepository historyRepo = null!,
        CancellationToken cancellationToken = default)
    {
        var stats = await historyRepo.GetDailyStatsAsync(days, cancellationToken);
        return Results.Ok(stats);
    }

    private static string ResolveTrigger(HttpContext? context)
    {
        if (context == null) return "Manual";
        var triggerParam = context.Request.Query["trigger"].ToString();
        if (string.IsNullOrWhiteSpace(triggerParam))
        {
            return "Manual";
        }
        return char.ToUpperInvariant(triggerParam[0]) + triggerParam[1..].ToLowerInvariant();
    }

    private static string ResolveTriggeredBy(HttpContext? context)
    {
        return context?.User?.Identity?.Name ?? "system";
    }
}
