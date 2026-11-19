namespace NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

/// <summary>
/// Detailed outcome of an Issue to Shop Floor execution: issue document created, or refusal/shortage reasons.
/// </summary>
public sealed class IssueToShopFloorOutcome
{
    // Materialisation constructor for EF Core.
    private IssueToShopFloorOutcome()
    {
        IssueSource = string.Empty;
        DocumentNumber = string.Empty;
        Outcome = string.Empty;
    }

    private IssueToShopFloorOutcome(
        Guid id,
        Guid jobId,
        string issueSource,
        string documentNumber,
        string outcome,
        string? issueNumber,
        int lineCount,
        decimal totalQuantity,
        string? refusalReason,
        string? checksJson,
        string? shortagesJson,
        string? linesJson,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        JobId = jobId;
        IssueSource = issueSource;
        DocumentNumber = documentNumber;
        Outcome = outcome;
        IssueNumber = issueNumber;
        LineCount = lineCount;
        TotalQuantity = totalQuantity;
        RefusalReason = refusalReason;
        ChecksJson = checksJson;
        ShortagesJson = shortagesJson;
        LinesJson = linesJson;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid JobId { get; private set; }

    public string IssueSource { get; private set; }

    public string DocumentNumber { get; private set; }

    /// <summary>"Created", "Refused", or "Failed".</summary>
    public string Outcome { get; private set; }

    public string? IssueNumber { get; private set; }

    public int LineCount { get; private set; }

    public decimal TotalQuantity { get; private set; }

    public string? RefusalReason { get; private set; }

    public string? ChecksJson { get; private set; }

    public string? ShortagesJson { get; private set; }

    public string? LinesJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static IssueToShopFloorOutcome Create(
        Guid jobId,
        string issueSource,
        string documentNumber,
        string outcome,
        string? issueNumber,
        int lineCount,
        decimal totalQuantity,
        string? refusalReason,
        string? checksJson,
        string? shortagesJson,
        string? linesJson,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        return new IssueToShopFloorOutcome(
            Guid.NewGuid(),
            jobId,
            issueSource.Trim(),
            documentNumber.Trim(),
            outcome.Trim(),
            issueNumber?.Trim(),
            lineCount,
            totalQuantity,
            refusalReason?.Trim(),
            checksJson,
            shortagesJson,
            linesJson,
            nowUtc);
    }
}
