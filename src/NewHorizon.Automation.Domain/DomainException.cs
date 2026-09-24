namespace NewHorizon.Automation.Domain;

/// <summary>
/// Thrown when a caller attempts something the model forbids — a rejected state transition,
/// re-running a completed operation, advancing an unapproved gate. Always a bug in the caller,
/// never a business outcome, so it is never converted into a retry.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
