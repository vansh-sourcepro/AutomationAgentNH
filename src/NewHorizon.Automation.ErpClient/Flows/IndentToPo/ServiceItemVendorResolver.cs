using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

public sealed partial class IndentToPoService
{
    /// <summary>
    /// Service item code → the vendor the ERP would offer for it, or null when it offers none.
    /// Separate from the material cache because the two read different masters and an item code
    /// can legitimately exist in both.
    /// </summary>
    private readonly Dictionary<string, VendorTerms?> _serviceVendorTermsByItem =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Service item code → its <c>MSMITMAUTOID</c>, which is how its vendors are addressed.</summary>
    private readonly Dictionary<string, long> _serviceItemIdsByCode =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the vendor to buy a service from, and the currency and rate structure that come with
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same step, and the same reason for it, as <see cref="ResolveVendorTermsAsync"/> on the
    /// material side: the ERP's pending-lines query is answered per vendor and will only return a
    /// line whose item has a row in the item/vendor master for the vendor asked about, so something
    /// has to decide which vendor to ask about first. A service item with no <c>MSERITMVND</c> row
    /// at all cannot be ordered from anybody — that is master data missing in the ERP, and the only
    /// honest response is to name the item and leave it alone.
    /// </para>
    /// <para>
    /// One real difference: the service item/vendor master has no "default" flag and no priority
    /// column, so there is no ERP-defined winner when an item lists several vendors. The ERP's own
    /// pending-lines query takes the first row per item and vendor in unspecified order; this takes
    /// the lowest vendor code, which is at least the same answer twice running, and says so in the
    /// log when there was more than one to choose from.
    /// </para>
    /// </remarks>
    private async Task<VendorTerms?> ResolveServiceVendorTermsAsync(
        string itemCode,
        CancellationToken cancellationToken)
    {
        if (_serviceVendorTermsByItem.TryGetValue(itemCode, out var cached))
        {
            return cached;
        }

        VendorTerms? terms = null;
        var itemId = await ResolveServiceItemIdAsync(itemCode, cancellationToken);

        if (itemId > 0)
        {
            var rows = await GetJsonArrayAsync(_endpoints.ServiceItemVendor(itemId), cancellationToken);

            var candidates = rows.OfType<JsonObject>()
                .Where(row => !string.IsNullOrWhiteSpace(row.String("vendorCode")))
                .OrderBy(row => row.String("vendorCode").Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chosen = candidates.FirstOrDefault();

            if (chosen is not null)
            {
                terms = new VendorTerms(
                    VendorCode: chosen.String("vendorCode").Trim(),
                    Currency: chosen.String("vendorCurrency", _options.Currency).Trim(),
                    RateStructure: chosen.String("taxStructure").Trim());

                if (candidates.Count > 1)
                {
                    _logger.LogInformation(
                        "Service item {ItemCode} lists {Count} vendors and the ERP marks none of them "
                        + "preferred; ordering it on {VendorTerms}",
                        itemCode,
                        candidates.Count,
                        terms.Describe());
                }
                else
                {
                    _logger.LogDebug(
                        "Service item {ItemCode} will be ordered on {VendorTerms}",
                        itemCode,
                        terms.Describe());
                }
            }
        }

        if (terms is null)
        {
            _logger.LogWarning(
                "Service item {ItemCode} has no item/vendor record in the ERP, so no vendor can supply "
                + "it and it cannot be put on a purchase order",
                itemCode);
        }

        _serviceVendorTermsByItem[itemCode] = terms;

        return terms;
    }

    /// <summary>
    /// A service item's <c>MSMITMAUTOID</c>, or 0 when the ERP does not know the code. Zero is not
    /// an error here: the caller turns it into the same "no vendor can supply this" note as an item
    /// that exists but has no vendor, which is the same thing from the buyer's point of view.
    /// </summary>
    private async Task<long> ResolveServiceItemIdAsync(string itemCode, CancellationToken cancellationToken)
    {
        if (_serviceItemIdsByCode.TryGetValue(itemCode, out var cached))
        {
            return cached;
        }

        var body = new JsonObject
        {
            ["itemCode"] = itemCode,
            // The ERP names this "itemDescription" and then ignores what is in it — its procedure
            // pins the type flag to 'P' itself. It must not be blank, though: the controller
            // refuses the request outright when it is.
            ["itemDescription"] = "P",
        };

        var rows = await PostJsonArrayAsync(_endpoints.ServiceItemDetail, body, cancellationToken);

        // The ERP matches the code as a prefix, so "SRV01" also answers for "SRV010". Only the row
        // for exactly this item may decide its id.
        var match = rows.OfType<JsonObject>()
            .FirstOrDefault(row => row.String("itemCode").Trim()
                .Equals(itemCode.Trim(), StringComparison.OrdinalIgnoreCase));

        var itemId = match is null ? 0L : (long)match.Decimal("itemAutoID");

        if (itemId <= 0)
        {
            _logger.LogWarning(
                "Service item {ItemCode} is not in the service item master, so its vendors cannot be "
                + "looked up",
                itemCode);
        }

        _serviceItemIdsByCode[itemCode] = itemId;

        return itemId;
    }
}
