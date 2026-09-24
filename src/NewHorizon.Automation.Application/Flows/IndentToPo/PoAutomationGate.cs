using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.Application.Flows.IndentToPo;

/// <summary>
/// The PO Automation master switch, checked wherever a conversion can start — the scheduler, the
/// screen's Run button, every external API path — <b>and</b> re-checked before every single indent
/// inside a running sweep. When the toggle is off (<see cref="IndentPoAutomationConfig.IsActive"/>
/// false on a targeted type's row) no authorised indent may be converted into a purchase order.
/// </summary>
/// <remarks>
/// <para>
/// The persisted <c>IsActive</c> is the source of truth. Every call reads it fresh (the repository's
/// <c>GetAsync</c> is an untracked query) — the value is never cached for the life of a run — so a
/// user turning PO Automation off mid-sweep is observed before the next indent is converted.
/// </para>
/// <para>
/// A no-op when there is no automation database: nothing persists the switch, and the agent has
/// always converted on request without one (the same carve-out <c>Startup_converts_nothing</c> and
/// <c>ProcessJobOrchestrator</c>'s <c>_tracker.IsEnabled</c> check rely on).
/// </para>
/// </remarks>
public interface IPoAutomationGate
{
    /// <summary>
    /// <c>null</c> when every one of <paramref name="kinds"/> is switched on (or there is no
    /// automation database); otherwise the refusal detail, naming the first type that is off.
    /// </summary>
    Task<string?> OffReasonAsync(IEnumerable<IndentKind> kinds, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class PoAutomationGate : IPoAutomationGate
{
    private readonly IIndentPoAutomationConfigRepository _configs;

    public PoAutomationGate(IIndentPoAutomationConfigRepository configs) =>
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));

    /// <inheritdoc />
    public async Task<string?> OffReasonAsync(IEnumerable<IndentKind> kinds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        if (!_configs.IsEnabled)
        {
            return null;
        }

        foreach (var kind in kinds.Distinct())
        {
            // A fresh read every time — not cached across a run — so a mid-sweep toggle-off stops
            // the next conversion.
            var config = await _configs.GetAsync(kind, cancellationToken);
            if (!config.IsActive)
            {
                return $"{kind} indent automation is turned off. "
                    + "Turn PO Automation on from the ERP screen to run it.";
            }
        }

        return null;
    }
}
