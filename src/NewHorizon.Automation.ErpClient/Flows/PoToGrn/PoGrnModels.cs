using NewHorizon.Automation.Domain.Flows.PoToGrn;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

public static class PoGrnTypes
{
    public static IReadOnlyList<PoGrnType> All { get; } = [PoGrnType.Regular, PoGrnType.Capital];

    /// <summary>The ERP's <c>POHTYPE</c> code.</summary>
    public static string Code(this PoGrnType type) => type == PoGrnType.Capital ? "C" : "R";

    /// <summary>The GRN's Document Control sub type: <c>OR</c> ordinary, <c>CG</c> capital goods.</summary>
    public static string GrnSubType(this PoGrnType type) => type == PoGrnType.Capital ? "CG" : "OR";

    /// <summary>The PO's own Document Control sub type, which the PO list is asked with.</summary>
    public static string PoSubType(this PoGrnType type) => type == PoGrnType.Capital ? "CP" : "RP";

    public static PoGrnType? FromCode(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "R" => PoGrnType.Regular,
        "C" => PoGrnType.Capital,
        _ => null,
    };
}

/// <summary>An authorised PO the list says still has quantity waiting for a GRN.</summary>
public sealed record PoGrnCandidate(
    long PoId,
    string Year,
    string Group,
    string SiteCode,
    string Number,
    int SiteId,
    PoGrnType PoType,
    DateOnly? PoDate,
    string Vendor)
{
    public string DisplayNumber => string.Join('/', Year, Group, SiteCode, Number);
}

/// <summary>One pass over the authorised POs.</summary>
public sealed record PoGrnSweepRequest
{
    public IReadOnlyList<int>? Sites { get; init; }

    /// <summary>Empty means Regular and Capital.</summary>
    public IReadOnlyList<PoGrnType>? PoTypes { get; init; }

    /// <summary>Only these POs, by <c>POHAUTOID</c>. Empty means no filter.</summary>
    public IReadOnlyList<long>? PoIds { get; init; }

    /// <summary>
    /// Only these POs, by whole number (<c>26-27/PR/NF1/000012</c>) or bare running number
    /// (<c>000012</c>) — exact, case-insensitive. A bare number that fits two POs refuses the call.
    /// </summary>
    public IReadOnlyList<string>? PoNumbers { get; init; }

    public required GrnReceiptMode ReceiptMode { get; init; }

    /// <summary>Stamped on every GRN. Required unless <see cref="DryRun"/>.</summary>
    public string? InvoiceNumber { get; init; }

    /// <summary>How many POs this pass may examine.</summary>
    public int MaxPos { get; init; } = 25;

    /// <summary>Work out and report every GRN, create none.</summary>
    public bool DryRun { get; init; }
}

/// <summary>What became of one PO at one warehouse.</summary>
/// <param name="Planned">A dry run found this receivable; nothing was created.</param>
/// <param name="Notes">Why anything was not received, in words an operator can act on.</param>
public sealed record PoGrnResult(
    long PoId,
    string PoNumber,
    string PoType,
    int SiteId,
    string VendorCode,
    int WarehouseId,
    PoGrnReceiptStatus Status,
    bool Planned,
    long? GrnId,
    string? GrnNumber,
    int LinesReceived,
    int LinesSkipped,
    IReadOnlyList<string> Notes);

public sealed record PoGrnSweepResult(
    int Examined,
    bool DryRun,
    IReadOnlyList<PoGrnResult> Results,
    IReadOnlyList<string> NotFound,
    string? StoppedReason)
{
    public int GrnsCreated => Results.Count(result => result.Status == PoGrnReceiptStatus.Created && !result.Planned);

    public int GrnsPlanned => Results.Count(result => result.Planned);
}
