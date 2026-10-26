using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Endpoints;
using NewHorizon.Automation.Worker.Flows.QuotationToSo.Services;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo;

/// <summary>
/// Worker wiring for the Quotation to Sales Order automation flow.
/// </summary>
public static class QuotationToSoModule
{
    public static IServiceCollection AddQuotationToSoScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(QuotationToSoSchedulerService.HttpClientName);
        services.AddHostedService<QuotationToSoSchedulerService>();

        return services;
    }

    public static WebApplication MapQuotationToSoEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Endpoints.QuotationToSoEndpoints.MapQuotationToSoEndpoints(app);
        SoAutomationEndpoints.MapSoAutomationEndpoints(app);

        return app;
    }
}
