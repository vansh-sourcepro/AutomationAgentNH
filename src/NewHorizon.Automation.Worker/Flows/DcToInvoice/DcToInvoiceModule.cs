using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.ErpClient.Flows.DcToInvoice;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Configuration;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Endpoints;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Services;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice;

/// <summary>
/// The Delivery Challan -> Sales Invoice flow's share of the host.
/// Follows exactly the same module pattern as SoAllocationToDcModule.
/// </summary>
public static class DcToInvoiceModule
{
    /// <summary>
    /// Registers the ERP client service and its configuration for this flow.
    /// Called from Program.cs after AddErpClient().
    /// </summary>
    public static IServiceCollection AddDcToInvoiceServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration
            .GetSection(DcToInvoiceOptions.SectionName)
            .Get<DcToInvoiceOptions>() ?? new DcToInvoiceOptions();

        services.AddSingleton(options);

        // Register the ERP client service
        services.AddDcToInvoiceErp();

        services.AddSingleton<IDcToInvoiceConfigRepository, JsonDcToInvoiceConfigRepository>();
        services.AddHostedService<DcToInvoiceSchedulerService>();

        return services;
    }

    /// <summary>Maps the flow's endpoints.</summary>
    public static WebApplication MapDcToInvoiceEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Endpoints.DcToInvoiceEndpoints.MapDcToInvoiceEndpoints(app);

        return app;
    }
}
