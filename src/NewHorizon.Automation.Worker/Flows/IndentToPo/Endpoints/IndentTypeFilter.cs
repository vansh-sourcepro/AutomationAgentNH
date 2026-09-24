using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Endpoints;

/// <summary>
/// Reads the caller's indent-type allow-list off the wire.
/// </summary>
/// <remarks>
/// The transport half of <see cref="IndentTypeSelection"/>: this turns whatever the caller wrote
/// into indent types or into a refusal, and everything after it deals in the enum. Kept out of the
/// endpoint bodies because every endpoint asks the same question and a filter that is parsed
/// slightly differently in each place is a filter that eventually leaks.
/// </remarks>
internal static class IndentTypeFilter
{
    /// <summary>What went wrong, or the selection. Never both, never neither.</summary>
    internal readonly record struct Result(IReadOnlyList<IndentType>? Selection, string? Error)
    {
        public bool IsValid => Error is null;
    }

    /// <summary>
    /// Parses the plural <c>indentTypes</c> and the singular <c>indentType</c> together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are accepted so the singular form that shipped first keeps working; naming the same
    /// type twice across the two is not an error, because the answer is the same set either way.
    /// Values are case-insensitive, may be repeated (<c>?indentTypes=regular&amp;indentTypes=service</c>)
    /// or comma-separated (<c>?indentTypes=regular,service</c>), and duplicates collapse.
    /// </para>
    /// <para>
    /// Nothing at all — both absent, or an empty array — is not a filter, and means every type.
    /// That is the convention every entry point shares, so an empty selection is answered here the
    /// same way an absent one is rather than being turned into a new kind of error.
    /// </para>
    /// </remarks>
    public static Result Parse(IReadOnlyList<string>? indentTypes, string? indentType)
    {
        var written = new List<string>();

        if (indentTypes is not null)
        {
            // One comma-separated value and several repeated ones are the same request written two
            // ways; a query string can carry either and a JSON array can carry both.
            written.AddRange(indentTypes
                .Where(value => value is not null)
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        if (!string.IsNullOrWhiteSpace(indentType))
        {
            written.Add(indentType.Trim());
        }

        if (written.Count == 0)
        {
            // Not a filter. Normalise turns null into every type, in one documented place.
            return new Result(null, null);
        }

        var parsed = new List<IndentType>(written.Count);
        var rejected = new List<string>();

        foreach (var value in written)
        {
            // Enum.TryParse would also accept "0", "1" and "Regular, Capital"; an ordinal that
            // silently means Regular is a worse answer than a refusal that names the mistake.
            var match = IndentPoProfile.AllIndentTypes
                .Cast<IndentType?>()
                .FirstOrDefault(candidate =>
                    candidate!.Value.ToString().Equals(value, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                rejected.Add(value);
                continue;
            }

            parsed.Add(match.Value);
        }

        if (rejected.Count > 0)
        {
            return new Result(
                null,
                $"{Quote(rejected)} {(rejected.Count == 1 ? "is not an indent type" : "are not indent types")}. "
                + $"Expected any of: {string.Join(", ", IndentPoProfile.AllIndentTypes)}.");
        }

        // De-duplicated and canonically ordered by Normalise, so ["service","regular","service"]
        // and ["regular","service"] are the same selection and each type is swept once.
        return new Result(IndentTypeSelection.Normalise(parsed), null);
    }

    private static string Quote(IReadOnlyList<string> values) =>
        string.Join(", ", values.Select(value => $"'{value}'"));
}
