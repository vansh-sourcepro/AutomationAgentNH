using System.ComponentModel.DataAnnotations;

namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// Fallbacks used only when a module has no row in AutomationConfig, or before that
/// row can be read. The database values always win once present.
/// </summary>
public sealed class DefaultsOptions
{
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 30;

    [Range(1, 1440)]
    public int ReconciliationIntervalMinutes { get; init; } = 5;

    [Range(1, 64)]
    public int ParallelWorkers { get; init; } = 4;

    [Range(0, 10)]
    public int MaxRetry { get; init; } = 3;
}
