using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Application.Flows.IndentToPo;

namespace NewHorizon.Automation.Infrastructure.Flows.IndentToPo;

/// <summary>The Indent → PO flow's share of the automation database.</summary>
/// <remarks>
/// Its EF configurations live beside this file under <c>Configurations/</c> and are picked up by
/// <c>ApplyConfigurationsFromAssembly</c>; nothing here needs to list them.
/// </remarks>
public static class IndentToPoRegistration
{
    /// <summary>Called by <see cref="DependencyInjection.AddAutomationInfrastructure"/>.</summary>
    public static IServiceCollection AddIndentToPoPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IIndentPoTrackingRepository, IndentPoTrackingRepository>();

        // Replaces the no-op registered by AddAutomationApplication: with a database the three
        // per-indent-type automation rows are real, the scheduler runs, and the API mode-gate enforces.
        services.AddScoped<IIndentPoAutomationConfigRepository, IndentPoAutomationConfigRepository>();

        return services;
    }
}
