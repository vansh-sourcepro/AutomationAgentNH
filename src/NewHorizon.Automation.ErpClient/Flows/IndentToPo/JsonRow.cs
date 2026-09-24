using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Tolerant readers for the ERP's JSON rows.
/// </summary>
/// <remarks>
/// The ERP is not consistent about types across endpoints — <c>puomdigaftdec</c> arrives as a number
/// on one call and the string "0" on another, <c>site</c> is sent as "1" and read back as 1 — and it
/// omits properties rather than sending nulls. Every read here therefore tolerates the other
/// representation and falls back to a default, because a type mismatch in a field the agent only
/// passes through must not abandon a whole PO.
/// </remarks>
internal static class JsonRow
{
    public static string String(this JsonObject? row, string property, string fallback = "")
    {
        if (row?[property] is not JsonValue value)
        {
            return fallback;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                value.ToJsonString().Trim('"'),
            _ => fallback,
        };
    }

    public static decimal Decimal(this JsonObject? row, string property, decimal fallback = 0m)
    {
        if (row?[property] is not JsonValue value)
        {
            return fallback;
        }

        return value.GetValueKind() switch
        {
            // TryGetValue rather than GetValue: a JsonValue backed by int does not hand out a
            // decimal, and the backing type depends on whether the node was parsed or constructed.
            // Falling back to the raw token covers every numeric backing there is.
            JsonValueKind.Number => value.TryGetValue<decimal>(out var number)
                ? number
                : Parse(value.ToJsonString(), fallback),
            JsonValueKind.String => Parse(value.GetValue<string>(), fallback),
            _ => fallback,
        };
    }

    private static decimal Parse(string? raw, decimal fallback) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// A boolean the ERP may have sent as <c>true</c>, as the string "true"/"Y", or as 1. Every
    /// one of those spellings turns up across its endpoints for the same logical flag.
    /// </summary>
    public static bool Flag(this JsonObject? row, string property)
    {
        if (row?[property] is not JsonValue value)
        {
            return false;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetValue<decimal>(out var number) && number != 0m,
            JsonValueKind.String => value.GetValue<string>().Trim() is var text
                && (text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Y", StringComparison.OrdinalIgnoreCase)
                    || text == "1"),
            _ => false,
        };
    }

    public static int Int(this JsonObject? row, string property, int fallback = 0) =>
        (int)Math.Round(row.Decimal(property, fallback), MidpointRounding.AwayFromZero);

    /// <summary>
    /// A conversion factor, never zero. The screen substitutes 1 for a zero factor before dividing;
    /// so does this, because a zero factor would otherwise turn every quantity into infinity.
    /// </summary>
    public static decimal ConversionFactor(this JsonObject? row, string property)
    {
        var value = row.Decimal(property, 1m);

        return value == 0m ? 1m : value;
    }

    /// <summary>
    /// Reformats an ERP date into the <c>MM/dd/yyyy</c> the create payload uses. Anything
    /// unparseable is passed through untouched — the ERP wrote it, and mangling it would be worse
    /// than forwarding it.
    /// </summary>
    public static string ErpDate(this JsonObject? row, string property)
    {
        var raw = row.String(property);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)
            : raw;
    }

    /// <summary>
    /// As <see cref="ErpDate"/>, but null rather than empty when the row carries no date. The
    /// difference matters where the ERP's own property is a nullable date: an empty string binds to
    /// a hard error on its side, while a JSON null binds to the null it means.
    /// </summary>
    public static string? ErpDateOrNull(this JsonObject? row, string property)
    {
        var formatted = row.ErpDate(property);

        return formatted.Length == 0 ? null : formatted;
    }

    /// <summary>The row's value as-is, for fields copied straight through to the create payload.</summary>
    public static JsonNode? Raw(this JsonObject? row, string property) =>
        row?[property] is { } node ? node.DeepClone() : null;

    public static IEnumerable<JsonObject> Objects(this JsonNode? node) =>
        node is JsonArray array
            ? array.OfType<JsonObject>()
            : [];
}
