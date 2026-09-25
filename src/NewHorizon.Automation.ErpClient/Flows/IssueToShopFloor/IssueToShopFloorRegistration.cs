using Microsoft.Extensions.DependencyInjection;

namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>The SJO → Issue to Shop Floor flow's share of the ERP client.</summary>
public static class IssueToShopFloorRegistration
{
    /// <summary>
    /// Registers the conversion service. Called by <see cref="DependencyInjection.AddErpClient"/>,
    /// after the named ERP client it borrows is registered.
    /// </summary>
    public static IServiceCollection AddIssueToShopFloorErp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IIssueToShopFloorService, IssueToShopFloorService>();

        return services;
    }
}
