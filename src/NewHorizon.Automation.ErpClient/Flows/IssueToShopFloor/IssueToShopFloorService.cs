using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// Creates the Issue to Shop Floor for one SJO, Work Order or Sales OAF, or says why it will not.
/// </summary>
public interface IIssueToShopFloorService
{
    /// <summary>
    /// Checks the document, plans the issue warehouse by warehouse, and creates it — all or nothing.
    /// </summary>
    /// <param name="source">What the issue is raised against: the Issue screen's three tabs.</param>
    /// <param name="documentNumber">
    /// "26-27/SJ/NF1/000123" (an SJO may also be given by its bare running number),
    /// "26-27/WO/NF1/000010" or "26-27/OF/NF1/000042".
    /// </param>
    /// <param name="dryRun">True reports the planned lines and creates nothing.</param>
    /// <returns>
    /// The result. A failed prerequisite or a shortage is a result with <see cref="IssueToShopFloorResult.Reason"/>
    /// set and nothing created, never a partial issue.
    /// </returns>
    /// <exception cref="ErpException">The ERP refused a call, or could not be reached.</exception>
    Task<IssueToShopFloorResult> ConvertAsync(
        IssueSource source,
        string documentNumber,
        bool dryRun,
        CancellationToken cancellationToken);
}

/// <summary>
/// Walks the ERP's Issue to Shop Floor screen for one document: the SJO-wise, work-order-wise or
/// sales-OAF-wise tab, all allocated warehouses, Fill, Save.
/// </summary>
/// <remarks>
/// <para>
/// Every call is an existing ERP endpoint — the ones the SJO, Allocation and Issue to Shop Floor
/// screens use — so ERP validation, rights and audit all apply and the agent writes nothing itself.
/// </para>
/// <para>
/// <b>SJO-wise</b>, the prerequisites are checked one at a time, in the order a person would find them
/// missing, and the first one that fails stops the run with its own reason: SJO found → authorised →
/// CBOM generated → Work Order open → Issue numbering configured → Work Allocation done → the ERP's own
/// eligibility check. That last one is the Issue screen's SJO validation, which combines every rule the
/// ERP applies; it is kept after the specific checks so a rule they do not cover still stops the run.
/// </para>
/// <para>
/// <b>Work-order-wise and sales-OAF-wise</b>, the document is found through the Issue screen's own
/// site and number validation, which only answers for a document it would issue: an open, authorised
/// Work Order (for an OAF, one of its customer-order SJOs' Work Orders) with allocated stock left.
/// A Work Order or OAF can span several SJOs, so stock is looked up per line, for that line's SJO.
/// </para>
/// <para>
/// <b>Nothing is created unless everything passes.</b> A shortage on any item refuses the whole
/// issue and lists every short item. The single write is <c>createIssuetoShopFloor</c>, marked
/// non-retryable like the purchase order create: the ERP raises the issued quantities as it saves, so
/// a second run finds nothing pending rather than issuing twice.
/// </para>
/// </remarks>
internal sealed class IssueToShopFloorService : IIssueToShopFloorService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IErpTokenProvider _tokenProvider;
    private readonly ErpEndpointOptions _sharedEndpoints;
    private readonly IssueToShopFloorOptions _options;
    private readonly IClock _clock;
    private readonly ILogger<IssueToShopFloorService> _logger;

    public IssueToShopFloorService(
        IHttpClientFactory httpClientFactory,
        IErpTokenProvider tokenProvider,
        IOptions<ErpEndpointOptions> sharedEndpoints,
        IOptions<IssueToShopFloorOptions> options,
        IClock clock,
        ILogger<IssueToShopFloorService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(sharedEndpoints);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _tokenProvider = tokenProvider;
        _sharedEndpoints = sharedEndpoints.Value;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    private IssueToShopFloorErpPaths Endpoints => _options.Endpoints;

    public async Task<IssueToShopFloorResult> ConvertAsync(
        IssueSource source,
        string documentNumber,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown issue source.");
        }

        var run = new Run(source, documentNumber, dryRun);

        if (!DocumentNumberSelection.TryParse(documentNumber, out var selection, out var parseError))
        {
            return run.Refuse("DocumentNumber", parseError!);
        }

        if (source == IssueSource.Sjo && _options.Sites.Count == 0)
        {
            return run.Refuse(
                "Configuration",
                $"No sites are configured to look SJOs up in. Set {IssueToShopFloorOptions.SectionName}:Sites.");
        }

        if (string.IsNullOrWhiteSpace(_options.IssueTo) || string.IsNullOrWhiteSpace(_options.IssueBy))
        {
            return run.Refuse(
                "Configuration",
                $"The ERP requires 'Issue To' and 'Issue By' on every issue. Set {IssueToShopFloorOptions.SectionName}:IssueTo and :IssueBy.");
        }

        // 1. Which document, and is it ready to be issued against.
        var (document, refusal) = source == IssueSource.Sjo
            ? await ResolveSjoAsync(run, selection!, cancellationToken)
            : await ResolveWorkOrderOrOafAsync(run, source, selection!, cancellationToken);

        if (refusal is not null)
        {
            return refusal;
        }

        // 2. Where the issue is numbered and raised.
        var today = _clock.LocalDate;
        var documentControl = await GetDocumentControlAsync(today, cancellationToken);
        if (documentControl.Error is not null)
        {
            return run.Refuse("IssueNumbering", documentControl.Error);
        }

        var numbering = documentControl.Defaults!;
        run.Pass("IssueNumbering", $"Issues are numbered {numbering.FinancialYear}/{numbering.GroupCode}/{numbering.LocationCode}.");

        // 3. Work Allocation: stock reserved for the document and not yet issued, in warehouses the agent may use.
        var userId = await _tokenProvider.GetUserIdAsync(cancellationToken);
        var warehouses = await GetAllocatedWarehousesAsync(source, document!.Id, numbering.LocationId, userId, cancellationToken);
        if (warehouses.Count == 0)
        {
            return run.Refuse(
                "WorkAllocation",
                $"No allocated stock is left to issue for {source.Noun()} {document.FullNumber}. Work Allocation has not been done, "
                + "everything allocated has already been issued, or the agent's ERP user has no rights to the allocated "
                + $"warehouses for Issue to Shop Floor (transaction {_options.WarehouseTransaction}).");
        }

        run.Pass("WorkAllocation", $"Allocated warehouse(s): {string.Join(", ", warehouses.Select(wh => wh.Code))}.");

        // 4. SJO-wise only: the ERP's own verdict, the Issue screen's SJO validation. Work Order and
        // Sales OAF already came through the equivalent validation when they were found.
        if (document.Sjo is { } sjo)
        {
            if (!await IsSjoOfferedForIssueAsync(sjo, cancellationToken))
            {
                return run.Refuse(
                    "ErpEligibility",
                    $"The ERP does not offer SJO {sjo.FullNumber} for Issue to Shop Floor: its Work Order is not authorised, "
                    + "or nothing is left pending to issue.");
            }

            run.Pass("ErpEligibility", "The ERP offers the SJO for Issue to Shop Floor.");
        }

        // 5. What to issue, and from where.
        var warehouseIds = string.Join(',', warehouses.Select(wh => wh.Id.ToString(CultureInfo.InvariantCulture)));
        var items = (await GetPendingItemsAsync(source, document.Id, warehouseIds, cancellationToken))
            .Where(item => item.QuantityToIssue > 0)
            .ToList();

        if (items.Count == 0)
        {
            return run.Refuse("PendingItems", $"Nothing is pending to issue for {source.Noun()} {document.FullNumber}.");
        }

        var barcoded = _options.BarcodeScanningEnabled
            ? items.Where(item => item.BarcodeRequired).Select(item => item.ItemCode).Distinct().ToList()
            : [];
        if (barcoded.Count > 0)
        {
            return run.Refuse(
                "PendingItems",
                $"Item(s) {string.Join(", ", barcoded)} must be scanned by barcode on the Issue to Shop Floor screen, "
                + $"so the agent cannot issue this {source.Noun()}.");
        }

        var inwardWiseAllocation = await UsesInwardWiseAllocationAsync(numbering.LocationCode, cancellationToken);

        // Without inward-wise allocation the screen's Fill skips inward-tracked items (onFillData) and a
        // person picks the inwards in a popup. The agent picks them itself, oldest inward first. With
        // inward-wise allocation on, Fill takes the ERP's own order, and so does this.
        var pickInwardsFifo = !inwardWiseAllocation;
        var fifoItems = pickInwardsFifo ? items.Count(item => item.InwardRequired) : 0;

        run.Pass(
            "PendingItems",
            fifoItems == 0
                ? $"{items.Count} item line(s) pending."
                : $"{items.Count} item line(s) pending; {fifoItems} inward-tracked line(s) take the oldest inward first.");

        var stockByItem = new List<(IssueItem Item, IReadOnlyList<StockRow> Stock)>(items.Count);
        foreach (var item in items)
        {
            var stock = await GetStockAsync(item, warehouseIds, inwardWiseAllocation, cancellationToken);
            stockByItem.Add((item, pickInwardsFifo && item.InwardRequired ? InwardFifo.Order(stock) : stock));
        }

        var allocation = IssueAllocator.Allocate(stockByItem);
        if (allocation.Shortages.Count > 0)
        {
            return run.Refuse(
                "Stock",
                $"{allocation.Shortages.Count} item(s) do not have enough stock in the allocated warehouses; nothing was issued.",
                allocation.Shortages);
        }

        run.Pass("Stock", $"Every item is covered: {allocation.Lines.Count} line(s) across {allocation.Lines.Select(line => line.WarehouseCode).Distinct().Count()} warehouse(s).");

        // Read on a dry run too, so the answer shows the date a real run would send.
        var (authorisationDate, authorisationDetail) = await GetAuthorisationDateAsync(numbering.LocationId, today, cancellationToken);
        run.Pass("AuthorisationDate", authorisationDetail);

        if (dryRun)
        {
            _logger.LogInformation(
                "{Source} {DocumentNumber}: dry run — would issue {LineCount} line(s); nothing created.",
                source.Noun(),
                document.FullNumber,
                allocation.Lines.Count);

            return run.Ready(allocation.Lines);
        }

        // 6. The one write.
        var header = new IssueHeader(
            numbering.FinancialYear,
            numbering.GroupCode,
            numbering.LocationId,
            numbering.LocationCode,
            today,
            authorisationDate,
            _options.IssueTo,
            _options.IssueBy,
            numbering.AuthorisationRequired,
            numbering.AutoNumberRequired,
            numbering.SiteRequired,
            _options.LocationId,
            userId,
            _options.FinFlag,
            _options.CurrencyCode,
            _options.CompanyId);

        var remarks = items
            .GroupBy(item => (item.ItemCode, item.RandomNumber))
            .ToDictionary(group => group.Key, group => group.First().Remarks);

        var payload = IssuePayloadBuilder.Build(source, header, allocation.Lines, remarks);
        var issueNumber = await CreateIssueAsync(payload, cancellationToken);

        _logger.LogInformation(
            "{Source} {DocumentNumber}: created Issue to Shop Floor {IssueNumber} with {LineCount} line(s).",
            source.Noun(),
            document.FullNumber,
            issueNumber ?? "(number not reported)",
            allocation.Lines.Count);

        return run.Created(issueNumber, allocation.Lines);
    }

    // ---- Finding the document -------------------------------------------------------------------

    /// <summary>
    /// SJO-wise: found, authorised, CBOM generated, an open Work Order — each its own check and reason.
    /// </summary>
    private async Task<(IssueDocument? Document, IssueToShopFloorResult? Refusal)> ResolveSjoAsync(
        Run run,
        DocumentNumberSelection selection,
        CancellationToken cancellationToken)
    {
        var candidates = selection.Match(await FindSjosAsync(selection.Number, cancellationToken));
        if (candidates.Count == 0)
        {
            return (null, run.Refuse("SjoFound", $"No SJO '{selection.Input}' was found at sites {string.Join(", ", _options.Sites)}."));
        }

        if (candidates.Count > 1)
        {
            return (null, run.Refuse(
                "SjoFound",
                $"'{selection.Input}' matches {candidates.Count} SJOs ({string.Join(", ", candidates.Select(sjo => sjo.FullNumber))}). "
                + "Give the full number, e.g. \"26-27/SJ/NF1/000123\"."));
        }

        var sjo = candidates[0];
        run.Resolved(sjo.FullNumber);
        run.Pass("SjoFound", $"SJO {sjo.FullNumber} (id {sjo.Id}), item {sjo.ItemCode}.");

        if (!sjo.Status.Equals(_options.AuthorisedStatus, StringComparison.OrdinalIgnoreCase))
        {
            var status = sjo.StatusDescription.Length > 0 ? sjo.StatusDescription : sjo.Status switch
            {
                "P" => "pending authorisation",
                "D" => "deleted",
                "" => "not authorised",
                var other => other,
            };

            return (null, run.Refuse("Authorised", $"SJO {sjo.FullNumber} is not authorised (status: {status}). Authorise it first."));
        }

        run.Pass("Authorised", "The SJO is authorised.");

        var cbomRandomNumber = await FindCbomRandomNumberAsync(sjo, cancellationToken);
        if (cbomRandomNumber is null)
        {
            return (null, run.Refuse(
                "Cbom",
                $"No CBOM has been generated for SJO {sjo.FullNumber} (item {sjo.ItemCode}), or it is not frozen. Generate the CBOM first."));
        }

        run.Pass("Cbom", "The CBOM is generated.");

        var workOrders = await GetWorkOrdersAsync(sjo.Id, cbomRandomNumber.Value, cancellationToken);
        if (workOrders.Count == 0)
        {
            return (null, run.Refuse("WorkOrder", $"No Work Order exists for SJO {sjo.FullNumber}. Create the Work Order first."));
        }

        var openWorkOrders = workOrders.Where(wo => wo.Status.Equals("OPEN", StringComparison.OrdinalIgnoreCase)).ToList();
        if (openWorkOrders.Count == 0)
        {
            return (null, run.Refuse(
                "WorkOrder",
                $"SJO {sjo.FullNumber} has no open Work Order ({string.Join(", ", workOrders.Select(wo => $"{wo.Number} {wo.Status}"))})."));
        }

        run.Pass("WorkOrder", $"Open Work Order(s): {string.Join(", ", openWorkOrders.Select(wo => wo.Number))}.");

        return (new IssueDocument(sjo.Id, sjo.FullNumber, sjo), null);
    }

    /// <summary>
    /// Work-order-wise or sales-OAF-wise: the full number, validated the way the Issue screen validates
    /// it. The ERP answers only for a document it would issue, so one check stands for existence,
    /// authorisation and allocation together, and the reason says so.
    /// </summary>
    private async Task<(IssueDocument? Document, IssueToShopFloorResult? Refusal)> ResolveWorkOrderOrOafAsync(
        Run run,
        IssueSource source,
        DocumentNumberSelection selection,
        CancellationToken cancellationToken)
    {
        var noun = source.Noun();
        var example = source == IssueSource.WorkOrder ? "26-27/WO/NF1/000010" : "26-27/OF/NF1/000042";

        if (!selection.IsFullNumber)
        {
            return (null, run.Refuse(
                "DocumentNumber",
                $"Give the full {noun} number (year/group/site/number), e.g. \"{example}\"; a bare running number is not unique."));
        }

        var fullNumber = $"{selection.Year}/{selection.Group}/{selection.SiteCode}/{selection.Number}";
        run.Resolved(fullNumber);

        var why = source == IssueSource.WorkOrder
            ? "it does not exist, it is not open and authorised, or it has no allocated stock left to issue"
            : "it does not exist, none of its customer-order SJOs has an open and authorised Work Order, or none has allocated stock left to issue";

        var siteId = await FindIssueSiteIdAsync(source, selection, cancellationToken);
        if (siteId is null)
        {
            return (null, run.Refuse(
                "DocumentFound",
                $"The ERP offers no {noun} at site {selection.SiteCode} in {selection.Year}/{selection.Group} for Issue to Shop Floor, "
                + $"so {noun} {fullNumber} cannot be issued: {why}."));
        }

        var documentId = await FindIssuableDocumentIdAsync(source, selection, siteId.Value, cancellationToken);
        if (documentId is null)
        {
            return (null, run.Refuse("DocumentFound", $"{noun} {fullNumber} cannot be issued: {why}."));
        }

        run.Pass("DocumentFound", $"{noun} {fullNumber} (id {documentId}) is open, authorised and has allocated stock to issue.");

        return (new IssueDocument(documentId.Value, fullNumber, Sjo: null), null);
    }

    // ---- ERP reads ------------------------------------------------------------------------------

    private const int SjoListPageSize = 100;
    private const int SjoListMaxPages = 20;

    /// <summary>The SJOs the ERP's list search offers for a running number — candidates, not a match.</summary>
    /// <remarks>
    /// <para>
    /// The search is a <c>LIKE '%x%'</c> over several columns, so this is only a candidate list;
    /// <see cref="DocumentNumberSelection"/> picks the exact one.
    /// </para>
    /// <para>
    /// Paged, never <c>pageSize 0</c>. Zero is the list's "every match" mode, and in that branch
    /// <c>CSP_XSJO_List_EM</c> builds its count query with a broken quote
    /// (<c>ISNULL(OAFH.XPOAFHYR,')+'/'…</c>), so SQL Server fails with "Incorrect syntax near 'F'" and
    /// the ERP answers 500 — which reached the caller as a 503 after the retries. The paged branch
    /// is the one the ERP's own SJO screen uses, and it works.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<SjoHeader>> FindSjosAsync(string runningNumber, CancellationToken cancellationToken)
    {
        var endpoint = Endpoints.SjoList(_options.Sites);
        var candidates = new List<SjoHeader>();
        var seen = 0;
        int? totalRows = null;

        for (var page = 1; page <= SjoListMaxPages; page++)
        {
            var body = new JsonObject
            {
                ["pageNumber"] = page,
                ["pageSize"] = SjoListPageSize,
                ["sortField"] = string.Empty,
                ["sortDirection"] = string.Empty,
                ["searchValue"] = runningNumber,
            };

            var rows = (await PostJsonArrayAsync(endpoint, body, cancellationToken)).Objects().ToList();
            if (rows.Count == 0)
            {
                break;
            }

            totalRows ??= rows[0].Int("totalRows", rows.Count);
            seen += rows.Count;

            candidates.AddRange(rows
                .Select(row => new SjoHeader(
                    Id: (long)row.Decimal("sjoEntryId"),
                    Year: row.String("entryYear"),
                    Group: row.String("groupCode"),
                    SiteId: row.Int("siteId"),
                    SiteCode: row.String("siteCode"),
                    Number: row.String("number"),
                    ItemCode: row.String("itemCode"),
                    Status: row.String("status"),
                    StatusDescription: row.String("statusDescription")))
                .Where(sjo => sjo.Id > 0));

            if (seen >= totalRows || rows.Count < SjoListPageSize)
            {
                return candidates;
            }
        }

        if (totalRows > seen)
        {
            _logger.LogWarning(
                "The SJO list search for '{Number}' has {TotalRows} candidates; only the first {Seen} were read.",
                runningNumber,
                totalRows,
                seen);
        }

        return candidates;
    }

    /// <summary>The SJO item's CBOM random number, or null when there is no frozen CBOM row for it.</summary>
    private async Task<int?> FindCbomRandomNumberAsync(SjoHeader sjo, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["sjoid"] = sjo.Id,
            ["itemcode"] = sjo.ItemCode,
            ["flag"] = "V",
            ["intRandomNum"] = 0,
            ["pageNumber"] = 1,
            ["pageSize"] = 10,
            ["sortField"] = string.Empty,
            ["sortDirection"] = string.Empty,
            ["searchValue"] = string.Empty,
        };

        var row = (await PostJsonArrayAsync(Endpoints.CbomItem, body, cancellationToken)).Objects().FirstOrDefault();

        return row is null ? null : row.Int("randomno");
    }

    private async Task<IReadOnlyList<(string Number, string Status)>> GetWorkOrdersAsync(
        long sjoId,
        int randomNumber,
        CancellationToken cancellationToken)
    {
        var rows = await GetJsonArrayAsync(Endpoints.WorkOrders(sjoId, randomNumber), cancellationToken);

        return rows.Objects()
            .Select(row => (row.String("wonumber"), row.String("wostatus")))
            .Where(wo => wo.Item1.Length > 0)
            .ToList();
    }

    private async Task<(IssueNumbering? Defaults, string? Error)> GetDocumentControlAsync(
        DateOnly issueDate,
        CancellationToken cancellationToken)
    {
        var financialYear = FinancialYears.For(issueDate);
        var endpoint = _sharedEndpoints.DocumentControlDefault(
            financialYear,
            IssuePayloadBuilder.DocType,
            IssuePayloadBuilder.DocSubType,
            _options.LocationId);

        JsonArray rows;
        try
        {
            rows = await GetJsonArrayAsync(endpoint, cancellationToken);
        }
        catch (ErpBusinessException ex)
        {
            return (null, $"Document Control has no Issue to Shop Floor numbering for {financialYear} at location {_options.LocationId}: {ex.LaymanMessage}");
        }

        var row = rows.Objects().FirstOrDefault(r => r.String("isDefault").Equals("Y", StringComparison.OrdinalIgnoreCase))
            ?? rows.Objects().FirstOrDefault();

        if (row is null)
        {
            return (null, $"Document Control has no Issue to Shop Floor (IS/NI) numbering for {financialYear} at location {_options.LocationId}.");
        }

        var numbering = new IssueNumbering(
            financialYear,
            row.String("groupCode"),
            row.Int("locationId", _options.LocationId),
            row.String("locationCode"),
            YesNo(row, "isLocationRequired"),
            YesNo(row, "isAutoNumberGenerated"),
            YesNo(row, "isAutorisationRequired"));

        if (numbering.AutoNumberRequired != "Y")
        {
            // Refused rather than numbered by the agent: an invented number would collide with the
            // next one a person types.
            return (null, $"Issue to Shop Floor is not auto-numbered at {numbering.LocationCode} for {financialYear}; "
                + "the agent will not invent a document number. Turn on auto-numbering in Document Control.");
        }

        return (numbering, null);
    }

    private async Task<IReadOnlyList<IssueWarehouse>> GetAllocatedWarehousesAsync(
        IssueSource source,
        long documentId,
        int issueSiteId,
        int userId,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["docType"] = source.DocType(),
            ["warhouseCode"] = string.Empty,
            ["flag"] = "L",
            ["docSiteID"] = issueSiteId,
            ["userID"] = userId,
            ["transName"] = _options.WarehouseTransaction,
            ["docID"] = documentId.ToString(CultureInfo.InvariantCulture),
            // CSP_XISSHDR_GetWarehouseList only knows OA in its single-warehouse ('S') branch, which is
            // why the screen's OAF tab sends S; the SJO and Work Order tabs send blank.
            ["whType"] = source == IssueSource.SalesOaf ? "S" : string.Empty,
        };

        var rows = await PostJsonArrayAsync(Endpoints.IssueWarehouses, body, cancellationToken);

        return rows.Objects()
            .Select(row => new IssueWarehouse(row.Int("warehouseId"), row.String("warehouseCode")))
            .Where(wh => wh.Id > 0)
            .DistinctBy(wh => wh.Id)
            .ToList();
    }

    private async Task<bool> IsSjoOfferedForIssueAsync(SjoHeader sjo, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["pageNumber"] = 0,
            ["pageSize"] = 10,
            ["sortField"] = string.Empty,
            ["sortDirection"] = string.Empty,
            ["searchValue"] = sjo.Number,
        };

        var rows = await PostJsonArrayAsync(Endpoints.IssueEligibility(sjo.Year, sjo.Group, sjo.SiteId), body, cancellationToken);

        return rows.Objects().Any(row => (long)row.Decimal("id") == sjo.Id);
    }

    /// <summary>
    /// The site's id, from the Issue screen's site validation for Work Orders or Sales OAFs. Null when
    /// the ERP offers nothing to issue at that site in that year and group.
    /// </summary>
    private async Task<int?> FindIssueSiteIdAsync(
        IssueSource source,
        DocumentNumberSelection selection,
        CancellationToken cancellationToken)
    {
        var endpoint = Endpoints.IssueSite(source, selection.Year!, selection.Group!, selection.SiteCode!);
        var row = (await GetJsonArrayAsync(endpoint, cancellationToken)).Objects()
            .FirstOrDefault(site => site.String("site").Equals(selection.SiteCode, StringComparison.OrdinalIgnoreCase));

        return row is null || row.Int("siteID") <= 0 ? null : row.Int("siteID");
    }

    /// <summary>
    /// The Work Order's or OAF's id, from the Issue screen's number validation. Null when the ERP would
    /// not issue against it.
    /// </summary>
    private async Task<long?> FindIssuableDocumentIdAsync(
        IssueSource source,
        DocumentNumberSelection selection,
        int siteId,
        CancellationToken cancellationToken)
    {
        var endpoint = Endpoints.IssueDocument(source, selection.Year!, selection.Group!, siteId, selection.Number);
        var row = (await GetJsonArrayAsync(endpoint, cancellationToken)).Objects()
            .FirstOrDefault(document => document.String("number").Trim().PadLeft(6, '0') == selection.Number);

        return row is null || row.Decimal("id") <= 0 ? null : (long)row.Decimal("id");
    }

    private async Task<IReadOnlyList<IssueItem>> GetPendingItemsAsync(
        IssueSource source,
        long documentId,
        string warehouseIds,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["docType"] = source.DocType(),
            ["warhouseId"] = warehouseIds,
            ["docId"] = documentId.ToString(CultureInfo.InvariantCulture),
        };

        var rows = await PostJsonArrayAsync(Endpoints.IssueItems, body, cancellationToken);

        var items = rows.Objects()
            .Select(row => new IssueItem(
                ItemCode: row.String("itemCode"),
                SjoId: (long)row.Decimal("sjoId", source == IssueSource.Sjo ? documentId : 0),
                WoId: (long)row.Decimal("woId"),
                RandomNumber: row.Int("randomNumber"),
                RequiredQuantity: row.Decimal("requiredQuantity"),
                PendingQuantity: row.Decimal("pendingQuantity"),
                InwardRequired: row.Flag("inwardRequired"),
                BarcodeRequired: row.Flag("barcodereqflag"),
                Remarks: row.String("remarks")))
            .Where(item => item.ItemCode.Length > 0 && item.SjoId > 0);

        // SJO-wise every line is the SJO's; a Work Order or OAF legitimately spans several SJOs.
        return source == IssueSource.Sjo
            ? items.Where(item => item.SjoId == documentId).ToList()
            : items.ToList();
    }

    /// <summary>The stock rows for one line — keyed by the line's own SJO, which is what the ERP asks for.</summary>
    private async Task<IReadOnlyList<StockRow>> GetStockAsync(
        IssueItem item,
        string warehouseIds,
        bool inwardWiseAllocation,
        CancellationToken cancellationToken)
    {
        var endpoint = Endpoints.Stock(item.SjoId, item.RandomNumber, warehouseIds, inwardWiseAllocation, item.InwardRequired);
        var rows = await GetJsonArrayAsync(endpoint, cancellationToken);

        return rows.Objects()
            .Select(row => new StockRow(
                (long)row.Decimal("stockId"),
                (long)row.Decimal("lineNo"),
                row.String("wareHouseCode"),
                row.Decimal("quantity"),
                row.String("inwardNo")))
            .Where(row => row.StockId > 0 && row.Available > 0)
            .ToList();
    }

    /// <summary>
    /// The screen's authorisation date (<c>onSave</c>): today, unless today is outside the issue site's
    /// current finance period, in which case the period's end date.
    /// </summary>
    /// <remarks>
    /// The screen reads the periods from the system configuration it loaded at login and compares
    /// formatted date strings; this reads the same configuration and compares dates. When the period
    /// cannot be read, or has no row for the site, today stands — the screen does the same when it
    /// finds no row — and the warning says why.
    /// </remarks>
    private async Task<(DateOnly Date, string Detail)> GetAuthorisationDateAsync(
        int issueSiteId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        JsonObject config;
        try
        {
            config = await GetJsonObjectAsync(Endpoints.SystemConfig, cancellationToken);
        }
        catch (ErpBusinessException ex)
        {
            _logger.LogWarning(
                "The finance period could not be read from {Endpoint} ({Reason}); authorising with today's date.",
                Endpoints.SystemConfig,
                ex.TechnicalMessage);
            return (today, $"Today ({today:yyyy-MM-dd}); the finance period could not be read.");
        }

        var period = config.Raw("financePeriodSetting").Objects()
            .FirstOrDefault(row => row.Int("siteId") == issueSiteId);

        if (period is null
            || !TryReadDate(period, "periodSDt", out var start)
            || !TryReadDate(period, "periodEDt", out var end))
        {
            _logger.LogWarning(
                "No current finance period is configured for site {SiteId}; authorising with today's date.",
                issueSiteId);
            return (today, $"Today ({today:yyyy-MM-dd}); no finance period is configured for site {issueSiteId}.");
        }

        return today < start || today > end
            ? (end, $"{end:yyyy-MM-dd}, the end of the current finance period ({start:yyyy-MM-dd} to {end:yyyy-MM-dd}), because today is outside it.")
            : (today, $"Today ({today:yyyy-MM-dd}), inside the current finance period ({start:yyyy-MM-dd} to {end:yyyy-MM-dd}).");

        static bool TryReadDate(JsonObject row, string property, out DateOnly date)
        {
            date = default;
            if (!DateTime.TryParse(row.String(property), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                return false;
            }

            date = DateOnly.FromDateTime(value);
            return true;
        }
    }

    /// <summary>
    /// The MRP policy's inward-wise allocation switch. A refusal is not fatal: it falls back to "off",
    /// the ERP's default, under which inward-tracked items take the oldest inward first
    /// (<see cref="InwardFifo"/>).
    /// </summary>
    private async Task<bool> UsesInwardWiseAllocationAsync(string locationCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.CompanyCode) || string.IsNullOrWhiteSpace(locationCode))
        {
            _logger.LogWarning(
                "{Section}:CompanyCode or the issue location code is blank, so the MRP policy cannot be read; "
                + "treating inward-wise allocation as off.",
                IssueToShopFloorOptions.SectionName);
            return false;
        }

        var endpoint = Endpoints.MrpPolicy(_options.CompanyCode, locationCode);
        try
        {
            var policy = await GetJsonObjectAsync(endpoint, cancellationToken);
            return policy.String("useInwardNoWiseAllocation").Equals("Y", StringComparison.OrdinalIgnoreCase);
        }
        catch (ErpBusinessException ex)
        {
            _logger.LogWarning(
                "The MRP policy could not be read from {Endpoint} ({Reason}); treating inward-wise allocation as off.",
                endpoint,
                ex.TechnicalMessage);
            return false;
        }
    }

    // ---- ERP write ------------------------------------------------------------------------------

    /// <summary>Saves the issue and returns its number as the ERP reports it ("26-27/IS/NF1/000045").</summary>
    private async Task<string?> CreateIssueAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var endpoint = Endpoints.CreateIssue;
        using var request = CreateJsonRequest(endpoint, payload, retryable: false);
        using var response = await SendAsync(request, cancellationToken);

        await ErpResponseHandler.EnsureSuccessAsync(response, endpoint, cancellationToken);

        // Read the envelope directly: the number is in 'message' ("<resource key>#26-27/IS/NF1/000045"),
        // not in 'data', which ReadDataAsync would return.
        var envelope = await ErpEnvelopeReader.ReadAsync<JsonNode>(response, cancellationToken);
        if (envelope is null || !envelope.Success)
        {
            throw new ErpBusinessException(
                envelope?.Reason ?? "The ERP did not save the Issue to Shop Floor.",
                $"'{endpoint}' returned success=false: {envelope?.Reason ?? "no body"}.",
                endpoint);
        }

        var message = envelope.Message ?? string.Empty;
        var separator = message.IndexOf('#', StringComparison.Ordinal);

        return separator >= 0 && separator < message.Length - 1 ? message[(separator + 1)..].Trim() : null;
    }

    // ---- HTTP -----------------------------------------------------------------------------------

    private static string YesNo(JsonObject row, string property) => row.Flag(property) ? "Y" : "N";

    private async Task<JsonArray> GetJsonArrayAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonArray>(response, endpoint, cancellationToken);
    }

    private async Task<JsonObject> GetJsonObjectAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonObject>(response, endpoint, cancellationToken);
    }

    private async Task<JsonArray> PostJsonArrayAsync(string endpoint, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = CreateJsonRequest(endpoint, body);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonArray>(response, endpoint, cancellationToken);
    }

    private static HttpRequestMessage CreateJsonRequest(string endpoint, JsonObject body, bool retryable = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: SerializerOptions),
        };

        if (!retryable)
        {
            request.Options.Set(DependencyInjection.NonRetryableKey, true);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
            or Polly.ExecutionRejectedException)
        {
            throw new ErpTransientException(
                "The ERP could not be reached. Please try again shortly.",
                $"'{request.RequestUri}' failed: {ex.Message}",
                request.RequestUri?.ToString(),
                ex);
        }
    }

    /// <summary>Document Control's default Issue row: where issues are numbered and the three Y/N flags.</summary>
    private sealed record IssueNumbering(
        string FinancialYear,
        string GroupCode,
        int LocationId,
        string LocationCode,
        string SiteRequired,
        string AutoNumberRequired,
        string AuthorisationRequired);

    /// <summary>The checks of one run, and the result they add up to.</summary>
    /// <summary>The document an issue is raised against. <see cref="Sjo"/> is set on the SJO-wise tab only.</summary>
    private sealed record IssueDocument(long Id, string FullNumber, SjoHeader? Sjo);

    private sealed class Run(IssueSource source, string documentNumber, bool dryRun)
    {
        private readonly List<IssueCheck> _checks = [];
        private string _documentNumber = documentNumber?.Trim() ?? string.Empty;

        public void Resolved(string fullNumber) => _documentNumber = fullNumber;

        public void Pass(string name, string detail) => _checks.Add(new IssueCheck(name, true, detail));

        public IssueToShopFloorResult Refuse(string name, string reason, IReadOnlyList<IssueShortage>? shortages = null)
        {
            _checks.Add(new IssueCheck(name, false, reason));
            return new IssueToShopFloorResult(source, _documentNumber, false, dryRun, null, reason, _checks, [], shortages ?? []);
        }

        public IssueToShopFloorResult Ready(IReadOnlyList<IssueLine> lines) =>
            new(source, _documentNumber, false, dryRun, null, null, _checks, lines, []);

        public IssueToShopFloorResult Created(string? issueNumber, IReadOnlyList<IssueLine> lines) =>
            new(source, _documentNumber, true, dryRun, issueNumber, null, _checks, lines, []);
    }
}
