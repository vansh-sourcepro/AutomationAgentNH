namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// Lifecycle of one workflow run. Persisted as a string so the filtered unique index on
/// IdempotencyKey (WHERE Status &lt;&gt; 'Cancelled') stays readable in SQL.
/// </summary>
public enum JobStatus
{
    /// <summary>Enqueued and waiting to be claimed. This set is the queue.</summary>
    Pending = 0,

    /// <summary>Claimed by a worker and executing.</summary>
    Running = 1,

    /// <summary>Paused at a Partial-mode gate; needs a business approval to continue.</summary>
    AwaitingApproval = 2,

    /// <summary>Stopped on a technical/system error or exhausted retries. Recoverable via retry/resume.</summary>
    Failed = 3,

    /// <summary>All stages and operations finished. Terminal.</summary>
    Completed = 4,

    /// <summary>Abandoned by an operator or by a rejection. Terminal.</summary>
    Cancelled = 5,

    /// <summary>
    /// Stopped on a business-rule refusal — no purchase order could be created (not authorised, an
    /// LBT item, no vendor master data, and the like). Terminal: not auto-retried, since retrying
    /// without the underlying data changing just reproduces the same refusal. A future sweep may
    /// reconsider the indent from scratch.
    /// </summary>
    Skipped = 6,
}
