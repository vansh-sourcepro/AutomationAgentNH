using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHorizon.Automation.Application.Flows.QuotationToSo;

/// <summary>The Quotation → SO flow's share of the Application layer.</summary>
public static class QuotationToSoRegistration
{
    public static IServiceCollection AddQuotationToSoDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IQuotationSoAutomationConfigRepository, NullQuotationSoAutomationConfigRepository>();

        return services;
    }
}
