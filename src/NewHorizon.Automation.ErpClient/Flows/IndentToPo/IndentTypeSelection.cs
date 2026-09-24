namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// The set of indent types a caller has allowed to become purchase orders.
/// </summary>
/// <remarks>
/// <para>
/// One definition, used by every entry point, because "which types may be converted" has to mean
/// the same thing whether it arrived on the vendor-driven call, on the convert call, on the
/// read-only eligible list, or on the vendor-driven one.
/// Three separate readings of the same question is how a filter ends up applied on the list and
/// forgotten on the conversion.
/// </para>
/// <para>
/// The selection is an <em>allow-list</em>, never an instruction: selecting all three does not mean
/// "make three orders", it means "any of these three may be converted if it is otherwise eligible".
/// </para>
/// </remarks>
public static class IndentTypeSelection
{
    /// <summary>
    /// The selection as the rest of the flow should see it: de-duplicated, and in a fixed order so
    /// two callers who asked for the same set get the same sweep.
    /// </summary>
    /// <remarks>
    /// Null and empty both mean every type. That is the convention this API has always had — an
    /// absent filter is not a filter. It is deliberately the only case in which an unstated
    /// selection widens rather than narrows, and the only reason it is safe is that the widening
    /// happens once, here, where it can be read.
    /// </remarks>
    public static IReadOnlyList<IndentType> Normalise(IReadOnlyList<IndentType>? selected)
    {
        if (selected is not { Count: > 0 })
        {
            return IndentPoProfile.AllIndentTypes;
        }

        // Ordered by the canonical list rather than by the order the caller happened to type them,
        // and Distinct so ["regular","regular"] is one sweep of Regular rather than two.
        var chosen = selected.ToHashSet();

        return IndentPoProfile.AllIndentTypes.Where(chosen.Contains).ToList();
    }

    /// <summary>
    /// Whether this indent may be converted under that selection. The question is asked again
    /// immediately before conversion, not only during discovery: discovery narrowing the search is
    /// an optimisation, and this is the guarantee.
    /// </summary>
    public static bool Allows(IReadOnlyList<IndentType>? selected, IndentType candidate) =>
        Normalise(selected).Contains(candidate);

    /// <summary>The selection in words, for a log line or a refusal message.</summary>
    public static string Describe(IReadOnlyList<IndentType>? selected) =>
        string.Join(", ", Normalise(selected));
}
