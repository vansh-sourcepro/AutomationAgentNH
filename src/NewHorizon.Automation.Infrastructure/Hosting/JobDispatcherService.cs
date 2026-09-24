using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows;
using NewHorizon.Automation.Application.Workflows.Definitions;

namespace NewHorizon.Automation.Infrastructure.Hosting;

/// <summary>
/// Turns the Pending job set into work. Claims a batch, runs each job through the engine, repeats.
/// </summary>
/// <remarks>
/// The Pending set <em>is</em> the queue — no external broker on an on-premise server. Claiming is
/// atomic (UPDLOCK/READPAST in <see cref="IJobRepository.ClaimPendingJobsAsync"/>), so several
/// agents against one database skip each other's locked rows rather than double-running a job.
/// </remarks>
public sealed class JobDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobDispatcherService> _logger;

    public JobDispatcherService(IServiceScopeFactory scopeFactory, ILogger<JobDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job dispatcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var idleDelay = TimeSpan.FromSeconds(30);
            var claimed = 0;

            try
            {
                var (batchSize, pollSeconds) = await ReadLimitsAsync(stoppingToken);
                idleDelay = TimeSpan.FromSeconds(pollSeconds);

                claimed = await RunBatchAsync(batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatcher pass failed; retrying after the poll interval");
            }

            // A full batch means there is probably more waiting, so go straight round again rather
            // than sleeping through a backlog.
            if (claimed == 0)
            {
                await SafeDelayAsync(idleDelay, stoppingToken);
            }
        }

        _logger.LogInformation("Job dispatcher stopped");
    }

    private async Task<(int BatchSize, int PollSeconds)> ReadLimitsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var configs = scope.ServiceProvider.GetRequiredService<IAutomationConfigRepository>();
        var config = await configs.GetOrDefaultAsync(WorkflowNames.AutoShopCycle, cancellationToken);

        return (config.ParallelWorkers, config.PollIntervalSeconds);
    }

    private async Task<int> RunBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> jobIds;

        using (var scope = _scopeFactory.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            jobIds = await jobs.ClaimPendingJobsAsync(batchSize, cancellationToken);
        }

        if (jobIds.Count == 0)
        {
            return 0;
        }

        _logger.LogDebug("Claimed {Count} job(s)", jobIds.Count);

        // Each job gets its own scope, so one job's DbContext and its failures stay its own.
        await Parallel.ForEachAsync(
            jobIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = batchSize,
                CancellationToken = cancellationToken,
            },
            async (jobId, token) => await RunOneAsync(jobId, token));

        return jobIds.Count;
    }

    private async Task RunOneAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        try
        {
            var job = await jobs.GetAsync(jobId, cancellationToken);

            if (job is null)
            {
                // Claimed then deleted — nothing to run, and nothing to fix.
                _logger.LogWarning("Claimed job {JobId} no longer exists", jobId);
                return;
            }

            await engine.RunAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-job. The job stays Running and orphan recovery re-queues it, so the
            // work resumes at its last checkpoint instead of being lost or repeated.
            _logger.LogInformation("Job {JobId} interrupted by shutdown; it will be resumed", jobId);
        }
        catch (Exception ex)
        {
            // The engine records business and ERP failures itself. Reaching here means something
            // unexpected escaped it, and losing it silently would strand the job as Running.
            _logger.LogError(ex, "Unhandled failure running job {JobId}", jobId);
        }
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
