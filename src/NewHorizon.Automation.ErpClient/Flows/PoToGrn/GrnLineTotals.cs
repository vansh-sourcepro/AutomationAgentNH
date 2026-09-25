using System.Text.Json.Nodes;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

/// <summary>
/// One GRN line's quantities and money, worked out the way the GRN screen does
/// (<c>grn.component.ts</c> <c>CalculatePrice</c> and <c>rateStructurevaluechange</c>).
/// </summary>
/// <remarks>
/// Quantity is the line's pending quantity, in both units — the confirmed rule. Value is basic
/// price × quantity, less the item discount, less the line's share of any PO-level discount. The tax
/// per component comes from the ERP's own rate engine; the agent only multiplies by quantity.
/// </remarks>
internal sealed class GrnLineTotals
{
    private GrnLineTotals(JsonObject source, int rowId)
    {
        Source = source;
        RowId = rowId;
    }

    public JsonObject Source { get; }

    /// <summary><c>XGRNDJORDNO</c>: the line's position on the GRN, 1-based.</summary>
    public int RowId { get; }

    public string ItemCode => Source.Text("itmcode").Trim();

    public long PoId => Source.Long("xgrndpoid");

    public decimal QuantityPuom { get; private init; }

    public decimal QuantityIuom { get; private init; }

    /// <summary>Line value after item and PO-level discount — <c>purvalue</c> / <c>purchaseRate</c>.</summary>
    public decimal PurchaseValue { get; private init; }

    /// <summary>Per-PUOM rate the tax engine is asked to price.</summary>
    public decimal DiscountedRate { get; private init; }

    /// <summary>Cost per IUOM including non-postable taxes — <c>unitrate</c>.</summary>
    public decimal UnitRate { get; private set; }

    /// <summary>This line's tax rows, ready for <c>rateStructureDetail</c>.</summary>
    public JsonArray TaxRows { get; private set; } = [];

    /// <summary>
    /// The rate structure the line is taxed under: the PO line's own when line-level rate
    /// structures are on, otherwise the one the PO's tax rows were stored with.
    /// </summary>
    public string RateStructureCode { get; private init; } = string.Empty;

    public static GrnLineTotals From(JsonObject line, int rowId, IReadOnlyList<JsonObject> taxTemplate)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(taxTemplate);

        var quantityPuom = GrnPendingQuantity.PendingPuom(line);
        var quantityIuom = GrnPendingQuantity.PendingIuom(line);

        var basicPrice = line.Number("basicprice");
        var itemValue = basicPrice * quantityPuom;

        var discountType = line.Text("disctype").Trim();
        var discountValue = line.Number("discvalue");

        var itemDiscount = discountType.ToUpperInvariant() switch
        {
            "V" or "VALUE" or "AMOUNT" => quantityPuom * discountValue,
            "P" or "PERCENTAGE" => itemValue * discountValue / 100m,
            _ => 0m,
        };

        if (itemDiscount > itemValue)
        {
            throw new ErpBusinessException(
                $"Item {line.Text("itmcode").Trim()}: its discount is larger than its value, so the GRN cannot be priced.",
                $"Discount {itemDiscount} exceeds item value {itemValue} (basicprice {basicPrice} × {quantityPuom}).");
        }

        var afterItemDiscount = itemValue - itemDiscount;

        var poDiscountType = line.Text("pohdiscpa").Trim().ToUpperInvariant();
        var poDiscountValue = line.Number("pohdiscval");
        var poValueBeforeDiscount = line.Number("pohpovalbfdisc");

        var poLevelDiscount = poDiscountType switch
        {
            "V" when poValueBeforeDiscount != 0m => afterItemDiscount * poDiscountValue / poValueBeforeDiscount,
            "P" => afterItemDiscount * poDiscountValue / 100m,
            _ => 0m,
        };

        var purchaseValue = Math.Round(afterItemDiscount - poLevelDiscount, 4, MidpointRounding.AwayFromZero);

        var rateStructure = line.Text("rateStructureCode").Trim();

        if (rateStructure.Length == 0)
        {
            rateStructure = taxTemplate.Select(row => row.Text("taxRateCode").Trim()).FirstOrDefault(code => code.Length > 0)
                ?? string.Empty;
        }

        return new GrnLineTotals(line, rowId)
        {
            QuantityPuom = quantityPuom,
            QuantityIuom = quantityIuom,
            PurchaseValue = purchaseValue,
            DiscountedRate = quantityPuom == 0m ? 0m : Math.Round(purchaseValue / quantityPuom, 4, MidpointRounding.AwayFromZero),
            RateStructureCode = rateStructure,
        };
    }

    /// <summary>
    /// Stamps each of the PO's tax rows for this line with its amount, from the ERP's per-unit
    /// components (<c>msprtcd</c> / <c>rtamt</c>) × quantity, and derives the unit rate.
    /// </summary>
    public void ApplyTaxes(IReadOnlyList<JsonObject> taxTemplate, JsonArray perUnitComponents)
    {
        ArgumentNullException.ThrowIfNull(taxTemplate);
        ArgumentNullException.ThrowIfNull(perUnitComponents);

        var perUnit = perUnitComponents.Rows()
            .Where(component => component.Text("msprtcd").Trim().Length > 0)
            .GroupBy(component => component.Text("msprtcd").Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Number("rtamt"), StringComparer.OrdinalIgnoreCase);

        var rows = new JsonArray();
        var inclusive = 0m;
        var nonPostable = 0m;

        foreach (var template in taxTemplate)
        {
            var row = (JsonObject)template.DeepClone();
            var rateCode = row.Text("rateCode").Trim();
            var amount = Math.Round(
                (perUnit.TryGetValue(rateCode, out var unitAmount) ? unitAmount : 0m) * QuantityPuom,
                2,
                MidpointRounding.AwayFromZero);

            row["rateAmount"] = JsonValue.Create(amount);
            row["itemCode"] = ItemCode;
            row["xgrndpoid"] = JsonValue.Create(PoId);

            if (row.Text("ie").Equals("I", StringComparison.OrdinalIgnoreCase))
            {
                inclusive += amount;
            }
            else if (!row.Flag("postnonpost"))
            {
                // Not posted to a tax account, so it is part of what the stock cost.
                nonPostable += amount;
            }

            rows.Add(row);
        }

        if (PurchaseValue - inclusive <= 0m)
        {
            // The screen refuses the same thing: taxes included in the price swallow all of it.
            throw new ErpBusinessException(
                $"Item {ItemCode}: its inclusive taxes are not less than its value, so the GRN cannot be priced.",
                $"Inclusive tax {inclusive} >= purchase value {PurchaseValue} for item {ItemCode} on PO {PoId}.");
        }

        TaxRows = rows;
        UnitRate = QuantityIuom == 0m
            ? 0m
            : Math.Round((PurchaseValue + nonPostable) / QuantityIuom, 4, MidpointRounding.AwayFromZero);
    }
}
