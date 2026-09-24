using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;

/// <summary>
/// Asks for purchase orders from a vendor's outstanding indent lines.
/// </summary>
/// <param name="IndentTypes">
/// The indent types this call may convert: any of "Regular", "Capital" and "Service", in any
/// combination. Case-insensitive, duplicates collapse, and an unselected type is never converted.
/// Absent or empty means every type — an absent filter is not a filter.
/// <para>
/// One purchase order is raised per selected type that has anything outstanding, because Regular
/// and Capital are separate documents in the ERP even for the same vendor. "Service" is accepted
/// but skipped with a note: this entry point runs the material PO sequence, and the ERP has no
/// vendor-first "what does this vendor have outstanding" question for service indents. To convert
/// service indents use <c>/convert</c>.
/// </para>
/// </param>
/// <param name="IndentType">
/// The singular form this endpoint shipped with, still honoured. Naming a type here and in
/// <paramref name="IndentTypes"/> is not an error; the two are read as one set.
/// </param>
/// <param name="VendorCode">
/// Optional. Blank uses <c>AutomationAgent:PurchaseOrder:DefaultVendorCode</c> — the pending-indent
/// query is vendor-driven, so one of the two has to supply a vendor.
/// </param>
public sealed record CreatePurchaseOrderRequest(
    IReadOnlyList<string>? IndentTypes = null,
    string? IndentType = null,
    string? VendorCode = null);

/// <param name="PoNumber">The ERP's document number, e.g. "26-27/TE/NF1/000002".</param>
/// <param name="PoId">The ERP's surrogate key for the order.</param>
public sealed record CreatePurchaseOrderResponse(
    string PoNumber,
    long PoId,
    string VendorCode,
    int ItemCount);

/// <summary>
/// What a vendor-driven call produced, across the indent types it was allowed to convert.
/// </summary>
/// <param name="IndentTypes">
/// The selection as it was understood — de-duplicated and expanded when nothing was named — so the
/// caller can see which types this run was actually willing to convert.
/// </param>
/// <param name="Notes">
/// Why a selected type produced nothing: the vendor had nothing outstanding of that type, or it is
/// a type this entry point cannot serve. Empty when every selected type produced an order.
/// </param>
public sealed record CreatePurchaseOrdersResponse(
    IReadOnlyList<string> IndentTypes,
    IReadOnlyList<CreatePurchaseOrderResponse> PurchaseOrders,
    IReadOnlyList<string> Notes);

/// <summary>One authorised indent that still needs a purchase order.</summary>
public sealed record EligibleIndentResponse(
    long IndentId,
    string IndentNumber,
    string IndentType,
    int SiteId,
    string SiteCode,
    string Status,
    DateOnly? IndentDate,
    string RequestedBy);

/// <summary>
/// Asks for one or more authorised indents to be converted.
/// </summary>
/// <param name="IndentId">
/// Convert exactly this indent — the ERP's <c>XINDID</c> for a material indent or its
/// <c>XINDAUTOID</c> for a service one, as returned by the eligible list. Null
/// converts every eligible indent the sweep finds.
/// </param>
/// <param name="Sites">Optional override of <c>AutomationAgent:PurchaseOrder:Sites</c>.</param>
/// <param name="IndentTypes">
/// The indent types this run may convert: any of "Regular", "Capital" and "Service", in any
/// combination — <c>["regular","service"]</c> converts Regular and Service indents and leaves
/// Capital ones alone. Case-insensitive, duplicates collapse. Absent or empty means every type.
/// <para>
/// It is a strict allow-list applied before any purchase order work: an unselected type is not
/// filtered out of the results, it is never asked about — the ERP list behind it is not even
/// called — and the conversion refuses it again on the way in.
/// </para>
/// <para>
/// With <paramref name="IndentId"/> set it also disambiguates, since a material and a service
/// indent can share an id.
/// </para>
/// </param>
/// <param name="IndentType">
/// The singular form this endpoint shipped with, still honoured, and read together with
/// <paramref name="IndentTypes"/> as one set.
/// </param>
/// <param name="MaxIndents">Ceiling on how many indents one call may convert.</param>
/// <param name="DryRun">Report what would be created without creating it.</param>
/// <param name="VendorCode">
/// Not used here, and declared only so it can be refused. This path used to be the vendor-driven
/// form; it is now the conversion trigger, and the vendor-driven form moved to
/// <c>/api/automation/indent-to-po/vendor</c>. Without this, an old caller posting a vendor code
/// would silently get a sweep of every authorised indent instead of one vendor's order — the kind
/// of migration that is only noticed by the purchase orders it leaves behind.
/// </param>
/// <param name="IndentNumbers">
/// Convert only these indents. Either form is accepted: the whole number the ERP and this API both
/// quote — <c>24-25/PI/NF1/000100</c>, what <c>/eligible</c> returns — or the bare running number
/// on its own, <c>000100</c>. Absent or empty converts every eligible indent of the selected types,
/// which is what this endpoint has always done.
/// <para>
/// A second allow-list beside <paramref name="IndentTypes"/>, and it narrows with it: naming two
/// numbers and <c>["Regular"]</c> converts those two if they are Regular, and nothing else. Both
/// forms match exactly, case- and whitespace-insensitively — never as a substring or a prefix.
/// </para>
/// <para>
/// A bare running number is not unique: the same <c>000100</c> exists in other years and at other
/// sites. Where one fits more than a single eligible indent the call is refused with a 400 naming
/// the candidates, because ordering an indent nobody named is the failure this filter exists to
/// prevent — give the whole number to resolve it.
/// </para>
/// <para>
/// A number that matches nothing is reported in <c>notFound</c> rather than failing the call: one
/// unknown number must not stop the others converting.
/// </para>
/// </param>
public sealed record ConvertIndentsRequest(
    long? IndentId = null,
    IReadOnlyList<int>? Sites = null,
    IReadOnlyList<string>? IndentTypes = null,
    string? IndentType = null,
    IReadOnlyList<string>? IndentNumbers = null,
    int? MaxIndents = null,
    bool DryRun = false,
    string? VendorCode = null)
{
    /// <summary>
    /// Forgiving about case, strict about names. <c>indenttypes</c> is the same parameter as
    /// <c>indentTypes</c>; <c>indentnumber</c> is not a parameter at all and is refused.
    /// </summary>
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Why the body could not be read, or null when it was read cleanly.</summary>
    internal string? BindingError { get; private init; }

    /// <summary>
    /// Reads the body strictly, so a property this endpoint does not know is refused instead of
    /// discarded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The defect this exists for: <c>indentnumber</c> silently bound to nothing, the request
    /// became "no filter", and a caller who named one indent got every eligible indent converted
    /// — twenty real purchase orders from a single mistyped letter. On an endpoint that spends
    /// money, an unrecognised field has to be a refusal.
    /// </para>
    /// <para>
    /// Scoped to this type deliberately. Configuring the host's JSON globally would also tighten
    /// <c>/api/process-jobs</c>, where ignoring unknown fields is deliberate and tested.
    /// </para>
    /// <para>
    /// The failure is carried on the request rather than thrown: a <c>BadHttpRequestException</c>
    /// from here is turned into a 500 by the exception handler, and a caller's typo is not a
    /// server fault.
    /// </para>
    /// </remarks>
    public static async ValueTask<ConvertIndentsRequest?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.HasJsonContentType() || context.Request.ContentLength is 0)
        {
            // No body at all is how the trigger is called when every default suits, and the
            // handler already treats null as "an empty request".
            return null;
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<ConvertIndentsRequest>(Strict);
        }
        catch (JsonException exception)
        {
            return new ConvertIndentsRequest { BindingError = exception.Message };
        }
    }
}

