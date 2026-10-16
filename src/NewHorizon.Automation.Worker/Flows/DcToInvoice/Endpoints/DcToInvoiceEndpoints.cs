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

        // Master batch automation API: processes ALL eligible Delivery Challans sequentially in one execution
        endpoints
            .MapPost(
                "/api/v1/sales/salesinvoice/CreateInvoicesFromDeliveryChallans",
                CreateInvoicesFromAllDeliveryChallansAsync)
            .WithName("CreateInvoicesFromDeliveryChallans")
            .AddEndpointFilter<ApiKeyFilter>();

        // Individual DC-to-Invoice conversion endpoint
        endpoints
            .MapPost(
                "/api/v1/sales/salesinvoice/CreateInvoiceFromDeliveryChallan",
                CreateInvoiceFromSingleDcAsync)
            .WithName("CreateInvoiceFromDeliveryChallan")
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

        return endpoints;
    }

    private static async Task<IResult> GetAutomationConfigAsync(
        IDcToInvoiceConfigRepository repo,
        CancellationToken cancellationToken)
    {
        var config = await repo.GetConfigAsync(cancellationToken);
        return Results.Ok(new DcToInvoiceAutomationConfigDto
        {
            AutomationEnabled = config.AutomationEnabled,
            AutomationTime = config.AutomationTime,
            DcNumber = config.DcNumbers?.FirstOrDefault(),
            AutomationMode = config.AutomationMode,
            DcNumbers = config.DcNumbers
        });
    }

    private static async Task<IResult> UpdateAutomationConfigAsync(
        DcToInvoiceAutomationConfigDto? dto,
        IDcToInvoiceConfigRepository repo,
        IClock clock,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (dto is null)
        {
            return Results.BadRequest("Invalid request body. Configuration payload is required.");
        }

        string? normalizedTime = null;
        if (!string.IsNullOrWhiteSpace(dto.AutomationTime))
        {
            string[] formats = { "HH:mm", "H:mm", "HH:mm:ss", "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt" };
            if (!DateTime.TryParseExact(dto.AutomationTime.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
            {
                return Results.BadRequest("Invalid AutomationTime format. Please provide time in standard 24-hour (HH:mm) or 12-hour (hh:mm PM) format.");
            }
            normalizedTime = parsedTime.ToString("HH:mm");
        }

        var dcList = dto.DcNumbers;
        if (dcList == null || dcList.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(dto.DcNumber))
            {
                dcList = new List<string> { dto.DcNumber.Trim() };
            }
        }

        var config = await repo.GetConfigAsync(cancellationToken);
        bool timeChanged = config.AutomationTime != normalizedTime;

        var mode = dto.AutomationMode?.ToUpperInvariant();
        if (!dto.AutomationEnabled)
        {
            // When Toggle is OFF, safety shutoff resets all parameters to null
            mode = null;
            normalizedTime = null;
            dcList = null;
        }
        else
        {
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

        config.AutomationEnabled = dto.AutomationEnabled;
        config.AutomationTime = normalizedTime;
        config.DcNumbers = dcList;
        config.AutomationMode = mode;
        config.UpdatedAtUtc = clock.UtcNow;

        if (timeChanged)
        {
            config.LastScheduledRunDate = null;
        }

        await repo.SaveConfigAsync(config, cancellationToken);

        // Immediate run trigger if mode is INDIVIDUAL (or BOTH without a configured time)
        if (config.AutomationEnabled && (config.AutomationMode == "INDIVIDUAL" || (config.AutomationMode == "BOTH" && string.IsNullOrWhiteSpace(config.AutomationTime))))
        {
            var factory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            _ = Task.Run(async () =>
            {
                using var scope = factory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IDcToInvoiceService>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<IDcToInvoiceService>>();
                try
                {
                    var target = config.DcNumbers != null && config.DcNumbers.Count > 0
                        ? string.Join(", ", config.DcNumbers)
                        : "ALL";
                    logger.LogInformation("DcToInvoice: Immediate run triggered (Mode: {Mode}) for DC(s): {Target}", config.AutomationMode, target);
                    await service.CreateInvoicesFromDeliveryChallansAsync(
                        new BatchInvoiceCreationRequest(DcNumbers: config.DcNumbers), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DcToInvoice: Immediate run failed");
                }
            }, CancellationToken.None);
        }

        return Results.Ok(new DcToInvoiceAutomationConfigDto
        {
            AutomationEnabled = config.AutomationEnabled,
            AutomationTime = config.AutomationTime,
            DcNumber = config.DcNumbers?.FirstOrDefault(),
            AutomationMode = config.AutomationMode,
            DcNumbers = config.DcNumbers
        });
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

        // Apply selection parameter logic based on mode
        if (req.DcNumbers == null || req.DcNumbers.Count == 0)
        {
            if (config.AutomationMode == "INDIVIDUAL" && config.DcNumbers != null && config.DcNumbers.Count > 0)
            {
                req = req with { DcNumbers = config.DcNumbers };
            }
            else
            {
                // In TIMER or BOTH mode without specific DC numbers, process all eligible Delivery Challans dynamically
                req = req with { DcNumbers = null };
            }
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

            return Results.Ok(new CreateInvoiceFromDcResponse(
                Success: true,
                Message: $"Sales Invoice {result.InvoiceNumber} created successfully.",
                Data: new CreateInvoiceFromDcData(
                    InvoiceNumber: result.InvoiceNumber,
                    InvoiceId: result.InvoiceId,
                    DcId: result.DcId,
                    DcNo: result.DcNo,
                    CustomerCode: result.CustomerCode,
                    ItemCount: result.ItemCount,
                    TotalInvoiceQty: result.TotalInvoiceQty,
                    TotalInvoiceAmount: result.TotalInvoiceAmount,
                    Status: result.Status,
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
}
