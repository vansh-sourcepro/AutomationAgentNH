namespace NewHorizon.Automation.Domain.Workflows;

/// <summary>
/// What an operation concluded. Distinguishing <see cref="BusinessFailure"/> from
/// <see cref="TransientFailure"/> is what stops the engine from retrying a rejection that will
/// fail identically every time.
/// </summary>
public enum OperationOutcome
{
    /// <summary>The ERP work was done (or was already done and was found by query-before-create).</summary>
    Succeeded = 0,

    /// <summary>Nothing to do — no net shortage, precondition not met. Successful and terminal.</summary>
    Skipped = 1,

    /// <summary>The ERP refused on business grounds. Never retried; goes to human review.</summary>
    BusinessFailure = 2,

    /// <summary>Timeout, 5xx, breaker open, network. Retried with backoff up to MaxRetry.</summary>
    TransientFailure = 3,
}
