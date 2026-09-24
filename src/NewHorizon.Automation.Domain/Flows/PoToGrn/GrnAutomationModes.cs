using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.PoToGrn;

/// <summary>
/// Which triggers may start a PO → GRN run. Its own enum rather than Indent → PO's, so the two
/// flows' settings can never be changed by each other's code.
/// </summary>
public enum PoGrnRunMode
{
    /// <summary>"PO-based": runs only when asked — the Run button or an API caller.</summary>
    Api = 1,

    /// <summary>"Timer-based": runs only from the daily schedule.</summary>
    Timer = 2,

    Both = 3,
}

public static class PoGrnRunModeExtensions
{
    /// <summary>
    /// Whether <paramref name="mode"/> admits <paramref name="trigger"/>. A manual run is an operator
    /// override and is always admitted; the master toggle is a separate check.
    /// </summary>
    public static bool Allows(this PoGrnRunMode mode, TriggerSource trigger) => trigger switch
    {
        TriggerSource.Timer => mode is PoGrnRunMode.Timer or PoGrnRunMode.Both,
        TriggerSource.Api => mode is PoGrnRunMode.Api or PoGrnRunMode.Both,
        _ => true,
    };
}

/// <summary>
/// What to do with a PO whose pending lines include a "parameter" item — one the GRN screen needs a
/// person to type batch, heat, inward, serial, mfg-batch, shelf-life or LBT size details for.
/// Confirmed 2026-09-24; see <c>.claude/context/po-to-grn-decisions.md</c>.
/// </summary>
public enum GrnReceiptMode
{
    /// <summary>Receive a PO only when every pending line is parameter-free; otherwise skip it whole.</summary>
    Complete = 0,

    /// <summary>
    /// Receive every PO with at least one parameter-free line, GRN only those lines, and leave the
    /// rest pending on the PO for a person.
    /// </summary>
    Partial = 1,
}

public enum PoGrnReceiptStatus
{
    /// <summary>A GRN was created.</summary>
    Created = 0,

    /// <summary>Nothing was received and it is not a fault — parameter items, dates, nothing pending.</summary>
    Skipped = 1,

    /// <summary>The ERP refused the GRN, or could not be reached for this PO.</summary>
    Failed = 2,
}
