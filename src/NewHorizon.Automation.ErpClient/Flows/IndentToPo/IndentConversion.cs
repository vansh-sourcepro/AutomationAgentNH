using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>What became of one indent.</summary>
/// <param name="Notes">
/// Why anything that was not ordered was not ordered, in words an operator can act on. An indent
/// that converts cleanly has none; one that converts partly has one note per item or vendor left
/// behind. This is the difference between "nothing happened" and "here is what stopped it".
/// </param>
/// <param name="PlannedPurchaseOrders">
/// How many orders a dry run worked out it would place. Without it a dry run can only report zero
/// created, which reads exactly like "nothing to do" — the one thing an operator commissioning this
/// needs to be able to tell apart.
/// </param>
public sealed record IndentConversionResult(
    IndentReference Indent,
    IReadOnlyList<IndentPoResult> PurchaseOrders,
    IReadOnlyList<string> Notes,
    int PlannedPurchaseOrders = 0)
{
    public bool Converted => PurchaseOrders.Count > 0;
}

/// <summary>One pass over the authorised indents.</summary>
public sealed record IndentSweepRequest
{
    public IReadOnlyList<int>? Sites { get; init; }

    public IReadOnlyList<IndentType>? IndentTypes { get; init; }

    /// <summary>
    /// Only these indents, by whole document number or bare running number — see
    /// <see cref="IndentNumberSelection"/>. Null or empty sweeps every eligible indent of the
    /// selected types, which is what this pass has always done.
    /// </summary>
    public IReadOnlyList<string>? IndentNumbers { get; init; }

    /// <summary>How many indents this pass may convert.</summary>
    public int MaxIndents { get; init; } = 25;

    /// <inheritdoc cref="IndentDiscoveryRequest.MaxScannedIndents" />
    public int MaxScannedIndents { get; init; } = 2000;

    /// <summary>Work out and report every order, and place none of them.</summary>
    public bool DryRun { get; init; }
}

public sealed record IndentSweepResult(
    int Examined,
    bool DryRun,
    IReadOnlyList<IndentConversionResult> Results)
{
    public int PurchaseOrdersCreated => Results.Sum(result => result.PurchaseOrders.Count);

    /// <summary>What a dry run would have created; equal to <see cref="PurchaseOrdersCreated"/> otherwise.</summary>
    public int PurchaseOrdersPlanned => Results.Sum(result => result.PlannedPurchaseOrders);
}

