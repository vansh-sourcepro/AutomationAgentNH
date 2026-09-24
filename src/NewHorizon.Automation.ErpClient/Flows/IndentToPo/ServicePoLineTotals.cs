using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// The money arithmetic for one Service PO line.
/// </summary>
/// <remarks>
/// The service sibling of <see cref="LineTotals"/>, and simpler than it in one respect: a service
/// item has a single unit of measure, so there is no purchase/internal conversion and the quantity
/// the indent asked for is the quantity the order carries. Everything else is the same deal — the
/// ERP prices the line through <c>getAllRateStructureDetails</c> and the only arithmetic here is
/// multiplying its per-unit figures by that quantity. The tax engine stays on the ERP.
/// </remarks>
internal sealed class ServicePoLineTotals
{
    private readonly Dictionary<string, decimal> _taxAmountsByRateCode;

    private ServicePoLineTotals(
        ServiceIndentLine source,
        int itemLine,
        decimal quantity,
        decimal discountedRate,
        decimal landedPrice,
        JsonArray rateComponents,
        Dictionary<string, decimal> taxAmountsByRateCode)
    {
        Source = source;
        ItemLine = itemLine;
        Quantity = quantity;
        DiscountedRate = discountedRate;
        LandedPrice = landedPrice;
        RateComponents = rateComponents;
        _taxAmountsByRateCode = taxAmountsByRateCode;
    }

    public ServiceIndentLine Source { get; }

    /// <summary>
    /// <c>PIDSLINE</c>, and the <c>srno</c> the delivery rows point back at. Assigned 1..n over the
    /// lines this order actually takes, not copied from the ERP's answer, which numbers every
    /// pending line for the vendor including the ones belonging to other indents.
    /// </summary>
    public int ItemLine { get; }

    public string ItemCode => Source.ItemCode;

    public decimal Quantity { get; }

    /// <summary>Basic rate after the item/vendor discount — the ERP's <c>PIDSBPURRT</c> is the rate
    /// before it, so this figure only drives the amounts.</summary>
    public decimal DiscountedRate { get; }

    /// <summary>Rate including every tax component, as priced by the ERP.</summary>
    public decimal LandedPrice { get; }

    /// <summary>
    /// The ERP's own pricing rows for this line, kept so the tax detail the order carries is built
    /// from the same answer the amounts came from rather than from a second, differently shaped
    /// call. Read-only here; the payload builder copies the fields it needs out of them.
    /// </summary>
    public JsonArray RateComponents { get; }

    public decimal AmountAfterDiscount => Round2(DiscountedRate * Quantity);

    public decimal TaxAmount => Round2(_taxAmountsByRateCode.Values.Sum());

    public IEnumerable<KeyValuePair<string, decimal>> TaxAmountsByRateCode => _taxAmountsByRateCode;

    public decimal TaxAmountFor(string rateCode) =>
        _taxAmountsByRateCode.TryGetValue(rateCode, out var amount) ? amount : 0m;

    /// <param name="rateComponents">
    /// <c>getAllRateStructureDetails</c>'s answer for this line's discounted rate: one row per
    /// component, each carrying that component's per-unit amount and the running landed price.
    /// </param>
    public static ServicePoLineTotals For(ServiceIndentLine source, int itemLine, JsonArray rateComponents)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rateComponents);

        var quantity = source.Outstanding;

        if (quantity <= 0m)
        {
            throw new InvalidOperationException(
                $"Service indent line {source.IndentLineNumber} ('{source.ItemCode}') has nothing outstanding.");
        }

        var discountedRate = DiscountRate(source.BasicPrice, source.DiscountType, source.DiscountValue);

        var taxAmountsByRateCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var landedPrice = discountedRate;

        foreach (var component in rateComponents.OfType<JsonObject>())
        {
            var rateCode = component.String("msprtcd");

            if (!string.IsNullOrEmpty(rateCode))
            {
                taxAmountsByRateCode[rateCode] = Round2(component.Decimal("rtamt") * quantity);
            }

            // Every row carries the same running landed price; the last one is the final figure.
            var stated = component.Decimal("landprice");

            if (stated > 0m)
            {
                landedPrice = stated;
            }
        }

        return new ServicePoLineTotals(
            source,
            itemLine,
            quantity,
            discountedRate,
            landedPrice,
            rateComponents,
            taxAmountsByRateCode);
    }

    /// <summary>
    /// "Percentage" takes a share of the rate; "Value" and "None" subtract the value outright —
    /// with "None" the value is zero, so the two collapse into one branch, exactly as the screen
    /// treats them.
    /// </summary>
    private static decimal DiscountRate(decimal rate, string discountType, decimal discountValue) =>
        discountType.Equals("Percentage", StringComparison.OrdinalIgnoreCase)
            ? rate - (rate * discountValue / 100m)
            : rate - discountValue;

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
