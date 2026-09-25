using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.Worker.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Worker.Configuration;

/// <summary>
/// Binds the single bootstrap section and validates it at startup, so a misconfigured server
/// fails immediately and visibly rather than at the first ERP call.
/// </summary>
public static class OptionsRegistration
{
    public static IServiceCollection AddAutomationAgentOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<AutomationAgentOptions>, AutomationAgentOptionsValidator>();

        services.AddOptions<AutomationAgentOptions>()
            .Bind(configuration.GetSection(AutomationAgentOptions.SectionName))
            // Database:ConnectionString is deliberately not validated here. It is optional: with no
            // connection string the agent still runs the ERP-facing features and only the job
            // queue, its history and the config table are unavailable. See Program.cs.
            .Validate(
                options => Uri.TryCreate(options.ErpApi.BaseUrl, UriKind.Absolute, out _),
                "AutomationAgent:ErpApi:BaseUrl must be an absolute URL.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Host.InboundApiKey),
                "AutomationAgent:Host:InboundApiKey must be set; the agent API is never left unauthenticated.")
            .ValidateOnStart();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AutomationAgentOptions>>().Value);

        // Which JSON properties the agent reads and sets on an SJO row. The ERP team has not
        // confirmed these names, so they are bound separately and can be corrected on the server
        // without a rebuild. Unset leaves the documented defaults in place.
        // ERP paths. Most are placeholders the ERP team has yet to confirm, so being able to
        // correct one in config beats redeploying to change a string.
        services.AddOptions<ErpEndpointOptions>()
            .Bind(configuration.GetSection($"{AutomationAgentOptions.SectionName}:ErpEndpoints"))
            .Validate(
                endpoints => endpoints.SjoSequenceTemplate.Contains(ErpEndpointOptions.SiteIdToken, StringComparison.Ordinal)
                    && endpoints.AutoShopTemplate.Contains(ErpEndpointOptions.SiteIdToken, StringComparison.Ordinal),
                $"The per-site endpoint templates must contain '{ErpEndpointOptions.SiteIdToken}', "
                + "or every site would be sent to the same URL.")
            .ValidateOnStart();

        // Each automation flow's own configuration section. A new flow adds its own line here.
        services.AddIndentToPoOptions(configuration);
        services.AddIssueToShopFloorOptions(configuration);

        services.AddOptions<AutoShopFieldMap>()
            .Bind(configuration.GetSection($"{AutomationAgentOptions.SectionName}:AutoShop"))
            .Validate(
                map => !string.IsNullOrWhiteSpace(map.SelectionFlag),
                "AutomationAgent:AutoShop:SelectionFlag must name the property the agent sets before submitting.")
            .ValidateOnStart();

        // How the agent validates the ERP bearer token WebApp2 presents, and which browser origins
        // may call the management API. Both optional: a blank signing key leaves the browser-facing
        // endpoints unavailable and the agent serving only its API-key callers.
        services.AddOptions<InboundJwtOptions>()
            .Bind(configuration.GetSection(InboundJwtOptions.SectionName))
            .Validate(
                jwt => !jwt.IsConfigured || (!string.IsNullOrWhiteSpace(jwt.Issuer) && !string.IsNullOrWhiteSpace(jwt.Audience)),
                "AutomationAgent:InboundJwt needs an Issuer and an Audience when a SigningKey is set.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<InboundJwtOptions>>().Value);

        services.AddOptions<AgentCorsOptions>()
            .Bind(configuration.GetSection(AgentCorsOptions.SectionName));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentCorsOptions>>().Value);

        return services;
    }
}
