using System.Globalization;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.Application.Erp;

/// <summary>
/// A site (location) in the client's ERP. One installation serves one client, but that client has
/// several sites, and every AutoShop call is scoped to one of them.
/// </summary>
/// <remarks>
/// From <c>GET /api/v1/admin/location/list</c>, which the ERP team is modifying. Fields beyond the
/// identifier are provisional until that response is confirmed.
/// </remarks>
public sealed record ErpSite(string SiteId, string? Name);

/// <summary>
/// An OAF awaiting SJO creation — the agent's entry point, since everything before this is
/// manually authorised inside the ERP.
/// </summary>
public sealed record OafAwaitingSjo(string OafNumber, string? SiteId);

/// <summary>Outcome of submitting a sorted SJO sequence for one site.</summary>
public sealed record SequenceSubmissionResult(int RowsSubmitted, string? Reference);

/// <summary>
/// Which JSON properties on an SJO row carry the values the agent acts on.
/// </summary>
/// <remarks>
/// <para>
/// The agent reads exactly two properties and writes exactly one; everything else in the row is
/// carried through untouched. Naming them here rather than in a typed model is what allows that —
/// see <see cref="SjoSequenceRow"/>.
/// </para>
/// <para>
/// <b>These names are not confirmed.</b> The ERP team has not yet supplied the row shape for
/// <c>GetSJODetails</c>, nor said which property the agent must set to true before submitting. They
/// are bound from <c>AutomationAgent:AutoShop</c> so a correction is a configuration change on the
/// server, not a rebuild.
/// </para>
/// </remarks>
/// <remarks>
/// Declared with init-only properties rather than positional parameters on purpose: the options
/// factory creates this type with <c>Activator.CreateInstance&lt;T&gt;()</c>, which needs a real
/// parameterless constructor. Optional positional parameters satisfy the compiler but throw at run
/// time in any host that has not registered the map.
/// </remarks>
public sealed record AutoShopFieldMap
{
    public static readonly AutoShopFieldMap Default = new();

    /// <summary>Identifies the row in logs and as the sort tie-break.</summary>
    public string SjoNumber { get; init; } = "sjoNumber";

    /// <summary>The sort key — ascending delivery date <i>is</i> the sequence.</summary>
    public string DeliveryDate { get; init; } = "deliveryDate";

    /// <summary>The property the agent sets before submitting.</summary>
    public string SelectionFlag { get; init; } = "isSelected";

    /// <summary>The value to set it to.</summary>
    public bool SelectionValue { get; init; } = true;
}

/// <summary>
/// One SJO row for a site, held as the JSON the ERP actually returned.
/// </summary>
/// <remarks>
/// <para>
/// This workflow is a pass-through: the agent GETs the rows, sorts them, sets one flag, and POSTs
/// the same body back for the ERP to act on. Modelling the row as a typed record would mean every
/// property the agent does not know about is dropped on the way back — silent data loss in the one
/// place where fidelity is the whole point. So the payload is kept verbatim and only the mapped
/// properties are read or written.
/// </para>
/// <para>
/// The ERP owns all the business logic here. The agent's only deliberate mutation is
/// <see cref="MarkForSubmission"/>.
/// </para>
/// </remarks>
public sealed class SjoSequenceRow
{
    private readonly AutoShopFieldMap _fields;

    private SjoSequenceRow(JsonObject payload, AutoShopFieldMap fields)
    {
        Payload = payload;
        _fields = fields;
    }

    /// <summary>The row exactly as the ERP sent it, plus whatever the agent has set on it.</summary>
    public JsonObject Payload { get; }

    public string? SjoNumber => Payload[_fields.SjoNumber]?.GetValue<string>();

    /// <summary>
    /// Null when the property is absent, null, or unparseable — all of which sort last rather than
    /// failing the row, because an incomplete record must not stop a whole site from sequencing.
    /// </summary>
    public DateTimeOffset? DeliveryDate
    {
        get
        {
            if (Payload[_fields.DeliveryDate] is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue<DateTimeOffset>(out var typed))
            {
                return typed;
            }

            return value.TryGetValue<string>(out var text)
                && DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed)
                ? parsed
                : null;
        }
    }

    /// <summary>Wraps a row the ERP returned, leaving every property in place.</summary>
    public static SjoSequenceRow FromJson(JsonObject payload, AutoShopFieldMap? fields = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new SjoSequenceRow(payload, fields ?? AutoShopFieldMap.Default);
    }

    /// <summary>Builds a row from the values the agent cares about. For tests and fakes.</summary>
    public static SjoSequenceRow Create(
        string sjoNumber,
        DateTimeOffset? deliveryDate,
        AutoShopFieldMap? fields = null)
    {
        var map = fields ?? AutoShopFieldMap.Default;

        var payload = new JsonObject
        {
            [map.SjoNumber] = sjoNumber,
            [map.DeliveryDate] = deliveryDate is { } date
                ? JsonValue.Create(date.ToString("O", CultureInfo.InvariantCulture))
                : null,
        };

        return new SjoSequenceRow(payload, map);
    }

    /// <summary>
    /// Sets the flag the ERP looks for. The agent's one write to the payload — everything else it
    /// received is returned unchanged.
    /// </summary>
    public void MarkForSubmission() =>
        Payload[_fields.SelectionFlag] = JsonValue.Create(_fields.SelectionValue);
}
