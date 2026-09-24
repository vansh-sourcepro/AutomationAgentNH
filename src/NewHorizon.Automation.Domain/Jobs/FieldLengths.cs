namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// The column widths the domain trims to, so the entity and the mapping cannot disagree.
/// </summary>
/// <remarks>
/// An entity that trims a message to a length the column does not have is the worst of both:
/// either the save fails on a long ERP message and takes the whole row with it, or a field is
/// shortened for no reason. The widths live here and both sides read them, the same way
/// <see cref="IdempotencyKey.Length"/> already works.
/// </remarks>
public static class FieldLengths
{
    /// <summary>A message meant for a person — a refusal, a failure reason, a remark.</summary>
    public const int Message = 1000;

    /// <summary>An ERP document reference: a purchase order number, or a few of them.</summary>
    public const int DocumentRef = 100;
}
