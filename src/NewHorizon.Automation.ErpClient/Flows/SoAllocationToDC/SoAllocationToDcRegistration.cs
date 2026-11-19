using Microsoft.Extensions.DependencyInjection;

namespace NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

/// <summary>The Sales Allocation -> DC flow's share of the ERP client.</summary>
public static class SoAllocationToDcRegistration
{
    /// <summary>
    /// Registers the conversion service. Called by
    /// <see cref="DependencyInjection.AddErpClient"/> after the named ERP client it borrows
    /// is registered.
    /// </summary>
    public static IServiceCollection AddSoAllocationToDcErp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Borrows the named ERP client so it runs on the same auth handler and resilience
        // pipeline without appearing on IErpClient — nothing else in the agent creates
        // Delivery Challans.
        services.AddScoped<ISoAllocationToDcService, SoAllocationToDcService>();

        return services;
    }
}
