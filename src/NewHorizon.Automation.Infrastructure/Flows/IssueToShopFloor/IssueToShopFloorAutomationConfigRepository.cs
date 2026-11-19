using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.IssueToShopFloor;

/// <summary>
/// Reads and writes the three per-source Issue to Shop Floor automation rows. Nothing here caches: the scheduler
/// asks for the settings on every tick so a UI change applies from the next run.
/// </summary>
public sealed class IssueToShopFloorAutomationConfigRepository : IIssueToShopFloorAutomationConfigRepository
{
    private static readonly IReadOnlyList<IssueToShopFloorSource> AllSources =
        [IssueToShopFloorSource.Sjo, IssueToShopFloorSource.WorkOrder, IssueToShopFloorSource.SalesOaf];

    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;

    public IssueToShopFloorAutomationConfigRepository(AutomationDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public bool IsEnabled => true;

    public async Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> GetAllAsync(CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IssueToShopFloorAutomationConfigs
            .AsNoTracking()
            .ToDictionaryAsync(config => config.Source, cancellationToken);

        return [.. AllSources.Select(source =>
            stored.TryGetValue(source, out var config)
                ? config
                : IssueToShopFloorAutomationConfig.CreateDefault(source, _clock.UtcNow))];
    }

    public async Task<IssueToShopFloorAutomationConfig> GetAsync(IssueToShopFloorSource source, CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IssueToShopFloorAutomationConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(config => config.Source == source, cancellationToken);

        return stored ?? IssueToShopFloorAutomationConfig.CreateDefault(source, _clock.UtcNow);
    }

    public async Task<IssueToShopFloorAutomationConfig> UpsertAsync(
        IssueToShopFloorSource source,
        IssueToShopFloorAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var config = await _dbContext.IssueToShopFloorAutomationConfigs
            .FirstOrDefaultAsync(row => row.Source == source, cancellationToken);

        if (config is null)
        {
            config = IssueToShopFloorAutomationConfig.CreateDefault(source, _clock.UtcNow);
            _dbContext.IssueToShopFloorAutomationConfigs.Add(config);
        }

        config.Update(update, _clock.UtcNow, updatedBy);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return config;
    }

    public async Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> SetAllActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IssueToShopFloorAutomationConfigs
            .ToDictionaryAsync(row => row.Source, cancellationToken);

        var rows = new List<IssueToShopFloorAutomationConfig>(AllSources.Count);

        foreach (var source in AllSources)
        {
            if (!stored.TryGetValue(source, out var config))
            {
                config = IssueToShopFloorAutomationConfig.CreateDefault(source, _clock.UtcNow);
                _dbContext.IssueToShopFloorAutomationConfigs.Add(config);
            }

            config.Update(new IssueToShopFloorAutomationConfigUpdate { IsActive = active }, _clock.UtcNow, updatedBy);
            rows.Add(config);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return rows;
    }

    public async Task SaveAsync(IssueToShopFloorAutomationConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_dbContext.Entry(config).State == EntityState.Detached)
        {
            _dbContext.IssueToShopFloorAutomationConfigs.Update(config);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
