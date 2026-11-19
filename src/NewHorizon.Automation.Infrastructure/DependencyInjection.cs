using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Notifications;
using NewHorizon.Automation.Infrastructure.Flows.IndentToPo;
using NewHorizon.Automation.Infrastructure.Flows.PoToGrn;
using NewHorizon.Automation.Infrastructure.Hosting;
using NewHorizon.Automation.Infrastructure.Notifications;
using NewHorizon.Automation.Infrastructure.Persistence;
using NewHorizon.Automation.Infrastructure.Time;

namespace NewHorizon.Automation.Infrastructure;

/// <summary>
/// Wires the automation database and the adapters that implement the Application ports.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddAutomationInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AutomationDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AutomationDbContext).Assembly.FullName);

                // Transient SQL faults are the database's own business; workflow-level retry
                // handles anything that survives this.
                sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
            }));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IAutomationConfigRepository, AutomationConfigRepository>();

        // Each automation flow's repositories. A new flow adds its own line here.
        services.AddIndentToPoPersistence();
        services.AddPoToGrnPersistence();

        // Log-based until the client confirms the real channel (§18). TryAdd so a host that
        // registers a real notifier keeps it.
        services.TryAddSingleton<INotificationService, LogNotificationService>();

        services.AddHealthChecks()
            .AddCheck<AutomationDatabaseHealthCheck>("database", tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Starts the three services that actually make the agent do work: the timer that begins a
    /// cycle, the dispatcher that runs claimed jobs, and the sweep that recovers jobs abandoned by
    /// a stopped process.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="AddAutomationInfrastructure"/>. The integration tests
    /// host the same application to exercise its endpoints, and a live timer there would enqueue
    /// and run real cycles against whatever ERP the test host is pointed at.
    /// </remarks>
    public static IServiceCollection AddAutomationHostedServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<CycleSchedulerService>();
        services.AddHostedService<JobDispatcherService>();
        services.AddHostedService<OrphanRecoveryService>();

        return services;
    }
}
