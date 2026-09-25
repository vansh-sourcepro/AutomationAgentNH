using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor;

/// <summary>
/// The SJO → Issue to Shop Floor flow's share of the host: its options and its endpoint.
/// </summary>
/// <remarks>
/// No database, no scheduler, no tracking yet: the ERP's own issued quantities are what stop an SJO
/// being issued twice, so the flow works on an installation with no automation database.
/// </remarks>
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

    public static WebApplication MapIssueToShopFloorEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapIssueToShopFloorApi();

        return app;
    }
}
