using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>The Issue to Shop Floor flow's share of the Application layer.</summary>
public static class IssueToShopFloorApplicationRegistration
{
    /// <summary>
    /// Fallback registration when database tracking is off.
    /// </summary>
    public static IServiceCollection AddIssueToShopFloorDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IIssueToShopFloorTracker>(_ => NullIssueToShopFloorTracker.Instance);
        services.TryAddScoped<IIssueToShopFloorHistoryService>(_ => NullIssueToShopFloorTracker.Instance);
        return services;
    }

    /// <summary>
    /// Process tracking registration when database is available.
    /// </summary>
    public static IServiceCollection AddIssueToShopFloorTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IssueToShopFloorTrackerService>();
        services.AddScoped<IIssueToShopFloorTracker>(provider => provider.GetRequiredService<IssueToShopFloorTrackerService>());
        services.AddScoped<IIssueToShopFloorHistoryService>(provider => provider.GetRequiredService<IssueToShopFloorTrackerService>());

        return services;
    }
}
