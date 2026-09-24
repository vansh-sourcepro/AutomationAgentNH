namespace NewHorizon.Automation.Domain.Errors;

/// <summary>
/// A recorded failure. Carries two messages on purpose: the ERP UI shows
/// <see cref="LaymanMessage"/> to planners ("Vendor missing for item X"), and expands
/// <see cref="TechnicalMessage"/> only for administrators.
/// </summary>
public sealed class AutomationError
{
    private AutomationError()
    {
        TechnicalMessage = string.Empty;
        LaymanMessage = string.Empty;
    }

    private AutomationError(
        Guid id,
        Guid jobId,
        Guid? stepId,
        ErrorType errorType,
        string technicalMessage,
        string laymanMessage,
        string? stackTrace,
        string? apiEndpoint,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        JobId = jobId;
        StepId = stepId;
        ErrorType = errorType;
        TechnicalMessage = technicalMessage;
        LaymanMessage = laymanMessage;
        StackTrace = stackTrace;
        ApiEndpoint = apiEndpoint;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    /// <summary>Null when the failure was not attributable to a single operation.</summary>
    public Guid? StepId { get; private set; }

    public ErrorType ErrorType { get; private set; }

    public string TechnicalMessage { get; private set; }

    /// <summary>Plain-language reason shown by default in the ERP UI.</summary>
    public string LaymanMessage { get; private set; }

    public string? StackTrace { get; private set; }

    public string? ApiEndpoint { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static AutomationError Create(
        Guid jobId,
        Guid? stepId,
        ErrorType errorType,
        string technicalMessage,
        string laymanMessage,
        DateTimeOffset nowUtc,
        string? stackTrace = null,
        string? apiEndpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(technicalMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(laymanMessage);

        return new AutomationError(
            Guid.NewGuid(),
            jobId,
            stepId,
            errorType,
            technicalMessage,
            laymanMessage,
            stackTrace,
            apiEndpoint,
            nowUtc);
    }
}
