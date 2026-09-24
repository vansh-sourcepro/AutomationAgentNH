using System.Collections.Concurrent;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// An in-memory ERP that remembers what it created. Counting creates per document kind is how the
/// tests prove that a resumed or retried job produces no duplicate work orders or requisitions.
/// </summary>
public sealed class FakeErpClient : IErpClient
{
    private readonly ConcurrentDictionary<string, string> _documents = new();

    /// <summary>How many times each document kind was actually created.</summary>
    public ConcurrentDictionary<string, int> CreateCounts { get; } = new();

    /// <summary>Net shortage the ERP reports; zero makes creating operations skip.</summary>
    public decimal NetShortage { get; set; } = 5m;

    public decimal MilShortage { get; set; } = 5m;

    /// <summary>Drives the work-order precondition.</summary>
    public bool ChildrenAllocated { get; set; } = true;

    /// <summary>Whether the ERP's own flag-driven automation is on.</summary>
    public bool ErpAutomationEnabled { get; set; } = true;

    /// <summary>Whether the ERP has finished the transition it automates itself.</summary>
    public bool ErpAutomationCompleted { get; set; } = true;

    /// <summary>Document kinds that should throw on the next create, to simulate ERP failure.</summary>
    public ConcurrentDictionary<string, Queue<Exception>> ScriptedFailures { get; } = new();

    public IReadOnlyDictionary<string, string> Documents => _documents;

    public void FailNext(string documentKind, params Exception[] exceptions)
    {
        var queue = ScriptedFailures.GetOrAdd(documentKind, _ => new Queue<Exception>());
        foreach (var exception in exceptions)
        {
            queue.Enqueue(exception);
        }
    }

    public int CreateCountFor(string documentKind) => CreateCounts.GetValueOrDefault(documentKind);

    public Task<ErpDocumentResult> DeAllocateAsync(DeAllocationRequest request, CancellationToken cancellationToken) =>
        CreateAsync("deallocation", request.Context);

    public Task<ErpDocumentResult> AllocateAsync(AllocationRequest request, CancellationToken cancellationToken) =>
        CreateAsync("allocation", request.Context);

    public Task<ErpDocumentResult> CreateWorkOrderAsync(WorkOrderRequest request, CancellationToken cancellationToken) =>
        CreateAsync("workorder", request.Context);

    public Task<ErpDocumentResult> CreatePurchaseRequisitionAsync(
        PurchaseRequisitionRequest request,
        CancellationToken cancellationToken) =>
        CreateAsync("purchase-requisition", request.Context);

    public Task<ErpDocumentResult> CreateLaborRequisitionAsync(
        LaborRequisitionRequest request,
        CancellationToken cancellationToken) =>
        CreateAsync("labor-requisition", request.Context);

    public Task<ErpDocumentResult> AttachOafLinkAsync(OafLinkRequest request, CancellationToken cancellationToken) =>
        CreateAsync("oaf-link", request.Context);

    public Task<ExistingDocumentResult> FindExistingDocumentAsync(
        ErpOperationRequest request,
        string documentKind,
        CancellationToken cancellationToken)
    {
        var key = KeyFor(documentKind, request);

        return Task.FromResult(_documents.TryGetValue(key, out var reference)
            ? new ExistingDocumentResult(true, reference)
            : new ExistingDocumentResult(false, null));
    }

    public Task<ErpAutomationOutcome> VerifyErpAutomationAsync(
        ErpOperationRequest request,
        string transitionKind,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ErpAutomationOutcome(
            Completed: ErpAutomationCompleted,
            ErpDocumentRef: ErpAutomationCompleted ? $"ERP-{transitionKind}" : null,
            ErpAutomationEnabled: ErpAutomationEnabled,
            InProgress: ErpAutomationEnabled && !ErpAutomationCompleted,
            Detail: null));

    public Task<ShortageResult> GetNetShortageAsync(ErpOperationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ShortageResult(NetShortage, NetShortage > 0 ? "short" : "no shortage"));

    public Task<ShortageResult> GetMilShortageAsync(MilShortageRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ShortageResult(MilShortage, MilShortage > 0 ? "short" : "no shortage"));

