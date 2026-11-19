using System.Globalization;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// Builds the body of <c>createIssuetoShopFloor</c> exactly as the Issue to Shop Floor screen's
/// <c>onSave</c> does, for whichever tab the issue is raised from: one SJO, one Work Order, or one
/// Sales OAF.
/// </summary>
/// <remarks>
/// The ERP binds <c>SaveIssueToShopFloorModel</c>; everything not listed here the screen does not
/// send either. <c>userId</c> is overwritten by the ERP from the bearer token, and is sent only so the
/// body reads the same as the screen's.
/// </remarks>
internal static class IssuePayloadBuilder
{
    /// <summary>The Issue document type and sub-type the screen saves under: <c>IS</c> / <c>NI</c>.</summary>
    public const string DocType = "IS";
    public const string DocSubType = "NI";

    public static JsonObject Build(
        IssueSource source,
        IssueHeader header,
        IReadOnlyList<IssueLine> lines,
        IReadOnlyDictionary<(string ItemCode, int RandomNumber), string> remarks)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new ArgumentException("An issue needs at least one line.", nameof(lines));
        }

        var issueDate = header.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var warehouses = lines.Select(line => line.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var itemDetails = new JsonArray();
        foreach (var line in lines)
        {
            itemDetails.Add(new JsonObject
            {
                ["itemCode"] = line.ItemCode,
                ["randomNumber"] = line.RandomNumber,
                ["woId"] = line.WoId,
                ["sjoId"] = line.SjoId,
                ["reqId"] = null,
                ["stockId"] = line.StockId,
                ["lineNo"] = line.LineNo,
                ["issueQuantity"] = line.Quantity,
                ["remark"] = remarks.GetValueOrDefault((line.ItemCode, line.RandomNumber), string.Empty),
                ["issueWeight"] = 0,
                ["issueCutlPcs"] = 0,
                ["issueCutWeight"] = 0,
            });
        }

        return new JsonObject
        {
            ["issueId"] = 0,
            ["issueYear"] = header.FinancialYear,
            ["issueGroup"] = header.GroupCode,
            ["issueSiteId"] = header.SiteId,
            ["issueSiteCode"] = header.SiteCode,
            // Blank: Document Control numbers the issue. The flow refuses a site that does not
            // auto-number rather than invent a number that would collide with the next one a person types.
            ["issueNo"] = string.Empty,
            // S = SJO-wise, W = work-order-wise, O = sales-OAF-wise (R, requisition, is not automated).
            ["issueType"] = source.IssueType(),
            ["issueDate"] = issueDate,
            ["issueTo"] = header.IssueTo,
            ["issueBy"] = header.IssueBy,
            // The screen sends the first line's SJO id on every tab (onSave: itemGridData[0].sjoId),
            // work-order- and OAF-wise included, so this does too.
            ["docId"] = lines[0].SjoId,
            // S for a single SJO or a single Work Order. The screen sends M for everything else,
            // which includes the Sales OAF tab.
            ["sjowoType"] = source == IssueSource.SalesOaf ? "M" : "S",
            // S when every line comes from one warehouse, M otherwise — the screen's warehouse selection.
            ["whType"] = warehouses > 1 ? "M" : "S",
            ["docType"] = DocType,
            ["docSubType"] = DocSubType,
            ["authorizationRequired"] = header.AuthorisationRequired,
            // Today, or the period end when today is outside the site's finance period — the screen's rule.
            ["authorizationDate"] = header.AuthorisationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["autoNumberRequired"] = header.AutoNumberRequired,
            ["siteRequired"] = header.SiteRequired,
            ["fromLocationId"] = header.FromLocationId,
            ["userId"] = header.UserId,
            ["finFlag"] = header.FinFlag,
            ["currencyCode"] = header.CurrencyCode,
            ["companyId"] = header.CompanyId,
            ["periodStartDate"] = issueDate,
            ["periodEndDate"] = issueDate,
            ["itemDetails"] = itemDetails,
        };
    }
}

/// <summary>Everything on the issue header that is not an item line.</summary>
internal sealed record IssueHeader(
    string FinancialYear,
    string GroupCode,
    int SiteId,
    string SiteCode,
    DateOnly IssueDate,
    DateOnly AuthorisationDate,
    string IssueTo,
    string IssueBy,
    string AuthorisationRequired,
    string AutoNumberRequired,
    string SiteRequired,
    int FromLocationId,
    int UserId,
    string FinFlag,
    string CurrencyCode,
    int CompanyId);
