namespace NewHorizon.Automation.Application.Erp;

/// <summary>
/// Base for failures raised while talking to the ERP. The split between
/// <see cref="ErpTransientException"/> and <see cref="ErpBusinessException"/> is the single fact
/// the engine uses to decide "retry" versus "send to a human", so every ERP failure must be
/// classified into one of the two — never left ambiguous.
/// </summary>
public abstract class ErpException : Exception
{
    protected ErpException(string laymanMessage, string technicalMessage, string? apiEndpoint, Exception? innerException)
        : base(technicalMessage, innerException)
    {
        LaymanMessage = laymanMessage;
        TechnicalMessage = technicalMessage;
        ApiEndpoint = apiEndpoint;
    }

    /// <summary>Plain-language reason shown to planners in the ERP UI.</summary>
    public string LaymanMessage { get; }

    /// <summary>Full detail for the administrator view and the error log.</summary>
    public string TechnicalMessage { get; }

    public string? ApiEndpoint { get; }

    /// <summary>
    /// Whether another attempt is worth making. Declared on the exception itself rather than
    /// inferred by the engine from the concrete type, so adding a new ERP failure kind cannot
    /// silently fall into the wrong retry behaviour.
    /// </summary>
    public abstract bool IsTransient { get; }
}

/// <summary>
/// Timeout, 5xx, network drop, or an open circuit breaker — conditions that a later attempt may
/// well survive. Retried with backoff and jitter up to the configured limit.
/// </summary>
public sealed class ErpTransientException : ErpException
{
    public ErpTransientException(
        string laymanMessage,
        string technicalMessage,
        string? apiEndpoint = null,
        Exception? innerException = null)
        : base(laymanMessage, technicalMessage, apiEndpoint, innerException)
    {
    }

    public override bool IsTransient => true;
}

/// <summary>
/// The ERP understood the request and refused it: missing vendor, failed validation, no
/// permission. Never retried — a second identical call produces the same refusal — so the job
/// goes straight to human review with the layman message.
/// </summary>
public sealed class ErpBusinessException : ErpException
{
    public ErpBusinessException(
        string laymanMessage,
        string technicalMessage,
        string? apiEndpoint = null,
        Exception? innerException = null)
        : base(laymanMessage, technicalMessage, apiEndpoint, innerException)
    {
    }

    public override bool IsTransient => false;
}

/// <summary>
/// The agent could not obtain or refresh its service token. Transient in nature — the ERP auth
/// endpoint may simply be restarting — but called out separately because the fix is usually a
/// wrong client secret, and that should be obvious in the logs rather than buried in retries.
/// </summary>
public sealed class ErpAuthenticationException : ErpException
{
    public ErpAuthenticationException(
        string laymanMessage,
        string technicalMessage,
        string? apiEndpoint = null,
        Exception? innerException = null)
        : base(laymanMessage, technicalMessage, apiEndpoint, innerException)
    {
    }

    /// <summary>
    /// Transient: the auth endpoint may simply be restarting, and a job must not be sent to a
    /// human because the ERP was briefly down. A genuinely wrong secret is distinguished by its
    /// message rather than by being classified as a business failure.
    /// </summary>
    public override bool IsTransient => true;
}
