using NewHorizon.Automation.Domain.Configuration;

namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// Per-module runtime settings. Read fresh at the start of each job — never cached across jobs —
/// so a UI change takes effect from the next run without a redeploy.
/// </summary>
public interface IAutomationConfigRepository
{
    /// <summary>Returns the stored row, or an unsaved default when the module has never been configured.</summary>
    Task<AutomationConfig> GetOrDefaultAsync(string module, CancellationToken cancellationToken);

    Task<IReadOnlyList<AutomationConfig>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates the row if absent, then applies the partial update.</summary>
    Task<AutomationConfig> UpsertAsync(
        string module,
        AutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken);
}
