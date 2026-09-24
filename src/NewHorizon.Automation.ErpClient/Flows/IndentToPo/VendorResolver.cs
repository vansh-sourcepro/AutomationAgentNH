using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// The three values that together decide which purchase order an indent line can join.
/// </summary>
/// <remarks>
/// They travel as a set because the ERP's pending-indent query matches on all three at once
/// (<c>MIVVNDCD</c>, <c>MIVCURCD</c>, <c>MIVRTSTRCD</c>), and because they are exactly the break
/// key the ERP's own bulk generation uses when it decides where one purchase order ends and the
/// next begins.
/// </remarks>
internal sealed record VendorTerms(string VendorCode, string Currency, string RateStructure)
{
    public string Describe() => $"{VendorCode} / {Currency} / {RateStructure}";
}

public sealed partial class IndentToPoService
{
    /// <summary>
    /// Item code → the vendor the ERP would offer for it, or null when it offers none. Held for the
    /// lifetime of the service, which is one request or one sweep: the same item recurs across
    /// indents, and this master data does not change mid-cycle.
    /// </summary>
    private readonly Dictionary<string, VendorTerms?> _vendorTermsByItem =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the vendor to buy an item from, the way the ERP's own screens do: the row flagged as
    /// the item's default, and failing that the highest-priority active row.
    /// </summary>
    /// <remarks>
    /// This is the step the vendor-driven entry point could not do, and the reason authorised
    /// indents went nowhere. The ERP's pending-indent query is answered per vendor and will only
    /// return an item that already has an active item/vendor purchase row for the vendor asked
    /// about — so something has to work out which vendor to ask about first. An item with no such
    /// row at all cannot be ordered from anybody; that is master data missing in the ERP, and the
    /// only honest response is to name the item and leave it alone.
    /// </remarks>
    private async Task<VendorTerms?> ResolveVendorTermsAsync(
        string itemCode,
        CancellationToken cancellationToken)
    {
        if (_vendorTermsByItem.TryGetValue(itemCode, out var cached))
        {
            return cached;
        }

        var body = new JsonObject
        {
            ["SearchValue"] = itemCode,
            ["PageSize"] = 200,
            ["PageNumber"] = 1,
            ["SortField"] = string.Empty,
            ["SortDirection"] = string.Empty,
            ["UserId"] = await _tokenProvider.GetUserIdAsync(cancellationToken),
        };

        var rows = await PostJsonArrayAsync(_endpoints.ItemVendorPurchaseList, body, cancellationToken);

        // SearchValue is a search, not a filter: "97000ASC0162" also matches longer codes that
        // contain it. Only rows for exactly this item may decide its vendor.
        var candidates = rows.OfType<JsonObject>()
            .Where(row => row.String("mivhitmcd").Trim().Equals(itemCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(row => row.String("mivhactive").Equals("true", StringComparison.OrdinalIgnoreCase))
            .Where(row => !string.IsNullOrWhiteSpace(row.String("mivhvndcd")))
            .ToList();

        var chosen = candidates
            .OrderByDescending(row => row.String("mivhisdefault").Equals("true", StringComparison.OrdinalIgnoreCase))
            .ThenBy(row => row.Int("mivhvndpriority", int.MaxValue))
            .FirstOrDefault();

        VendorTerms? terms = chosen is null
            ? null
            : new VendorTerms(
                VendorCode: chosen.String("mivhvndcd").Trim(),
                Currency: chosen.String("mivhcurcd", _options.Currency).Trim(),
                RateStructure: chosen.String("mivhrtstrcd").Trim());

        if (terms is null)
        {
            _logger.LogWarning(
                "Item {ItemCode} has no active item/vendor purchase record in the ERP, so no vendor "
                + "can supply it and it cannot be put on a purchase order",
                itemCode);
        }
        else
        {
            _logger.LogDebug(
                "Item {ItemCode} will be ordered on {VendorTerms}",
                itemCode,
                terms.Describe());
        }

        _vendorTermsByItem[itemCode] = terms;

        return terms;
    }
}