/// <param name="Notes">
/// Why anything on the indent was not ordered — a missing item/vendor record, a warehouse the ERP
/// cannot offer, or the ERP's own refusal. Empty when the indent converted cleanly.
/// </param>
public sealed record IndentConversionResponse(
    long IndentId,
    string IndentNumber,
    string IndentType,
    int SiteId,
    bool Converted,
    IReadOnlyList<CreatePurchaseOrderResponse> PurchaseOrders,
    IReadOnlyList<string> Notes);

/// <summary>One indent that was examined and produced no purchase order, and why.</summary>
/// <remarks>
/// Pulled out of <see cref="ConvertIndentsResponse.Indents"/> rather than replacing it: the same
/// indent is in both, but a caller checking "did anything not go through" should not have to filter
/// a list to find out. A dry run has no skipped indents by this definition — it creates nothing by
/// design — so the list is only populated on a real run.
/// </remarks>
public sealed record SkippedIndentResponse(
    long IndentId,
    string IndentNumber,
    string IndentType,
    int SiteId,
    IReadOnlyList<string> Reasons);

/// <param name="PurchaseOrdersPlanned">
/// What a dry run worked out it would create. Equals the created count otherwise, so a caller can
/// read this one field either way.
/// </param>
/// <param name="Skipped">
/// The examined indents that produced nothing, with the reason for each — a missing item/vendor
/// record, a site whose Document Control cannot number the order, the ERP's own refusal. Empty on a
/// clean run, and empty on a dry run.
/// </param>
/// <param name="IndentTypes">
/// The selection as it was understood — de-duplicated and expanded when nothing was named. Worth
/// reading back: it is the difference between "the sweep found no Capital indents" and "the sweep
/// was never allowed to look for Capital indents".
/// </param>
/// <param name="IndentNumbers">
/// The numbers this run was restricted to, as they were understood — trimmed and de-duplicated.
/// Empty when the caller named none, which means every eligible indent was in scope. Read it the
/// way <paramref name="IndentTypes"/> is read: it separates "that indent produced nothing" from
/// "that indent was never in scope".
/// </param>
/// <param name="NotFound">
/// Named numbers that matched no examined indent. One list rather than several, because from the
/// caller's side "does not exist", "not authorised", "wrong type", "not at a configured site" and
/// "beyond the scan window" are the same answer: this number produced nothing. Kept separate from
/// <paramref name="Skipped"/>, whose entries carry an indent id, type and site that an unmatched
/// number does not have.
/// </param>
public sealed record ConvertIndentsResponse(
    int Examined,
    int PurchaseOrdersCreated,
    int PurchaseOrdersPlanned,
    bool DryRun,
    IReadOnlyList<string> IndentTypes,
    IReadOnlyList<string> IndentNumbers,
    IReadOnlyList<IndentConversionResponse> Indents,
    IReadOnlyList<SkippedIndentResponse> Skipped,
    IReadOnlyList<string> NotFound);
