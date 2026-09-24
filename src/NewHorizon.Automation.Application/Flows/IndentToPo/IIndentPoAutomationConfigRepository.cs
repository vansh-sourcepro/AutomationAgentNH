using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// The three per-indent-type automation rows. Read fresh on every scheduler tick and every
/// management call — never cached — so a UI change takes effect from the next run.
/// </summary>
public interface IIndentPoAutomationConfigRepository
{
    /// <summary>
    /// False when there is no automation database. The scheduler never starts without one, and the
    /// API mode-gate does not enforce — an installation with no database keeps converting on request
    /// exactly as it did before this feature existed.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>All three rows, ordered by <see cref="IndentKind"/>. Missing rows are returned as unsaved defaults.</summary>
    Task<IReadOnlyList<IndentPoAutomationConfig>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>One type's row, or an unsaved default when it has never been configured.</summary>
    Task<IndentPoAutomationConfig> GetAsync(IndentKind indentKind, CancellationToken cancellationToken);

    /// <summary>Creates the row if absent, applies the partial update, and returns the saved row.</summary>
    Task<IndentPoAutomationConfig> UpsertAsync(
        IndentKind indentKind,
        IndentPoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken);

    /// <summary>Persists changes made to a row this repository handed out (e.g. after the scheduler marks a run).</summary>
    Task SaveAsync(IndentPoAutomationConfig config, CancellationToken cancellationToken);

    /// <summary>
    /// Flips the master on/off switch (<see cref="IndentPoAutomationConfig.IsActive"/>) on all three
    /// rows in one transaction — the PO Automation screen's toggle. Returns the saved rows.
    /// </summary>
    Task<IReadOnlyList<IndentPoAutomationConfig>> SetAllActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken);
}
