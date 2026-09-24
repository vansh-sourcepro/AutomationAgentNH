using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Infrastructure.Flows.PoToGrn.Configurations;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.PoToGrn;

public sealed class PoGrnAutomationConfigRepository : IPoGrnAutomationConfigRepository
{
    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;

    public PoGrnAutomationConfigRepository(AutomationDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public bool IsEnabled => true;

    public async Task<PoGrnAutomationConfig> GetAsync(CancellationToken cancellationToken) =>
        await _dbContext.PoGrnAutomationConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Id == PoGrnAutomationConfigConfiguration.SingletonId, cancellationToken)
        // The migration seeds it; a defensive default keeps the read total if it is ever deleted by hand.
        ?? PoGrnAutomationConfig.CreateDefault(_clock.UtcNow);

    public async Task<PoGrnAutomationConfig> UpdateAsync(
        PoGrnAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var config = await LoadTrackedAsync(cancellationToken);
        config.Update(update, _clock.UtcNow, updatedBy);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Detached, like every other read here, so a later SaveAsync in the same scope can attach it.
        _dbContext.Entry(config).State = EntityState.Detached;

        return config;
    }

    public async Task SaveAsync(PoGrnAutomationConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Rows are read untracked, so a changed one is attached as modified — the single row, in place.
        _dbContext.PoGrnAutomationConfigs.Update(config);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.Entry(config).State = EntityState.Detached;
    }

    private async Task<PoGrnAutomationConfig> LoadTrackedAsync(CancellationToken cancellationToken)
    {
        var config = await _dbContext.PoGrnAutomationConfigs
            .FirstOrDefaultAsync(row => row.Id == PoGrnAutomationConfigConfiguration.SingletonId, cancellationToken);

        if (config is not null)
        {
            return config;
        }

        throw new InvalidOperationException(
            "The PoGrnAutomationConfig row is missing. Apply the AddPoToGrn migration (dotnet ef database update).");
    }
}

/// <summary>
/// Writes runs and per-PO results. Best-effort: a GRN that exists in the ERP is never turned into a
/// failure because its history could not be written, so every error is logged and swallowed.
/// </summary>
public sealed class PoGrnHistory : IPoGrnHistory
{
    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<PoGrnHistory> _logger;

    private PoGrnRun? _run;

    public PoGrnHistory(AutomationDbContext dbContext, IClock clock, ILogger<PoGrnHistory> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public Guid? RunId => _run?.Id;

    public Task StartRunAsync(StartGrnRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _run = PoGrnRun.Start(
            request.Trigger,
            request.TriggeredBy,
            request.TriggerReference,
            request.ReceiptMode,
            request.RequestedSites,
            _clock.UtcNow);

        return TryAsync("start the run", () =>
        {
            _dbContext.PoGrnRuns.Add(_run);
            return _dbContext.SaveChangesAsync(cancellationToken);
        });
    }

    public Task RecordAsync(PoGrnOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (_run is null)
        {
            return Task.CompletedTask;
        }

        var now = _clock.UtcNow;

        var receipt = outcome.Status switch
        {
            PoGrnReceiptStatus.Created => PoGrnReceipt.Created(
                _run.Id, outcome.Subject, outcome.GrnId ?? 0, outcome.GrnNumber!, outcome.LinesReceived,
                outcome.LinesSkipped, outcome.Reason, now),
            PoGrnReceiptStatus.Skipped => PoGrnReceipt.Skipped(
                _run.Id, outcome.Subject, outcome.LinesSkipped, outcome.Reason ?? "No reason given.", now),
            _ => PoGrnReceipt.Failed(_run.Id, outcome.Subject, outcome.Reason ?? "No reason given.", now),
        };

        _run.Count(outcome.Status);

        return TryAsync($"record PO {outcome.Subject.PoNumber}", () =>
        {
            _dbContext.PoGrnReceipts.Add(receipt);
            return _dbContext.SaveChangesAsync(cancellationToken);
        });
    }

    public Task CompleteRunAsync(CancellationToken cancellationToken)
    {
        if (_run is null)
        {
            return Task.CompletedTask;
        }

        _run.Complete(_clock.UtcNow);

        return TryAsync("complete the run", () => _dbContext.SaveChangesAsync(cancellationToken));
    }

    public Task FailRunAsync(string reason, CancellationToken cancellationToken)
    {
        if (_run is null)
        {
            return Task.CompletedTask;
        }

        _run.Fail(reason, _clock.UtcNow);

        return TryAsync("fail the run", () => _dbContext.SaveChangesAsync(cancellationToken));
    }

    private async Task TryAsync(string what, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not {What} in the PO → GRN history; the GRNs themselves are unaffected", what);

            // Drop whatever failed to save, so the next write does not retry it and fail too. The
            // run row stays attached so later counts still land if the database recovers.
            foreach (var entry in _dbContext.ChangeTracker.Entries<PoGrnReceipt>().ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }
}
