using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Services;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor;

/// <summary>
/// The Issue to Shop Floor flow's share of the host: its options, its scheduler, and its endpoints.
/// </summary>
public static class IssueToShopFloorModule
{
    /// <summary>
    /// Binds <c>AutomationAgent:IssueToShopFloor</c>. Deliberately not validated on start: a missing value
    /// (sites, Issue To / Issue By) refuses each request with a message naming the setting, instead
    /// of stopping an agent whose other flows are configured correctly.
    /// Called by <see cref="Configuration.OptionsRegistration.AddAutomationAgentOptions"/>.
    /// </summary>
    public static IServiceCollection AddIssueToShopFloorOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IssueToShopFloorOptions>()
            .Bind(configuration.GetSection(IssueToShopFloorOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Issue to Shop Floor automation: the daily scheduler that executes scheduled conversions.
    /// Needs the database (config rows + tracked runs), so it is registered under the same
    /// usable-database gate as the other schedulers.
    /// </summary>
    public static IServiceCollection AddIssueToShopFloorScheduler(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(IssueToShopFloorSchedulerService.HttpClientName);
        services.AddHostedService<IssueToShopFloorSchedulerService>();

        return services;
    }

    public static WebApplication MapIssueToShopFloorEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapIssueToShopFloorApi();
        app.MapIssueToShopFloorHistoryEndpoints();
        app.MapIssueToShopFloorDashboardEndpoints();
        app.MapIssueToShopFloorConfigEndpoints();

        return app;
    }
}
