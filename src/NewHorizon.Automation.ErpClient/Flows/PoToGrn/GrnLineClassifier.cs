using System.Text.Json.Nodes;
using NewHorizon.Automation.Domain.Flows.PoToGrn;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

/// <summary>Which of a PO's lines the agent may receive, and why the rest are left for a person.</summary>
/// <param name="SkipReason">Set when nothing on this PO/warehouse may be received.</param>
internal sealed record GrnLineSelection(
    IReadOnlyList<JsonObject> Receivable,
    IReadOnlyList<string> SkippedLines,
    string? SkipReason);

/// <summary>
/// Decides, line by line, what the GRN screen would need a person to type — the rule confirmed on
/// 2026-09-24 (<c>.claude/context/po-to-grn-decisions.md</c>).
/// </summary>
/// <remarks>
/// Pure, and reads nothing but the <c>getposearchdetails</c> (mode <c>DIS</c>) row: the item-master
/// flags <c>MIMBCHREQD</c>, <c>MIMINWDREQ</c>, <c>MIMHEATREQ</c>, <c>MIMITMSRREQD</c>,
/// <c>MIMMFGREQ</c> and the class's shelf-life flag already travel on every line. None of those
/// values exist anywhere in the ERP before receipt, so an agent that filled them would be inventing
/// them.
/// </remarks>
internal static class GrnLineClassifier
{
    /// <param name="otherBlocker">
    /// A further reason a line cannot be received that depends on more than the row — no tax rows
    /// on the PO, no warehouse the agent's user may receive into. Treated exactly like a parameter
    /// item, so Complete/Partial means the same thing whatever stopped the line.
    /// </param>
    public static GrnLineSelection Select(
        IReadOnlyList<JsonObject> lines,
        GrnReceiptMode mode,
        Func<JsonObject, string?>? otherBlocker = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Any(line => line.Text("blkpotype").Trim().Equals("B", StringComparison.OrdinalIgnoreCase)))
        {
            // A blanket PO has no fixed quantity to receive in full; the ERP reports its pending
            // quantity as zero on purpose.
            return new GrnLineSelection([], [], "This is a blanket PO, which has no fixed quantity to receive; a person must receive it.");
        }

        var pending = lines
            .Where(line => GrnPendingQuantity.PendingPuom(line) > 0m && GrnPendingQuantity.PendingIuom(line) > 0m)
            .ToList();

        if (pending.Count == 0)
        {
            // Name what the ERP actually said, so "nothing pending" can be told apart from "the
            // agent read the wrong field" — the two look identical otherwise.
            var sample = lines.Count == 0
                ? string.Empty
                : $" (e.g. item {lines[0].Text("itmcode").Trim()}: pendinggrnpuom={lines[0].Text("pendinggrnpuom", "missing")}, "
                    + $"pendinggrniuom={lines[0].Text("pendinggrniuom", "missing")}, "
                    + $"IUOM pending from PO columns={GrnPendingQuantity.PendingIuom(lines[0])})";

            return new GrnLineSelection(
                [],
                [],
                lines.Count == 0
                    ? "Nothing is left to receive on this PO."
                    : $"The ERP returned {lines.Count} line(s) for this PO but none with a pending quantity{sample}.");
        }

        var receivable = new List<JsonObject>(pending.Count);
        var skipped = new List<string>();

        foreach (var line in pending)
        {
            var reason = ParameterReason(line) ?? otherBlocker?.Invoke(line);

            if (reason is null)
            {
                receivable.Add(line);
            }
            else
            {
                skipped.Add(reason);
            }
        }

        if (skipped.Count > 0 && mode == GrnReceiptMode.Complete)
        {
            return new GrnLineSelection(
                [],
                skipped,
                "Receipt mode is Complete and this PO has line(s) a person must fill in: " + string.Join(" ", skipped));
        }

        if (receivable.Count == 0)
        {
            return new GrnLineSelection(
                [],
                skipped,
                "Every pending line needs details a person must fill in: " + string.Join(" ", skipped));
        }

        return new GrnLineSelection(receivable, skipped, null);
    }

    /// <summary>Null when the line needs nothing typed; otherwise what it needs, naming the item.</summary>
    public static string? ParameterReason(JsonObject line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var needs = new List<string>();

        if (line.Flag("mimbchreqd"))
        {
            needs.Add("a batch number");
        }

        if (line.Flag("miminwdreq"))
        {
            needs.Add("an inward number");
        }

        if (line.Flag("mimheatreq"))
        {
            needs.Add("a heat number");
        }

        if (line.Flag("mimitmsrreqd"))
        {
            needs.Add("serial numbers");
        }

        if (line.Flag("mimmfgreq"))
        {
            needs.Add("a manufacturing batch number");
        }

        if (line.Flag("isShelfLife"))
        {
            needs.Add("a shelf-life expiry date");
        }

        // LBT (dimensioned) lines are received by size, typed on the screen.
        if (line.Text("xgrndsize").Trim().Length > 0)
        {
            needs.Add("its LBT size details");
        }

        return needs.Count == 0
            ? null
            : $"Item {line.Text("itmcode").Trim()} needs {string.Join(", ", needs)}.";
    }
}
