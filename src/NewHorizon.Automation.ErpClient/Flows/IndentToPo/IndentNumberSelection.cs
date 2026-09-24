namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// The set of indent numbers a caller has named, when they want specific indents rather than
/// everything of a type.
/// </summary>
/// <remarks>
/// <para>
/// A second allow-list beside <see cref="IndentTypeSelection"/>, and it behaves the same way:
/// absent means no filter, and naming numbers never means "make this many orders" — it means
/// "only these may be converted, if they are otherwise eligible". Both filters narrow together.
/// </para>
/// <para>
/// A number matches either the whole document number the ERP and the agent both quote —
/// <c>24-25/PI/NF1/000100</c>, the form <c>/eligible</c> returns — or the bare running number on
/// its own, <c>000100</c>. Both are <b>exact</b>, case- and whitespace-insensitive: never a
/// substring, never a prefix, so <c>0001</c> matches nothing and <c>000100</c> can never reach
/// <c>0001000</c>.
/// </para>
/// <para>
/// The bare form is accepted because it is the number people read off a screen, but it is not
/// unique — the same <c>000100</c> exists in other years and at other sites. Where it fits more
/// than one eligible indent the sweep <b>refuses the whole call</b> rather than guessing (see
/// <see cref="Ambiguous"/>), because converting an indent nobody named is the one failure this
/// filter exists to prevent.
/// </para>
/// </remarks>
public static class IndentNumberSelection
{
    /// <summary>
    /// The numbers as the rest of the flow should see them, or null when there is no filter.
    /// </summary>
    /// <remarks>
    /// Blank entries are dropped and duplicates collapse, so <c>["A", "", " a "]</c> is one
    /// number. A list that is empty once cleaned is treated as absent — the same convention
    /// <see cref="IndentTypeSelection.Normalise"/> follows, so an empty array cannot silently
    /// become "convert nothing" in one place and "convert everything" in another.
    /// </remarks>
    public static IReadOnlySet<string>? Normalise(IReadOnlyList<string>? numbers)
    {
        if (numbers is not { Count: > 0 })
        {
            return null;
        }

        var cleaned = numbers
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return cleaned.Count == 0 ? null : cleaned;
    }

    /// <summary>
    /// Whether this indent is one of the named ones. Asked during discovery so an unnamed indent
    /// is never a candidate, and asked again immediately before the first ERP write — discovery
    /// narrowing the search is an optimisation, and this is the guarantee.
    /// </summary>
    public static bool Allows(IReadOnlySet<string>? selected, IndentReference indent)
    {
        ArgumentNullException.ThrowIfNull(indent);

        return selected is null || Matches(selected, indent);
    }

    /// <summary>
    /// Whether either form of this indent's number was named: the whole document number, or the
    /// bare running number on its own.
    /// </summary>
    private static bool Matches(IReadOnlySet<string> selected, IndentReference indent) =>
        selected.Contains(indent.DisplayNumber.Trim()) || selected.Contains(indent.Number.Trim());

    /// <summary>
    /// Named numbers that fit more than one of the examined indents, with the indents they fit.
    /// </summary>
    /// <remarks>
    /// Only a bare running number can do this — <c>000100</c> exists in several years and at
    /// several sites. Converting all of them would order indents the caller never named, and
    /// picking one would be a guess, so the sweep refuses and asks for the whole number instead.
    /// </remarks>
    public static IReadOnlyList<AmbiguousIndentNumber> Ambiguous(
        IReadOnlySet<string>? selected,
        IEnumerable<IndentReference> examined)
    {
        ArgumentNullException.ThrowIfNull(examined);

        if (selected is null)
        {
            return [];
        }

        var candidates = examined.ToList();

        return
        [
            .. selected
                .Select(number => new AmbiguousIndentNumber(
                    number,
                    [.. candidates
                        .Where(indent =>
                            number.Equals(indent.DisplayNumber.Trim(), StringComparison.OrdinalIgnoreCase)
                            || number.Equals(indent.Number.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Select(indent => indent.DisplayNumber)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)]))
                .Where(entry => entry.Candidates.Count > 1),
        ];
    }

    /// <summary>
    /// The named numbers that no examined indent matched — "does not exist", "not authorised",
    /// "wrong type", "not at a configured site" and "beyond the scan window" all land here,
    /// because from the caller's side they are one answer: this number produced nothing.
    /// </summary>
    public static IReadOnlyList<string> Unmatched(
        IReadOnlySet<string>? selected,
        IEnumerable<IndentReference> examined)
    {
        ArgumentNullException.ThrowIfNull(examined);

        if (selected is null)
        {
            return [];
        }

        var candidates = examined.ToList();

        return
        [
            .. selected
                .Where(number => !candidates.Any(indent =>
                    number.Equals(indent.DisplayNumber.Trim(), StringComparison.OrdinalIgnoreCase)
                    || number.Equals(indent.Number.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The selection in words, for a log line or a refusal message.</summary>
    public static string Describe(IReadOnlySet<string>? selected) =>
        selected is null ? "any indent number" : string.Join(", ", selected.Order(StringComparer.OrdinalIgnoreCase));
}

/// <summary>A named number that fits more than one eligible indent, and the indents it fits.</summary>
/// <param name="Number">The number as the caller typed it, trimmed.</param>
/// <param name="Candidates">
/// The whole document numbers it matched, ordered — the forms the caller should pick between.
/// </param>
public sealed record AmbiguousIndentNumber(string Number, IReadOnlyList<string> Candidates);