    public Task<AllocationStatusResult> GetAllocationStatusAsync(
        ErpOperationRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AllocationStatusResult(ChildrenAllocated, null));

    public Task<IReadOnlyList<PendingDocument>> GetPendingDocumentsAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingDocument>>([]);

    // ---- AutoShop cycle ----------------------------------------------------

    /// <summary>OAFs the cycle should turn into SJOs. Empty means a quiet cycle.</summary>
    public List<OafAwaitingSjo> OafAwaitingSjo { get; } = [];

    /// <summary>Sites the discovery step will find.</summary>
    public List<ErpSite> Sites { get; } = [];

    /// <summary>SJO rows per site, keyed by Site ID.</summary>
    public Dictionary<string, List<SjoSequenceRow>> SjoBySite { get; } = [];

    /// <summary>AutoShop rows per site, keyed by Site ID.</summary>
    public Dictionary<string, List<SjoSequenceRow>> AutoShopBySite { get; } = [];

    /// <summary>Every sequence submission, in order, so tests can assert on sort order and sites.</summary>
    public List<(string Endpoint, string SiteId, IReadOnlyList<SjoSequenceRow> Rows)> Submissions { get; } = [];

    /// <summary>Site IDs whose next submission should throw.</summary>
    public Dictionary<string, Queue<Exception>> SiteFailures { get; } = [];

    public void FailSite(string siteId, params Exception[] exceptions)
    {
        if (!SiteFailures.TryGetValue(siteId, out var queue))
        {
            queue = new Queue<Exception>();
            SiteFailures[siteId] = queue;
        }

        foreach (var exception in exceptions)
        {
            queue.Enqueue(exception);
        }
    }

    public Task<IReadOnlyList<OafAwaitingSjo>> GetOafAwaitingSjoAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OafAwaitingSjo>>(OafAwaitingSjo);

    public Task<ErpDocumentResult> CreateSjoFromOafAsync(
        ErpOperationRequest request,
        string oafNumber,
        CancellationToken cancellationToken) =>
        CreateAsync("sjo", request);

    public Task<IReadOnlyList<ErpSite>> GetSitesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ErpSite>>(Sites);

    public Task<IReadOnlyList<SjoSequenceRow>> GetSjoSequenceAsync(
        string siteId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SjoSequenceRow>>(SjoBySite.GetValueOrDefault(siteId) ?? []);

    public Task<SequenceSubmissionResult> SubmitSjoSequenceAsync(
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken) =>
        SubmitAsync("sjo-sequence", siteId, orderedRows);

    public Task<IReadOnlyList<SjoSequenceRow>> GetAutoShopAsync(
        string siteId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SjoSequenceRow>>(AutoShopBySite.GetValueOrDefault(siteId) ?? []);

    public Task<SequenceSubmissionResult> SubmitAutoShopAsync(
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken) =>
        SubmitAsync("autoshop", siteId, orderedRows);

    private Task<SequenceSubmissionResult> SubmitAsync(
        string endpoint,
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows)
    {
        if (SiteFailures.TryGetValue(siteId, out var failures) && failures.Count > 0)
        {
            throw failures.Dequeue();
        }

        Submissions.Add((endpoint, siteId, orderedRows));

        return Task.FromResult(new SequenceSubmissionResult(orderedRows.Count, $"{endpoint}-{siteId}"));
    }

    private Task<ErpDocumentResult> CreateAsync(string documentKind, ErpOperationRequest request)
    {
        if (ScriptedFailures.TryGetValue(documentKind, out var failures) && failures.Count > 0)
        {
            throw failures.Dequeue();
        }

        CreateCounts.AddOrUpdate(documentKind, 1, (_, count) => count + 1);

        var reference = $"{documentKind.ToUpperInvariant()}-{CreateCounts[documentKind]:D4}";
        _documents[KeyFor(documentKind, request)] = reference;

        return Task.FromResult(ErpDocumentResult.Created(reference));
    }

    private static string KeyFor(string documentKind, ErpOperationRequest request) =>
        $"{request.DocumentType}|{request.DocumentId}|{documentKind}";
}
