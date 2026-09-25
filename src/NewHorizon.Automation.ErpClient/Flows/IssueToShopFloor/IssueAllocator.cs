namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// Spreads each item's requirement over its stock rows, warehouse by warehouse: take as much as a
/// row has, move to the next while anything is still needed.
/// </summary>
/// <remarks>
/// <para>
/// This is the Issue to Shop Floor screen's own Fill rule (<c>issuetoshop.component.ts</c>,
/// <c>onFillData</c>), so an automated issue splits exactly the way a person pressing Fill would:
/// requirement 22 over rows of 12 and 200 gives 12 + 10. Rows are used in the order the ERP returns
/// them.
/// </para>
/// <para>
/// A stock row can be offered to more than one line — the same item at two positions of the CBOM —
/// so what an earlier line took is subtracted before a later one sees the row. Without that, two
/// lines would each be promised the whole row and the ERP would refuse the save.
/// </para>
/// <para>
/// Pure: no ERP, no clock. A line that cannot be covered in full is a shortage, and the caller
/// decides what a shortage means (this flow refuses the whole issue).
/// </para>
/// </remarks>
internal static class IssueAllocator
{
    public static Allocation Allocate(IEnumerable<(IssueItem Item, IReadOnlyList<StockRow> Stock)> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var usedByStockId = new Dictionary<long, decimal>();
        var lines = new List<IssueLine>();
        var shortages = new List<IssueShortage>();

        foreach (var (item, stock) in items)
        {
            var required = item.QuantityToIssue;
            if (required <= 0)
            {
                continue;
            }

            var remaining = required;

            foreach (var row in stock)
            {
                if (remaining <= 0)
                {
                    break;
                }

                var alreadyUsed = usedByStockId.GetValueOrDefault(row.StockId);
                var free = row.Available - alreadyUsed;
                if (free <= 0)
                {
                    continue;
                }

                var take = Math.Min(free, remaining);
                remaining -= take;
                usedByStockId[row.StockId] = alreadyUsed + take;

                lines.Add(new IssueLine(
                    item.ItemCode,
                    row.WarehouseCode,
                    row.StockId,
                    row.LineNo,
                    take,
                    item.SjoId,
                    item.WoId,
                    item.RandomNumber,
                    row.InwardNo));
            }

            if (remaining > 0)
            {
                shortages.Add(new IssueShortage(item.ItemCode, item.RandomNumber, required, required - remaining));
            }
        }

        return new Allocation(lines, shortages);
    }

    internal sealed record Allocation(IReadOnlyList<IssueLine> Lines, IReadOnlyList<IssueShortage> Shortages);
}