public sealed partial class IndentToPoService
{
    /// <inheritdoc />
    public async Task<IndentSweepResult> ConvertEligibleAsync(
        IndentSweepRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Normalised once, then used for both halves of the pass: discovery narrows the search to
        // these types, and the conversion below refuses anything outside them. One value, so the
        // two can never disagree.
        var selection = IndentTypeSelection.Normalise(request.IndentTypes);

        // The second allow-list, normalised once for the same reason as the first: discovery
        // narrows to it and the conversion refuses anything outside it, from one value.
        var numbers = IndentNumberSelection.Normalise(request.IndentNumbers);

        _logger.LogInformation(
            "Indent sweep starting for indent type(s) {IndentTypes}{Named}{DryRun}",
            IndentTypeSelection.Describe(selection),
            numbers is null ? string.Empty : $", limited to {IndentNumberSelection.Describe(numbers)}",
            request.DryRun ? " (dry run — nothing will be created)" : string.Empty);

        var eligible = await FindEligibleAsync(
            new IndentDiscoveryRequest
            {
                Sites = request.Sites,
                IndentTypes = selection,
                IndentNumbers = request.IndentNumbers,
                MaxResults = request.MaxIndents,
                MaxScannedIndents = request.MaxScannedIndents,
            },
            cancellationToken);

        // A bare running number can fit several indents — the same 000100 exists in other years and
        // at other sites. Converting all of them would order indents the caller never named, and
        // choosing one would be a guess, so the whole call is refused before the first ERP write.
        var ambiguous = IndentNumberSelection.Ambiguous(numbers, eligible.Select(candidate => candidate.Indent));

        if (ambiguous.Count > 0)
        {
            var detail = string.Join(
                "; ",
                ambiguous.Select(entry => $"{entry.Number} matches {string.Join(", ", entry.Candidates)}"));

            throw new ErpBusinessException(
                $"Indent number(s) named more than one authorised indent — {detail}. Give the whole "
                + "indent number so only the intended indent is converted.",
                $"ConvertEligibleAsync refused an ambiguous indent number selection: {detail}.");
        }

        if (numbers is not null)
        {
            // The named numbers and what they actually selected, on one line, because the two are
            // only the same when every number matched — and that is the thing worth seeing.
            var unmatched = IndentNumberSelection.Unmatched(
                numbers,
                eligible.Select(candidate => candidate.Indent));

            _logger.LogInformation(
                "Indent numbers requested: {Requested}. Selected for conversion: {Selected}. Not found: {NotFound}",
                IndentNumberSelection.Describe(numbers),
                eligible.Count == 0
                    ? "none"
                    : string.Join(", ", eligible.Select(candidate => candidate.Indent.DisplayNumber)),
                unmatched.Count == 0 ? "none" : string.Join(", ", unmatched));
        }

        var results = new List<IndentConversionResult>(eligible.Count);

        foreach (var candidate in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The master switch is checked before every indent, from the persisted value. If PO
            // Automation was turned off while this pass was running, the sweep stops here — the
            // remaining indents are left untouched rather than converted. (The per-indent guarantee
            // also lives in ConvertAsync; this break just stops the loop cleanly and reports where.)
            if (await _poAutomationGate.OffReasonAsync(
                    [Enum.Parse<IndentKind>(candidate.Indent.IndentType.ToString())], cancellationToken)
                is { } stopReason)
            {
                _logger.LogWarning(
                    "Indent sweep stopped after {Done} of {Total} indent(s): {Reason}",
                    results.Count,
                    eligible.Count,
                    stopReason);

                break;
            }

            // One indent's problem is not the next indent's problem: a missing vendor master or a
            // refusal on one must not stop the rest of the sweep. Transient trouble is different —
            // that is the ERP being unwell, and the whole pass should stop and be retried.
            try
            {
                results.Add(await ConvertAsync(
                    candidate.Indent,
                    selection,
                    request.DryRun,
                    cancellationToken,
                    numbers));
            }
            catch (ErpException ex)
            {
                // A refusal — business or transient — for one indent leaves the rest of the sweep,
                // and any indents already converted earlier in it, untouched: recorded here rather
                // than thrown, so a transient hiccup on indent N+1 can never discard indent N's real,
                // already-created (and already correctly recorded — see ConvertAsync) purchase order
                // by unwinding past `results`.
                _logger.LogWarning(
                    "Indent {IndentNumber} could not be converted: {Reason}",
                    candidate.Indent.DisplayNumber,
                    ex.LaymanMessage);

                results.Add(new IndentConversionResult(candidate.Indent, [], [ex.LaymanMessage]));
            }
        }

        var sweep = new IndentSweepResult(results.Count, request.DryRun, results);

        _logger.LogInformation(
            "Indent sweep finished: {Examined} indent(s) examined, {Count} purchase order(s) {Verb}",
            results.Count,
            request.DryRun ? sweep.PurchaseOrdersPlanned : sweep.PurchaseOrdersCreated,
            request.DryRun ? "identified (dry run, nothing created)" : "created");

        // Which indents those orders came from, not just how many there were: with a number filter
        // in play, "1 purchase order created" does not say whether it came from the named indent.
        var ordered = results
            .Where(result => request.DryRun ? result.PlannedPurchaseOrders > 0 : result.Converted)
            .Select(result => result.Indent.DisplayNumber)
            .ToList();

        _logger.LogInformation(
            "Indents {Verb}: {Indents}",
            request.DryRun ? "that would convert" : "converted",
            ordered.Count == 0 ? "none" : string.Join(", ", ordered));

        // A sweep that examined indents and ordered none of them is either a quiet day or a
        // misconfiguration, and the two look identical from the count alone. The distinct reasons
        // tell them apart in one line — usually one reason repeated across every indent.
        if (!request.DryRun && results.Count > 0 && sweep.PurchaseOrdersCreated == 0)
        {
            var reasons = results
                .SelectMany(result => result.Notes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (reasons.Count > 0)
            {
                _logger.LogWarning(
                    "The sweep ordered nothing. {Count} distinct reason(s): {Reasons}",
                    reasons.Count,
                    string.Join(" | ", reasons));
            }
        }

        return sweep;
    }

    /// <inheritdoc />
    public async Task<IndentConversionResult> ConvertIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        bool dryRun,
        CancellationToken cancellationToken) =>
        await ConvertIndentAsync(indentId, indentNumber, sites, indentTypes: null, dryRun, cancellationToken);

