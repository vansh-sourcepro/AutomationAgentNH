using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.ErpClient.Flows.PoToGrn;

public interface IPoToGrnService
{
    /// <summary>
    /// Finds authorised POs with quantity pending and raises an unauthorised GRN for each one's
    /// receivable lines, at the pending quantity.
    /// </summary>
    Task<PoGrnSweepResult> ReceiveEligibleAsync(PoGrnSweepRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Walks the ERP's GRN Entry sequence for authorised POs, the way the GRN screen does when a PO is
/// picked from the PO list — minus the dropdowns, and with every quantity set to what is pending.
/// </summary>
/// <remarks>
/// <para>
/// Per run: finance periods and inventory policy once; the authorised PO list, paged. Per PO: its
/// vendor/currency/warehouses, Document Control (cached), its receivable lines and tax rows, one
/// tax-engine call per distinct line price, then the create.
/// </para>
/// <para>
/// Duplicate safety is the ERP's, as with Indent → PO: the create raises the PO's received
/// quantity inside the same transaction, so a second run finds nothing pending — provided the tax
/// rows travel with it (see <see cref="GrnPayloadBuilder"/>) — and the ERP refuses an over-receipt
/// outright.
/// </para>
/// </remarks>
public sealed class PoToGrnService : IPoToGrnService
{
    private const int DiscoveryPageSize = 200;

    private readonly HttpClient _httpClient;
    private readonly IErpTokenProvider _tokenProvider;
    private readonly PoGrnEndpointOptions _endpoints;
    private readonly PoGrnOptions _options;
    private readonly IClock _clock;
    private readonly IPoGrnAutomationConfigRepository _configs;
    private readonly IPoGrnHistory _history;
    private readonly ILogger<PoToGrnService> _logger;

    private readonly Dictionary<string, GrnDocumentControl> _documentControl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonArray> _rateComponents = new(StringComparer.Ordinal);

    private bool _firstLineLogged;

    public PoToGrnService(
        IHttpClientFactory httpClientFactory,
        IErpTokenProvider tokenProvider,
        IOptions<PoGrnEndpointOptions> endpoints,
        IOptions<PoGrnOptions> options,
        IClock clock,
        IPoGrnAutomationConfigRepository configs,
        IPoGrnHistory history,
        ILogger<PoToGrnService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _tokenProvider = tokenProvider;
        _endpoints = endpoints.Value;
        _options = options.Value;
        _clock = clock;
        _configs = configs;
        _history = history;
        _logger = logger;
    }

    /// <summary>What a run needs to know once, not per PO.</summary>
    private sealed record RunContext(
        PoGrnSweepRequest Request,
        DateOnly GrnDate,
        int UserId,
        IReadOnlyDictionary<int, (DateOnly Start, DateOnly End)> Periods,
        bool ItemLevelWarehouse);

    /// <inheritdoc />
    public async Task<PoGrnSweepResult> ReceiveEligibleAsync(
        PoGrnSweepRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.DryRun && string.IsNullOrWhiteSpace(request.InvoiceNumber))
        {
            throw new ErpBusinessException(
                "Set the GRN invoice number in the GRN Automation settings before running it.",
                "PoGrnSweepRequest.InvoiceNumber is blank; every GRN needs XGRNBILLNO.");
        }

        var types = request.PoTypes is { Count: > 0 } wanted ? wanted.Distinct().ToList() : [.. PoGrnTypes.All];
        var sites = ResolveSites(request.Sites);
        var ids = request.PoIds is { Count: > 0 } ? request.PoIds.ToHashSet() : null;
        var numbers = NormaliseNumbers(request.PoNumbers);

        _logger.LogInformation(
            "PO → GRN sweep starting: {Types} PO(s) at site(s) {Sites}, receipt mode {Mode}{Named}{DryRun}",
            string.Join(", ", types),
            string.Join(",", sites),
            request.ReceiptMode,
            ids is null && numbers is null ? string.Empty : ", limited to named POs",
            request.DryRun ? " (dry run — nothing will be created)" : string.Empty);

        var run = new RunContext(
            request,
            _clock.LocalDate,
            await _tokenProvider.GetUserIdAsync(cancellationToken),
            await GetFinancePeriodsAsync(cancellationToken),
            await GetItemLevelWarehouseAsync(cancellationToken));

        var candidates = await DiscoverAsync(sites, types, ids, numbers, request.MaxPos, run.UserId, cancellationToken);

        var ambiguous = numbers?
            .Where(number => !number.Contains('/', StringComparison.Ordinal))
            .Select(number => (Number: number, Matches: candidates.Where(po => Matches(po, number)).ToList()))
            .Where(entry => entry.Matches.Count > 1)
            .ToList() ?? [];

        if (ambiguous.Count > 0)
        {
            var detail = string.Join(
                "; ",
                ambiguous.Select(entry => $"{entry.Number} matches {string.Join(", ", entry.Matches.Select(po => po.DisplayNumber))}"));

            throw new ErpBusinessException(
                $"PO number(s) named more than one authorised PO — {detail}. Give the whole PO number.",
                $"ReceiveEligibleAsync refused an ambiguous PO number selection: {detail}.");
        }

        var notFound = NotFound(ids, numbers, candidates);
        var results = new List<PoGrnResult>();
        string? stopped = null;

        foreach (var po in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The master switch, re-read before every PO: turning GRN Automation off mid-run stops
            // it before the next one. GRNs already made stay made.
            if (!request.DryRun && await OffReasonAsync(cancellationToken) is { } offReason)
            {
                stopped = offReason;
                _logger.LogWarning("PO → GRN sweep stopped after {Done} of {Total} PO(s): {Reason}", results.Count, candidates.Count, offReason);
                break;
            }

            try
            {
                results.AddRange(await ReceivePoAsync(po, run, cancellationToken));
            }
            catch (ErpException ex)
            {
                // One PO's problem is not the next PO's. Recorded, never thrown, so a hiccup on PO
                // N+1 cannot unwind past PO N's real GRN.
                _logger.LogWarning("PO {PoNumber} could not be received: {Reason}", po.DisplayNumber, ex.LaymanMessage);

                var failed = Result(po, vendorCode: string.Empty, warehouseId: 0, PoGrnReceiptStatus.Failed, notes: [ex.LaymanMessage]);
                results.Add(failed);
                await RecordAsync(failed, request.DryRun, cancellationToken);
            }
        }

        var sweep = new PoGrnSweepResult(results.Count, request.DryRun, results, notFound, stopped);

        _logger.LogInformation(
            "PO → GRN sweep finished: {Examined} result(s), {Count} GRN(s) {Verb}: {Grns}",
            results.Count,
            request.DryRun ? sweep.GrnsPlanned : sweep.GrnsCreated,
            request.DryRun ? "would be created (dry run)" : "created",
            string.Join(", ", results.Where(result => result.GrnNumber is not null).Select(result => result.GrnNumber)) is { Length: > 0 } list ? list : "none");

        return sweep;
    }

    // ---- Discovery --------------------------------------------------------------------------

    private async Task<IReadOnlyList<PoGrnCandidate>> DiscoverAsync(
        IReadOnlyList<int> sites,
        IReadOnlyList<PoGrnType> types,
        IReadOnlySet<long>? ids,
        IReadOnlySet<string>? numbers,
        int maxPos,
        int userId,
        CancellationToken cancellationToken)
    {
        var found = new List<PoGrnCandidate>();

        // One named bare number narrows the ERP's own search to a page; otherwise scan newest-first.
        var searchValue = numbers is { Count: 1 } ? numbers.First().Split('/').Last() : string.Empty;

        foreach (var type in types)
        {
            var ofType = 0;
            var scanned = 0;

            for (var page = 1; ofType < maxPos; page++)
            {
                var body = new JsonObject
                {
                    ["pageNumber"] = page,
                    ["pageSize"] = DiscoveryPageSize,
                    ["sortField"] = string.Empty,
                    ["sortDirection"] = string.Empty,
                    ["searchValue"] = searchValue,
                    // The PO list screen's own literals: a quoted SQL list of POHTYPE codes.
                    ["potype"] = $"'{type.Code()}'",
                    ["usrLvl"] = 0,
                    ["usrSubLvl"] = 0,
                    ["mulLvlAuthRed"] = false,
                    ["valLimit"] = 0,
                    ["docType"] = "PR",
                    ["docSubType"] = type.PoSubType(),
                    ["companyId"] = _options.CompanyId,
                    ["userId"] = userId,
                    ["ptype"] = "R",
                    ["vendorcode"] = string.Empty,
                    ["fromdate"] = string.Empty,
                    ["todate"] = string.Empty,
                };

                var endpoint = _endpoints.PoList(sites, _options.CompanyId, _options.LocationId);
                var batch = (await PostArrayAsync(endpoint, body, cancellationToken: cancellationToken)).Rows().ToList();

                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var row in batch)
                {
                    if (ReadCandidate(row, type) is not { } po)
                    {
                        continue;
                    }

                    if ((ids is not null || numbers is not null)
                        && !(ids?.Contains(po.PoId) ?? false)
                        && !(numbers?.Any(number => Matches(po, number)) ?? false))
                    {
                        continue;
                    }

                    found.Add(po);

                    if (++ofType >= maxPos)
                    {
                        break;
                    }
                }

                scanned += batch.Count;

                if (batch.Count < DiscoveryPageSize
                    || scanned >= batch[0].Whole("totalRows")
                    || (_options.MaxScannedPos > 0 && scanned >= _options.MaxScannedPos))
                {
                    break;
                }
            }
        }

        // Newest first across both types, then capped: the cap is on the run, not on each type.
        var eligible = found.OrderByDescending(po => po.PoId).Take(maxPos).ToList();

        _logger.LogInformation("Found {Count} authorised PO(s) with quantity waiting for a GRN", eligible.Count);

        return eligible;
    }

