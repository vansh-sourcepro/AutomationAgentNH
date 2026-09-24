using Serilog;
using Serilog.Events;

namespace NewHorizon.Automation.Worker.Logging;

/// <summary>
/// Configures Serilog from the single bootstrap section. The level lives at
/// <c>AutomationAgent:Serilog:MinimumLevel</c> rather than Serilog's own configuration schema,
/// so operators only ever edit one section of one file.
/// </summary>
public static class SerilogConfigurator
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration Configure(LoggerConfiguration configuration, IConfiguration appConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(appConfiguration);

        var configuredLevel = appConfiguration["AutomationAgent:Serilog:MinimumLevel"];
        var minimumLevel = ParseLevel(configuredLevel);

        return configuration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "NewHorizon.AutomationAgent")
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: Path.Combine(AppContext.BaseDirectory, "logs", "automation-agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                shared: true,
                outputTemplate: OutputTemplate);
    }

    /// <summary>
    /// Falls back to Information for a missing or unrecognised value: a typo in the level must
    /// never leave the service without logs.
    /// </summary>
    public static LogEventLevel ParseLevel(string? configuredLevel) =>
        Enum.TryParse<LogEventLevel>(configuredLevel, ignoreCase: true, out var level)
            ? level
            : LogEventLevel.Information;
}
