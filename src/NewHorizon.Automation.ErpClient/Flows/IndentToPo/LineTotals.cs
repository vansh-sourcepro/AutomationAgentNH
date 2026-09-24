using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>One delivery line's share of an item's quantity.</summary>
internal sealed record DeliveryTotals(JsonObject Source, decimal QuantityIuom, decimal QuantityPuom);

/// <summary>
/// The money and quantity arithmetic for one PO line.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. The screen's rate engine is a large client-side port of the ERP's tax
/// calculator, and reimplementing it would mean owning a second copy of the ERP's tax rules. Instead
/// the ERP is asked to price the line — <c>getAllRateStructureDetails</c> returns each component's
/// per-unit amount and the landed price for a given basic rate — and only the multiplication by
/// quantity happens here.
/// </para>
/// <para>
/// Verified against both recorded traces: Regular 450 → 405 × 20 = 8100 basic, 1458 tax, 9558 net,
/// 477.90 landed; Capital 63000 × 10 = 630000 basic, 113400 tax, 743400 net, 74340 landed.
/// </para>
/// </remarks>
internal sealed class LineTotals
{
    private readonly Dictionary<string, decimal> _taxAmountsByRateCode;

    private LineTotals(
        IndentPoLine line,
        int itemLine,
        IReadOnlyList<DeliveryTotals> deliveries,
        decimal quantityIuom,
        decimal quantityPuom,
        decimal purchaseRate,
        string discountType,
        decimal discountValue,
        decimal discountedRate,
        decimal landedPrice,
        decimal purchaseConversionFactor,
        decimal internalConversionFactor,
        int internalUomDecimals,
        int deliveryLeadTimeDays,
        Dictionary<string, decimal> taxAmountsByRateCode)
    {
        Line = line;
        ItemLine = itemLine;
        Deliveries = deliveries;
        QuantityIuom = quantityIuom;
        QuantityPuom = quantityPuom;
        PurchaseRate = purchaseRate;
        DiscountType = discountType;
        DiscountValue = discountValue;
        DiscountedRate = discountedRate;
        LandedPrice = landedPrice;
        PurchaseConversionFactor = purchaseConversionFactor;
        InternalConversionFactor = internalConversionFactor;
        InternalUomDecimals = internalUomDecimals;
        DeliveryLeadTimeDays = deliveryLeadTimeDays;
        _taxAmountsByRateCode = taxAmountsByRateCode;
    }

    public IndentPoLine Line { get; }

    public int ItemLine { get; }

    public IReadOnlyList<DeliveryTotals> Deliveries { get; }

    public string ItemCode => Line.Item.String("itemCode");

    public decimal QuantityIuom { get; }

    public decimal QuantityPuom { get; }

    public decimal PurchaseRate { get; }

    public string DiscountType { get; }

    public decimal DiscountValue { get; }

    /// <summary>Basic rate after the item's own discount — the ERP's <c>PIDDISCBSCRT</c>.</summary>
    public decimal DiscountedRate { get; }

    /// <summary>Rate including every tax component, as priced by the ERP.</summary>
    public decimal LandedPrice { get; }

    public decimal PurchaseConversionFactor { get; }

    public decimal InternalConversionFactor { get; }

    public int InternalUomDecimals { get; }

    public int DeliveryLeadTimeDays { get; }

    public decimal GrossAmount => Round2(PurchaseRate * QuantityPuom);

    public decimal AmountAfterDiscount => Round2(DiscountedRate * QuantityPuom);

    public decimal RateStructureAmount => Round2(_taxAmountsByRateCode.Values.Sum());

    public decimal TaxAmountFor(string rateCode) =>
        _taxAmountsByRateCode.TryGetValue(rateCode, out var amount) ? amount : 0m;

    public static LineTotals For(IndentPoLine line, int itemLine)
    {
        ArgumentNullException.ThrowIfNull(line);

        // The item/vendor purchase row is the authority on rate, discount and conversion when it
        // exists; the pending-items row is the fallback. Same precedence the screen applies when it
        // stamps the row after the PUOM lookup.
        var vendorRow = line.VendorPuom;
        var item = line.Item;

        var purchaseRate = vendorRow is null
            ? item.Decimal("purchaseRate")
            : vendorRow.Decimal("pidbpurrt", item.Decimal("purchaseRate"));

        var discountType = vendorRow is null
            ? item.String("discountType", "None")
            : vendorRow.String("piddisctyp", item.String("discountType", "None"));

        var discountValue = vendorRow is null
            ? item.Decimal("discountValue")
            : vendorRow.Decimal("piddiscval", item.Decimal("discountValue"));

        var purchaseConversionFactor = vendorRow is null
            ? item.ConversionFactor("purConvFact")
            : vendorRow.ConversionFactor("pidpurcnvfct");

        var internalConversionFactor = vendorRow is null
            ? item.ConversionFactor("intConvFact")
            : vendorRow.ConversionFactor("pidintcnvfct");

        var purchaseUomDecimals = item.Int("puomdigaftdec");
        var internalUomDecimals = item.Int("iuomdigaftdec");

        // The delivery lead time only ever arrives on the item/vendor row; the pending-items call
        // reports zero.
        var deliveryLeadTimeDays = vendorRow.Int("mivdelldtm", item.Int("mimdelldtm"));

        var deliveries = new List<DeliveryTotals>(line.DeliveryRows.Count);

        foreach (var row in line.DeliveryRows)
        {
            // Order the indent's outstanding quantity, which is what the screen fills in when the
            // planner clicks "fill quantity".
            var quantityIuom = Round(row.Decimal("xindiodqty"), internalUomDecimals);

            if (quantityIuom <= 0m)
            {
                continue;
            }

            var quantityPuom = Round(
                quantityIuom * purchaseConversionFactor / internalConversionFactor,
                purchaseUomDecimals);

            deliveries.Add(new DeliveryTotals(row, quantityIuom, quantityPuom));
        }

        if (deliveries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Item '{item.String("itemCode")}' has no indent line with an outstanding quantity.");
        }

        var totalIuom = deliveries.Sum(delivery => delivery.QuantityIuom);
        var totalPuom = deliveries.Sum(delivery => delivery.QuantityPuom);

        var discountedRate = DiscountRate(purchaseRate, discountType, discountValue);
        var amountAfterDiscount = Round2(discountedRate * totalPuom);

        // Component amounts and the landed price both come from the ERP's pricing call, keyed by
        // rate code. Multiplying its per-unit figure by quantity is the whole of the tax maths here.
        var taxAmountsByRateCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var landedPrice = discountedRate;

        foreach (var component in line.RateComponents.OfType<JsonObject>())
        {
            var rateCode = component.String("msprtcd");

            if (!string.IsNullOrEmpty(rateCode))
            {
                taxAmountsByRateCode[rateCode] = Round2(component.Decimal("rtamt") * totalPuom);
            }

            // Every row carries the same running landed price; the last one is the final figure.
            var stated = component.Decimal("landprice");

            if (stated > 0m)
            {
                landedPrice = stated;
            }
        }

        return new LineTotals(
            line,
            itemLine,
            deliveries,
            totalIuom,
            totalPuom,
            purchaseRate,
            discountType,
            discountValue,
            discountedRate,
            landedPrice,
            purchaseConversionFactor,
            internalConversionFactor,
            internalUomDecimals,
            deliveryLeadTimeDays,
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

    private static decimal Round(decimal value, int decimals) =>
        Math.Round(value, Math.Clamp(decimals, 0, 6), MidpointRounding.AwayFromZero);
}
