using NewHorizon.Automation.Domain.Flows.PoToGrn;

namespace NewHorizon.Automation.Application.Flows.PoToGrn;

/// <summary>The single PO → GRN settings row.</summary>
public interface IPoGrnAutomationConfigRepository
{
    /// <summary>False on an installation with no automation database: nothing can be saved.</summary>
    bool IsEnabled { get; }

    /// <summary>Always a fresh read — never cached across a run.</summary>
    Task<PoGrnAutomationConfig> GetAsync(CancellationToken cancellationToken);

    Task<PoGrnAutomationConfig> UpdateAsync(
        PoGrnAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken);

    /// <summary>Persists bookkeeping changes (last run, scheduler slot) made on a loaded row.</summary>
    Task SaveAsync(PoGrnAutomationConfig config, CancellationToken cancellationToken);
}

/// <summary>
/// The database-less stand-in: an inert, switched-off row that cannot be saved. With no database
/// there is no toggle to turn on, so PO → GRN never runs — unlike Indent → PO, this flow has no
/// history of working without one.
/// </summary>
public sealed class NullPoGrnAutomationConfigRepository : IPoGrnAutomationConfigRepository
{
    public bool IsEnabled => false;

    public Task<PoGrnAutomationConfig> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(PoGrnAutomationConfig.CreateDefault(DateTimeOffset.UtcNow));

    public Task<PoGrnAutomationConfig> UpdateAsync(
        PoGrnAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("PO → GRN settings cannot be saved without an automation database.");

    public Task SaveAsync(PoGrnAutomationConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
}
