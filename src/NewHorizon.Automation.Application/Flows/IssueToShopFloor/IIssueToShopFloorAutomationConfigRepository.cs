using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// The three per-source Issue to Shop Floor automation rows: Sjo, WorkOrder, SalesOaf.
/// Read fresh on every scheduler tick and every management call — never cached — so a UI
/// change takes effect from the next run.
/// </summary>
public interface IIssueToShopFloorAutomationConfigRepository
{
    /// <summary>
    /// False when there is no automation database. The scheduler never starts without one, and the
    /// API mode-gate does not enforce — an installation with no database keeps converting on request
    /// without gating.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>All three rows, ordered by <see cref="IssueToShopFloorSource"/>. Missing rows are returned as unsaved defaults.</summary>
    Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>One source's row, or an unsaved default when it has never been configured.</summary>
    Task<IssueToShopFloorAutomationConfig> GetAsync(IssueToShopFloorSource source, CancellationToken cancellationToken);

    /// <summary>Creates the row if absent, applies the partial update, and returns the saved row.</summary>
    Task<IssueToShopFloorAutomationConfig> UpsertAsync(
        IssueToShopFloorSource source,
        IssueToShopFloorAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken);

    /// <summary>Persists changes made to a row this repository handed out (e.g. after the scheduler marks a run).</summary>
    Task SaveAsync(IssueToShopFloorAutomationConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// Flips the master on/off switch (<see cref="IssueToShopFloorAutomationConfig.IsActive"/>) on all three
    /// rows in one transaction. Returns the saved rows.
    /// </summary>
    Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> SetAllActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken);
}
