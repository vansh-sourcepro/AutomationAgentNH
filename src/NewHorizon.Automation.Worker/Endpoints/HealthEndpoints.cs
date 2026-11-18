using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewHorizon.Automation.Worker.Contracts;
using NewHorizon.Automation.Worker.Diagnostics;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// Liveness/readiness surface for the ERP and for operators. Deliberately unauthenticated and
/// loopback-bound: it reveals no job data, and the service monitor must be able to reach it.
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/automation/health", GetHealthAsync)
            .WithName("GetAutomationHealth");

        endpoints.MapGet("/api/automation/types", GetAutomationTypes)
            .WithName("GetAutomationTypes");

        // Convenience alias for service monitors and the install scripts.
        endpoints.MapGet("/health", GetHealthAsync)
            .WithName("GetHealth");

        return endpoints;
    }

    private static IResult GetAutomationTypes()
    {
        return Results.Ok(new[]
        {
            new
            {
                id = "IndentToPo",
                name = "Indent-to-PO",
                description = "Convert authorised material and service indents into purchase orders",
                configEndpoint = "po-automation",
                dashboardEndpoint = "process-jobs/dashboard",
                historyEndpoint = "process-jobs/runs"
            },
            new
            {
                id = "QuotationToSo",
                name = "Quotation-to-SO",
                description = "Convert authorised customer quotations into Sales Orders",
                configEndpoint = "so-automation",
                dashboardEndpoint = "quotation-to-so/dashboard",
                historyEndpoint = "quotation-to-so/history"
            },
            new
            {
                id = "PoToGrn",
                name = "PO-to-GRN",
                description = "Convert authorised purchase orders into goods receipt notes",
                configEndpoint = "grn-automation",
                dashboardEndpoint = "po-to-grn/dashboard",
                historyEndpoint = "po-to-grn/history"
            },
            new
            {
                id = "SjoToIssue",
                name = "SJO-to-Issue-to-Shop-Floor",
                description = "Reserve stock and create shop floor issues from SJO, Work Order, or Sales OAF",
                configEndpoint = "issue-to-shop-floor/config",
                dashboardEndpoint = "issue-to-shop-floor/dashboard",
                historyEndpoint = "issue-to-shop-floor/history"
            },
            new
            {
                id = "SjoToDc",
                name = "SJO-to-DC",
                description = "Create Delivery Challans from Sales Order Allocations",
                configEndpoint = "v1/sales/SalesAllocationDeAllocation/AutomationConfig",
                dashboardEndpoint = "sales/SalesAllocationDeAllocation/dashboard",
                historyEndpoint = "sales/SalesAllocationDeAllocation/history"
            },
            new
            {
                id = "DcToInvoice",
                name = "DC-to-Invoice",
                description = "Create Sales Invoices from Delivery Challans",
                configEndpoint = "v1/sales/salesinvoice/AutomationConfig",
                dashboardEndpoint = "sales/salesinvoice/dashboard",
                historyEndpoint = "sales/salesinvoice/history"
            }
        });
    }

    private static async Task<IResult> GetHealthAsync(
        AgentRuntimeInfo runtimeInfo,
        HealthCheckService healthCheckService,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var report = await healthCheckService.CheckHealthAsync(cancellationToken);

        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = "Healthy",
        };

        foreach (var (name, entry) in report.Entries)
        {
            checks[name] = entry.Status.ToString();
        }

        var response = new HealthResponse(
            Status: report.Status.ToString(),
            Version: runtimeInfo.Version,
            StartedAtUtc: runtimeInfo.StartedAtUtc,
            UptimeSeconds: Math.Round(runtimeInfo.UptimeAt(nowUtc).TotalSeconds, 3),
            Checks: checks);

        // Degraded still answers 200: the service is running and can report why it is unhappy.
        return report.Status is HealthStatus.Unhealthy
            ? Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(response);
    }
}
