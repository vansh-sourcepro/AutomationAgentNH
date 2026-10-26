using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Endpoints;

/// <summary>
/// Exposes the Quotation to Sales Order automation API: <c>POST /api/automation/quotation-to-so</c>.
/// Governed by the master on/off switch and run mode configured in <see cref="SoAutomationEndpoints"/>.
/// </summary>
public static class QuotationToSoEndpoints
{
    public const string Route = "/api/automation/quotation-to-so";

    public static IEndpointRouteBuilder MapQuotationToSoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Primary conversion trigger
        endpoints.MapPost(Route, ConvertAsync)
            .WithName("ConvertQuotationToSalesOrder")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        // Preview open quotations for customer and site without converting
        endpoints.MapGet($"{Route}/open", GetOpenQuotationsAsync)
            .WithName("GetOpenAuthorisedQuotations")
            .AddEndpointFilter<ErpUserOrApiKeyFilter>();

        return endpoints;
    }

    private static async Task<IResult> ConvertAsync(
        ConvertQuotationsRequest? request,
        IQuotationToSoService service,
        IQuotationSoAutomationConfigRepository configRepo,
        IClock clock,
        ILogger<QuotationToSoService> logger,
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

        try
        {
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
