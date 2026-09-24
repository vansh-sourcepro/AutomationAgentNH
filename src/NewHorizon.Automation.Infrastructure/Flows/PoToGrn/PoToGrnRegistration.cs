using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Application.Flows.PoToGrn;

namespace NewHorizon.Automation.Infrastructure.Flows.PoToGrn;

public static class PoToGrnRegistration
{
    public static IServiceCollection AddPoToGrnPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Replace the database-less defaults registered by AddAutomationApplication.
        services.AddScoped<IPoGrnAutomationConfigRepository, PoGrnAutomationConfigRepository>();
        services.AddScoped<IPoGrnHistory, PoGrnHistory>();

        return services;
    }
}
