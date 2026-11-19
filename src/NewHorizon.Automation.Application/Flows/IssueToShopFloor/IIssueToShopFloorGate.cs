using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// The Issue to Shop Floor automation master switch, checked before conversions start.
/// When the toggle is off (<see cref="IssueToShopFloorAutomationConfig.IsActive"/> false),
/// automated conversions for the source are refused.
/// </summary>
public interface IIssueToShopFloorGate
{
    /// <summary>
    /// <c>null</c> when every one of <paramref name="sources"/> is switched on (or there is no
    /// automation database); otherwise the refusal detail, naming the first source that is off.
    /// </summary>
    Task<string?> OffReasonAsync(IEnumerable<IssueToShopFloorSource> sources, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class IssueToShopFloorGate : IIssueToShopFloorGate
{
    private readonly IIssueToShopFloorAutomationConfigRepository _configs;

    public IssueToShopFloorGate(IIssueToShopFloorAutomationConfigRepository configs) =>
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));

    /// <inheritdoc />
    public async Task<string?> OffReasonAsync(IEnumerable<IssueToShopFloorSource> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (!_configs.IsEnabled)
        {
            return null;
        }

        foreach (var source in sources.Distinct())
        {
            var config = await _configs.GetAsync(source, cancellationToken);
            if (!config.IsActive)
            {
                return $"{source} automation is turned off. "
                    + "Turn Issue to Shop Floor automation on from the ERP screen to run it.";
            }
        }

        return null;
    }
}
