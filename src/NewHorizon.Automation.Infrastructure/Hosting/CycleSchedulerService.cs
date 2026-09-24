using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;

namespace NewHorizon.Automation.Infrastructure.Hosting;

/// <summary>
/// The cycle's only trigger: a timer.
/// </summary>
/// <remarks>
/// The design doc's §6 push-on-save trigger does not apply to this workflow. Automation begins
/// <em>after</em> OAF creation, which a person authorises inside the ERP, so there is no ERP-side
/// event to push. A tick that finds a cycle already running simply does nothing — that is the
/// normal case, not an error.
/// </remarks>
public sealed class CycleSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CycleSchedulerService> _logger;

    public CycleSchedulerService(IServiceScopeFactory scopeFactory, ILogger<CycleSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cycle scheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(5);

            try
            {
                using var scope = _scopeFactory.CreateScope();

                var configs = scope.ServiceProvider.GetRequiredService<IAutomationConfigRepository>();
                var config = await configs.GetOrDefaultAsync(WorkflowNames.AutoShopCycle, stoppingToken);

                interval = TimeSpan.FromMinutes(config.ReconcileIntervalMinutes);

                var enqueue = scope.ServiceProvider.GetRequiredService<ICycleEnqueueService>();

                var outcome = await enqueue.EnqueueAsync(
                    WorkflowNames.AutoShopCycle,
                    respectSchedule: true,
                    priority: 0,
                    stoppingToken);

                if (!outcome.Started)
                {
                    _logger.LogDebug("Scheduler tick did not start a cycle: {Reason}", outcome.Reason);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed tick must not kill the scheduler: the next one may well succeed, and a
                // dead timer would silently stop all automation.
                _logger.LogError(ex, "Scheduler tick failed; the next tick will try again");
            }

            await SafeDelayAsync(interval, stoppingToken);
        }

        _logger.LogInformation("Cycle scheduler stopped");
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }
    }
}