    /// <inheritdoc />
    public async Task<IndentConversionResult> ConvertIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        IReadOnlyList<IndentType>? indentTypes,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var wanted = IndentTypeSelection.Normalise(indentTypes);
        var materialTypes = wanted.Where(IndentPoProfile.MaterialIndentTypes.Contains).ToList();

        // Looked up rather than taken on trust: the caller supplies an id, and everything else the
        // conversion needs — year, group, site code, type — has to come from the ERP, along with
        // fresh confirmation that the indent really is authorised and still open.
        //
        // The two families are searched separately rather than through FindEligibleAsync, because
        // an id means nothing without a family: XINDID and XINDAUTOID are keys into different
        // tables and 50823 is a perfectly plausible value for both. One search each, each stopping
        // at its first hit, also keeps the lookup as cheap as it was before service indents
        // existed.
        var match = materialTypes.Count == 0
            ? null
            : (await FindEligibleMaterialIndentsAsync(
                new IndentDiscoveryRequest
                {
                    Sites = sites,
                    IndentId = indentId,
                    // The indent number narrows the ERP's own search to a page or two. Without it
                    // the lookup falls back to scanning newest-first, which finds recent indents at
                    // once and old ones only if they are within the scan budget.
                    SearchValue = indentNumber,
                    MaxResults = 1,
                },
                ResolveSites(new IndentDiscoveryRequest { Sites = sites }),
                materialTypes,
                cancellationToken)).FirstOrDefault();

        var serviceMatch = wanted.Contains(IndentType.Service)
            ? await FindServiceIndentAsync(indentId, indentNumber, sites, cancellationToken)
            : null;

        if (match is not null && serviceMatch is not null)
        {
            // Both families claim the id. Converting the wrong one would raise a real purchase
            // order against a document nobody named, so the caller is asked which they meant.
            throw new ErpBusinessException(
                $"{indentId} is both an eligible {match.Indent.IndentType} indent "
                + $"({match.Indent.DisplayNumber}) and an eligible service indent "
                + $"({serviceMatch.Indent.DisplayNumber}). Say which by passing indentType.",
                $"XINDID {indentId} and XINDAUTOID {indentId} both resolve to convertible indents.");
        }

        var chosen = match ?? serviceMatch
            ?? throw new ErpBusinessException(
                $"Indent {indentNumber ?? indentId.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + "is not an authorised, still-open indent awaiting a purchase order.",
                $"No eligible indent with id {indentId} was found among {string.Join(", ", wanted)}. It "
                + "may be unauthorised, closed, already fully ordered, raised at a site outside "
                + "AutomationAgent:PurchaseOrder:Sites, or beyond the scan budget when no indent "
                + "number was given.");

        return await ConvertAsync(chosen.Indent, wanted, dryRun, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IndentConversionResult> CreateFromIndentAsync(
        IndentReference indent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indent);

        // The caller named this exact indent, so its own type is the whole of the allow-list.
        return ConvertAsync(indent, [indent.IndentType], dryRun: false, cancellationToken);
    }

