namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>
/// One authorised indent the agent has been asked to convert — the case, not the attempt.
/// </summary>
/// <remarks>
/// <para>
/// The indent's identity is held here once, however many times it is attempted. Every attempt is
/// an <c>AutomationJob</c> pointing back at this row, so "every execution for indent 6841" is a
/// foreign key rather than a string match, and a retry costs one job row rather than a second copy
/// of the indent's number, site and date.
/// </para>
/// <para>
/// Nothing here duplicates the ERP. <see cref="IndentId"/> is a logical reference to
/// <c>XINDHDR.XINDID</c> or <c>XINDSHDR.XINDAUTOID</c> — a value, never a foreign key, because the
/// automation database is not the ERP database and must survive the ERP row being archived.
/// </para>
/// </remarks>
public sealed class IndentPoConversion
{
    // Materialisation constructor for EF Core.
    private IndentPoConversion()
    {
        IndentNumber = string.Empty;
    }

    private IndentPoConversion(
        Guid id,
        long indentId,
        IndentKind indentKind,
        string indentNumber,
        int siteId,
        DateOnly? indentDate,
        DateTimeOffset firstSeenAtUtc)
    {
        Id = id;
        IndentId = indentId;
        IndentKind = indentKind;
        IndentNumber = indentNumber;
        SiteId = siteId;
        IndentDate = indentDate;
        FirstSeenAtUtc = firstSeenAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>Logical reference: <c>XINDHDR.XINDID</c>, or <c>XINDSHDR.XINDAUTOID</c> for a service indent.</summary>
    public long IndentId { get; private set; }

    /// <summary>Decides which ERP table <see cref="IndentId"/> names.</summary>
    public IndentKind IndentKind { get; private set; }

    /// <summary>As a person reads it — "26-27/TE/NF1/000012". Not derivable from the id without an ERP call.</summary>
    public string IndentNumber { get; private set; }

    /// <summary>
    /// The indent's own site (<c>XINDHLOCID</c> / <c>XINDSHSITE</c>), never the configured one.
    /// Logical reference to <c>MLOCMST.MLOCID</c>.
    /// </summary>
    public int SiteId { get; private set; }

    /// <summary>Enables ageing — "authorised six days ago, still not converted" — with no ERP round trip.</summary>
    public DateOnly? IndentDate { get; private set; }

    /// <summary>
    /// When the agent first saw this indent. Kept here rather than derived from the oldest job,
    /// because job rows are trimmed by their retention window and this row outlives them.
    /// </summary>
    public DateTimeOffset FirstSeenAtUtc { get; private set; }

    /// <summary>The <c>DocumentType</c> every job for this indent is enqueued under.</summary>
    public string DocumentType => IndentDocumentTypes.For(IndentKind);

    public static IndentPoConversion Open(
        long indentId,
        IndentKind indentKind,
        string indentNumber,
        int siteId,
        DateTimeOffset nowUtc,
        DateOnly? indentDate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(indentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(indentNumber);

        return new IndentPoConversion(
            Guid.NewGuid(),
            indentId,
            indentKind,
            indentNumber.Trim(),
            siteId,
            indentDate,
            nowUtc);
    }

    /// <summary>
    /// Refreshes the descriptive fields when a later run reads the indent again. The identity —
    /// id and kind — is fixed; only what the ERP says about it can move.
    /// </summary>
    public void Refresh(string indentNumber, int siteId, DateOnly? indentDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indentNumber);

        IndentNumber = indentNumber.Trim();
        SiteId = siteId;
        IndentDate = indentDate ?? IndentDate;
    }
}
