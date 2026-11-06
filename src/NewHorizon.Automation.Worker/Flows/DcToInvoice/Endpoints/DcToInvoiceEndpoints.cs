using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.DcToInvoice;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Configuration;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Contracts;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice.Endpoints;

/// <summary>
/// Automation endpoints for Delivery Challan to Sales Invoice conversion.
/// Follows the established Allocation-to-DC architecture and conventions.
/// </summary>
public static class DcToInvoiceEndpoints
{
    public static IEndpointRouteBuilder MapDcToInvoiceEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Master automation API: processes ALL, MULTIPLE, or SINGLE Delivery Challans
        endpoints
            .MapPost(
                "/api/v1/sales/salesinvoice/CreateInvoiceFromDeliveryChallan",
                CreateInvoicesFromAllDeliveryChallansAsync)
            .WithName("CreateInvoiceFromDeliveryChallan")
            .AddEndpointFilter<ApiKeyFilter>();

        endpoints
            .MapPost(
                "/api/v1/sales/salesinvoice/CreateInvoicesFromDeliveryChallans",
                CreateInvoicesFromAllDeliveryChallansAsync)
            .WithName("CreateInvoicesFromDeliveryChallans")
            .AddEndpointFilter<ApiKeyFilter>();

        // Query pending / eligible Delivery Challans
        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/PendingDeliveryChallans",
                GetPendingDeliveryChallansAsync)
            .WithName("GetPendingDeliveryChallansForInvoice")
            .AddEndpointFilter<ApiKeyFilter>();

        // Configuration endpoints
        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/AutomationConfig",
                GetAutomationConfigAsync)
            .WithName("GetInvoiceAutomationConfig")
            .AddEndpointFilter<ApiKeyFilter>();

        endpoints
            .MapPost(
                "/api/v1/sales/salesinvoice/AutomationConfig",
                UpdateAutomationConfigAsync)
            .WithName("UpdateInvoiceAutomationConfig")
            .AddEndpointFilter<ApiKeyFilter>();

        // ── History & Dashboard Endpoints ──
        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/History",
                GetHistoryAsync)
            .WithName("GetInvoiceConversionHistory")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/History/dc/{dcNo}",
                GetDcHistoryAsync)
            .WithName("GetDeliveryChallanConversionHistory")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/Dashboard",
                GetDashboardAsync)
            .WithName("GetInvoiceAutomationDashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/Summary",
                GetDashboardAsync)
            .WithName("GetInvoiceAutomationSummary")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/DailySummary",
                GetDailySummaryAsync)
            .WithName("GetInvoiceAutomationDailySummary")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/v1/sales/salesinvoice/DailyStats",
                GetDailySummaryAsync)
            .WithName("GetInvoiceAutomationDailyStats")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        // Browser / UI friendly aliases
        endpoints
            .MapGet(
                "/api/automation/dc-to-invoice/history",
                GetHistoryAsync)
            .WithName("GetAutomationDcToInvoiceHistory")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/automation/dc-to-invoice/dashboard",
                GetDashboardAsync)
            .WithName("GetAutomationDcToInvoiceDashboard")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        endpoints
            .MapGet(
                "/api/automation/dc-to-invoice/daily-summary",
                GetDailySummaryAsync)
            .WithName("GetAutomationDcToInvoiceDailySummary")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        return endpoints;
    }

    private static async Task<IResult> GetAutomationConfigAsync(
        IDcToInvoiceConfigRepository repo,
        CancellationToken cancellationToken)
    {
        var config = await repo.GetConfigAsync(cancellationToken);
        var data = MapToConfigData(config);
        return Results.Ok(new DcToInvoiceAutomationConfigResponse(
            Success: true,
            Message: "Delivery Challan to Sales Invoice automation configuration retrieved successfully.",
            Data: data,
            ErrorMessage: null));
    }

    private static async Task<IResult> UpdateAutomationConfigAsync(
        DcToInvoiceAutomationConfigDto? dto,
        IDcToInvoiceConfigRepository repo,
        IDcToInvoiceService service,
        IConvertedDcTracker tracker,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (dto is null)
        {
            return Results.BadRequest(new BatchInvoiceCreationResponse(
                Success: false,
                Message: "Invalid request body. Configuration payload is required.",
                Data: null,
                ErrorMessage: "Invalid request body. Configuration payload is required."));
        }

        string? normalizedTime = null;
        if (!string.IsNullOrWhiteSpace(dto.AutomationTime))
        {
            string[] formats = { "HH:mm", "H:mm", "HH:mm:ss", "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt" };
            if (!DateTime.TryParseExact(dto.AutomationTime.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
            {
                return Results.BadRequest(new BatchInvoiceCreationResponse(
                    Success: false,
                    Message: "Invalid AutomationTime format. Please provide time in standard 24-hour (HH:mm) or 12-hour (hh:mm PM) format.",
                    Data: null,
                    ErrorMessage: "Invalid AutomationTime format. Please provide time in standard 24-hour (HH:mm) or 12-hour (hh:mm PM) format."));
            }
            normalizedTime = parsedTime.ToString("HH:mm");
        }

        var dcList = dto.DcNumbers?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var config = await repo.GetConfigAsync(cancellationToken);
        bool timeChanged = config.AutomationTime != normalizedTime;

        var mode = dto.AutomationMode?.ToUpperInvariant();
        bool explicitlyDisabled = dto.AutomationEnabled.HasValue && !dto.AutomationEnabled.Value;
        bool hasAnyConfig = !string.IsNullOrWhiteSpace(mode) || !string.IsNullOrWhiteSpace(dto.AutomationTime) || (dcList != null && dcList.Count > 0);
        bool shouldEnable = dto.AutomationEnabled ?? hasAnyConfig;

        if (explicitlyDisabled || !shouldEnable)
        {
            // When Toggle is OFF, safety shutoff resets all parameters to null
            mode = null;
            normalizedTime = null;
            dcList = null;
            config.AutomationEnabled = false;
        }
        else
        {
            config.AutomationEnabled = true;
            if (mode != "INDIVIDUAL" && mode != "TIMER" && mode != "BOTH")
            {
                // Default based on whether time is provided if mode is not specified
                mode = string.IsNullOrWhiteSpace(dto.AutomationTime) ? "INDIVIDUAL" : "TIMER";
            }

            // Enforce strict mode-based restrictions:
            if (mode == "TIMER")
            {
                // In TIMER mode, user can ONLY configure time. Target DC list cannot be set.
                dcList = null;
            }
            else if (mode == "INDIVIDUAL")
            {
                // In INDIVIDUAL mode, user can ONLY configure DCs. Time cannot be set.
                normalizedTime = null;
            }
            // In BOTH mode, user can configure both time and DC list.
        }

        config.AutomationTime = normalizedTime;
        config.DcNumbers = dcList;
        config.AutomationMode = mode;
        config.UpdatedAtUtc = clock.UtcNow;

        if (timeChanged)
        {
            config.LastScheduledRunDate = null;
        }

        await repo.SaveConfigAsync(config, cancellationToken);

        // Case 1: When Automation is DISABLED
        if (!config.AutomationEnabled)
        {
            return Results.Ok(new BatchInvoiceCreationResponse(
                Success: true,
                Message: "Automation is disabled. Configuration saved successfully. No Delivery Challans were processed.",
                Data: new BatchInvoiceCreationData(
                    TotalDeliveryChallans: 0,
                    ProcessedDeliveryChallans: 0,
                    SuccessfulDeliveryChallans: 0,
                    FailedDeliveryChallans: 0,
                    TotalItems: 0,
                    SuccessfulItems: 0,
                    FailedItems: 0,
                    CreatedInvoiceCount: 0,
                    Results: Array.Empty<DcInvoiceResult>()),
                ErrorMessage: null));
        }

        // Case 2: INDIVIDUAL mode (or BOTH mode without scheduled time) -> Execute conversion directly & return itemized results
        if (config.AutomationMode == "INDIVIDUAL" || (config.AutomationMode == "BOTH" && string.IsNullOrWhiteSpace(config.AutomationTime)))
        {
            if (config.DcNumbers == null || config.DcNumbers.Count == 0)
            {
                return Results.Ok(new BatchInvoiceCreationResponse(
                    Success: true,
                    Message: "Delivery Challan automation configuration saved in INDIVIDUAL mode, but no specific Delivery Challans were configured.",
                    Data: new BatchInvoiceCreationData(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<DcInvoiceResult>()),
                    ErrorMessage: null));
            }

            var batchRequest = new BatchInvoiceCreationRequest(
                DcNumbers: config.DcNumbers
            );

            var batchResult = await service.CreateInvoicesFromDeliveryChallansAsync(batchRequest, cancellationToken);
            return Results.Ok(new BatchInvoiceCreationResponse(
                Success: batchResult.Success,
                Message: batchResult.Message,
                Data: batchResult.Data,
                ErrorMessage: null));
        }

        // Case 3: TIMER mode -> Save schedule & return preview of all currently pending authorized DCs
        if (config.AutomationMode == "TIMER")
        {
            var pendingSummaries = await service.GetPendingDeliveryChallansAsync(null, cancellationToken);
            var scheduledResults = new List<DcInvoiceResult>();

            foreach (var p in pendingSummaries)
            {
                var itemResults = p.Items.Select(i => new InvoiceItemResult(
                    ItemCode: i.SalesItemCode,
                    ItemName: i.SalesItemName,
                    DcQuantity: i.DcQty,
                    InvoiceQuantity: i.InvoiceQty,
                    Rate: i.OrderRate,
                    Amount: Math.Round(i.InvoiceQty * i.OrderRate, 2),
                    RateStructureCode: i.RateStructureCode ?? p.RateStructureCode ?? "",
                    Status: p.IsReadyForInvoice ? "SCHEDULED" : "NOT_READY",
                    InvoiceCreated: false,
                    InvoiceId: null,
                    InvoiceNumber: null,
                    Message: p.IsReadyForInvoice ? $"Scheduled for automatic conversion at {config.AutomationTime}." : p.StatusMessage
                )).ToList();

                decimal totalAmt = itemResults.Sum(i => i.Amount);
                decimal totalQty = itemResults.Sum(i => i.InvoiceQuantity);

                scheduledResults.Add(new DcInvoiceResult(
                    DcId: p.DcId,
                    DcNo: p.DcNo,
                    CustomerCode: p.CustomerCode,
                    ShipmentCode: p.ShipmentCode,
                    Status: p.IsReadyForInvoice ? "SCHEDULED" : "NOT_READY",
                    InvoiceNumber: null,
                    InvoiceId: null,
                    Items: itemResults,
                    TotalAmount: totalAmt,
                    TotalQuantity: totalQty,
                    Message: p.IsReadyForInvoice
                        ? $"Delivery Challan {p.DcNo} is verified and scheduled for automatic conversion daily at {config.AutomationTime}."
                        : $"Delivery Challan {p.DcNo} is not ready: {p.StatusMessage}.",
                    FailedField: p.IsReadyForInvoice ? null : "PendingQuantity",
                    RateStructureCode: p.RateStructureCode));
            }

            string timerMessage = scheduledResults.Count > 0
                ? $"Delivery Challan automation configuration saved in TIMER mode (scheduled daily at {config.AutomationTime}). Found {scheduledResults.Count} Delivery Challan(s) in ERP scheduled for conversion."
                : $"Delivery Challan automation configuration saved in TIMER mode (scheduled daily at {config.AutomationTime}). Currently 0 pending authorized Delivery Challans in ERP.";

            return Results.Ok(new BatchInvoiceCreationResponse(
                Success: true,
                Message: timerMessage,
                Data: new BatchInvoiceCreationData(
                    TotalDeliveryChallans: scheduledResults.Count,
                    ProcessedDeliveryChallans: 0,
                    SuccessfulDeliveryChallans: scheduledResults.Count(r => r.Status == "SCHEDULED"),
                    FailedDeliveryChallans: scheduledResults.Count(r => r.Status != "SCHEDULED"),
                    TotalItems: scheduledResults.Sum(r => r.ItemCount),
                    SuccessfulItems: scheduledResults.Where(r => r.Status == "SCHEDULED").Sum(r => r.ItemCount),
                    FailedItems: scheduledResults.Where(r => r.Status != "SCHEDULED").Sum(r => r.ItemCount),
                    CreatedInvoiceCount: 0,
                    Results: scheduledResults),
                ErrorMessage: null));
        }

        // Case 4: BOTH mode with scheduled time -> Check each configured DC's current conversion and scheduling status
        var bothResults = new List<DcInvoiceResult>();
        var allPending = await service.GetPendingDeliveryChallansAsync(null, cancellationToken);

        if (config.DcNumbers != null)
        {
            foreach (var dcNo in config.DcNumbers)
            {
                var cleanNo = dcNo.Trim();
                if (tracker.IsDcConverted(null, cleanNo, out var alreadyRecord) && alreadyRecord != null)
                {
                    var itemResults = new List<InvoiceItemResult>
                    {
                        new(
                            ItemCode: "ALL_ITEMS",
                            ItemName: "All Dispatched Items",
                            DcQuantity: alreadyRecord.TotalQuantity,
                            InvoiceQuantity: alreadyRecord.TotalQuantity,
                            Rate: alreadyRecord.TotalQuantity > 0 ? Math.Round(alreadyRecord.TotalAmount / alreadyRecord.TotalQuantity, 2) : 0m,
                            Amount: alreadyRecord.TotalAmount,
                            RateStructureCode: alreadyRecord.RateStructureCode ?? "",
                            Status: "ALREADY_CREATED",
                            InvoiceCreated: false,
                            InvoiceId: alreadyRecord.InvoiceId,
                            InvoiceNumber: alreadyRecord.InvoiceNumber,
                            Message: $"Delivery Challan {cleanNo} is already converted into Sales Invoice {alreadyRecord.InvoiceNumber}.")
                    };

                    bothResults.Add(new DcInvoiceResult(
                        DcId: alreadyRecord.DcId,
                        DcNo: cleanNo,
                        CustomerCode: alreadyRecord.CustomerCode,
                        ShipmentCode: "",
                        Status: "ALREADY_CREATED",
                        InvoiceNumber: alreadyRecord.InvoiceNumber,
                        InvoiceId: alreadyRecord.InvoiceId,
                        Items: itemResults,
                        TotalAmount: alreadyRecord.TotalAmount,
                        TotalQuantity: alreadyRecord.TotalQuantity,
                        Message: $"Delivery Challan {cleanNo} is already converted into Sales Invoice {alreadyRecord.InvoiceNumber}. It will be skipped during scheduled run at {config.AutomationTime}.",
                        RateStructureCode: alreadyRecord.RateStructureCode));
                }
                else
                {
                    var match = allPending.FirstOrDefault(p =>
                        string.Equals(p.DcNo.TrimStart('0'), cleanNo.TrimStart('0'), StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        var itemResults = match.Items.Select(i => new InvoiceItemResult(
                            ItemCode: i.SalesItemCode,
                            ItemName: i.SalesItemName,
                            DcQuantity: i.DcQty,
                            InvoiceQuantity: i.InvoiceQty,
                            Rate: i.OrderRate,
                            Amount: Math.Round(i.InvoiceQty * i.OrderRate, 2),
                            RateStructureCode: i.RateStructureCode ?? match.RateStructureCode ?? "",
                            Status: match.IsReadyForInvoice ? "SCHEDULED" : "NOT_READY",
                            InvoiceCreated: false,
                            InvoiceId: null,
                            InvoiceNumber: null,
                            Message: match.IsReadyForInvoice ? $"Scheduled for automatic conversion at {config.AutomationTime}." : match.StatusMessage
                        )).ToList();

                        bothResults.Add(new DcInvoiceResult(
                            DcId: match.DcId,
                            DcNo: cleanNo,
                            CustomerCode: match.CustomerCode,
                            ShipmentCode: match.ShipmentCode,
                            Status: match.IsReadyForInvoice ? "SCHEDULED" : "NOT_READY",
                            InvoiceNumber: null,
                            InvoiceId: null,
                            Items: itemResults,
                            TotalAmount: itemResults.Sum(i => i.Amount),
                            TotalQuantity: itemResults.Sum(i => i.InvoiceQuantity),
                            Message: match.IsReadyForInvoice
                                ? $"Delivery Challan {cleanNo} is verified and scheduled for automatic conversion daily at {config.AutomationTime}."
                                : $"Delivery Challan {cleanNo} is not ready: {match.StatusMessage}.",
                            FailedField: match.IsReadyForInvoice ? null : "PendingQuantity",
                            RateStructureCode: match.RateStructureCode));
                    }
                    else
                    {
                        bothResults.Add(new DcInvoiceResult(
                            DcId: 0,
                            DcNo: cleanNo,
                            CustomerCode: "",
                            ShipmentCode: "",
                            Status: "NOT_FOUND",
                            InvoiceNumber: null,
                            InvoiceId: null,
                            Items: Array.Empty<InvoiceItemResult>(),
                            TotalAmount: 0m,
                            TotalQuantity: 0m,
                            Message: $"Delivery Challan {cleanNo} was not found among pending authorized Delivery Challans in ERP.",
                            FailedField: "DcNumber"));
                    }
                }
            }
        }

        int scheduledCount = bothResults.Count(r => r.Status == "SCHEDULED");
        int alreadyCount = bothResults.Count(r => r.Status == "ALREADY_CREATED");
        string bothMessage = $"Delivery Challan automation configuration saved in BOTH mode (scheduled daily at {config.AutomationTime} for {bothResults.Count} configured DC(s): {scheduledCount} scheduled, {alreadyCount} already converted).";

        return Results.Ok(new BatchInvoiceCreationResponse(
            Success: true,
            Message: bothMessage,
            Data: new BatchInvoiceCreationData(
                TotalDeliveryChallans: bothResults.Count,
                ProcessedDeliveryChallans: 0,
                SuccessfulDeliveryChallans: scheduledCount + alreadyCount,
                FailedDeliveryChallans: bothResults.Count(r => r.Status != "SCHEDULED" && r.Status != "ALREADY_CREATED"),
                TotalItems: bothResults.Sum(r => r.ItemCount),
                SuccessfulItems: bothResults.Where(r => r.Status == "SCHEDULED" || r.Status == "ALREADY_CREATED").Sum(r => r.ItemCount),
                FailedItems: bothResults.Where(r => r.Status != "SCHEDULED" && r.Status != "ALREADY_CREATED").Sum(r => r.ItemCount),
                CreatedInvoiceCount: 0,
                Results: bothResults),
            ErrorMessage: null));
    }

    private static DcToInvoiceAutomationConfigData MapToConfigData(DcToInvoiceAutomationConfig config)
    {
        int dcCount = config.DcNumbers?.Count ?? 0;
        string statusDesc;

        if (!config.AutomationEnabled)
        {
            statusDesc = "Automation is DISABLED. Background timer and automated conversions will not run.";
        }
        else
        {
            var mode = config.AutomationMode?.ToUpperInvariant() ?? "TIMER";
            statusDesc = mode switch
            {
                "TIMER" => $"Automation is ENABLED in TIMER mode (scheduled daily at {config.AutomationTime ?? "23:59"}). All pending authorized Delivery Challans will be converted automatically at this time.",
                "INDIVIDUAL" => dcCount > 0
                    ? $"Automation is ENABLED in INDIVIDUAL mode for {dcCount} configured Delivery Challan(s): [{string.Join(", ", config.DcNumbers!)}]."
                    : "Automation is ENABLED in INDIVIDUAL mode, but no specific Delivery Challans are configured.",
                "BOTH" => $"Automation is ENABLED in BOTH mode (Timer at {config.AutomationTime ?? "23:59"} and {dcCount} configured Delivery Challan(s)).",
                _ => $"Automation is ENABLED (Mode: {mode})."
            };
        }

        return new DcToInvoiceAutomationConfigData(
            AutomationEnabled: config.AutomationEnabled,
            AutomationTime: config.AutomationTime,
            AutomationMode: config.AutomationMode,
            DcNumbers: config.DcNumbers,
            ConfiguredDcCount: dcCount,
            StatusDescription: statusDesc,
            UpdatedAtUtc: config.UpdatedAtUtc != default ? config.UpdatedAtUtc : null,
            LastScheduledRunDate: config.LastScheduledRunDate?.ToString("yyyy-MM-dd"));
    }

    /// <summary>
    /// Master automation endpoint: finds ALL existing eligible Delivery Challans and processes them
    /// sequentially in one execution, creating Sales Invoices for each eligible record.
    /// </summary>
    private static async Task<IResult> CreateInvoicesFromAllDeliveryChallansAsync(
        BatchInvoiceCreationRequestDto? requestDto,
        IDcToInvoiceService service,
        IDcToInvoiceConfigRepository configRepo,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(DcToInvoiceEndpoints));
        var config = await configRepo.GetConfigAsync(cancellationToken);

        if (!config.AutomationEnabled)
        {
            logger.LogInformation("DcToInvoice: Automation is disabled. No Delivery Challans were processed.");
            return Results.Ok(new BatchInvoiceCreationResponse(
                Success: true,
                Message: "Automation is disabled. No delivery challans were processed.",
                Data: new BatchInvoiceCreationData(0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<DcInvoiceResult>())));
        }

        var req = requestDto?.ToRequest() ?? new BatchInvoiceCreationRequest();

        // The Master / Main API converts ALL eligible Delivery Challans from ERP dynamically
        // unless the caller explicitly supplies specific DC numbers or DC IDs in the request payload.
        // It does not restrict to saved config.DcNumbers so manual main API runs always sweep all pending DCs.
        if (req.DcNumbers != null && req.DcNumbers.Count == 0)
        {
            req = req with { DcNumbers = null };
        }
        if (req.DcIds != null && req.DcIds.Count == 0)
        {
            req = req with { DcIds = null };
        }

        try
        {
            var result = await service.CreateInvoicesFromDeliveryChallansAsync(req, cancellationToken);
            return Results.Ok(new BatchInvoiceCreationResponse(
                Success: result.Success,
                Message: result.Message,
                Data: result.Data));
        }
        catch (InvoiceValidationException ex)
        {
            logger.LogWarning(ex, "DcToInvoice: Batch validation error: {Message}", ex.Message);
            return Results.UnprocessableEntity(new BatchInvoiceCreationResponse(
                Success: false,
                Message: ex.Message,
                Data: null,
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            logger.LogError(ex, "DcToInvoice: ERP transient failure during batch invoice creation: {Message}", ex.Message);
            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DcToInvoice: Unexpected error during batch invoice creation");
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> CreateInvoiceFromSingleDcAsync(
        CreateInvoiceFromDcRequest? request,
        IDcToInvoiceService service,
        IDcToInvoiceConfigRepository configRepo,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(DcToInvoiceEndpoints));
        var config = await configRepo.GetConfigAsync(cancellationToken);

        if (!config.AutomationEnabled)
        {
            return Results.Ok(new CreateInvoiceFromDcResponse(
                Success: false,
                Message: "Invoice Automation is disabled in configuration.",
                Data: null,
                Errors: new[] { new InvoiceValidationErrorResponse("AutomationEnabled", "Invoice Automation is toggled OFF.") },
                ErrorMessage: "Invoice Automation is disabled."));
        }

        if (request is null || !request.HasAnyIdentifier)
        {
            return Results.BadRequest(new CreateInvoiceFromDcResponse(
                Success: false,
                Message: "No Delivery Challan identifier provided.",
                Data: null,
                Errors: new[] { new InvoiceValidationErrorResponse("Identifier", "Provide at least a DcId or DcNo.") },
                ErrorMessage: "Provide at least a DcId or DcNo."));
        }

        try
        {
            var result = await service.CreateInvoiceFromDeliveryChallanAsync(
                request.DcId, request.ResolvedDcNo, request.CustomerCode, cancellationToken);

            bool isAlreadyCreated = string.Equals(result.Status, "AlreadyCreated", StringComparison.OrdinalIgnoreCase);

            string message = isAlreadyCreated
                ? (string.Equals(result.InvoiceNumber, "ALREADY_INVOICED", StringComparison.OrdinalIgnoreCase)
                    ? $"Delivery Challan {result.DcNo} was already invoiced or has 0 pending quantity for invoicing in ERP."
                    : $"Delivery Challan {result.DcNo} is already converted into Sales Invoice {result.InvoiceNumber} (Invoice ID: {result.InvoiceId}). Duplicate invoice creation was skipped.")
                : $"Sales Invoice {result.InvoiceNumber} created successfully.";

            return Results.Ok(new CreateInvoiceFromDcResponse(
                Success: true,
                Message: message,
                Data: new CreateInvoiceFromDcData(
                    InvoiceNumber: result.InvoiceNumber,
                    InvoiceId: result.InvoiceId,
                    DcId: result.DcId,
                    DcNo: result.DcNo,
                    CustomerCode: result.CustomerCode,
                    ItemCount: result.ItemCount,
                    TotalInvoiceQty: result.TotalInvoiceQty,
                    TotalInvoiceAmount: result.TotalInvoiceAmount,
                    Status: isAlreadyCreated ? "ALREADY_CREATED" : result.Status,
                    Authorized: result.Authorized,
                    RateStructureCode: result.RateStructureCode,
                    LineLevelRateStructureApplied: result.LineLevelRateStructureApplied),
                Errors: null,
                ErrorMessage: null));
        }
        catch (InvoiceValidationException ex)
        {
            logger.LogWarning(ex, "DcToInvoice: Validation error for DC {DcNo}: {Message}", request.ResolvedDcNo, ex.Message);
            return Results.UnprocessableEntity(new CreateInvoiceFromDcResponse(
                Success: false,
                Message: ex.Message,
                Data: null,
                Errors: new[] { new InvoiceValidationErrorResponse(ex.Field, ex.Message) },
                ErrorMessage: ex.Message));
        }
        catch (ErpTransientException ex)
        {
            logger.LogError(ex, "DcToInvoice: ERP transient failure for DC {DcNo}: {Message}", request.ResolvedDcNo, ex.Message);
            return Results.Problem(
                detail: ex.LaymanMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DcToInvoice: Unexpected failure for DC {DcNo}", request.ResolvedDcNo);
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetPendingDeliveryChallansAsync(
        string? customerCode,
        IDcToInvoiceService service,
        CancellationToken cancellationToken)
    {
        try
        {
            var summaries = await service.GetPendingDeliveryChallansAsync(customerCode, cancellationToken);
            return Results.Ok(new PendingDeliveryChallansResponse(
                Success: true,
                Message: $"Retrieved {summaries.Count} pending Delivery Challan(s).",
                Data: summaries,
                ErrorMessage: null));
        }
        catch (InvoiceValidationException ex)
        {
            return Results.UnprocessableEntity(new PendingDeliveryChallansResponse(
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
        catch (Exception ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> GetHistoryAsync(
        IDcToInvoiceService service,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? customerCode = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int page = 1,
        int pageSize = 50)
    {
        var query = new DcToInvoiceHistoryQuery
        {
            Search = search,
            Status = status,
            CustomerCode = customerCode,
            FromUtc = from,
            ToUtc = to,
            Page = page,
            PageSize = pageSize
        };

        var result = await service.GetHistoryAsync(query, cancellationToken);

        return Results.Ok(new DcToInvoiceHistoryResponse(
            Success: true,
            Message: $"Retrieved {result.Items.Count} conversion history records (Page {result.Page} of {Math.Max(1, result.TotalPages)}, Total: {result.TotalCount}).",
            Data: result));
    }

    private static async Task<IResult> GetDcHistoryAsync(
        string dcNo,
        IDcToInvoiceService service,
        CancellationToken cancellationToken)
    {
        long? dcId = long.TryParse(dcNo, out var parsed) ? parsed : null;
        var result = await service.GetDcHistoryAsync(dcNo: dcNo, dcId: dcId, cancellationToken: cancellationToken);

        string message = result.IsConverted
            ? $"Delivery Challan {result.DcNo} is converted into Sales Invoice {result.LatestRecord?.InvoiceNumber}."
            : $"Delivery Challan {result.DcNo} has not been converted yet.";

        return Results.Ok(new DcSpecificHistoryResponse(
            Success: true,
            Message: message,
            Data: result));
    }

    private static async Task<IResult> GetDashboardAsync(
        IDcToInvoiceService service,
        CancellationToken cancellationToken)
    {
        var summary = await service.GetDashboardSummaryAsync(cancellationToken);

        return Results.Ok(new DcToInvoiceDashboardResponse(
            Success: true,
            Message: "Delivery Challan to Sales Invoice dashboard summary retrieved successfully.",
            Data: summary));
    }

    private static async Task<IResult> GetDailySummaryAsync(
        IDcToInvoiceService service,
        CancellationToken cancellationToken,
        int days = 30)
    {
        var stats = await service.GetDailyStatsAsync(days, cancellationToken);

        return Results.Ok(new DcToInvoiceDailyStatsResponse(
            Success: true,
            Message: $"Daily conversion statistics for the past {stats.Count} days retrieved successfully.",
            Data: stats));
    }
}
