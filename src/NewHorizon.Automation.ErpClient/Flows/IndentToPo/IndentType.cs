namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>Which kind of indent is being converted, and therefore which kind of PO comes out.</summary>
public enum IndentType
{
    Regular,
    Capital,

    /// <summary>
    /// A service indent. Not a third flavour of the same document: it lives in its own ERP tables
    /// (<c>XINDSHDR</c>/<c>XINDSDTL</c>, not <c>XINDHDR</c>/<c>XINDITM</c>), is discovered and read
    /// through its own controller, and becomes a Service PO (<c>XPOSHEAD</c>) through
    /// <c>createServicePOEntry</c> rather than a material PO through <c>POEntry/create</c>. See
    /// <c>ServiceIndentConversion.cs</c>.
    /// </summary>
    Service,
}

/// <summary>
/// Everything that differs between a Regular, a Capital and a Service PO.
/// </summary>
/// <remarks>
/// <para>
/// For Regular and Capital it really is only these two strings. The ERP screen sets
/// <c>strPoType</c> and <c>strDocSubType</c> together (<c>onpoTypechange</c>) and changes nothing
/// else: same create endpoint, same 73-field header, same arithmetic. The document group and the
/// delivery warehouse look different in the two recorded traces, but both are answers the ERP gives
/// back — the group from Document Control keyed on <see cref="DocSubType"/>, the warehouse from the
/// pending-items query keyed on <see cref="PoType"/> — so neither is set here.
/// </para>
/// <para>
/// Service is the odd one out. Only <see cref="DocSubType"/> carries its usual meaning — Document
/// Control numbers a Service PO as <c>PR</c>/<c>SP</c>, so the same
/// <c>getDefaultDocumentDetail</c> lookup serves all three. <see cref="PoType"/> has no ERP
/// counterpart for a service order (its screen's own <c>poType</c> is "IN" versus "PO", meaning
/// indent-based versus direct) and appears only in log lines and messages.
/// </para>
/// </remarks>
public sealed record IndentPoProfile(string PoType, string DocSubType, string IndentDocSubType)
{
    /// <summary>Constant for all three: the document type is always a Purchase Requisition.</summary>
    public const string DocType = "PR";

    /// <summary>The indent's own document type, for the indent endpoints rather than the PO ones.</summary>
    public const string IndentDocType = "IN";

    // Note the third value is not a duplicate of the second: a Regular indent is document sub-type
    // "RG" while the purchase order it becomes is "RP". Capital happens to use "CP" for both, and a
    // Service indent is "SI" against the Service PO's "SP".
    private static readonly IndentPoProfile Regular = new("R", "RP", "RG");
    private static readonly IndentPoProfile Capital = new("C", "CP", "CP");
    private static readonly IndentPoProfile Service = new("S", "SP", "SI");

    /// <summary>True when this indent goes down the Service Indent → Service PO path.</summary>
    public bool IsService => DocSubType == "SP";

    public static IndentPoProfile For(IndentType indentType) => indentType switch
    {
        IndentType.Regular => Regular,
        IndentType.Capital => Capital,
        IndentType.Service => Service,
        _ => throw new ArgumentOutOfRangeException(nameof(indentType), indentType, "Unknown indent type."),
    };

    /// <summary>Every indent type this flow can convert, in the order a sweep considers them.</summary>
    public static IReadOnlyList<IndentType> AllIndentTypes { get; } =
        [IndentType.Regular, IndentType.Capital, IndentType.Service];

    /// <summary>The material indent types — the ones the <c>indentEntryList</c> discovery answers for.</summary>
    public static IReadOnlyList<IndentType> MaterialIndentTypes { get; } =
        [IndentType.Regular, IndentType.Capital];

    /// <summary>
    /// The reverse of <see cref="For"/>, for reading the ERP's own one-letter code back off a
    /// material indent row. Returns null for anything else — the ERP also has "L" for labour
    /// indents, which this flow does not create. Service indents never reach it: they come from a
    /// different list, which carries no type code because every row on it is a service indent.
    /// </summary>
    public static IndentType? Parse(string? erpIndentTypeCode) =>
        erpIndentTypeCode?.Trim().ToUpperInvariant() switch
        {
            "R" => IndentType.Regular,
            "C" => IndentType.Capital,
            _ => null,
        };
}
