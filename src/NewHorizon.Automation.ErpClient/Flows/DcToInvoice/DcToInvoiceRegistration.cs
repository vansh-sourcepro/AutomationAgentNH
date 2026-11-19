using System;
using Microsoft.Extensions.DependencyInjection;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

/// <summary>The Delivery Challan -> Invoice flow's share of the ERP client.</summary>
public static class DcToInvoiceRegistration
{
    /// <summary>
    /// Registers the conversion service. Called by AddErpClient after the named ERP client is registered.
    /// </summary>
    public static IServiceCollection AddDcToInvoiceErp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IConvertedDcTracker, ConvertedDcTracker>();
        services.AddScoped<IDcToInvoiceService, DcToInvoiceService>();

        return services;
    }
}
