using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

/// <summary>
/// Execution modes for Issue to Shop Floor automation.
/// </summary>
public enum IssueToShopFloorExecutionMode
{
    TimerBased,
    NumberBased,
    Both,
    None
}

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
/// <param name="Mode">
/// The execution mode: <c>timerBased</c>, <c>numberBased</c>, <c>both</c>, or <c>none</c>.
/// </param>
/// <param name="Time">
/// Scheduled execution time (e.g. "14:30" or "14:30:00"). Required for <c>timerBased</c> and <c>both</c> modes.
/// </param>
public sealed record IssueToShopFloorRequest(
    string? IssueType = null,
    string? DocumentNumber = null,
    string? SjoNumber = null,
    bool DryRun = false,
    string? Mode = null,
    string? Time = null)
{
    public string? Number => string.IsNullOrWhiteSpace(DocumentNumber) ? SjoNumber : DocumentNumber;

    /// <summary>The accepted spellings, for the 400 that names them.</summary>
    public const string AcceptedIssueTypes = "sjo, workOrder, salesOaf";

    /// <summary>The accepted mode names, for validation errors.</summary>
    public const string AcceptedModes = "timerBased, numberBased, both, none";

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

    /// <summary>
    /// Reads <see cref="Mode"/>; when blank, returns true with null mode (for caller inference/backward compatibility).
    /// </summary>
    public bool TryGetMode(out IssueToShopFloorExecutionMode? mode, out string? error)
    {
        mode = null;
        error = null;

        if (string.IsNullOrWhiteSpace(Mode))
        {
            return true;
        }

        var text = Mode.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        switch (text.ToUpperInvariant())
        {
            case "TIMERBASED":
            case "TIMER":
                mode = IssueToShopFloorExecutionMode.TimerBased;
                return true;

            case "NUMBERBASED":
            case "NUMBER":
                mode = IssueToShopFloorExecutionMode.NumberBased;
                return true;

            case "BOTH":
                mode = IssueToShopFloorExecutionMode.Both;
                return true;

            case "NONE":
            case "DISABLED":
                mode = IssueToShopFloorExecutionMode.None;
                return true;

            default:
                error = $"'{Mode}' is not a valid mode. Expected one of: {AcceptedModes}.";
                return false;
        }
    }
}

/// <summary>
/// Response when configuring timer-based, both, or none modes.
/// </summary>
public sealed record IssueToShopFloorModeResponse(
    string IssueType,
    string Mode,
    string Status,
    string Message,
    string? ConfiguredTime = null,
    string? DocumentNumber = null,
    bool DryRun = false);

/// <summary>
/// Response for bulk conversion across all discovered eligible documents.
/// </summary>
public sealed record IssueToShopFloorBulkResponse(
    string IssueType,
    string Mode,
    int TotalDiscovered,
    int TotalProcessed,
    int CreatedCount,
    int RefusedCount,
    bool DryRun,
    IReadOnlyList<IssueToShopFloorResult> Results);

