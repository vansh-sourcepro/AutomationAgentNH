using Microsoft.Extensions.DependencyInjection;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>The Indent → PO flow's share of the ERP client.</summary>
public static class IndentToPoRegistration
{
    /// <summary>
    /// Registers the conversion service. Called by <see cref="DependencyInjection.AddErpClient"/>,
    /// after the named ERP client it borrows is registered.
    /// </summary>
    public static IServiceCollection AddIndentToPoErp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Borrows the named client, so it runs on the same auth handler and resilience pipeline
        // without appearing on IErpClient — nothing else in the agent creates purchase orders, and
        // widening that interface would push ERP screen detail into every workflow.
        services.AddScoped<IIndentToPoService, IndentToPoService>();

        return services;
    }
}
