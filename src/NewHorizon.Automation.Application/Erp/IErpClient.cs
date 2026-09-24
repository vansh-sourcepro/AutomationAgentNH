namespace NewHorizon.Automation.Application.Erp;

/// <summary>
/// The agent's only route to the ERP. Every method is an HTTP call to an ERP application API, so
/// ERP validation, permissions, audit and transactions apply to everything the agent does. There
/// is deliberately no method that touches ERP data any other way.
/// </summary>
public interface IErpClient
{
    Task<ErpDocumentResult> DeAllocateAsync(DeAllocationRequest request, CancellationToken cancellationToken);

    Task<ErpDocumentResult> AllocateAsync(AllocationRequest request, CancellationToken cancellationToken);

    Task<ErpDocumentResult> CreateWorkOrderAsync(WorkOrderRequest request, CancellationToken cancellationToken);

    Task<ErpDocumentResult> CreatePurchaseRequisitionAsync(
        PurchaseRequisitionRequest request,
        CancellationToken cancellationToken);

    Task<ErpDocumentResult> CreateLaborRequisitionAsync(
        LaborRequisitionRequest request,
        CancellationToken cancellationToken);

    Task<ErpDocumentResult> AttachOafLinkAsync(OafLinkRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Query-before-create. Called before every creating operation so a resumed job adopts the
    /// document a previous attempt already made instead of creating a second one.
    /// </summary>
    Task<ExistingDocumentResult> FindExistingDocumentAsync(
        ErpOperationRequest request,
        string documentKind,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks whether a transition the ERP automates internally has completed for this document.
    /// Used by <see cref="Domain.Workflows.OperationKind.VerifyOnly"/> and
    /// <see cref="Domain.Workflows.OperationKind.VerifyThenExecute"/> operations in place of a
    /// create call, so the agent never duplicates work the ERP already did behind its own flag.
    /// </summary>
    /// <param name="transitionKind">Which ERP-internal transition to ask about, e.g. "SO-to-OAF".</param>
    Task<ErpAutomationOutcome> VerifyErpAutomationAsync(
        ErpOperationRequest request,
        string transitionKind,
        CancellationToken cancellationToken);

    Task<ShortageResult> GetNetShortageAsync(ErpOperationRequest request, CancellationToken cancellationToken);

    Task<ShortageResult> GetMilShortageAsync(MilShortageRequest request, CancellationToken cancellationToken);

    Task<AllocationStatusResult> GetAllocationStatusAsync(
        ErpOperationRequest request,
        CancellationToken cancellationToken);

    /// <summary>Feeds the reconciliation poll — the safety net behind the ERP's save-time push.</summary>
    Task<IReadOnlyList<PendingDocument>> GetPendingDocumentsAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken);

    // ---- AutoShop cycle ----------------------------------------------------
    // The agent's actual scope: it starts once an OAF exists (SO → OAF is manually authorised
    // inside the ERP) and ends after AutoShop. SJO → CBOM is the ERP's own and is never called
    // here — the site query below returns only SJOs whose BOM already exists, so work created in
    // one cycle is naturally picked up by a later one.

    /// <summary>
    /// The cycle's entry point: OAFs that still need an SJO. An empty list means there is nothing
    /// to do this cycle, which is a normal outcome rather than a failure.
    /// </summary>
    Task<IReadOnlyList<OafAwaitingSjo>> GetOafAwaitingSjoAsync(CancellationToken cancellationToken);

    /// <summary>Creates the SJO for an OAF — the OAF → SJO transition the agent owns.</summary>
    Task<ErpDocumentResult> CreateSjoFromOafAsync(
        ErpOperationRequest request,
        string oafNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// The Site IDs every subsequent call loops over.
    /// <c>GET /api/v1/admin/location/list</c>.
    /// </summary>
    Task<IReadOnlyList<ErpSite>> GetSitesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One site's SJOs, restricted by the ERP to those whose BOM already exists.
    /// <c>GET /api/v1/planning/autoshopsjosequence/GetSJODetails/{siteId}/S</c>.
    /// </summary>
    Task<IReadOnlyList<SjoSequenceRow>> GetSjoSequenceAsync(string siteId, CancellationToken cancellationToken);

    /// <summary>
    /// Submits the sequence for one site, delivery date ascending.
    /// <c>POST /api/v1/planning/autoshopsjosequence/GetSJODetails/{siteId}/S</c>.
    /// </summary>
    Task<SequenceSubmissionResult> SubmitSjoSequenceAsync(
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken);

    /// <summary>One site's AutoShop rows. Endpoint pending from the ERP team.</summary>
    Task<IReadOnlyList<SjoSequenceRow>> GetAutoShopAsync(string siteId, CancellationToken cancellationToken);

    /// <summary>Submits one site's AutoShop rows. Endpoint pending from the ERP team.</summary>
    Task<SequenceSubmissionResult> SubmitAutoShopAsync(
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken);
}
