using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Infrastructure.Flows.QuotationToSo.Configurations;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.Infrastructure.Flows.QuotationToSo;

/// <summary>
/// EF Core repository for Quotation to Sales Order automation configuration.
/// Stores configuration directly in the existing <c>IndentPoAutomationConfig</c> database table
/// under <c>IndentKind.Quotation</c>.
/// </summary>
public sealed class QuotationSoAutomationConfigRepository : IQuotationSoAutomationConfigRepository
{
    private readonly AutomationDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<QuotationSoAutomationConfigRepository> _logger;

    private static string? _lastRunMessage;

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
        var stored = await _dbContext.IndentPoAutomationConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IndentKind == IndentKind.Quotation, cancellationToken);

        if (stored is not null)
        {
            return MapToQuotationConfig(stored);
        }

        return QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
    }

    public async Task<QuotationSoAutomationConfig> UpsertAsync(
        QuotationSoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        var stored = await _dbContext.IndentPoAutomationConfigs
            .FirstOrDefaultAsync(c => c.IndentKind == IndentKind.Quotation, cancellationToken);

        if (stored is null)
        {
            stored = IndentPoAutomationConfig.CreateQuotationDefault(
                _clock.UtcNow,
                QuotationSoAutomationConfigConfiguration.DefaultConfigId);
            _dbContext.IndentPoAutomationConfigs.Add(stored);
        }

        var (existingSite, existingCustomer) = ParseQuotationSites(stored.Sites);
        var newSite = update.ClearSiteCode ? null : (update.SiteCode ?? existingSite);
        var newCustomer = update.ClearCustomerCode ? null : (update.CustomerCode ?? existingCustomer);
        var formattedSites = FormatQuotationSites(newSite, newCustomer);

        var poMode = update.RunMode.HasValue ? ToPoRunMode(update.RunMode.Value) : (PoAutomationRunMode?)null;

        stored.UpdateFromQuotation(
            runMode: poMode,
            scheduleTime: update.ScheduleTime,
            clearScheduleTime: update.ClearScheduleTime,
            quotationNumbers: update.QuotationNumbers,
            clearQuotationNumbers: update.ClearQuotationNumbers,
            sites: formattedSites,
            isActive: update.IsActive,
            dryRun: update.DryRun,
            nowUtc: _clock.UtcNow,
            updatedBy: updatedBy);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return MapToQuotationConfig(stored);
    }

    public async Task<QuotationSoAutomationConfig> SetActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        var stored = await _dbContext.IndentPoAutomationConfigs
            .FirstOrDefaultAsync(c => c.IndentKind == IndentKind.Quotation, cancellationToken);

        if (stored is null)
        {
            stored = IndentPoAutomationConfig.CreateQuotationDefault(
                _clock.UtcNow,
                QuotationSoAutomationConfigConfiguration.DefaultConfigId);
            _dbContext.IndentPoAutomationConfigs.Add(stored);
        }

        stored.SetActiveForQuotation(active, _clock.UtcNow, updatedBy);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return MapToQuotationConfig(stored);
    }

    public async Task SaveAsync(QuotationSoAutomationConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);

        _lastRunMessage = config.LastRunMessage;

        var stored = await _dbContext.IndentPoAutomationConfigs
            .FirstOrDefaultAsync(c => c.IndentKind == IndentKind.Quotation, cancellationToken);

        if (stored is null)
        {
            stored = IndentPoAutomationConfig.CreateQuotationDefault(
                _clock.UtcNow,
                config.Id != Guid.Empty ? config.Id : QuotationSoAutomationConfigConfiguration.DefaultConfigId);
            _dbContext.IndentPoAutomationConfigs.Add(stored);
        }

        var formattedSites = FormatQuotationSites(config.SiteCode, config.CustomerCode);
        var poMode = ToPoRunMode(config.RunMode);

        stored.UpdateFromQuotation(
            runMode: poMode,
            scheduleTime: config.ScheduleTime,
            clearScheduleTime: config.ScheduleTime is null,
            quotationNumbers: config.QuotationNumbers,
            clearQuotationNumbers: string.IsNullOrWhiteSpace(config.QuotationNumbers),
            sites: formattedSites,
            isActive: config.IsActive,
            dryRun: config.DryRun,
            nowUtc: config.UpdatedAtUtc != default ? config.UpdatedAtUtc : _clock.UtcNow,
            updatedBy: config.UpdatedBy);

        if (config.LastScheduledRunDate.HasValue || config.LastTriggeredAtUtc.HasValue)
        {
            stored.RecordQuotationRunStarted(config.LastTriggeredAtUtc ?? _clock.UtcNow, config.LastScheduledRunDate);
        }

        if (!string.IsNullOrWhiteSpace(config.LastRunStatus))
        {
            stored.RecordQuotationRunCompleted(config.LastRunStatus, config.LastTriggeredAtUtc ?? _clock.UtcNow);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private QuotationSoAutomationConfig MapToQuotationConfig(IndentPoAutomationConfig row)
    {
        var (siteCode, customerCode) = ParseQuotationSites(row.Sites);

        return QuotationSoAutomationConfig.FromSnapshot(
            id: row.Id,
            isActive: row.IsActive,
            runMode: ToSoRunMode(row.RunMode),
            scheduleTime: row.ScheduleTime,
            quotationNumbers: row.IndentNumbers,
            customerCode: customerCode,
            siteCode: siteCode,
            dryRun: row.DryRun,
            lastScheduledRunDate: row.LastScheduledRunDate,
            lastTriggeredAtUtc: row.LastTriggeredAtUtc,
            lastRunStatus: row.LastRunStatus,
            lastRunMessage: _lastRunMessage,
            updatedAtUtc: row.UpdatedAtUtc,
            updatedBy: row.UpdatedBy);
    }

    private static (string? SiteCode, string? CustomerCode) ParseQuotationSites(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var site = root.TryGetProperty("site", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                var cust = root.TryGetProperty("customer", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return (site, cust);
            }
            catch
            {
                // Fallback to raw string if JSON parsing fails
            }
        }

        return (trimmed, null);
    }

    private static string? FormatQuotationSites(string? siteCode, string? customerCode)
    {
        var cleanSite = string.IsNullOrWhiteSpace(siteCode) ? null : siteCode.Trim();
        var cleanCustomer = string.IsNullOrWhiteSpace(customerCode) ? null : customerCode.Trim();

        if (cleanSite is null && cleanCustomer is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(new
        {
            site = cleanSite,
            customer = cleanCustomer,
        });
    }

    private static PoAutomationRunMode ToPoRunMode(SoAutomationRunMode mode) => mode switch
    {
        SoAutomationRunMode.Disabled => PoAutomationRunMode.Disabled,
        SoAutomationRunMode.Api => PoAutomationRunMode.Api,
        SoAutomationRunMode.Timer => PoAutomationRunMode.Timer,
        SoAutomationRunMode.Both => PoAutomationRunMode.Both,
        _ => PoAutomationRunMode.Both,
    };

    private static SoAutomationRunMode ToSoRunMode(PoAutomationRunMode mode) => mode switch
    {
        PoAutomationRunMode.Disabled => SoAutomationRunMode.Disabled,
        PoAutomationRunMode.Api => SoAutomationRunMode.Api,
        PoAutomationRunMode.Timer => SoAutomationRunMode.Timer,
        PoAutomationRunMode.Both => SoAutomationRunMode.Both,
        _ => SoAutomationRunMode.Both,
    };
}
