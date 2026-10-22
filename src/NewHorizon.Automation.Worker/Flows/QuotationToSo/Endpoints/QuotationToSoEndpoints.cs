using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Contracts;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Endpoints;

/// <summary>
/// Exposes the Quotation to Sales Order automation API: <c>POST /api/automation/quotation-to-so</c>.
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
        ILogger<QuotationToSoService> logger,
        CancellationToken cancellationToken)
    {
        request ??= new ConvertQuotationsRequest();

        try
        {
            // Flow A: Convert specific quotation by quotation number
            if (!string.IsNullOrWhiteSpace(request.QuotationNumber))
            {
                var qtnNo = request.QuotationNumber.Trim();
                logger.LogInformation("Triggered Sales Order creation for Quotation {QuotationNumber}", qtnNo);

                var itemResult = await service.ConvertQuotationByNumberAsync(
                    qtnNo,
                    request.CustomerCode,
                    request.SiteCode,
                    cancellationToken);

                if (!itemResult.Success)
                {
                    return Results.Ok(new ConvertQuotationsResponse
                    {
                        Success = false,
                        Message = itemResult.ErrorMessage ?? $"Failed to convert quotation '{qtnNo}'.",
                        SalesOrderNumber = itemResult.SalesOrderNumber,
                        Item = itemResult,
                    });
                }

                return Results.Ok(new ConvertQuotationsResponse
                {
                    Success = true,
                    Message = $"Quotation '{itemResult.QuotationNumber}' successfully converted to Sales Order '{itemResult.SalesOrderNumber}'.",
                    SalesOrderNumber = itemResult.SalesOrderNumber,
                    Item = itemResult,
                });
            }

            // Flow B: Convert all eligible quotations (for customer, or for all customers across site/company)
            logger.LogInformation(
                "Triggered Quotation to Sales Order conversion for all eligible quotations (Customer: {CustomerCode}, Site: {SiteCode})",
                request.CustomerCode ?? "ALL",
                request.SiteCode ?? "ALL");

            var sweepResult = await service.ConvertAllEligibleQuotationsAsync(
                request.CustomerCode,
                request.SiteCode,
                cancellationToken);

            if (sweepResult.TotalFound == 0)
            {
                var scopeDesc = !string.IsNullOrWhiteSpace(request.CustomerCode)
                    ? $"Customer '{request.CustomerCode}'"
                    : "the system";

                return Results.Ok(new ConvertQuotationsResponse
                {
                    Success = true,
                    Message = $"No eligible open authorised quotations found for conversion in {scopeDesc}.",
                    Result = sweepResult,
                });
            }

            var message = $"Processed {sweepResult.TotalFound} quotation(s): {sweepResult.Succeeded} converted successfully, {sweepResult.Failed} failed.";

            return Results.Ok(new ConvertQuotationsResponse
            {
                Success = sweepResult.Failed == 0,
                Message = message,
                Result = sweepResult,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Error executing Quotation to Sales Order conversion for request {@Request}",
                request);

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
}
