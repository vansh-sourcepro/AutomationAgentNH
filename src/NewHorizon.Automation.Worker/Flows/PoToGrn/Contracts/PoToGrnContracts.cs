using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Contracts;

/// <summary>The PO → GRN settings row, as <c>GET/PUT /api/automation/grn-automation</c> answers it.</summary>
public sealed record GrnAutomationConfigResponse(
    bool IsActive,
    string RunMode,
    TimeOnly? ScheduleTime,
    string ReceiptMode,
    string? InvoiceNumber,
    string? Sites,
    IReadOnlyList<string> PoTypes,
    string? PoNumbers,
    bool DryRun,
    int? MaxPosPerRun,
    DateOnly? LastScheduledRunDate,
    DateTimeOffset? LastTriggeredAtUtc,
    string? LastRunStatus,
    Guid? LastRunReference,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>
/// A partial update: every field is optional and an absent one is left alone.
/// </summary>
/// <param name="RunMode"><c>Api</c> (PO-based), <c>Timer</c> or <c>Both</c>.</param>
/// <param name="ScheduleTime"><c>HH:mm</c>, local time. Ignored in <c>Api</c> mode.</param>
/// <param name="ReceiptMode"><c>Complete</c> or <c>Partial</c>.</param>
/// <param name="InvoiceNumber">Stamped on every GRN. Blank clears it, which stops runs.</param>
/// <param name="Sites">Comma-separated site ids; blank clears the list.</param>
public sealed record UpdateGrnAutomationRequest(
    bool? IsActive = null,
    string? RunMode = null,
    string? ScheduleTime = null,
    bool ClearScheduleTime = false,
    string? ReceiptMode = null,
    string? InvoiceNumber = null,
    string? Sites = null,
    bool? DryRun = null,
    int? MaxPosPerRun = null,
    bool ClearMaxPosPerRun = false);

/// <summary>The master on/off switch: <c>PUT /api/automation/grn-automation/enabled</c>.</summary>
public sealed record SetGrnAutomationEnabledRequest(bool Enabled);

/// <summary>
/// Which PO types every run is limited to: <c>PUT /api/automation/grn-automation/po-types</c>.
/// </summary>
/// <param name="PoTypes"><c>Regular</c> and/or <c>Capital</c>; empty means both.</param>
public sealed record SetGrnPoTypesRequest(IReadOnlyList<string>? PoTypes);

/// <summary>
/// Which POs every run is limited to: <c>PUT /api/automation/grn-automation/po-numbers</c>.
/// </summary>
/// <param name="PoNumbers">
/// Comma-separated whole (<c>26-27/TE/NF1/000190</c>) or bare running numbers; blank means every
/// eligible PO.
/// </param>
public sealed record SetGrnPoNumbersRequest(string? PoNumbers);

/// <summary>
/// The body of <c>POST /api/automation/po-to-grn</c>. Every field optional: an empty body receives
/// every eligible authorised PO under the saved settings.
/// </summary>
/// <param name="PoIds">Only these POs, by ERP id (<c>POHAUTOID</c>).</param>
/// <param name="PoNumbers">Only these POs, by whole number or bare running number.</param>
/// <param name="PoTypes"><c>Regular</c> and/or <c>Capital</c>; empty means both.</param>
/// <param name="ReceiptMode">Overrides the saved receipt mode for this run only.</param>
public sealed record ReceivePosRequest(
    IReadOnlyList<long>? PoIds = null,
    IReadOnlyList<string>? PoNumbers = null,
    IReadOnlyList<int>? Sites = null,
    IReadOnlyList<string>? PoTypes = null,
    string? ReceiptMode = null,
    int? MaxPos = null,
    bool DryRun = false)
{
    // Forgiving about case, strict about names: on an endpoint that creates stock receipts, a
    // mistyped filter must be a refusal, not a silent "receive everything".
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Why the body could not be read, or null when it was read cleanly.</summary>
    internal string? BindingError { get; private init; }

    public static async ValueTask<ReceivePosRequest?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.HasJsonContentType() || context.Request.ContentLength is 0)
        {
            return null;
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<ReceivePosRequest>(Strict);
        }
        catch (JsonException exception)
        {
            return new ReceivePosRequest { BindingError = exception.Message };
        }
    }
}

public sealed record PoGrnResultResponse(
    long PoId,
    string PoNumber,
    string PoType,
    int SiteId,
    string VendorCode,
    int WarehouseId,
    string Status,
    bool Planned,
    long? GrnId,
    string? GrnNumber,
    int LinesReceived,
    int LinesSkipped,
    IReadOnlyList<string> Notes);

public sealed record ReceivePosResponse(
    string Trigger,
    bool DryRun,
    string ReceiptMode,
    IReadOnlyList<string> PoTypes,
    Guid? RunId,
    int Examined,
    int GrnsCreated,
    int GrnsPlanned,
    string? StoppedReason,
    IReadOnlyList<string> NotFound,
    IReadOnlyList<PoGrnResultResponse> Results);
