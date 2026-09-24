using NewHorizon.Automation.ErpClient.Flows.PoToGrn;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Endpoints;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Services;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn;

/// <summary>Everything the Worker wires for PO → GRN. Shares nothing with Indent → PO.</summary>
public static class PoToGrnModule
{
    public static IServiceCollection AddPoToGrnOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PoGrnOptions>()
            .Bind(configuration.GetSection(PoGrnOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.DomesticCurrency),
                "AutomationAgent:PoToGrn:DomesticCurrency must be set, e.g. \"INR\".")
            .ValidateOnStart();

        services.AddOptions<PoGrnEndpointOptions>()
            .Bind(configuration.GetSection(PoGrnEndpointOptions.SectionName));

        return services;
    }

    /// <summary>Only with a usable automation database — there is no toggle to read without one.</summary>
    public static IServiceCollection AddPoToGrnScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(GrnAutomationSchedulerService.HttpClientName);
        services.AddHostedService<GrnAutomationSchedulerService>();

        return services;
    }

    /// <summary>
    /// Mapped whether or not there is a database: without one both APIs answer 503 and say why,
    /// which beats a 404 that leaves the caller wondering if the path is wrong.
    /// </summary>
    public static WebApplication MapPoToGrnEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGrnAutomationEndpoints();
        Endpoints.PoToGrnEndpoints.MapPoToGrnEndpoints(app);

        return app;
    }
}
