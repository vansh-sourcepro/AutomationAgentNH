using System.ComponentModel.DataAnnotations;

namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// Minimum log level for the agent, kept inside the single bootstrap section so operators have
/// one file to edit. Accepted values: Verbose, Debug, Information, Warning, Error, Fatal.
/// </summary>
public sealed class SerilogOptions
{
    [Required(AllowEmptyStrings = false)]
    public string MinimumLevel { get; init; } = "Information";
}
