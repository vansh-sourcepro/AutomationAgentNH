using System.Globalization;
using System.Text.RegularExpressions;

namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// Orders an inward-tracked item's stock rows oldest inward first, so the agent can pick them itself.
/// </summary>
/// <remarks>
/// <para>
/// Only needed while the MRP policy has inward-wise allocation off. Then the Issue screen's Fill
/// skips inward-tracked items and a person picks the inwards in a popup; the agent picks them FIFO
/// instead, as the business decided.
/// </para>
/// <para>
/// The ERP does not hand over an order to follow. In that branch
/// <c>CSP_XISSHDR_GetItemCombinations4Issue</c> returns no inward date column and sorts by the inward
/// number's text, which is not date order (<c>INW03022025…</c> comes before <c>INW05022024…</c>, and
/// opening stock <c>OPINW01102024…</c> after both). The stock table has no date column either
/// (<c>XSTKIDEN</c>), but the inward number carries the receipt date: a letter prefix naming where the
/// stock came from, an optional underscore, then <c>ddMMyyyy</c>, then a time or letters —
/// <c>INW05022024013943I1</c>, <c>IW01062024125119I1</c>, <c>LBTIW02072024BWZ1</c>,
/// <c>OPINW01102024112816I</c>, <c>CPIW_22092025_174429</c> are all on the ERP. The date is read from there
/// and must be a real calendar date. The time part is not used: on the ERP data it does not agree with
/// receipt order within a day (it reads as a 12-hour clock), whereas stock ids are handed out in
/// receipt order, so they break ties. A number with no date in it (<c>OPINW0001</c>, <c>CASHINW001</c>)
/// sorts after every dated one, by stock id.
/// </para>
/// </remarks>
internal static partial class InwardFifo
{
    public static IReadOnlyList<StockRow> Order(IEnumerable<StockRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return rows
            .OrderBy(row => ReceivedOn(row.InwardNo) ?? DateOnly.MaxValue)
            .ThenBy(row => row.StockId)
            .ToList();
    }

    /// <summary>
    /// The receipt date in an inward number such as <c>INW05022024013943I1</c> or
    /// <c>OPINW01102024112816I</c>, or null when it carries none.
    /// </summary>
    internal static DateOnly? ReceivedOn(string? inwardNo)
    {
        var match = InwardNumber().Match(inwardNo?.Trim() ?? string.Empty);

        return match.Success
            && DateOnly.TryParseExact(match.Groups["date"].Value, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    // Letters, an optional underscore, then eight digits read as ddMMyyyy.
    [GeneratedRegex(@"^[A-Z]+_?(?<date>\d{8})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InwardNumber();
}
