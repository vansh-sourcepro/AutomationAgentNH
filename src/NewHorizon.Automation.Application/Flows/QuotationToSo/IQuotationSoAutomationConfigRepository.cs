using NewHorizon.Automation.Domain.Flows.QuotationToSo;

namespace NewHorizon.Automation.Application.Flows.QuotationToSo;

/// <summary>
/// Repository interface for Quotation to Sales Order automation configuration.
/// </summary>
public interface IQuotationSoAutomationConfigRepository
{
    /// <summary>
    /// Indicates whether a persistent automation database is configured and available.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Retrieves the current Quotation to SO automation configuration (or default if not yet stored).
    /// </summary>
    Task<QuotationSoAutomationConfig> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Updates configuration with provided settings.
    /// </summary>
    Task<QuotationSoAutomationConfig> UpsertAsync(
        QuotationSoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Flips the master on/off toggle.
    /// </summary>
    Task<QuotationSoAutomationConfig> SetActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists changes to an existing configuration entity (e.g. after a run records its outcome).
    /// </summary>
    Task SaveAsync(QuotationSoAutomationConfig config, CancellationToken cancellationToken);
}
