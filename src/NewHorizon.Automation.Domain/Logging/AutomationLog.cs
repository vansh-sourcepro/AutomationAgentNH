namespace NewHorizon.Automation.Domain.Logging;

/// <summary>
/// One ERP API call — the finest of the four granularity levels. High volume by design, and the
/// first thing the nightly retention purge trims.
/// </summary>
public sealed class AutomationLog
{
    private AutomationLog()
    {
        CorrelationId = string.Empty;
        Result = string.Empty;
    }

    private AutomationLog(
        Guid id,
        Guid jobId,
        Guid? stepId,
        string correlationId,
        string? module,
        string? apiEndpoint,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string result)
    {
        Id = id;
        JobId = jobId;
        StepId = stepId;
        CorrelationId = correlationId;
        Module = module;
        ApiEndpoint = apiEndpoint;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Result = result;
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    public Guid? StepId { get; private set; }

    public string CorrelationId { get; private set; }

    public string? Module { get; private set; }

    public string? ApiEndpoint { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset CompletedAtUtc { get; private set; }

    public long DurationMs { get; private set; }

    /// <summary>Short outcome token — "Success", "BusinessFailure", "Timeout", an HTTP status.</summary>
    public string Result { get; private set; }

    public static AutomationLog Record(
        Guid jobId,
        Guid? stepId,
        string correlationId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string result,
        string? module = null,
        string? apiEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);

        var log = new AutomationLog(
            Guid.NewGuid(),
            jobId,
            stepId,
            correlationId,
            module,
            apiEndpoint,
            startedAtUtc,
            completedAtUtc,
            result);

        log.DurationMs = (long)Math.Max(0, (completedAtUtc - startedAtUtc).TotalMilliseconds);

        return log;
    }
}
