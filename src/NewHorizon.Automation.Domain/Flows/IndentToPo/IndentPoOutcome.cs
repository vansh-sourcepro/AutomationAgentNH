using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>What one vendor group produced.</summary>
public enum PoOutcomeKind
{
    /// <summary>A purchase order exists in the ERP. <c>PoId</c> and <c>PoNumber</c> are set.</summary>
    Created = 0,

    /// <summary>Nothing was ordered, and the reason is not a fault — missing master data, nothing outstanding.</summary>
    Skipped = 1,

    /// <summary>The ERP refused the create, or it threw. The reason is the ERP's own words.</summary>
    Failed = 2,
}

/// <summary>
/// One vendor group inside one execution: the purchase order it produced, or the reason it
/// produced none.
/// </summary>
/// <remarks>
/// <para>
/// A job row holds one status, and one indent legitimately produces several purchase orders — its
/// items are grouped by (vendor, currency, rate structure), the ERP's own break rule, and each
/// group is a separate document. So the result of a conversion is a set, and needs a table.
/// </para>
/// <para>
/// It is also where a refusal lands. A refusal for one vendor group is a note, not an exception:
/// the other groups still convert. Without a row of its own that reason survives only in a log
/// line, which is how an indent that converted to nothing once read as "produced 0 purchase
/// order(s): none" with the cause nowhere.
/// </para>
/// </remarks>
public sealed class IndentPoOutcome
{
    // Materialisation constructor for EF Core.
    private IndentPoOutcome()
    {
    }

    private IndentPoOutcome(
        Guid id,
        Guid jobId,
        Guid? stepId,
        int sequence,
        PoOutcomeKind outcome,
        string? vendorCode,
        string? currencyCode,
        string? rateStructureCode,
        long? poId,
        string? poNumber,
        int lineCount,
        string? reason,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        JobId = jobId;
        StepId = stepId;
        Sequence = sequence;
        Outcome = outcome;
        VendorCode = vendorCode;
        CurrencyCode = currencyCode;
        RateStructureCode = rateStructureCode;
        PoId = poId;
        PoNumber = poNumber;
        LineCount = lineCount;
        Reason = reason;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The execution that produced it.</summary>
    public Guid JobId { get; private set; }

    /// <summary>The create operation, when the group got that far. Null when it never did.</summary>
    public Guid? StepId { get; private set; }

    /// <summary>1..n within the job, so the order the groups were attempted in is reproducible.</summary>
    public int Sequence { get; private set; }

    public PoOutcomeKind Outcome { get; private set; }

    /// <summary>
    /// The three values that decide where one purchase order ends and the next begins — the ERP's
    /// own Bulk PO break key. Null on a refusal the agent could not attribute to one vendor group,
    /// such as "every item line on this indent is already closed".
    /// </summary>
    public string? VendorCode { get; private set; }

    /// <inheritdoc cref="VendorCode" />
    public string? CurrencyCode { get; private set; }

    /// <inheritdoc cref="VendorCode" />
    public string? RateStructureCode { get; private set; }

    /// <summary>Logical reference: <c>XPOHEAD.POHAUTOID</c>, or <c>XPOSHEAD.POHSAUTOID</c> for a service order.</summary>
    public long? PoId { get; private set; }

    /// <summary><c>XPOHEAD.POHORDNO</c> — "26-27/TE/NF1/000002".</summary>
    public string? PoNumber { get; private set; }

    /// <summary>How many indent lines the agent put on the order.</summary>
    public int LineCount { get; private set; }

    /// <summary>Why nothing was ordered. Required unless <see cref="Outcome"/> is Created.</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static IndentPoOutcome Created(
        Guid jobId,
        int sequence,
        string vendorCode,
        string? currencyCode,
        string? rateStructureCode,
        long poId,
        string poNumber,
        int lineCount,
        DateTimeOffset nowUtc,
        Guid? stepId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(poNumber);

        return new IndentPoOutcome(
            Guid.NewGuid(),
            jobId,
            stepId,
            sequence,
            PoOutcomeKind.Created,
            vendorCode.Trim(),
            Blank(currencyCode),
            Blank(rateStructureCode),
            poId,
            poNumber.Trim(),
            lineCount,
            reason: null,
            nowUtc);
    }

    public static IndentPoOutcome Skipped(
        Guid jobId,
        int sequence,
        string reason,
        DateTimeOffset nowUtc,
        string? vendorCode = null,
        Guid? stepId = null) =>
        WithoutOrder(jobId, sequence, PoOutcomeKind.Skipped, reason, nowUtc, vendorCode, stepId);

    public static IndentPoOutcome Failed(
        Guid jobId,
        int sequence,
        string reason,
        DateTimeOffset nowUtc,
        string? vendorCode = null,
        Guid? stepId = null) =>
        WithoutOrder(jobId, sequence, PoOutcomeKind.Failed, reason, nowUtc, vendorCode, stepId);

    private static IndentPoOutcome WithoutOrder(
        Guid jobId,
        int sequence,
        PoOutcomeKind outcome,
        string reason,
        DateTimeOffset nowUtc,
        string? vendorCode,
        Guid? stepId)
    {
        // Enforced here as well as by the check constraint: an outcome that ordered nothing and
        // says nothing is the exact failure this table exists to prevent.
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new IndentPoOutcome(
            Guid.NewGuid(),
            jobId,
            stepId,
            sequence,
            outcome,
            Blank(vendorCode),
            currencyCode: null,
            rateStructureCode: null,
            poId: null,
            poNumber: null,
            lineCount: 0,
            Truncate(reason.Trim()),
            nowUtc);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Matches the column width, so an over-long ERP message cannot fail the save.</summary>
    private static string Truncate(string value) =>
        value.Length <= FieldLengths.Message ? value : value[..FieldLengths.Message];
}
