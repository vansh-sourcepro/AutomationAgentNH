using System.ComponentModel.DataAnnotations;

namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// Root of the single bootstrap configuration section ("AutomationAgent").
/// Holds only what the agent needs to start and connect. Per-tenant runtime behaviour
/// (Full/Partial mode, working hours, retry counts, parallelism) lives in the
/// AutomationConfig table and is changed through the API, never through this file.
/// </summary>
public sealed record AutomationAgentOptions
{
    public const string SectionName = "AutomationAgent";

    [Required]
    public DatabaseOptions Database { get; init; } = new();

    [Required]
    public ErpApiOptions ErpApi { get; init; } = new();

    [Required]
    public AgentHostOptions Host { get; init; } = new();

    [Required]
    public DefaultsOptions Defaults { get; init; } = new();

    [Required]
    public SerilogOptions Serilog { get; init; } = new();
}
