using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Infrastructure.Flows.QuotationToSo.Configurations;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.QuotationToSo;

/// <summary>
/// EF Core repository for Quotation to Sales Order automation configuration.
/// Resilient: falls back to an in-memory default if the database table is not yet migrated.
/// </summary>
public sealed class QuotationSoAutomationConfigRepository : IQuotationSoAutomationConfigRepository
{
    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<QuotationSoAutomationConfigRepository> _logger;

    // Resilient fallback if table is not yet migrated in database
    private static QuotationSoAutomationConfig? _fallbackConfig;
    private static bool _schemaWarned;

    public QuotationSoAutomationConfigRepository(
        AutomationDbContext dbContext,
        IClock clock,
        ILogger<QuotationSoAutomationConfigRepository> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public async Task<QuotationSoAutomationConfig> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _dbContext.QuotationSoAutomationConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            if (stored is not null)
            {
                return stored;
            }

            // If not found in database, return default (will be seeded/saved on first update)
            return _fallbackConfig ??= QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
        }
        catch (Exception ex) when (IsSchemaMissing(ex))
        {
            LogMissingSchemaOnce();
            return _fallbackConfig ??= QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
        }
    }

    public async Task<QuotationSoAutomationConfig> UpsertAsync(
        QuotationSoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        try
        {
            var stored = await _dbContext.QuotationSoAutomationConfigs
                .FirstOrDefaultAsync(cancellationToken);

            if (stored is null)
            {
                stored = QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
                stored.Update(update, _clock.UtcNow, updatedBy);
                _dbContext.QuotationSoAutomationConfigs.Add(stored);
            }
            else
            {
                stored.Update(update, _clock.UtcNow, updatedBy);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _fallbackConfig = stored;
            return stored;
        }
        catch (Exception ex) when (IsSchemaMissing(ex))
        {
            LogMissingSchemaOnce();
            _fallbackConfig ??= QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
            _fallbackConfig.Update(update, _clock.UtcNow, updatedBy);
            return _fallbackConfig;
        }
    }

    public async Task<QuotationSoAutomationConfig> SetActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _dbContext.QuotationSoAutomationConfigs
                .FirstOrDefaultAsync(cancellationToken);

            if (stored is null)
            {
                stored = QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
                stored.SetActive(active, _clock.UtcNow, updatedBy);
                _dbContext.QuotationSoAutomationConfigs.Add(stored);
            }
            else
            {
                stored.SetActive(active, _clock.UtcNow, updatedBy);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _fallbackConfig = stored;
            return stored;
        }
        catch (Exception ex) when (IsSchemaMissing(ex))
        {
            LogMissingSchemaOnce();
            _fallbackConfig ??= QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
            _fallbackConfig.SetActive(active, _clock.UtcNow, updatedBy);
            return _fallbackConfig;
        }
    }

    public async Task SaveAsync(QuotationSoAutomationConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var entry = _dbContext.Entry(config);
            if (entry.State == EntityState.Detached)
            {
                _dbContext.QuotationSoAutomationConfigs.Update(config);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _fallbackConfig = config;
        }
        catch (Exception ex) when (IsSchemaMissing(ex))
        {
            LogMissingSchemaOnce();
            _fallbackConfig = config;
        }
    }

    private static bool IsSchemaMissing(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "SqlException"
                && (current.Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private void LogMissingSchemaOnce()
    {
        if (!_schemaWarned)
        {
            _schemaWarned = true;
            _logger.LogWarning("QuotationSoAutomationConfig table not yet created in database. Using in-memory configuration fallback.");
        }
    }
}