    /// <summary>
    /// The one place an indent turns into purchase orders, and therefore the one place the
    /// caller's allow-list has to hold.
    /// </summary>
    /// <param name="allowed">
    /// The indent types this run may convert. Discovery has already narrowed the search to them,
    /// so under normal operation this check never fires — which is exactly why it is here. A
    /// narrowed search is an optimisation and can be got wrong; a refusal at the last step before
    /// the first ERP write cannot be, and it means no future entry point can quietly bypass the
    /// filter by reaching this method another way.
    /// </param>
    private async Task<IndentConversionResult> ConvertAsync(
        IndentReference indent,
        IReadOnlyList<IndentType>? allowed,
        bool dryRun,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? allowedNumbers = null)
    {
        // The master switch, re-read from the database immediately before this indent is converted —
        // never cached for the run. If PO Automation was turned off after the run started (or after
        // this indent was discovered), the conversion stops here, before the ERP is touched. Every
        // path funnels through this method — the sweep, a single indent by id, and the retry — so
        // this one check is the guarantee. A dry run is a read-only preview and passes (the calling
        // endpoint already refuses a dry run with 409 when the switch is off).
        if (!dryRun
            && await _poAutomationGate.OffReasonAsync(
                [Enum.Parse<IndentKind>(indent.IndentType.ToString())], cancellationToken) is { } offReason)
        {
            _logger.LogWarning("Not converting indent {IndentNumber}: {Reason}", indent.DisplayNumber, offReason);

            return new IndentConversionResult(indent, [], [offReason]);
        }

        // A dry run creates nothing, so there is nothing for it to have a history of. It is
        // reported to its caller and left out of the tracking tables entirely.
        if (dryRun)
        {
            return await ConvertCoreAsync(indent, allowed, dryRun, cancellationToken, allowedNumbers);
        }

        await _tracker.StartExecutionAsync(ToTrackedIndent(indent), cancellationToken);

        // Split from the tracking write below on purpose: only this call can fail with "the purchase
        // order was not created" being true. Once it returns, any purchase order(s) in `result` are
        // real and already exist in the ERP — a failure recording that fact locally afterward must
        // never be allowed to overwrite a real success with a false failure.
        IndentConversionResult result;

        try
        {
            result = await ConvertCoreAsync(indent, allowed, dryRun, cancellationToken, allowedNumbers);
        }
        catch (ErpTransientException ex)
        {
            await _tracker.FailExecutionAsync(ex.LaymanMessage, ex.Message, transient: true, cancellationToken);
            throw;
        }
        catch (ErpException ex)
        {
            await _tracker.FailExecutionAsync(ex.LaymanMessage, ex.Message, transient: false, cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            await _tracker.FailExecutionAsync(
                "The conversion stopped on an unexpected error. The purchase order was not created.",
                ex.ToString(),
                transient: false,
                cancellationToken);
            throw;
        }

        try
        {
            // Completed, even when it ordered nothing: the execution did its work and the answer
            // was "nothing to order". Each vendor group's own result — an order or the reason
            // there is none — is recorded beside it.
            await _tracker.CompleteExecutionAsync(ToOutcomes(result), cancellationToken);
        }
        catch (Exception ex)
        {
            // A local database write, unrelated to the ERP — the purchase order(s) above already
            // exist regardless of whether this write succeeds. Logged, not routed through
            // FailExecutionAsync and not rethrown: doing either would repeat the exact bug being
            // fixed here — reporting a real success as a failure.
            _logger.LogError(
                ex,
                "Indent {IndentNumber} converted successfully ({Count} purchase order(s)) but recording "
                + "the outcome failed; the purchase order(s) are real and are still returned to the caller",
                indent.DisplayNumber,
                result.PurchaseOrders.Count);
        }

        return result;
    }

    private static TrackedIndent ToTrackedIndent(IndentReference indent) =>
        new(
            indent.IndentId,
            Enum.Parse<IndentKind>(indent.IndentType.ToString()),
            indent.DisplayNumber,
            indent.SiteId);

    /// <summary>
    /// The conversion's own report, in the shape the tracker stores: one row per purchase order
    /// and one per note. A note is why a vendor group produced nothing, and losing it is how an
    /// indent that converted to nothing ends up with the reason recorded nowhere.
    /// </summary>
    private static List<TrackedOutcome> ToOutcomes(IndentConversionResult result)
    {
        var outcomes = new List<TrackedOutcome>(result.PurchaseOrders.Count + result.Notes.Count);

        outcomes.AddRange(result.PurchaseOrders.Select(order => TrackedOutcome.Order(
            order.VendorCode,
            order.PoId,
            order.PoNumber,
            order.ItemCount,
            order.Currency,
            order.RateStructure)));

        outcomes.AddRange(result.Notes.Select(note => TrackedOutcome.Note(note)));

        return outcomes;
    }

    private async Task<IndentConversionResult> ConvertCoreAsync(
        IndentReference indent,
        IReadOnlyList<IndentType>? allowed,
        bool dryRun,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? allowedNumbers = null)
    {
        if (!IndentTypeSelection.Allows(allowed, indent.IndentType))
        {
            throw new ErpBusinessException(
                $"{indent.DisplayNumber} is a {indent.IndentType} indent, and this run was asked to "
                + $"convert {IndentTypeSelection.Describe(allowed)} only.",
                $"ConvertAsync refused {indent.IndentType} against the selection "
                + $"[{IndentTypeSelection.Describe(allowed)}].");
        }

        // The same belt and braces the type filter gets, and for the same reason: discovery has
        // already narrowed to the named indents, so under normal operation this never fires. It is
        // here so that no future entry point can reach the conversion around the filter and order
        // an indent nobody named.
        if (!IndentNumberSelection.Allows(allowedNumbers, indent))
        {
            throw new ErpBusinessException(
                $"{indent.DisplayNumber} was not one of the indent numbers this run was asked to "
                + $"convert: {IndentNumberSelection.Describe(allowedNumbers)}.",
                $"ConvertAsync refused {indent.DisplayNumber} against the number selection "
                + $"[{IndentNumberSelection.Describe(allowedNumbers)}].");
        }

        // A service indent is a different document in a different module, read and ordered through
        // its own endpoints. Everything from here down is the material path.
        if (indent.IndentType == IndentType.Service)
        {
            return await ConvertServiceIndentAsync(indent, dryRun, cancellationToken);
        }

        var profile = IndentPoProfile.For(indent.IndentType);
        var detail = await GetIndentDetailAsync(indent, profile, cancellationToken);
        var notes = new List<string>();

        GuardIndentIsConvertible(indent, detail);
        GuardNoLbtItems(indent, detail);

        await _tracker.EnterStageAsync(
            IndentPoStages.VendorResolution,
            IndentPoTasks.ResolveItemVendor,
            cancellationToken);

        // Group the indent's outstanding items by the vendor terms the ERP would offer for each.
        // Same break key the ERP's own bulk generation uses, so an indent whose items come from two
        // vendors becomes two purchase orders rather than one impossible one.
        var groups = new Dictionary<VendorTerms, List<string>>();

        foreach (var line in detail["itemDetails"]?["itemDetails"].Objects() ?? [])
        {
            var itemCode = line.String("itemCode").Trim();

            if (itemCode.Length == 0)
            {
                continue;
            }

            // "O" is open; the ERP closes or short-closes a line once it is fully accounted for.
            if (!line.String("status").Equals("O", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var terms = await ResolveVendorTermsAsync(itemCode, cancellationToken);

            if (terms is null)
            {
                notes.Add(
                    $"Item {itemCode} has no item/vendor purchase record in the ERP, so no vendor "
                    + "can be asked to supply it. Add one in Item Vendor Purchase, then run again.");
                continue;
            }

            if (!groups.TryGetValue(terms, out var items))
            {
                items = [];
                groups[terms] = items;
            }

            items.Add(itemCode);
        }

        if (groups.Count == 0)
        {
            _logger.LogInformation(
                "Indent {IndentNumber} has nothing that can be ordered: {Notes}",
                indent.DisplayNumber,
                notes.Count == 0 ? "no open item lines remain" : string.Join(" ", notes));

            if (notes.Count == 0)
            {
                notes.Add("Every item line on this indent is already closed, so there is nothing left to order.");
            }

            return new IndentConversionResult(indent, [], notes);
        }

        var purchaseOrders = new List<IndentPoResult>(groups.Count);
        var planned = 0;

        foreach (var (terms, items) in groups)
        {
            if (dryRun)
            {
                var plan =
                    $"Would raise a {profile.PoType} purchase order on {terms.Describe()} at site "
                    + $"{indent.SiteId} for {items.Count} item(s): {string.Join(", ", items)}.";

                notes.Add(plan);
                planned++;

                // Logged, not just returned: a dry run started by the timer has no caller to read
                // the response, and the log is the only place its findings can land.
                _logger.LogInformation("Indent {IndentNumber}: {Plan}", indent.DisplayNumber, plan);

                continue;
            }

            try
            {
                purchaseOrders.Add(await RunSequenceAsync(
                    new PoBuildRequest(profile, indent.SiteId, terms.VendorCode, terms.RateStructure, indent.IndentId),
                    cancellationToken));
            }
            catch (ErpException ex)
            {
                // A refusal — business or transient — for one vendor leaves the others, and any
                // purchase order(s) already created in earlier groups, untouched: recorded as a note
                // rather than thrown, so a transient hiccup on this group can never discard an earlier
                // group's real, already-created purchase order by unwinding past `purchaseOrders`.
                notes.Add($"{terms.Describe()}: {ex.LaymanMessage}");
            }
        }

        if (!dryRun)
        {
            // The notes are the whole diagnosis. A refusal for one vendor group is recorded rather
            // than thrown, so unless it is logged here an indent that converted to nothing reads as
            // "produced 0 purchase order(s): none" with no clue anywhere as to why.
            if (purchaseOrders.Count == 0 && notes.Count > 0)
            {
                _logger.LogWarning(
                    "Indent {IndentNumber} produced no purchase order: {Reasons}",
                    indent.DisplayNumber,
                    string.Join(" ", notes));
            }
            else
            {
                _logger.LogInformation(
                    "Indent {IndentNumber} produced {Count} purchase order(s): {PoNumbers}{Reasons}",
                    indent.DisplayNumber,
                    purchaseOrders.Count,
                    purchaseOrders.Count == 0 ? "none" : string.Join(", ", purchaseOrders.Select(po => po.PoNumber)),
                    notes.Count == 0 ? string.Empty : $". Not ordered: {string.Join(" ", notes)}");
            }
        }

        return new IndentConversionResult(indent, purchaseOrders, notes, planned);
    }

    /// <summary>
    /// Re-reads the indent from the ERP rather than trusting the list row, because a list row can be
    /// minutes old and authorisation is the whole point of the check.
    /// </summary>
    private async Task<JsonObject> GetIndentDetailAsync(
        IndentReference indent,
        IndentPoProfile profile,
        CancellationToken cancellationToken)
    {
        var endpoint = _endpoints.IndentDetail(
            // Site flag "Y" and mode "S" are what the indent screen sends when it is only reading.
            siteFlag: "Y",
            indentYear: indent.Year,
            indentGroup: indent.Group,
            siteCode: indent.SiteCode,
            indentNumber: indent.Number,
            mode: "S",
            indentType: profile.PoType,
            indentDocSubType: profile.IndentDocSubType);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAsync(request, cancellationToken);

        return await ErpResponseHandler.ReadDataAsync<JsonObject>(response, endpoint, cancellationToken);
    }

    private static void GuardIndentIsConvertible(IndentReference indent, JsonObject detail)
    {
        var header = detail["headerDetail"] as JsonObject;

        var authorisation = header.String("authStatus");

        if (!authorisation.Contains("Authoris", StringComparison.OrdinalIgnoreCase)
            && !authorisation.Contains("Authoriz", StringComparison.OrdinalIgnoreCase))
        {
            throw new ErpBusinessException(
                $"Indent {indent.DisplayNumber} has not been authorised, so it cannot become a purchase order.",
                $"getIndentDetail reported authStatus '{authorisation}' for indent {indent.IndentId}.");
        }

        var documentStatus = header.String("docStatusDisp");

        if (!documentStatus.Equals("Open", StringComparison.OrdinalIgnoreCase))
        {
            throw new ErpBusinessException(
                $"Indent {indent.DisplayNumber} is {documentStatus.ToLowerInvariant()}, so there is "
                + "nothing left on it to order.",
                $"getIndentDetail reported docStatusDisp '{documentStatus}' for indent {indent.IndentId}.");
        }
    }

    /// <summary>
    /// An LBT (Length/Breadth/Thickness) line's "already ordered" quantity is written from a
    /// formula-computed theoretical weight rather than the requested count, and the ERP's own
    /// over-order guard (<c>CSP_XPOITMDEL_INSERT</c>) is scoped off for exactly these lines via
    /// <c>ISNULL(XINDITMSIZE,'') = ''</c> — so an indent containing one is refused here, before
    /// vendor resolution ever runs, rather than let it surface downstream as a misleading "nothing
    /// left to order" once a rounded-up PO weight has silently satisfied it.
    /// </summary>
    /// <remarks>
    /// <c>itemSize</c> (<c>XINDITMSIZE</c>) is the same signal the ERP's own guard treats as
    /// authoritative for "this indent line is LBT" — non-blank on a dimensioned line, blank
    /// otherwise. It is returned by <c>getIndentDetail</c> on every material item line, so no
    /// extra ERP call is needed to check it.
    /// </remarks>
    private static void GuardNoLbtItems(IndentReference indent, JsonObject detail)
    {
        var hasLbtItem = (detail["itemDetails"]?["itemDetails"].Objects() ?? [])
            .Any(line => line.String("itemSize").Trim().Length > 0);

        if (hasLbtItem)
        {
            throw new ErpBusinessException(
                $"Indent {indent.DisplayNumber} contains an LBT item and cannot be converted to PO.",
                $"getIndentDetail reported a non-blank itemSize (XINDITMSIZE) on at least one item "
                + $"line of indent {indent.IndentId}, identifying it as an LBT item.");
        }
    }
}
