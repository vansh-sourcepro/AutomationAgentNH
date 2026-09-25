using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

/// <summary>A PO line's pending quantity in each unit, as the GRN is raised for it.</summary>
/// <remarks>
/// <c>pendinggrniuom</c> from <c>getposearchdetails</c> is always 0: the ERP's <c>GRNRepository</c>
/// reads the SP column as <c>"pend_grn_qty_iuom"</c> while <c>CSP_XPOHead_GetPOSearch</c> returns
/// <c>PEND_GRN_QTY_IUOM</c>, and Dapper's row lookup is case-sensitive and yields null (→ 0) for a
/// missing name. Until the ERP fixes that key, the IUOM figure is rebuilt from the IUOM columns the
/// same row carries, exactly as the SP computes it. A non-zero value from the ERP always wins.
/// </remarks>
internal static class GrnPendingQuantity
{
    public static decimal PendingPuom(JsonObject line) => line.Number("pendinggrnpuom");

    public static decimal PendingIuom(JsonObject line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var reported = line.Number("pendinggrniuom");

        if (reported > 0m)
        {
            return reported;
        }

        var pending = line.Number("poiuomqty") - line.Number("poiuomrcvd") - line.Number("poiuomsc");

        // The SP adds rejections back when the ERP's MSCUPPOREJ is 'Y'. The agent cannot read that
        // setting, but the PUOM figure (which the ERP does send) shows whether it did.
        var rejectedPuom = line.Number("popuomrej");

        if (rejectedPuom != 0m
            && PendingPuom(line) == line.Number("popuomqty") - line.Number("popuomrcvd") - line.Number("popuomsc") + rejectedPuom)
        {
            pending += line.Number("poiuomrej");
        }

        return Math.Max(pending, 0m);
    }
}
