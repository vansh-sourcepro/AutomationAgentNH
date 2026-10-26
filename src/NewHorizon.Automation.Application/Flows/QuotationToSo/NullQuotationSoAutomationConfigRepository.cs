using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;

namespace NewHorizon.Automation.Application.Flows.QuotationToSo;

/// <summary>
/// In-memory fallback repository for Quotation to SO automation configuration.
/// Used when the agent has no SQL database configured or before database migrations are run.
/// </summary>
public sealed class NullQuotationSoAutomationConfigRepository : IQuotationSoAutomationConfigRepository
{
    private readonly IClock _clock;
    private QuotationSoAutomationConfig _current;

    public NullQuotationSoAutomationConfigRepository(IClock clock)
    {
        _clock = clock;
        _current = QuotationSoAutomationConfig.CreateDefault(_clock.UtcNow);
    }

    public bool IsEnabled => true;

    public Task<QuotationSoAutomationConfig> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_current);

    public Task<QuotationSoAutomationConfig> UpsertAsync(
        QuotationSoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        _current.Update(update, _clock.UtcNow, updatedBy);
        return Task.FromResult(_current);
    }

    public Task<QuotationSoAutomationConfig> SetActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        _current.SetActive(active, _clock.UtcNow, updatedBy);
        return Task.FromResult(_current);
    }

    public Task SaveAsync(QuotationSoAutomationConfig config, CancellationToken cancellationToken)
    {
        _current = config;
        return Task.CompletedTask;
    }
}
