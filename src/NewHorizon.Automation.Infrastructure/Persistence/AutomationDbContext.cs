using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Domain.Configuration;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Logging;

namespace NewHorizon.Automation.Infrastructure.Persistence;

/// <summary>
/// The automation database — and only the automation database. There is no DbSet, no view and no
/// raw query in this solution that reads or writes ERP tables; everything the ERP owns is reached
/// through <c>IErpClient</c> over HTTP.
/// </summary>
public sealed class AutomationDbContext : DbContext
{
    public AutomationDbContext(DbContextOptions<AutomationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<JobStep> JobSteps => Set<JobStep>();

    public DbSet<AutomationError> Errors => Set<AutomationError>();

    public DbSet<AutomationConfig> Configs => Set<AutomationConfig>();

    public DbSet<AutomationLog> Logs => Set<AutomationLog>();

    /// <summary>One trigger invocation. Kept even when it created no jobs at all.</summary>
    public DbSet<AutomationRun> Runs => Set<AutomationRun>();

    /// <summary>One authorised indent, however many times it has been attempted.</summary>
    public DbSet<IndentPoConversion> IndentPoConversions => Set<IndentPoConversion>();

    /// <summary>One vendor group's result: a purchase order, or the reason there is none.</summary>
    public DbSet<IndentPoOutcome> IndentPoOutcomes => Set<IndentPoOutcome>();

    /// <summary>One indent type's automation settings — run mode, daily schedule, target sites. Three rows.</summary>
    public DbSet<IndentPoAutomationConfig> IndentPoAutomationConfigs => Set<IndentPoAutomationConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AutomationDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
