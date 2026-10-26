using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Application.Flows.QuotationToSo;

namespace NewHorizon.Automation.Infrastructure.Flows.QuotationToSo;

/// <summary>The Quotation → SO flow's share of the automation database.</summary>
public static class QuotationToSoRegistration
{
    public static IServiceCollection AddQuotationToSoPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQuotationSoAutomationConfigRepository, QuotationSoAutomationConfigRepository>();

        return services;
    }
}
