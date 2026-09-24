using System.Reflection;

namespace NewHorizon.Automation.Worker.Diagnostics;

/// <summary>
/// Process-level facts the health endpoint reports. Registered as a singleton rather than held
/// in static state so tests can substitute it.
/// </summary>
public sealed class AgentRuntimeInfo
{
    public AgentRuntimeInfo(DateTimeOffset startedAtUtc)
    {
        StartedAtUtc = startedAtUtc;
        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
    }

    public DateTimeOffset StartedAtUtc { get; }

    public string Version { get; }

    public TimeSpan UptimeAt(DateTimeOffset nowUtc) => nowUtc - StartedAtUtc;
}
