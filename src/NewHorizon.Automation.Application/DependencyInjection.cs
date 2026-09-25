using Microsoft.Extensions.DependencyInjection;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows;
using NewHorizon.Automation.Application.Workflows.Definitions;

namespace NewHorizon.Automation.Application;

/// <summary>
/// Registers the engine and the workflow catalog.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// The workflow catalog, which needs nothing but itself.
    /// </summary>
    public static IServiceCollection AddAutomationApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Adding a workflow is one line here plus one definition file. Everything downstream —
        // queue, engine, retry, logging, API — is unchanged by it.
        services.AddSingleton<IWorkflowCatalog>(_ => new WorkflowCatalog(
        [
            SjoWorkflow.Create(),
            OafWorkflow.Create(),
            MilWorkflow.Create(),
            CbomWorkflow.Create(),
            AutoShopWorkflow.Create(),
            AutoShopCycleWorkflow.Create(),
        ]));

        // Each automation flow's database-less defaults. A new flow adds its own line here.
        services.AddIndentToPoDefaults();

        return services;
    }

    /// <summary>
    /// The queued half: the engine that walks a job and the funnel every trigger enqueues through.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddAutomationApplication"/> because both of these need
    /// <see cref="IJobRepository"/>, which only exists when a database is configured. Registering
    /// them regardless made the host fail validation at startup with no connection string — the
    /// database was meant to be optional, and this is what that promise costs.
    /// </remarks>
    public static IServiceCollection AddAutomationJobProcessing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IWorkflowEngine, WorkflowEngine>();

        // The one funnel every trigger goes through — the timer and the manual run-now call.
        services.AddScoped<ICycleEnqueueService, CycleEnqueueService>();

        return services;
    }

    /// <summary>
    /// Process tracking with somewhere to write, for every flow that records its runs.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddAutomationJobProcessing"/>, and called only when the database is
    /// <i>usable</i> rather than merely configured — see
    /// <see cref="IndentToPoRegistration.AddIndentToPoTracking"/> for why. A new flow that tracks
    /// its runs adds its own line here.
    /// </remarks>
    public static IServiceCollection AddProcessTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddIndentToPoTracking();

        return services;
    }
}