    private PoGrnCandidate? ReadCandidate(JsonObject row, PoGrnType type)
    {
        // "isgrn": a line still has quantity pending and is open. Status A already means authorised.
        if (!row.Flag("isgrn")
            || !row.Text("rowstatus").Trim().Equals("O", StringComparison.OrdinalIgnoreCase)
            || PoGrnTypes.FromCode(row.Text("poType")) != type)
        {
            return null;
        }

        // IsAmendment is the PO list's "authorised, printed and open" — the ERP's own GRN button rule.
        if (_options.RequirePrintedPo && !row.Flag("isAmendment"))
        {
            return null;
        }

        return new PoGrnCandidate(
            PoId: row.Long("id"),
            Year: row.Text("year").Trim(),
            Group: row.Text("grp").Trim(),
            SiteCode: row.Text("site").Trim(),
            Number: row.Text("nmbr").Trim(),
            SiteId: row.Whole("siteid"),
            PoType: type,
            PoDate: row.Date("date"),
            Vendor: row.Text("vendor").Trim());
    }

    // ---- One PO -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<PoGrnResult>> ReceivePoAsync(
        PoGrnCandidate po,
        RunContext run,
        CancellationToken cancellationToken)
    {
        var request = run.Request;
        var header = (await GetArrayAsync(_endpoints.PoToGrnData(po.PoId), cancellationToken)).Rows().ToList();

        if (header.Count == 0)
        {
            return [await SkipAsync(po, string.Empty, 0, 0, "Nothing is left to receive on this PO.", request.DryRun, cancellationToken)];
        }

        var vendorCode = header[0].Text("povndcd").Trim();
        var vendorName = header[0].Text("povndname").Trim();
        var currency = header[0].Text("vndcurcd").Trim();

        if (!currency.Equals(_options.DomesticCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return [await SkipAsync(po, vendorCode, 0, 0,
                $"The vendor trades in {currency}; automated GRNs support {_options.DomesticCurrency} only.",
                request.DryRun, cancellationToken)];
        }

        if (!run.Periods.TryGetValue(po.SiteId, out var period))
        {
            return [await SkipAsync(po, vendorCode, 0, 0,
                $"Period end settings are not done for site {po.SiteCode}, so no GRN can be dated there.",
                request.DryRun, cancellationToken)];
        }

        if (run.GrnDate < period.Start || run.GrnDate > period.End)
        {
            return [await SkipAsync(po, vendorCode, 0, 0,
                $"Today ({run.GrnDate:dd/MM/yyyy}) is outside site {po.SiteCode}'s open period "
                + $"{period.Start:dd/MM/yyyy}–{period.End:dd/MM/yyyy}.",
                request.DryRun, cancellationToken)];
        }

        if (po.PoDate is { } poDate && poDate > run.GrnDate)
        {
            return [await SkipAsync(po, vendorCode, 0, 0,
                $"The PO is dated {poDate:dd/MM/yyyy}, after today; a GRN cannot be dated before its PO.",
                request.DryRun, cancellationToken)];
        }

        var document = await GetDocumentControlAsync(po, run.GrnDate, cancellationToken);

        if (!document.AutoNumberRequired.Equals("Y", StringComparison.OrdinalIgnoreCase))
        {
            return [await SkipAsync(po, vendorCode, 0, 0,
                $"Site {po.SiteCode} does not auto-number GRNs, and the agent will not invent a number.",
                request.DryRun, cancellationToken)];
        }

        // One GRN per warehouse: with header-level warehouses the ERP lists a PO's lines one
        // warehouse at a time. Line-level warehouses put every line on one GRN.
        var warehouses = run.ItemLevelWarehouse
            ? [0]
            : header.Select(row => row.Whole("powhid")).Where(id => id > 0).Distinct().ToList();

        var results = new List<PoGrnResult>(warehouses.Count);

        foreach (var warehouseId in warehouses)
        {
            results.Add(await ReceiveAtWarehouseAsync(
                po, run, vendorCode, vendorName, currency, warehouseId, document, period, cancellationToken));
        }

        return results;
    }

    private async Task<PoGrnResult> ReceiveAtWarehouseAsync(
        PoGrnCandidate po,
        RunContext run,
        string vendorCode,
        string vendorName,
        string currency,
        int warehouseId,
        GrnDocumentControl document,
        (DateOnly Start, DateOnly End) period,
        CancellationToken cancellationToken)
    {
        var request = run.Request;

        var linesEndpoint = _endpoints.PoLines(po.SiteId, warehouseId, vendorCode, po.PoId, currency, po.PoType.Code(), run.GrnDate);
        var detail = await GetObjectAsync(linesEndpoint, cancellationToken);

        var allRows = detail["itemDetailsModule"].Rows().ToList();
        var lines = allRows.Where(line => line.Long("xgrndpoid") == po.PoId).ToList();

        LogFirstLineShapeOnce(linesEndpoint, detail, allRows);

        if (lines.Count == 0)
        {
            _logger.LogWarning(
                "'{Endpoint}' returned {Total} line row(s), none for PO {PoId}. Top-level keys: {Keys}",
                linesEndpoint,
                allRows.Count,
                po.PoId,
                string.Join(", ", detail.Select(property => property.Key)));

            return await SkipAsync(po, vendorCode, warehouseId, 0,
                $"The ERP returned no receivable lines for this PO at warehouse {warehouseId} "
                + $"({allRows.Count} line row(s) in total).",
                request.DryRun, cancellationToken);
        }

        var taxRows = detail["ratestruDetailsModule"].Rows()
            .Where(row => row.Long("xgrndpoid") == po.PoId)
            .ToList();

        if (lines.FirstOrDefault(line => line.Date("poamddate") is { } amended && amended > run.GrnDate) is { } amendedLine)
        {
            return await SkipAsync(po, vendorCode, warehouseId, lines.Count,
                $"The PO was amended on {amendedLine.Text("poamddate")}, after today; a GRN cannot be dated before it.",
                request.DryRun, cancellationToken);
        }

        List<JsonObject> TaxTemplateFor(JsonObject line) =>
            [.. taxRows.Where(row =>
                (row.Text("xdtdtmcd").Trim().Length == 0 ? row.Text("itemCode") : row.Text("xdtdtmcd")).Trim()
                    .Equals(line.Text("itmcode").Trim(), StringComparison.OrdinalIgnoreCase))];

        var selection = GrnLineClassifier.Select(lines, request.ReceiptMode, line =>
        {
            if (run.ItemLevelWarehouse && line.Whole("xgrndwhid") <= 0)
            {
                return $"Item {line.Text("itmcode").Trim()} has no warehouse the agent's ERP user may receive into.";
            }

            // Without tax rows the ERP does not raise the PO's received quantity, so the next run
            // would receive the same line again. Never send such a line.
            return TaxTemplateFor(line).Count == 0
                ? $"Item {line.Text("itmcode").Trim()} has no tax rows on the PO, so its receipt cannot be recorded against it."
                : null;
        });

        if (selection.SkipReason is { } skipReason)
        {
            return await SkipAsync(po, vendorCode, warehouseId, selection.SkippedLines.Count, skipReason, request.DryRun, cancellationToken);
        }

        var totals = selection.Receivable
            .Select((line, index) => GrnLineTotals.From(line, index + 1, TaxTemplateFor(line)))
            .ToList();

        if (request.DryRun)
        {
            var plan = $"Would receive {totals.Count} line(s): "
                + string.Join(", ", totals.Select(line => $"{line.ItemCode} × {line.QuantityPuom.ToString(CultureInfo.InvariantCulture)}"));

            _logger.LogInformation("PO {PoNumber} at warehouse {WarehouseId}: {Plan}", po.DisplayNumber, warehouseId, plan);

            return Result(po, vendorCode, warehouseId, PoGrnReceiptStatus.Created,
                notes: [plan, .. selection.SkippedLines],
                planned: true,
                linesReceived: totals.Count,
                linesSkipped: selection.SkippedLines.Count);
        }

        foreach (var line in totals)
        {
            if (line.RateStructureCode.Length == 0)
            {
                throw new ErpBusinessException(
                    $"Item {line.ItemCode} on PO {po.DisplayNumber} has no rate structure, so its tax cannot be worked out.",
                    $"Neither rateStructureCode nor a tax row's taxRateCode was set for item {line.ItemCode}.");
            }

            var components = await GetRateComponentsAsync(line.RateStructureCode, line.DiscountedRate, currency, cancellationToken);
            line.ApplyTaxes(TaxTemplateFor(line.Source), components);
        }

        var payload = GrnPayloadBuilder.Build(new GrnContext
        {
            Po = po,
            VendorCode = vendorCode,
            VendorName = vendorName,
            Currency = currency,
            WarehouseId = warehouseId,
            ItemLevelWarehouse = run.ItemLevelWarehouse,
            DocumentControl = document,
            GrnDate = run.GrnDate,
            PeriodStart = period.Start,
            PeriodEnd = period.End,
            InvoiceNumber = request.InvoiceNumber!.Trim(),
            CompanyId = _options.CompanyId,
            UserId = run.UserId,
            Remark = _options.Remark,
            Lines = totals,
        });

        var (grnId, grnNumber) = await CreateGrnAsync(payload, cancellationToken);

        _logger.LogInformation(
            "Created GRN {GrnNumber} (id {GrnId}) for PO {PoNumber}, {Lines} line(s){Skipped}",
            grnNumber,
            grnId,
            po.DisplayNumber,
            totals.Count,
            selection.SkippedLines.Count == 0 ? string.Empty : $"; left for a person: {string.Join(" ", selection.SkippedLines)}");

        var created = Result(po, vendorCode, warehouseId, PoGrnReceiptStatus.Created,
            notes: selection.SkippedLines,
            grnId: grnId,
            grnNumber: grnNumber,
            linesReceived: totals.Count,
            linesSkipped: selection.SkippedLines.Count);

        await RecordAsync(created, request.DryRun, cancellationToken);

        return created;
    }

    /// <summary>
    /// Once per run, the first line exactly as the ERP sent it — so a field-name or shape mismatch
    /// shows up in the log without anyone having to replay the call by hand.
    /// </summary>
    private void LogFirstLineShapeOnce(string endpoint, JsonObject detail, IReadOnlyList<JsonObject> rows)
    {
        if (_firstLineLogged)
        {
            return;
        }

        _firstLineLogged = true;

        var sample = rows.Count == 0 ? detail.ToJsonString() : rows[0].ToJsonString();

        _logger.LogInformation(
            "First PO lines answer this run, from '{Endpoint}' ({Count} row(s)): {Sample}",
            endpoint,
            rows.Count,
            sample.Length <= 4000 ? sample : sample[..4000] + "…");
    }

    private async Task<(long GrnId, string GrnNumber)> CreateGrnAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var endpoint = _endpoints.CreateGrn;

        // Never replayed by the transport retry: a create whose answer was lost may have committed,
        // and a blind resend would receive the PO twice. The next run's fresh read is the recovery.
        using var request = JsonRequest(endpoint, payload, retryable: false);
        using var response = await SendAsync(request, cancellationToken);

        await ErpResponseHandler.EnsureSuccessAsync(response, endpoint, cancellationToken);

        ErpEnvelope<JsonObject>? envelope;
        try
        {
            envelope = await ErpEnvelopeReader.ReadAsync<JsonObject>(response, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new ErpTransientException(
                "The ERP accepted the GRN but its reply could not be read.",
                $"'{endpoint}' returned a body that is not the ERP envelope: {ex.Message}",
                endpoint,
                ex);
        }

        if (envelope is null)
        {
            throw new ErpTransientException("The ERP returned an empty response.", $"'{endpoint}' returned an empty body.", endpoint);
        }

        if (!envelope.Success)
        {
            throw new ErpBusinessException(
                envelope.Reason ?? "The ERP refused the GRN.",
                $"'{endpoint}' returned success=false: {envelope.Reason ?? "no reason given"}.",
                endpoint);
        }

        // Unlike PO create, the GRN number travels only in the message: "GRNCreated#FY/grp/site/no#autoId".
        if (!TryParseCreated(envelope.Message, out var grnId, out var grnNumber))
        {
            throw new ErpTransientException(
                "The ERP accepted the GRN but did not say its number. Check the GRN list before running again.",
                $"'{endpoint}' reported success with message '{envelope.Message}', which is not 'key#number#id'.",
                endpoint);
        }

        return (grnId, grnNumber);
    }

    internal static bool TryParseCreated(string? message, out long grnId, out string grnNumber)
    {
        grnId = 0;
        grnNumber = string.Empty;

        var parts = (message ?? string.Empty).Split('#');

        if (parts.Length < 3
            || string.IsNullOrWhiteSpace(parts[1])
            || !long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out grnId))
        {
            return false;
        }

        grnNumber = parts[1].Trim();
        return true;
    }

    // ---- Once-per-run reads ----------------------------------------------------------------

    private async Task<IReadOnlyDictionary<int, (DateOnly Start, DateOnly End)>> GetFinancePeriodsAsync(CancellationToken cancellationToken)
    {
        var config = await GetObjectAsync(_endpoints.SystemConfig, cancellationToken);
        var periods = new Dictionary<int, (DateOnly, DateOnly)>();

        foreach (var row in config["financePeriodSetting"].Rows())
        {
            if (row.Date("periodSDt") is { } start && row.Date("periodEDt") is { } end)
            {
                periods[row.Whole("siteId")] = (start, end);
            }
        }

        return periods;
    }

    private async Task<bool> GetItemLevelWarehouseAsync(CancellationToken cancellationToken) =>
        (await GetObjectAsync(_endpoints.InventoryPolicy, cancellationToken)).Flag("islinelevelwhgrn");

    private async Task<GrnDocumentControl> GetDocumentControlAsync(
        PoGrnCandidate po,
        DateOnly grnDate,
        CancellationToken cancellationToken)
    {
        var financialYear = GrnFinancialYear.For(grnDate);
        var endpoint = _endpoints.DocumentControlDefault(financialYear, GrnPayloadBuilder.DocType, po.PoType.GrnSubType(), po.SiteId);

        if (_documentControl.TryGetValue(endpoint, out var cached))
        {
            return cached;
        }

        var rows = (await GetArrayAsync(endpoint, cancellationToken)).Rows().ToList();
        var row = rows.FirstOrDefault(r => r.Flag("isDefault")) ?? rows.FirstOrDefault()
            ?? throw new ErpBusinessException(
                $"The ERP has no GRN numbering set up for {po.PoType.GrnSubType()} at site {po.SiteCode} in {financialYear}.",
                $"'{endpoint}' returned no Document Control rows.",
                endpoint);

        var document = new GrnDocumentControl(
            FinancialYear: financialYear,
            GroupCode: row.Text("groupCode").Trim(),
            SiteCode: row.Text("locationCode", po.SiteCode).Trim(),
            SiteRequired: row.Flag("isLocationRequired") ? "Y" : "N",
            AutoNumberRequired: row.Flag("isAutoNumberGenerated") ? "Y" : "N");

        _documentControl[endpoint] = document;

        return document;
    }

    private async Task<JsonArray> GetRateComponentsAsync(
        string rateStructureCode,
        decimal basicRate,
        string currency,
        CancellationToken cancellationToken)
    {
        var endpoint = _endpoints.RateStructureCalculation(rateStructureCode, basicRate, currency);

        if (!_rateComponents.TryGetValue(endpoint, out var components))
        {
            components = await GetArrayAsync(endpoint, cancellationToken);
            _rateComponents[endpoint] = components;
        }

        return (JsonArray)components.DeepClone();
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private async Task<string?> OffReasonAsync(CancellationToken cancellationToken)
    {
        if (!_configs.IsEnabled)
        {
            return null;
        }

        return (await _configs.GetAsync(cancellationToken)).IsActive
            ? null
            : "GRN Automation was turned off.";
    }

    private async Task<PoGrnResult> SkipAsync(
        PoGrnCandidate po,
        string vendorCode,
        int warehouseId,
        int linesSkipped,
        string reason,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("PO {PoNumber} skipped: {Reason}", po.DisplayNumber, reason);

        var skipped = Result(po, vendorCode, warehouseId, PoGrnReceiptStatus.Skipped, notes: [reason], linesSkipped: linesSkipped);
        await RecordAsync(skipped, dryRun, cancellationToken);

        return skipped;
    }

    private async Task RecordAsync(PoGrnResult result, bool dryRun, CancellationToken cancellationToken)
    {
        // A dry run creates nothing, so it has no history.
        if (dryRun)
        {
            return;
        }

        await _history.RecordAsync(
            new PoGrnOutcome(
                new PoGrnReceiptSubject(result.PoId, result.PoNumber, result.PoType, result.SiteId, result.VendorCode, result.WarehouseId),
                result.Status,
                result.GrnId,
                result.GrnNumber,
                result.LinesReceived,
                result.LinesSkipped,
                result.Notes.Count == 0 ? null : string.Join(" ", result.Notes)),
            cancellationToken);
    }

    private static PoGrnResult Result(
        PoGrnCandidate po,
        string vendorCode,
        int warehouseId,
        PoGrnReceiptStatus status,
        IReadOnlyList<string> notes,
        bool planned = false,
        long? grnId = null,
        string? grnNumber = null,
        int linesReceived = 0,
        int linesSkipped = 0) =>
        new(po.PoId, po.DisplayNumber, po.PoType.Code(), po.SiteId, vendorCode, warehouseId, status, planned,
            grnId, grnNumber, linesReceived, linesSkipped, notes);

    private IReadOnlyList<int> ResolveSites(IReadOnlyList<int>? requested)
    {
        if (requested is { Count: > 0 })
        {
            return requested;
        }

        if (_options.Sites.Count > 0)
        {
            return _options.Sites;
        }

        _logger.LogWarning(
            "AutomationAgent:PoToGrn:Sites is empty, so only site {LocationId} is swept for authorised POs.",
            _options.LocationId);

        return [_options.LocationId];
    }

    private static HashSet<string>? NormaliseNumbers(IReadOnlyList<string>? numbers)
    {
        var set = numbers?
            .Select(number => number.Trim())
            .Where(number => number.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return set is { Count: > 0 } ? set : null;
    }

    private static bool Matches(PoGrnCandidate po, string number) =>
        number.Contains('/', StringComparison.Ordinal)
            ? po.DisplayNumber.Equals(number, StringComparison.OrdinalIgnoreCase)
            : po.Number.Equals(number, StringComparison.OrdinalIgnoreCase);

    private static List<string> NotFound(
        IReadOnlySet<long>? ids,
        IReadOnlySet<string>? numbers,
        IReadOnlyList<PoGrnCandidate> found)
    {
        var missing = new List<string>();

        missing.AddRange(ids?.Where(id => found.All(po => po.PoId != id)).Select(id => id.ToString(CultureInfo.InvariantCulture)) ?? []);
        missing.AddRange(numbers?.Where(number => !found.Any(po => Matches(po, number))) ?? []);

        return missing;
    }

    private async Task<JsonArray> GetArrayAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonArray>(response, endpoint, cancellationToken);
    }

    private async Task<JsonObject> GetObjectAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonObject>(response, endpoint, cancellationToken);
    }

    private async Task<JsonArray> PostArrayAsync(string endpoint, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = JsonRequest(endpoint, body, retryable: true);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonArray>(response, endpoint, cancellationToken);
    }

    private static HttpRequestMessage JsonRequest(string endpoint, JsonObject body, bool retryable)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
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
}
