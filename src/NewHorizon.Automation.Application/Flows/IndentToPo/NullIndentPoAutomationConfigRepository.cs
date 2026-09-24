using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// The fallback when there is no automation database. Every type reads back as an unsaved default
/// (no schedule time, no indent numbers), and <see cref="IsEnabled"/> is false so the scheduler
/// stays unregistered. An upsert is refused: there is nowhere to keep it.
/// </summary>
public sealed class NullIndentPoAutomationConfigRepository : IIndentPoAutomationConfigRepository
{
    private static readonly IReadOnlyList<IndentKind> AllKinds =
        [IndentKind.Regular, IndentKind.Capital, IndentKind.Service];

    private readonly IClock _clock;

    public NullIndentPoAutomationConfigRepository(IClock clock) => _clock = clock;

    public bool IsEnabled => false;

    public Task<IReadOnlyList<IndentPoAutomationConfig>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IndentPoAutomationConfig>>(
            [.. AllKinds.Select(kind => IndentPoAutomationConfig.CreateDefault(kind, _clock.UtcNow))]);

    public Task<IndentPoAutomationConfig> GetAsync(IndentKind indentKind, CancellationToken cancellationToken) =>
        Task.FromResult(IndentPoAutomationConfig.CreateDefault(indentKind, _clock.UtcNow));

    public Task<IndentPoAutomationConfig> UpsertAsync(
        IndentKind indentKind,
        IndentPoAutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Automation settings cannot be saved without an automation database. "
            + "Set AutomationAgent:Database:ConnectionString.");

    public Task SaveAsync(IndentPoAutomationConfig config, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<IndentPoAutomationConfig>> SetAllActiveAsync(
        bool active,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "PO Automation cannot be turned on or off without an automation database. "
            + "Set AutomationAgent:Database:ConnectionString.");
}
