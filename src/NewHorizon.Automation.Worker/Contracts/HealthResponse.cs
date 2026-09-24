namespace NewHorizon.Automation.Worker.Contracts;

/// <summary>
/// Response of <c>GET /api/automation/health</c>. <paramref name="Checks"/> carries one entry per
/// dependency; database and ERP reachability are wired in later phases and report
/// <c>NotConfigured</c> until then.
/// </summary>
public sealed record HealthResponse(
    string Status,
    string Version,
    DateTimeOffset StartedAtUtc,
    double UptimeSeconds,
    IReadOnlyDictionary<string, string> Checks);
