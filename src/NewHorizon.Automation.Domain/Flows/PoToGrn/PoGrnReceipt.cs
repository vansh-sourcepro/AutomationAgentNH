namespace NewHorizon.Automation.Domain.Flows.PoToGrn;

/// <summary>
/// What one run did with one PO at one warehouse: the GRN it created, or why there is none.
/// </summary>
/// <remarks>
/// <c>PoId</c>, <c>SiteId</c> and <c>GrnId</c> are values, not foreign keys: they name
/// <c>XPOHEAD.POHAUTOID</c>, <c>MLOCMST.MLOCID</c> and <c>XGRNHDR.XGRNHAUTOID</c> in the ERP's own
/// database, which this one never references. A row without a GRN always carries a reason — the
/// factories enforce it and so does <c>CK_PoGrnReceipt_Result</c>.
/// </remarks>
public sealed class PoGrnReceipt
{
    public const int ReasonMaxLength = 4000;

    private PoGrnReceipt()
    {
    }

    public Guid Id { get; private set; }

    public Guid RunId { get; private set; }

    public long PoId { get; private set; }

    public string PoNumber { get; private set; } = string.Empty;

    /// <summary>The ERP's PO type code: <c>R</c> Regular, <c>C</c> Capital.</summary>
    public string PoType { get; private set; } = string.Empty;

    public int SiteId { get; private set; }

    public string VendorCode { get; private set; } = string.Empty;

    public int WarehouseId { get; private set; }

    public PoGrnReceiptStatus Status { get; private set; }

    public long? GrnId { get; private set; }

    public string? GrnNumber { get; private set; }

    public int LinesReceived { get; private set; }

    public int LinesSkipped { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static PoGrnReceipt Created(
        Guid runId,
        PoGrnReceiptSubject subject,
        long grnId,
        string grnNumber,
        int linesReceived,
        int linesSkipped,
        string? note,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grnNumber);

        if (linesReceived <= 0)
        {
            throw new DomainException("A created GRN must have received at least one line.");
        }

        var receipt = New(runId, subject, PoGrnReceiptStatus.Created, note, nowUtc);
        receipt.GrnId = grnId;
        receipt.GrnNumber = grnNumber.Trim();
        receipt.LinesReceived = linesReceived;
        receipt.LinesSkipped = linesSkipped;

        return receipt;
    }

    public static PoGrnReceipt Skipped(
        Guid runId,
        PoGrnReceiptSubject subject,
        int linesSkipped,
        string reason,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var receipt = New(runId, subject, PoGrnReceiptStatus.Skipped, reason, nowUtc);
        receipt.LinesSkipped = linesSkipped;

        return receipt;
    }

    public static PoGrnReceipt Failed(
        Guid runId,
        PoGrnReceiptSubject subject,
        string reason,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return New(runId, subject, PoGrnReceiptStatus.Failed, reason, nowUtc);
    }

    private static PoGrnReceipt New(
        Guid runId,
        PoGrnReceiptSubject subject,
        PoGrnReceiptStatus status,
        string? reason,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return new PoGrnReceipt
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            PoId = subject.PoId,
            PoNumber = subject.PoNumber,
            PoType = subject.PoType,
            SiteId = subject.SiteId,
            VendorCode = subject.VendorCode,
            WarehouseId = subject.WarehouseId,
            Status = status,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? null
                : reason.Length <= ReasonMaxLength ? reason : reason[..ReasonMaxLength],
            RecordedAtUtc = nowUtc,
        };
    }
}

/// <summary>Which PO, at which warehouse, a receipt row is about.</summary>
public sealed record PoGrnReceiptSubject(
    long PoId,
    string PoNumber,
    string PoType,
    int SiteId,
    string VendorCode,
    int WarehouseId);
