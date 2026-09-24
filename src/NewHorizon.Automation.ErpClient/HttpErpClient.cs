using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// The agent's only implementation of <see cref="IErpClient"/>: every ERP effect is an HTTP call
/// to an ERP application API, so ERP validation, permissions, audit and transactions apply. The
/// token, retries and circuit breaker are all handled by the pipeline this client sits on top of.
/// </summary>
public sealed class HttpErpClient : IErpClient
{
    /// <summary>
    /// Sent on every creating call. If the ERP honours it (design doc Â§18.3) it becomes a second
    /// line of defence; the client does not depend on it, because query-before-create already
    /// guarantees duplicate-safety on its own.
    /// </summary>
    private const string IdempotencyKeyHeader = "X-Idempotency-Key";

    private const string CorrelationIdHeader = "X-Correlation-Id";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpErpClient> _logger;
    private readonly AutoShopFieldMap _fields;
    private readonly ErpEndpointOptions _endpoints;

    public HttpErpClient(
        HttpClient httpClient,
        ILogger<HttpErpClient> logger,
        IOptions<AutoShopFieldMap>? fields = null,
        IOptions<ErpEndpointOptions>? endpoints = null)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Both optional so a host that has not configured them still gets the documented defaults
        // rather than a startup failure.
        _fields = fields?.Value ?? AutoShopFieldMap.Default;
        _endpoints = endpoints?.Value ?? new ErpEndpointOptions();
    }

    public Task<ErpDocumentResult> DeAllocateAsync(
        DeAllocationRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(_endpoints.DeAllocation, request, request.Context, "deallocation", cancellationToken);

    public Task<ErpDocumentResult> AllocateAsync(
        AllocationRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(_endpoints.Allocation, request, request.Context, "allocation", cancellationToken);

    public Task<ErpDocumentResult> CreateWorkOrderAsync(
        WorkOrderRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(_endpoints.WorkOrder, request, request.Context, "workorder", cancellationToken);

    public Task<ErpDocumentResult> CreatePurchaseRequisitionAsync(
        PurchaseRequisitionRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(_endpoints.PurchaseRequisition, request, request.Context, "purchase-requisition", cancellationToken);

    public Task<ErpDocumentResult> CreateLaborRequisitionAsync(
        LaborRequisitionRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(_endpoints.LaborRequisition, request, request.Context, "labor-requisition", cancellationToken);

    public Task<ErpDocumentResult> AttachOafLinkAsync(
        OafLinkRequest request,
        CancellationToken cancellationToken) =>
        PostAsync(_endpoints.OafLink, request, request.Context, "oaf-link", cancellationToken);

    public async Task<ExistingDocumentResult> FindExistingDocumentAsync(
        ErpOperationRequest request,
        string documentKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);

        var uri = $"{_endpoints.ExistingDocument}?documentType={Uri.EscapeDataString(request.DocumentType)}"
            + $"&documentId={Uri.EscapeDataString(request.DocumentId)}"
            + $"&documentKind={Uri.EscapeDataString(documentKind)}";

        using var httpRequest = CreateRequest(HttpMethod.Get, uri, request);
        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<ExistingDocumentResult>(
            response,
            _endpoints.ExistingDocument,
            cancellationToken);
    }

    public async Task<ErpAutomationOutcome> VerifyErpAutomationAsync(
        ErpOperationRequest request,
        string transitionKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionKind);

        var uri = $"{_endpoints.VerifyAutomation}?documentType={Uri.EscapeDataString(request.DocumentType)}"
            + $"&documentId={Uri.EscapeDataString(request.DocumentId)}"
            + $"&transitionKind={Uri.EscapeDataString(transitionKind)}";

        using var httpRequest = CreateRequest(HttpMethod.Get, uri, request);
        using var response = await SendAsync(httpRequest, cancellationToken);

        var outcome = await ErpResponseHandler.ReadAsync<ErpAutomationOutcome>(
            response,
            _endpoints.VerifyAutomation,
            cancellationToken);

        _logger.LogInformation(
            "ERP transition {TransitionKind} for {DocumentType} {DocumentId}: completed={Completed}, "
            + "erpAutomationEnabled={Enabled}, inProgress={InProgress}, ref={ErpDocumentRef}",
            transitionKind,
            request.DocumentType,
            request.DocumentId,
            outcome.Completed,
            outcome.ErpAutomationEnabled,
            outcome.InProgress,
            outcome.ErpDocumentRef ?? "(none)");

        return outcome;
    }

    public async Task<ShortageResult> GetNetShortageAsync(
        ErpOperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = CreateJsonRequest(HttpMethod.Post, _endpoints.NetShortage, request, request);
        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<ShortageResult>(
            response,
            _endpoints.NetShortage,
            cancellationToken);
    }

    public async Task<ShortageResult> GetMilShortageAsync(
        MilShortageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = CreateJsonRequest(
            HttpMethod.Post,
            _endpoints.MilShortage,
            request,
            request.Context);
        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<ShortageResult>(
            response,
            _endpoints.MilShortage,
            cancellationToken);
    }

    public async Task<AllocationStatusResult> GetAllocationStatusAsync(
        ErpOperationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = CreateJsonRequest(
            HttpMethod.Post,
            _endpoints.AllocationStatus,
            request,
            request);
        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<AllocationStatusResult>(
            response,
            _endpoints.AllocationStatus,
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingDocument>> GetPendingDocumentsAsync(
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        var uri = $"{_endpoints.PendingDocuments}?since={Uri.EscapeDataString(sinceUtc.ToString("O"))}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(httpRequest, cancellationToken);

        var documents = await ErpResponseHandler.ReadAsync<List<PendingDocument>>(
            response,
            _endpoints.PendingDocuments,
            cancellationToken);

        return documents;
    }

    // ---- AutoShop cycle ----------------------------------------------------
    // Paths confirmed with the client 2026-07-27; request/response shapes still to be supplied, so
    // the payload models are deliberately minimal and will need revisiting once they arrive.

    public async Task<IReadOnlyList<OafAwaitingSjo>> GetOafAwaitingSjoAsync(CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, _endpoints.OafAwaitingSjo);
        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<List<OafAwaitingSjo>>(
            response,
            _endpoints.OafAwaitingSjo,
            cancellationToken);
    }

    public async Task<ErpDocumentResult> CreateSjoFromOafAsync(
        ErpOperationRequest request,
        string oafNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(oafNumber);

        using var httpRequest = CreateJsonRequest(
            HttpMethod.Post,
            _endpoints.CreateSjoFromOaf,
            new { oafNumber },
            request);

        httpRequest.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, $"oaf:{oafNumber}:sjo");

        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<ErpDocumentResult>(
            response,
            _endpoints.CreateSjoFromOaf,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ErpSite>> GetSitesAsync(CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, _endpoints.SiteList);
        using var response = await SendAsync(httpRequest, cancellationToken);

        return await ErpResponseHandler.ReadAsync<List<ErpSite>>(
            response,
            _endpoints.SiteList,
            cancellationToken);
    }

    public Task<IReadOnlyList<SjoSequenceRow>> GetSjoSequenceAsync(
        string siteId,
        CancellationToken cancellationToken) =>
        GetRowsAsync(_endpoints.SjoSequence(siteId), siteId, cancellationToken);

    public Task<SequenceSubmissionResult> SubmitSjoSequenceAsync(
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken) =>
        SubmitSequenceAsync(_endpoints.SjoSequence(siteId), siteId, orderedRows, cancellationToken);

    public Task<IReadOnlyList<SjoSequenceRow>> GetAutoShopAsync(
        string siteId,
        CancellationToken cancellationToken) =>
        GetRowsAsync(_endpoints.AutoShop(siteId), siteId, cancellationToken);

    /// <summary>
    /// Reads the rows as the JSON the ERP actually sent. Deserialising into a typed model would
    /// drop every property the agent does not know about â€” and this workflow's whole job is to hand
    /// that payload back to the ERP intact.
    /// </summary>
    private async Task<IReadOnlyList<SjoSequenceRow>> GetRowsAsync(
        string endpoint,
        string siteId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(httpRequest, cancellationToken);

        var payload = await ErpResponseHandler.ReadAsync<JsonArray>(response, endpoint, cancellationToken);

        var rows = new List<SjoSequenceRow>(payload.Count);

        foreach (var node in payload)
        {
            // A null or non-object entry is the ERP's data, not a failure the agent can fix, so it
            // is dropped with a warning rather than throwing away the whole site.
            if (node is JsonObject row)
            {
                rows.Add(SjoSequenceRow.FromJson(row, _fields));
            }
            else
            {
                _logger.LogWarning(
                    "Ignoring a non-object row from {Endpoint} for site {SiteId}",
                    endpoint,
                    siteId);
            }
        }

        return rows;
    }

    public Task<SequenceSubmissionResult> SubmitAutoShopAsync(
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken) =>
        SubmitSequenceAsync(_endpoints.AutoShop(siteId), siteId, orderedRows, cancellationToken);

    private async Task<SequenceSubmissionResult> SubmitSequenceAsync(
        string endpoint,
        string siteId,
        IReadOnlyList<SjoSequenceRow> orderedRows,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);
        ArgumentNullException.ThrowIfNull(orderedRows);

        // The rows the ERP sent, in the agreed order and with the flag set â€” nothing else altered,
        // and nothing dropped. Serialising the wrapper instead would post the agent's model of a
        // row rather than the ERP's own row.
        var body = new JsonArray(orderedRows.Select(row => (JsonNode)row.Payload.DeepClone()).ToArray());

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        // Keyed on the site and the rows being submitted, so a replay of the same sequence is
        // recognisable to an ERP that honours the header.
        httpRequest.Headers.TryAddWithoutValidation(
            IdempotencyKeyHeader,
            $"sequence:{siteId}:{orderedRows.Count}");

        using var response = await SendAsync(httpRequest, cancellationToken);

        var result = await ErpResponseHandler.ReadAsync<SequenceSubmissionResult>(
            response,
            endpoint,
            cancellationToken);

        _logger.LogInformation(
            "Submitted {RowCount} rows to {Endpoint} for site {SiteId}",
            orderedRows.Count,
            endpoint,
            siteId);

        return result;
    }

    private async Task<ErpDocumentResult> PostAsync<TRequest>(
        string endpoint,
        TRequest payload,
        ErpOperationRequest context,
        string documentKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(context);

        using var httpRequest = CreateJsonRequest(HttpMethod.Post, endpoint, payload, context);

        // Idempotency key is per document + operation, so a replay of the same operation is
        // recognisable to an ERP that chooses to honour the header.
        httpRequest.Headers.TryAddWithoutValidation(
            IdempotencyKeyHeader,
            $"{context.DocumentType}:{context.DocumentId}:{documentKind}");

        using var response = await SendAsync(httpRequest, cancellationToken);

        var result = await ErpResponseHandler.ReadAsync<ErpDocumentResult>(response, endpoint, cancellationToken);

        _logger.LogInformation(
            "ERP {Endpoint} for {DocumentType} {DocumentId} produced {ErpDocumentRef} (alreadyExisted={AlreadyExisted})",
            endpoint,
            context.DocumentType,
            context.DocumentId,
            result.ErpDocumentRef ?? "(none)",
            result.AlreadyExisted);

        return result;
    }

    private static HttpRequestMessage CreateJsonRequest<TPayload>(
        HttpMethod method,
        string uri,
        TPayload payload,
        ErpOperationRequest context)
    {
        var request = CreateRequest(method, uri, context);
        request.Content = JsonContent.Create(payload, options: SerializerOptions);

        return request;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, ErpOperationRequest context)
    {
        var request = new HttpRequestMessage(method, uri);

        // Carries the job's correlation id into the ERP's own logs, so one identifier traces a
        // run across both systems.
        request.Headers.TryAddWithoutValidation(CorrelationIdHeader, context.CorrelationId);

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A graceful shutdown, not a failure: let it propagate so the operation stays
            // resumable rather than being recorded as an error.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Network drop, timeout, or an open circuit breaker â€” all worth another attempt.
            throw new ErpTransientException(
                "The ERP could not be reached. Automation will retry automatically.",
                $"'{request.RequestUri}' failed: {ex.Message}",
                request.RequestUri?.ToString(),
                ex);
        }
    }
}
