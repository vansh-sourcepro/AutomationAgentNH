namespace NewHorizon.Automation.Application.Workflows;

/// <summary>
/// How long the engine waits before re-attempting an operation. Distinct from the Polly pipeline,
/// which retries a single HTTP call within one attempt: this governs re-queueing the whole job,
/// which survives a process restart.
/// </summary>
public static class RetryPolicy
{
    private const int MaxBackoffSeconds = 300;

    /// <summary>How often to re-check a transition the ERP is performing on its own.</summary>
    public static readonly TimeSpan ErpAutomationPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Exponential backoff with jitter: 2s, 4s, 8s … capped at five minutes. Jitter keeps several
    /// jobs that failed on the same ERP outage from all returning at the same instant and
    /// knocking it over again.
    /// </summary>
    public static TimeSpan BackoffFor(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        var exponent = Math.Min(attempt, 10);
        var seconds = Math.Min(Math.Pow(2, exponent + 1), MaxBackoffSeconds);

        // Up to 25% jitter, added rather than subtracted so a delay is never shorter than intended.
        var jitter = Random.Shared.NextDouble() * seconds * 0.25;

        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
