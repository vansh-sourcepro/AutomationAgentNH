using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

/// <summary>
/// Body of <c>POST /api/automation/issue-to-shop-floor</c> (and its older alias <c>/api/automation/sjo-to-issue</c>).
/// </summary>
/// <param name="IssueType">
/// Which tab of the Issue to Shop Floor screen to raise the issue from: <c>sjo</c> (the default),
/// <c>workOrder</c> or <c>salesOaf</c>. Case-insensitive; the short forms <c>SJ</c>/<c>S</c>,
/// <c>WO</c>/<c>W</c> and <c>OAF</c>/<c>OA</c>/<c>O</c> are accepted too.
/// </param>
/// <param name="DocumentNumber">
/// The SJO, Work Order or Sales OAF number: "26-27/SJ/NF1/000123", "26-27/WO/NF1/000010",
/// "26-27/OF/NF1/000042". An SJO may be given by its bare running number when that is unique.
/// </param>
/// <param name="SjoNumber">The name this field had first; read when <paramref name="DocumentNumber"/> is absent.</param>
/// <param name="DryRun">True checks and plans the issue but creates nothing. Defaults to false.</param>
public sealed record IssueToShopFloorRequest(
    string? IssueType = null,
    string? DocumentNumber = null,
    string? SjoNumber = null,
    bool DryRun = false)
{
    public string? Number => string.IsNullOrWhiteSpace(DocumentNumber) ? SjoNumber : DocumentNumber;

    /// <summary>The accepted spellings, for the 400 that names them.</summary>
    public const string AcceptedIssueTypes = "sjo, workOrder, salesOaf";

    /// <summary>Reads <see cref="IssueType"/>; blank means SJO-wise, the flow's original and default mode.</summary>
    public bool TryGetSource(out IssueSource source)
    {
        source = IssueSource.Sjo;

        var text = IssueType?.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        switch (text.ToUpperInvariant())
        {
            case "SJO":
            case "SJOWISE":
            case "SJ":
            case "S":
                source = IssueSource.Sjo;
                return true;

            case "WORKORDER":
            case "WORKORDERWISE":
            case "WO":
            case "W":
                source = IssueSource.WorkOrder;
                return true;

            case "SALESOAF":
            case "SALESOAFWISE":
            case "OAF":
            case "OA":
            case "O":
                source = IssueSource.SalesOaf;
                return true;

            default:
                return false;
        }
    }
}
