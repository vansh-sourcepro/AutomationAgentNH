using System.Text.Json.Serialization;

namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// What the issue is raised against — the three tabs of the Issue to Shop Floor screen.
/// </summary>
/// <remarks>Serialised by name ("Sjo", "WorkOrder", "SalesOaf") so a caller can read the result.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<IssueSource>))]
public enum IssueSource
{
    /// <summary>SJO-wise: one SJO. The ERP's doc type <c>SJ</c>, issue type <c>S</c>.</summary>
    Sjo,

    /// <summary>Work-order-wise: one Work Order, whichever SJOs it serves. Doc type <c>WO</c>, issue type <c>W</c>.</summary>
    WorkOrder,

    /// <summary>Sales-OAF-wise: every customer-order SJO of one OAF. Doc type <c>OA</c>, issue type <c>O</c>.</summary>
    SalesOaf,
}

internal static class IssueSources
{
    /// <summary>The ERP's document type for the source, as the Issue screen's lookups take it.</summary>
    public static string DocType(this IssueSource source) => source switch
    {
        IssueSource.Sjo => "SJ",
        IssueSource.WorkOrder => "WO",
        IssueSource.SalesOaf => "OA",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary><c>issueType</c> on the create payload (<c>XIHWOSJORQWISE</c>).</summary>
    public static string IssueType(this IssueSource source) => source switch
    {
        IssueSource.Sjo => "S",
        IssueSource.WorkOrder => "W",
        IssueSource.SalesOaf => "O",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };

    /// <summary>How a person would name the document in a message.</summary>
    public static string Noun(this IssueSource source) => source switch
    {
        IssueSource.Sjo => "SJO",
        IssueSource.WorkOrder => "Work Order",
        IssueSource.SalesOaf => "Sales OAF",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}

/// <summary>One prerequisite, as the caller sees it: what was checked and what the ERP said.</summary>
public sealed record IssueCheck(string Name, bool Passed, string Detail);

/// <summary>One line of the issue: this much of an item, from this stock row, in this warehouse.</summary>
public sealed record IssueLine(
    string ItemCode,
    string WarehouseCode,
    long StockId,
    long LineNo,
    decimal Quantity,
    long SjoId,
    long WoId,
    int RandomNumber,
    string InwardNo = "");

/// <summary>An item the allocated warehouses cannot cover in full.</summary>
public sealed record IssueShortage(string ItemCode, int RandomNumber, decimal Required, decimal Available);

/// <summary>
/// What a conversion did — or, when <see cref="Created"/> is false, why it did nothing.
/// </summary>
/// <remarks>
/// A refusal is a result, not an exception: the checks that ran and the shortages found are the
/// answer the caller needs, and an exception would carry only a message. ERP failures (the ERP
/// refusing a call, or not answering) still surface as <c>ErpException</c>.
/// </remarks>
public sealed record IssueToShopFloorResult(
    IssueSource IssueType,
    string DocumentNumber,
    bool Created,
    bool DryRun,
    string? IssueNumber,
    string? Reason,
    IReadOnlyList<IssueCheck> Checks,
    IReadOnlyList<IssueLine> Lines,
    IReadOnlyList<IssueShortage> Shortages)
{
    /// <summary>True when every check passed and every item is covered — created, or would be on a dry run.</summary>
    public bool Ready => Reason is null;
}

/// <summary>The SJO as the ERP's SJO list reports it.</summary>
internal sealed record SjoHeader(
    long Id,
    string Year,
    string Group,
    int SiteId,
    string SiteCode,
    string Number,
    string ItemCode,
    string Status,
    string StatusDescription)
{
    public string FullNumber => $"{Year}/{Group}/{SiteCode}/{Number}";
}

/// <summary>A warehouse the SJO has allocated stock in.</summary>
internal sealed record IssueWarehouse(int Id, string Code);

/// <summary>One pending line of the SJO, as <c>getListOfItems4IssueEntry</c> reports it.</summary>
internal sealed record IssueItem(
    string ItemCode,
    long SjoId,
    long WoId,
    int RandomNumber,
    decimal RequiredQuantity,
    decimal PendingQuantity,
    bool InwardRequired,
    bool BarcodeRequired,
    string Remarks)
{
    /// <summary>What the screen's Fill issues for the line: the pending quantity, capped at the requirement.</summary>
    public decimal QuantityToIssue => Math.Min(PendingQuantity, RequiredQuantity);
}

/// <summary>One stock row an item can be issued from, as <c>getItemCombinations4IssueEntry</c> reports it.</summary>
/// <param name="InwardNo">The inward the row belongs to, for an inward-tracked item; blank otherwise.</param>
internal sealed record StockRow(long StockId, long LineNo, string WarehouseCode, decimal Available, string InwardNo = "");
