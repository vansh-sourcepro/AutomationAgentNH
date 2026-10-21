using Microsoft.AspNetCore.Builder;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo;

/// <summary>
/// Worker wiring for the Quotation to Sales Order automation flow.
/// </summary>
public static class QuotationToSoModule
{
    public static WebApplication MapQuotationToSoEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Endpoints.QuotationToSoEndpoints.MapQuotationToSoEndpoints(app);

        return app;
    }
}
