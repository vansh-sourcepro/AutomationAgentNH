using NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC;

/// <summary>
/// The Sales Allocation -> Delivery Challan flow''s share of the host.
/// Follows exactly the same module pattern as IndentToPoModule.
/// </summary>
/// <remarks>
/// Program.cs calls each method once. A new flow gets a module of its own beside this
/// one and its own calls in Program.cs.
/// </remarks>
public static class SoAllocationToDcModule
{
    /// <summary>
    /// Registers the ERP client service and its configuration for this flow.
    /// Called from Program.cs after AddErpClient().
    /// </summary>
    public static IServiceCollection AddSoAllocationToDcServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind and register options.
        var options = configuration
            .GetSection(SoAllocationToDcOptions.SectionName)
            .Get<SoAllocationToDcOptions>() ?? new SoAllocationToDcOptions();

        services.AddSingleton(options);

        // Register the ERP client service (borrows the named ERP HttpClient).
        services.AddSoAllocationToDcErp();
        
        services.AddSingleton<NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration.ISoAllocationToDcConfigRepository, NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration.JsonSoAllocationToDcConfigRepository>();
        services.AddHostedService<NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Services.SoAllocationToDcSchedulerService>();

        return services;
    }

    /// <summary>Maps the flow''s single endpoint. No database or JWT requirement.</summary>
    public static WebApplication MapSoAllocationToDcEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        Endpoints.SoAllocationToDcEndpoints.MapSoAllocationToDcEndpoints(app);

        return app;
    }
}
