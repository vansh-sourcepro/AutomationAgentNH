using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>The Indent → PO flow's share of the Application layer.</summary>
public static class IndentToPoRegistration
{
    /// <summary>
    /// The database-less defaults. Called by <see cref="DependencyInjection.AddAutomationApplication"/>.
    /// </summary>
    public static IServiceCollection AddIndentToPoDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Process tracking with nowhere to write. TryAdd so the real one wins when there is a
        // database: this is the fallback for an installation without one, where indent → PO still
        // converts and only its history is missing.
        services.TryAddScoped<IIndentPoTracker, NullProcessJobService>();
        services.TryAddScoped<IProcessJobService, NullProcessJobService>();

        // The per-indent-type automation settings, with nowhere to write. Same story: the real
        // repository (Infrastructure) wins when there is a database, and this keeps the mode-gate
        // and the config endpoints resolvable without one — both simply report "not enabled".
        services.TryAddScoped<IIndentPoAutomationConfigRepository, NullIndentPoAutomationConfigRepository>();

        // The master-switch check, shared by every conversion path and re-run before each indent.
        // Resolves whichever config repository is registered above.
        services.TryAddScoped<IPoAutomationGate, PoAutomationGate>();

        return services;
    }

    /// <summary>
    /// Process tracking with somewhere to write: the history of indent → purchase order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only when the database is <i>usable</i> rather than merely configured. A connection
    /// string naming a database the agent's login cannot open never recovers, and EF classes that
    /// SQL error as transient — so every tracked write would retry three times with backoff before
    /// failing, on every conversion, for as long as the process runs. The no-op fallback registered
    /// by <see cref="AddIndentToPoDefaults"/> is the right answer there: the conversion still runs
    /// at full speed and only its history is missing, which is what "tracking is best-effort" has
    /// always meant.
    /// </para>
    /// <para>
    /// It also keeps <see cref="IIndentPoTracker.IsEnabled"/> honest, so
    /// <c>POST /api/process-jobs</c> answers 503 saying tracking is off instead of taking the
    /// retries and failing.
    /// </para>
    /// <para>
    /// One instance behind both interfaces: the conversion writes through
    /// <see cref="IIndentPoTracker"/> while the endpoints read through
    /// <see cref="IProcessJobService"/>, and they must share the run in progress. Registered after
    /// the no-op, so this is what resolves.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddIndentToPoTracking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ProcessJobService>();
        services.AddScoped<IIndentPoTracker>(provider => provider.GetRequiredService<ProcessJobService>());
        services.AddScoped<IProcessJobService>(provider => provider.GetRequiredService<ProcessJobService>());

        return services;
    }
}
