namespace NewHorizon.Automation.Domain.Errors;

/// <summary>
/// Drives retry policy: only <see cref="Technical"/> failures are retried.
/// <see cref="Business"/> failures go straight to human review — retrying them just burns
/// attempts and produces the same rejection.
/// </summary>
public enum ErrorType
{
    Technical = 0,
    Business = 1,
}
