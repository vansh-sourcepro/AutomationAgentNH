using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHorizon.Automation.Application.Flows.PoToGrn;

public static class PoToGrnRegistration
{
    /// <summary>
    /// The database-less defaults. TryAdd, so the Infrastructure repositories win whenever there is
    /// a database.
    /// </summary>
    public static IServiceCollection AddPoToGrnDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPoGrnAutomationConfigRepository, NullPoGrnAutomationConfigRepository>();
        services.TryAddScoped<IPoGrnHistory, NullPoGrnHistory>();

        return services;
    }
}
