namespace NewHorizon.Automation.Application.Erp;

/// <summary>
/// Outcome of a single ERP call that creates or affects a document.
/// <paramref name="AlreadyExisted"/> distinguishes "I created this" from "query-before-create
/// found it, so a previous attempt had already created it" — the observable proof that a re-run
/// did not duplicate.
/// </summary>
public sealed record ErpDocumentResult(
    string? ErpDocumentRef,
    bool AlreadyExisted,
    string? RequestPayload,
    string? ResponsePayload)
{
    public static ErpDocumentResult Created(string erpDocumentRef, string? request = null, string? response = null) =>
        new(erpDocumentRef, AlreadyExisted: false, request, response);

    public static ErpDocumentResult Existing(string erpDocumentRef, string? request = null, string? response = null) =>
        new(erpDocumentRef, AlreadyExisted: true, request, response);

    /// <summary>The ERP accepted the call but produced no document (nothing to allocate, no shortage).</summary>
    public static ErpDocumentResult NoDocument(string? request = null, string? response = null) =>
        new(ErpDocumentRef: null, AlreadyExisted: false, request, response);
}
