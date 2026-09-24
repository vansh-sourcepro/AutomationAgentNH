using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Domain.Configuration;

namespace NewHorizon.Automation.Infrastructure.Persistence;

/// <summary>
/// Reads and writes the per-module settings. Nothing here caches: the engine asks for the
/// configuration at the start of every job so a UI change applies from the next run.
/// </summary>
public sealed class AutomationConfigRepository : IAutomationConfigRepository
{
    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;

    public AutomationConfigRepository(AutomationDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<AutomationConfig> GetOrDefaultAsync(string module, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        var stored = await FindAsync(module, tracked: false, cancellationToken);

        // An unconfigured module still gets sane behaviour rather than an exception; the row is
        // created the first time an administrator saves settings for it.
        return stored ?? AutomationConfig.CreateDefault(module, _clock.UtcNow);
    }

    public async Task<IReadOnlyList<AutomationConfig>> ListAsync(CancellationToken cancellationToken) =>
        await _dbContext.Configs
            .AsNoTracking()
            .OrderBy(config => config.Module)
            .ToListAsync(cancellationToken);

    public async Task<AutomationConfig> UpsertAsync(
        string module,
        AutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(update);

        var config = await FindAsync(module, tracked: true, cancellationToken);

        if (config is null)
        {
            config = AutomationConfig.CreateDefault(module, _clock.UtcNow);
            _dbContext.Configs.Add(config);
        }

        config.Update(update, _clock.UtcNow, updatedBy);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return config;
    }

    private Task<AutomationConfig?> FindAsync(string module, bool tracked, CancellationToken cancellationToken)
    {
        var configs = tracked ? _dbContext.Configs : _dbContext.Configs.AsNoTracking();

        return configs.FirstOrDefaultAsync(config => config.Module == module, cancellationToken);
    }
}
