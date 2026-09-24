using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.IndentToPo;

/// <summary>
/// Reads and writes the three per-indent-type automation rows. Nothing here caches: the scheduler
/// asks for the settings on every tick so a UI change applies from the next run.
/// </summary>
public sealed class IndentPoAutomationConfigRepository : IIndentPoAutomationConfigRepository
{
    private static readonly IReadOnlyList<IndentKind> AllKinds =
        [IndentKind.Regular, IndentKind.Capital, IndentKind.Service];

    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;

    public IndentPoAutomationConfigRepository(AutomationDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public bool IsEnabled => true;

    public async Task<IReadOnlyList<IndentPoAutomationConfig>> GetAllAsync(CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IndentPoAutomationConfigs
            .AsNoTracking()
            .ToDictionaryAsync(config => config.IndentKind, cancellationToken);

        // The migration seeds all three, but a defensive fill keeps the read total even if a row
        // is ever removed by hand.
        return [.. AllKinds.Select(kind =>
            stored.TryGetValue(kind, out var config)
                ? config
                : IndentPoAutomationConfig.CreateDefault(kind, _clock.UtcNow))];
    }

    public async Task<IndentPoAutomationConfig> GetAsync(IndentKind indentKind, CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IndentPoAutomationConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.IndentKind == indentKind, cancellationToken);

        return stored ?? IndentPoAutomationConfig.CreateDefault(indentKind, _clock.UtcNow);
    }

    public async Task<IndentPoAutomationConfig> UpsertAsync(
        IndentKind indentKind,
        IndentPoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var config = await _dbContext.IndentPoAutomationConfigs
            .FirstOrDefaultAsync(row => row.IndentKind == indentKind, cancellationToken);

        if (config is null)
        {
            config = IndentPoAutomationConfig.CreateDefault(indentKind, _clock.UtcNow);
            _dbContext.IndentPoAutomationConfigs.Add(config);
        }

        config.Update(update, _clock.UtcNow, updatedBy);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return config;
    }

    public async Task<IReadOnlyList<IndentPoAutomationConfig>> SetAllActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IndentPoAutomationConfigs
            .ToDictionaryAsync(row => row.IndentKind, cancellationToken);

        var rows = new List<IndentPoAutomationConfig>(AllKinds.Count);

        foreach (var kind in AllKinds)
        {
            if (!stored.TryGetValue(kind, out var config))
            {
                config = IndentPoAutomationConfig.CreateDefault(kind, _clock.UtcNow);
                _dbContext.IndentPoAutomationConfigs.Add(config);
            }

            config.Update(new IndentPoAutomationConfigUpdate { IsActive = active }, _clock.UtcNow, updatedBy);
            rows.Add(config);
        }

        // One SaveChanges for all three: the toggle is on or it is off, never half-applied.
        await _dbContext.SaveChangesAsync(cancellationToken);

        return rows;
    }

    public async Task SaveAsync(IndentPoAutomationConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        // The scheduler hands back a row it read tracked in the same scope; attach covers the case
        // where it did not.
        if (_dbContext.Entry(config).State == EntityState.Detached)
        {
            _dbContext.IndentPoAutomationConfigs.Update(config);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
