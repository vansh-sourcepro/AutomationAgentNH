namespace NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

/// <summary>
/// One SJO, Work Order or Sales OAF the agent has been asked to issue — the document case, not the attempt.
/// </summary>
public sealed class IssueToShopFloorConversion
{
    // Materialisation constructor for EF Core.
    private IssueToShopFloorConversion()
    {
        IssueSource = string.Empty;
        DocumentNumber = string.Empty;
        SiteCode = string.Empty;
    }

    private IssueToShopFloorConversion(
        Guid id,
        string issueSource,
        string documentNumber,
        long documentId,
        int siteId,
        string? siteCode,
        DateTimeOffset firstSeenAtUtc)
    {
        Id = id;
        IssueSource = issueSource;
        DocumentNumber = documentNumber;
        DocumentId = documentId;
        SiteId = siteId;
        SiteCode = siteCode ?? string.Empty;
        FirstSeenAtUtc = firstSeenAtUtc;
        LastAttemptedAtUtc = firstSeenAtUtc;
        TotalAttempts = 1;
        SuccessfulAttempts = 0;
        IsTerminal = false;
    }

    public Guid Id { get; private set; }

    /// <summary>"Sjo", "WorkOrder", or "SalesOaf".</summary>
    public string IssueSource { get; private set; }

    /// <summary>The document number as given — e.g. "26-27/SJ/NF1/000123".</summary>
    public string DocumentNumber { get; private set; }

    /// <summary>The ERP internal id for the document (e.g. SJO ID or WO ID).</summary>
    public long DocumentId { get; private set; }

    public int SiteId { get; private set; }

    public string SiteCode { get; private set; }

    public DateTimeOffset FirstSeenAtUtc { get; private set; }

    public DateTimeOffset LastAttemptedAtUtc { get; private set; }

    public int TotalAttempts { get; private set; }

    public int SuccessfulAttempts { get; private set; }

    public bool IsTerminal { get; private set; }

    public string? TerminalIssueNumber { get; private set; }

    public static IssueToShopFloorConversion Open(
        string issueSource,
        string documentNumber,
        long documentId,
        int siteId,
        string? siteCode,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issueSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNumber);

        return new IssueToShopFloorConversion(
            Guid.NewGuid(),
            issueSource.Trim(),
            documentNumber.Trim(),
            documentId,
            siteId,
            siteCode,
            nowUtc);
    }

    public void RecordAttempt(DateTimeOffset nowUtc, bool succeeded, string? issueNumber = null)
    {
        LastAttemptedAtUtc = nowUtc;
        TotalAttempts++;

        if (succeeded)
        {
            SuccessfulAttempts++;
            if (!string.IsNullOrWhiteSpace(issueNumber))
            {
                TerminalIssueNumber = issueNumber.Trim();
                IsTerminal = true;
            }
        }
    }
}
