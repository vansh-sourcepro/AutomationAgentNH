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
        services.TryAddScoped<IQuotationToSoTracker, NullQuotationToSoTracker>();

        return services;
    }

    public static IServiceCollection AddQuotationToSoTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQuotationToSoTracker, QuotationToSoTracker>();

        return services;
    }
}
