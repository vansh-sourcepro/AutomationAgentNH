namespace NewHorizon.Automation.Domain.Flows.IndentToPo;

/// <summary>
/// Which family of indent is being converted. Part of the identity rather than a lookup, because
/// <c>XINDID</c> (material) and <c>XINDAUTOID</c> (service) are keys into different ERP tables and
/// the same number is a plausible value for both.
/// </summary>
/// <remarks>
/// The agent's own copy of the ERP client's <c>IndentType</c>. Domain references no other project,
/// and the two are mapped by name at the edge.
/// </remarks>
public enum IndentKind
{
    Regular = 0,
    Capital = 1,
    Service = 2,
}

/// <summary>
/// The <c>DocumentType</c> a conversion job is enqueued under. Distinct per family so the
/// idempotency key — hash(DocumentType, DocumentId, WorkflowType) — cannot collide between a
/// material and a service indent that happen to share an id.
/// </summary>
public static class IndentDocumentTypes
{
    public const string Regular = "RegularIndent";
    public const string Capital = "CapitalIndent";
    public const string Service = "ServiceIndent";

    public static string For(IndentKind kind) => kind switch
    {
        IndentKind.Regular => Regular,
        IndentKind.Capital => Capital,
        IndentKind.Service => Service,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown indent kind."),
    };
}
