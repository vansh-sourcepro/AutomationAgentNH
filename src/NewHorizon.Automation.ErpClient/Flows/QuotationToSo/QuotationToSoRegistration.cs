using Microsoft.Extensions.DependencyInjection;

namespace NewHorizon.Automation.ErpClient.Flows.QuotationToSo;

public static class QuotationToSoRegistration
{
    public static IServiceCollection AddQuotationToSoErp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IQuotationToSoService, QuotationToSoService>();

        return services;
    }
}
