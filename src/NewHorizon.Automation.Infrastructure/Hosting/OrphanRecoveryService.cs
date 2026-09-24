using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Jobs;

namespace NewHorizon.Automation.Infrastructure.Hosting;

/// <summary>
/// Re-queues jobs left <c>Running</c> by a process that died.
/// </summary>
/// <remarks>
/// Without this, a killed agent strands its in-flight job: no worker will claim a Running row, and
/// the live-cycle index would then refuse every new cycle for ever. Re-queueing is safe because the
/// job resumes at the first operation that is not Completed, and each operation queries before it
/// creates.
/// </remarks>
public sealed class OrphanRecoveryService : BackgroundService
{
    /// <summary>
    /// How long a job may be Running before it is presumed abandoned. Generous on purpose: a cycle
    /// that loops many sites is legitimately long, and re-queueing a job that is still alive would
    /// have two workers on it.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanRecoveryService> _logger;

    public OrphanRecoveryService(IServiceScopeFactory scopeFactory, ILogger<OrphanRecoveryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Orphan sweep failed; the next sweep will try again");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var cutoff = clock.UtcNow - StaleAfter;

        var orphaned = await jobs.GetOrphanedRunningJobsAsync(cutoff, cancellationToken);

        if (orphaned.Count == 0)
        {
            return;
        }

        _logger.LogWarning("Recovering {Count} job(s) left running by a stopped agent", orphaned.Count);

        foreach (var jobId in orphaned)
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);

            if (job is null)
            {
                continue;
            }

            // Not RequeueAfterTransientFailure: the job was abandoned, not failing, so there is no
            // backoff to serve and its retry budget must not be charged for a crash.
            job.MarkResumable();
            await jobs.SaveAsync(job, cancellationToken);

            _logger.LogInformation("Job {JobId} re-queued; it resumes at its last checkpoint", jobId);
        }
    }
}
