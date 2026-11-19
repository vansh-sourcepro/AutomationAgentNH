using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// The fallback when there is no automation database. Every source reads back as an unsaved default
/// (no schedule time, no document numbers), and <see cref="IsEnabled"/> is false so the scheduler
/// stays unregistered. An upsert is refused: there is nowhere to keep it.
/// </summary>
public sealed class NullIssueToShopFloorAutomationConfigRepository : IIssueToShopFloorAutomationConfigRepository
{
    private static readonly IReadOnlyList<IssueToShopFloorSource> AllSources =
        [IssueToShopFloorSource.Sjo, IssueToShopFloorSource.WorkOrder, IssueToShopFloorSource.SalesOaf];

    private readonly IClock _clock;

    public NullIssueToShopFloorAutomationConfigRepository(IClock clock) => _clock = clock;

    public bool IsEnabled => false;

    public Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IssueToShopFloorAutomationConfig>>(
            [.. AllSources.Select(source => IssueToShopFloorAutomationConfig.CreateDefault(source, _clock.UtcNow))]);

    public Task<IssueToShopFloorAutomationConfig> GetAsync(IssueToShopFloorSource source, CancellationToken cancellationToken) =>
        Task.FromResult(IssueToShopFloorAutomationConfig.CreateDefault(source, _clock.UtcNow));

    public Task<IssueToShopFloorAutomationConfig> UpsertAsync(
        IssueToShopFloorSource source,
        IssueToShopFloorAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Issue to Shop Floor automation settings cannot be saved without an automation database. "
            + "Set AutomationAgent:Database:ConnectionString.");

    public Task SaveAsync(IssueToShopFloorAutomationConfig config, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> SetAllActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Issue to Shop Floor automation cannot be turned on or off without an automation database. "
            + "Set AutomationAgent:Database:ConnectionString.");
}
