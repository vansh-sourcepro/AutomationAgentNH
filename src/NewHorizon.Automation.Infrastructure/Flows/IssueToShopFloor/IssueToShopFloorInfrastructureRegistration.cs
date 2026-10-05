using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Infrastructure.Flows.IssueToShopFloor;

/// <summary>The Issue to Shop Floor flow's share of the automation database.</summary>
public static class IssueToShopFloorInfrastructureRegistration
{
    public static IServiceCollection AddIssueToShopFloorPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IIssueToShopFloorTrackingRepository, IssueToShopFloorTrackingRepository>();

        return services;
    }
}
