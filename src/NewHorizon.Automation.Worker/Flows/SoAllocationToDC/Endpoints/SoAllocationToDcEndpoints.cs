using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Contracts;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration;
using NewHorizon.Automation.Application.Abstractions;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Endpoints;

/// <summary>
/// The one new automation endpoint: given a Sales Allocation (identified by OAF ID),
/// create the corresponding Delivery Challan in the ERP.
/// </summary>
/// <remarks>
/// Request-driven (like the Indent->PO endpoints): no database required, the caller learns
/// the DC number directly, and the ERP's own bookkeeping prevents duplicate DCs.
/// </remarks>
public static class SoAllocationToDcEndpoints
{
    public static IEndpointRouteBuilder MapSoAllocationToDcEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // API-key protected — same security model as /api/automation/indent-to-po.
        // Master automation API: processes ALL existing Sales Order Allocations sequentially
        endpoints
            .MapPost(
                "/api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallansFromAllocations",
                CreateDeliveryChallansFromAllAllocationsAsync)
            .WithName("CreateDeliveryChallansFromAllocations")
            .AddEndpointFilter<ApiKeyFilter>();

        endpoints
            .MapPost(
                "/api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallanFromAllocation",
                CreateDeliveryChallanAsync)
            .WithName("CreateDeliveryChallanFromAllocation")
            .AddEndpointFilter<ApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/PendingAllocations",
                GetPendingAllocationsAsync)
            .WithName("GetPendingAllocationsForDc")
            .AddEndpointFilter<ApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/SalesAllocationDeAllocation/AutomationConfig",
                GetAutomationConfigAsync)
            .WithName("GetSoAutomationConfig")
            .AddEndpointFilter<ApiKeyFilter>();

        endpoints
            .MapPost(
                "/api/v1/sales/SalesAllocationDeAllocation/AutomationConfig",
                UpdateAutomationConfigAsync)
            .WithName("UpdateSoAutomationConfig")
            .AddEndpointFilter<ApiKeyFilter>();

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
            OafNumbers = config.OafNumbers
        });
    }

    private static async Task<IResult> UpdateAutomationConfigAsync(
        SoAllocationToDcAutomationConfigDto dto,
        ISoAllocationToDcConfigRepository repo,
        IClock clock,
        ISoAllocationToDcService service,
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

        // Backwards compatibility for setting single OAF
        var oafList = dto.OafNumbers;
        if (oafList == null || oafList.Count == 0)
        {
            var fallback = dto.OafNumber ?? dto.AllocationNumber;
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                oafList = new List<string> { fallback.Trim() };
            }
        }
        var primaryOaf = oafList?.FirstOrDefault();

        var config = await repo.GetConfigAsync(cancellationToken);
        
        bool timeChanged = config.AutomationTime != normalizedTime;

        var mode = dto.AutomationMode?.ToUpperInvariant();
        if (!dto.AutomationEnabled)
        {
            // When Toggle is OFF, safety shutoff resets all parameters to null
            mode = null;
            normalizedTime = null;
            oafList = null;
        }
        else
        {
            if (mode != "INDIVIDUAL" && mode != "TIMER" && mode != "BOTH")
            {
                // If mode not explicitly provided, preserve the existing saved mode.
                // Only fall back to auto-detection if there is no existing mode in config.
                var existingMode = config.AutomationMode?.ToUpperInvariant();
                if (existingMode == "INDIVIDUAL" || existingMode == "TIMER" || existingMode == "BOTH")
                {
                    mode = existingMode; // Keep what was already set - do NOT silently switch modes
                }
                else
                {
                    // Brand-new config: infer mode from presence of time
                    mode = string.IsNullOrWhiteSpace(dto.AutomationTime) ? "INDIVIDUAL" : "TIMER";
                }
            }

            // Enforce strict mode-based restrictions:
            if (mode == "TIMER")
            {
                // In TIMER mode, user can ONLY configure time. OAF info cannot be set.
                oafList = null;
            }
            else if (mode == "INDIVIDUAL")
            {
                // In INDIVIDUAL mode, user can ONLY configure OAFs/allocations. Time cannot be set.
                normalizedTime = null;
            }
            // In BOTH mode, user can configure both time and OAF info.
        }

        config.AutomationEnabled = dto.AutomationEnabled;
        config.AutomationTime = normalizedTime;
        config.OafNumbers = oafList;
        config.AutomationMode = mode;
        
        config.UpdatedAtUtc = clock.UtcNow;
        
        if (timeChanged)
        {
            // Reset run tracker so it can run again if time is changed
            config.LastScheduledRunDate = null;
        }

        await repo.SaveConfigAsync(config, cancellationToken);
        
        if (!config.AutomationEnabled)
        {
            return Results.Ok(new BatchDcCreationResponse(
                Success: true,
                Message: "Automation is disabled. Configuration saved successfully. No allocations were processed.",
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

        if (config.AutomationMode == "TIMER")
        {
            try
            {
                var dryRunRequest = new BatchDcCreationRequest
                {
                    DryRun = true
                };
                var previewResult = await service.CreateDeliveryChallansFromAllocationsAsync(dryRunRequest, cancellationToken);
                
                var scheduledResults = previewResult.Data.Results.Select(r =>
                {
                    if (r.Status == "SUCCESS")
                    {
                        var updatedItems = r.Items.Select(item => item with
                        {
                            Status = "SCHEDULED",
                            Message = $"Allocation verified and scheduled for automatic Delivery Challan creation daily at {config.AutomationTime}."
                        }).ToList();
                        return r with { Status = "SCHEDULED", Items = updatedItems };
                    }
                    return r;
                }).ToList();

                var scheduledCount = scheduledResults.Count(r => r.Status == "SCHEDULED");
                var alreadyDcCount = scheduledResults.Count(r => r.Items.Any(i => i.FailedField == "Duplicate"));
                var issuesCount = scheduledResults.Count(r => r.Status != "SCHEDULED" && !r.Items.Any(i => i.FailedField == "Duplicate"));

                string timerMessage = scheduledResults.Count > 0
                    ? $"Delivery Challan automation configuration saved in TIMER mode (scheduled daily at {config.AutomationTime}). Found {scheduledResults.Count} allocation candidate(s) in ERP ({scheduledCount} scheduled, {alreadyDcCount} already have DC, {issuesCount} with issues)."
                    : $"Delivery Challan automation configuration saved in TIMER mode (scheduled daily at {config.AutomationTime}). Currently 0 pending allocation candidates found in ERP.";

                return Results.Ok(new BatchDcCreationResponse(
                    Success: true,
                    Message: timerMessage,
                    Data: new BatchDcCreationData(
                        TotalAllocations: previewResult.Data.TotalAllocations,
                        ProcessedAllocations: scheduledResults.Count,
                        SuccessfulAllocations: scheduledCount,
                        PartialAllocations: previewResult.Data.PartialAllocations,
                        FailedAllocations: previewResult.Data.FailedAllocations,
                        TotalItems: previewResult.Data.TotalItems,
                        SuccessfulItems: scheduledResults.Where(r => r.Status == "SCHEDULED").Sum(r => r.Items.Count),
                        FailedItems: scheduledResults.Where(r => r.Status != "SCHEDULED").Sum(r => r.Items.Count),
                        CreatedDCCount: 0,
                        Results: scheduledResults),
                    ErrorMessage: null));
            }
            catch (Exception)
            {
                // Fallback if preview scan cannot reach ERP
                return Results.Ok(new BatchDcCreationResponse(
                    Success: true,
                    Message: $"Delivery Challan automation configuration saved successfully in TIMER mode (scheduled daily at {config.AutomationTime}).",
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
        }

        if (config.AutomationMode == "BOTH" && !string.IsNullOrWhiteSpace(config.AutomationTime))
        {
            try
            {
                var dryRunRequest = new BatchDcCreationRequest
                {
                    DryRun = true,
                    OafNumbers = config.OafNumbers
                };
                var previewResult = await service.CreateDeliveryChallansFromAllocationsAsync(dryRunRequest, cancellationToken);
                
                var scheduledResults = previewResult.Data.Results.Select(r =>
                {
                    if (r.Status == "SUCCESS")
                    {
                        var updatedItems = r.Items.Select(item => item with
                        {
                            Status = "SCHEDULED",
                            Message = $"Allocation verified and scheduled for automatic Delivery Challan creation daily at {config.AutomationTime}."
                        }).ToList();
                        return r with { Status = "SCHEDULED", Items = updatedItems };
                    }
                    return r;
                }).ToList();

                var scheduledCount = scheduledResults.Count(r => r.Status == "SCHEDULED");
                var alreadyDcCount = scheduledResults.Count(r => r.Items.Any(i => i.FailedField == "Duplicate"));
                var issuesCount = scheduledResults.Count(r => r.Status != "SCHEDULED" && !r.Items.Any(i => i.FailedField == "Duplicate"));

                string bothMessage = $"Delivery Challan automation configuration saved in BOTH mode (scheduled daily at {config.AutomationTime} for {config.OafNumbers?.Count ?? 0} configured OAF(s)). Found {scheduledResults.Count} allocation(s) ({scheduledCount} scheduled, {alreadyDcCount} already have DC, {issuesCount} with issues).";

                return Results.Ok(new BatchDcCreationResponse(
                    Success: true,
                    Message: bothMessage,
                    Data: new BatchDcCreationData(
                        TotalAllocations: previewResult.Data.TotalAllocations,
                        ProcessedAllocations: scheduledResults.Count,
                        SuccessfulAllocations: scheduledCount,
                        PartialAllocations: previewResult.Data.PartialAllocations,
                        FailedAllocations: previewResult.Data.FailedAllocations,
                        TotalItems: previewResult.Data.TotalItems,
                        SuccessfulItems: scheduledResults.Where(r => r.Status == "SCHEDULED").Sum(r => r.Items.Count),
                        FailedItems: scheduledResults.Where(r => r.Status != "SCHEDULED").Sum(r => r.Items.Count),
                        CreatedDCCount: 0,
                        Results: scheduledResults),
                    ErrorMessage: null));
            }
            catch (Exception)
            {
                // Fallback if preview scan cannot reach ERP
                return Results.Ok(new BatchDcCreationResponse(
                    Success: true,
                    Message: $"Delivery Challan automation configuration saved successfully in BOTH mode (scheduled daily at {config.AutomationTime} for {config.OafNumbers?.Count ?? 0} configured OAF(s)).",
                    Data: new BatchDcCreationData(
                        TotalAllocations: config.OafNumbers?.Count ?? 0,
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
        }

        // For INDIVIDUAL mode (or BOTH mode without scheduled time):
        // Execute conversion immediately and return detailed itemized DC results matching the Main API structure.
        try
        {
            var batchRequest = new BatchDcCreationRequest
            {
                OafNumbers = config.OafNumbers
            };

            var batchResult = await service.CreateDeliveryChallansFromAllocationsAsync(batchRequest, cancellationToken);

            return Results.Ok(new BatchDcCreationResponse(
                Success: batchResult.Success,
                Message: batchResult.Message,
                Data: batchResult.Data,
                ErrorMessage: null));
        }
        catch (DcValidationException ex)
        {
            return Results.UnprocessableEntity(new BatchDcCreationResponse(
                Success: false,
                Message: ex.Message,
                Data: null,
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Scans for OAFs under the specified customer (or default customer) and inspects
    /// whether they are ready to be converted into a Delivery Challan.
    /// </summary>
    private static async Task<IResult> GetPendingAllocationsAsync(
        string? customerCode,
        ISoAllocationToDcService service,
        CancellationToken cancellationToken)
    {
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
            return Results.UnprocessableEntity(new PendingAllocationsResponse(
                Success: false,
                Message: null,
                Data: null,
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Accepts an OAF ID, OAF number, or internal order number, validates the existing allocation,
    /// and calls the ERP's own <c>createDCEntry</c> endpoint. Returns the generated DC number and DC id on success.
    /// </summary>
    private static async Task<IResult> CreateDeliveryChallanAsync(
        CreateDcFromAllocationRequest? request,
        ISoAllocationToDcService service,
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

        try
        {
            var result = await service.CreateDeliveryChallanFromAllocationAsync(
                request.OafId,
                request.ResolvedOafNo,
                request.ResolvedIntOrdNo,
                request.CustomerCode,
                cancellationToken);

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
            // A known, deterministic business-rule failure. 422 Unprocessable Entity is
            // semantically correct: the request was understood, but the ERP cannot fulfill it.
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
            // The ERP could not be reached or answered unexpectedly. 503 signals the caller
            // to retry after the ERP recovers.
            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Master automation endpoint: finds ALL existing Sales Order Allocations and processes them
    /// sequentially using the exact ERP sequence, creating Delivery Challans for each eligible record.
    /// </summary>
    private static async Task<IResult> CreateDeliveryChallansFromAllAllocationsAsync(
        BatchDcCreationRequestDto? requestDto,
        ISoAllocationToDcService service,
        ISoAllocationToDcConfigRepository configRepo,
        CancellationToken cancellationToken)
    {
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
            
            // Apply NULL selection parameter logic based on mode
            if (request.OafNumbers == null || request.OafNumbers.Count == 0)
            {
                if (config.AutomationMode == "INDIVIDUAL" || config.AutomationMode == "TIMER" || config.AutomationMode == "BOTH")
                {
                    // TL Request: "If all individual-selection parameters are null or empty while automation is enabled, process all eligible allocations."
                    // Since it's null, we just let it pass as null, and the underlying service will process ALL eligible allocations dynamically.
                    request = request with { OafNumbers = null };
                }
            }

            var batchResult = await service.CreateDeliveryChallansFromAllocationsAsync(
                request, cancellationToken);

            return Results.Ok(new BatchDcCreationResponse(
                Success: batchResult.Success,
                Message: batchResult.Message,
                Data: batchResult.Data,
                ErrorMessage: null));
        }
        catch (DcValidationException ex)
        {
            return Results.UnprocessableEntity(new BatchDcCreationResponse(
                Success: false,
                Message: ex.Message,
                Data: null,
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

