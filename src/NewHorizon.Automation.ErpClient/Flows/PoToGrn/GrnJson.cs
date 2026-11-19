using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

/// <summary>
/// Tolerant reads of ERP JSON rows. The ERP sends the same field as a number on one call and a
/// numeric string on the next, and booleans as <c>true</c>, <c>1</c> or <c>"Y"</c>.
/// </summary>
/// <remarks>This flow's own copy: flows never reference each other's folders.</remarks>
internal static class GrnJson
{
    public static string Text(this JsonObject? row, string property, string fallback = "")
    {
        if (row?[property] is not JsonValue value)
        {
            return fallback;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToJsonString().Trim('"'),
            _ => fallback,
        };
    }

    public static decimal Number(this JsonObject? row, string property, decimal fallback = 0m)
    {
        if (row?[property] is not JsonValue value)
        {
            return fallback;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.Number => value.TryGetValue<decimal>(out var number)
                ? number
                : Parse(value.ToJsonString(), fallback),
            JsonValueKind.String => Parse(value.GetValue<string>(), fallback),
            _ => fallback,
        };
    }

    public static int Whole(this JsonObject? row, string property, int fallback = 0) =>
        (int)Math.Round(row.Number(property, fallback), MidpointRounding.AwayFromZero);

    public static long Long(this JsonObject? row, string property, long fallback = 0) =>
        (long)Math.Round(row.Number(property, fallback), MidpointRounding.AwayFromZero);

    public static bool Flag(this JsonObject? row, string property)
    {
        if (row?[property] is not JsonValue value)
        {
            return false;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => row.Number(property) != 0m,
            JsonValueKind.String => value.GetValue<string>().Trim() is var text
                && (text.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("Y", StringComparison.OrdinalIgnoreCase)
                    || text == "1"),
            _ => false,
        };
    }

    public static DateOnly? Date(this JsonObject? row, string property)
    {
        var raw = row.Text(property).Trim();

        if (raw.Length == 0)
        {
            return null;
        }

        // ISO from a serialised DateTime; dd/MM/yyyy from the procedures that CONVERT(..., 103).
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)
            && raw.Contains('-', StringComparison.Ordinal))
        {
            return DateOnly.FromDateTime(iso);
        }

        return DateTime.TryParseExact(raw, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var british)
            ? DateOnly.FromDateTime(british)
            : null;
    }

    public static IEnumerable<JsonObject> Rows(this JsonNode? node) =>
        node is JsonArray array ? array.OfType<JsonObject>() : [];

    private static decimal Parse(string? raw, decimal fallback) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}

/// <summary>The ERP's April–March financial year string, <c>"yy-yy"</c>, for a date.</summary>
internal static class GrnFinancialYear
{
    public static string For(DateOnly date)
    {
        var startYear = date.Month >= 4 ? date.Year : date.Year - 1;

        return string.Create(CultureInfo.InvariantCulture, $"{startYear % 100:00}-{(startYear + 1) % 100:00}");
    }
}
