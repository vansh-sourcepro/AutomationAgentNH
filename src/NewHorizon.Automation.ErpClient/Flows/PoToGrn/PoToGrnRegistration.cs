using Microsoft.Extensions.DependencyInjection;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

public static class PoToGrnRegistration
{
    public static IServiceCollection AddPoToGrnErp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Borrows the named ERP client, so it rides the same auth handler and resilience pipeline.
        // Scoped: the Document Control and tax caches live for one run.
        services.AddScoped<IPoToGrnService, PoToGrnService>();

        return services;
    }
}
