namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// Turns authorised indents into Purchase Orders in the ERP.
/// </summary>
/// <remarks>
/// Every effect is an ERP application API call, so ERP validation, permissions, audit and
/// transactions apply — the agent never writes purchase data itself and holds no business rules
/// beyond assembling the payload the ERP's own screen would have sent.
/// </remarks>
public interface IIndentToPoService
{
    /// <summary>
    /// Orders everything a vendor has outstanding, at the configured site. The original entry
    /// point, kept because it is still the right tool when a buyer knows the vendor and wants the
    /// lot; it cannot answer "an indent was just authorised, order it".
    /// </summary>
    /// <exception cref="Application.Erp.ErpBusinessException">
    /// The ERP refused the request, or there is nothing to order. Not retryable.
    /// </exception>
    /// <exception cref="Application.Erp.ErpTransientException">
    /// The ERP could not be reached or answered unhealthily. Worth another attempt.
    /// </exception>
    Task<IndentPoResult> CreateAsync(IndentPoRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the authorised indents that are still open and therefore still need a purchase order.
    /// Reads only — nothing is created, so it is safe to call to see what a sweep would consider.
    /// </summary>
    Task<IReadOnlyList<EligibleIndent>> FindEligibleAsync(
        IndentDiscoveryRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Converts one authorised indent, raising one purchase order per vendor its items resolve to.
    /// </summary>
    /// <remarks>
    /// Safe to call twice. The ERP raises <c>XINDITM.XINDIPOQTY</c> and closes the line as part of
    /// creating the order, so a second call finds nothing outstanding for that indent and reports
    /// it rather than ordering it again.
    /// </remarks>
    Task<IndentConversionResult> CreateFromIndentAsync(
        IndentReference indent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Converts the one indent with this ERP id, after confirming with the ERP that it is still
    /// authorised, still open and still has something outstanding.
    /// </summary>
    /// <param name="indentNumber">
    /// Optional, and worth supplying: it narrows the ERP-side search from the whole authorised
    /// history to a page.
    /// </param>
    Task<IndentConversionResult> ConvertIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        bool dryRun,
        CancellationToken cancellationToken);

    /// <summary>
    /// As above, restricted to indents of these types.
    /// </summary>
    /// <param name="indentTypes">
    /// Which families the id may belong to. Empty or null means all of them — and then an id that
    /// is a live material indent *and* a live service indent is refused rather than guessed at,
    /// because <c>XINDID</c> and <c>XINDAUTOID</c> are keys into different tables and the same
    /// number is a plausible value for both.
    /// </param>
    Task<IndentConversionResult> ConvertIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        IReadOnlyList<IndentType>? indentTypes,
        bool dryRun,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds every authorised indent still awaiting a purchase order and converts them.
    /// </summary>
    Task<IndentSweepResult> ConvertEligibleAsync(
        IndentSweepRequest request,
        CancellationToken cancellationToken);
}
