using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>Enough of an indent to find it again in the ERP.</summary>
/// <param name="IndentId">
/// <c>XINDID</c>, the ERP's surrogate key. This is what the delivery rows of a purchase order carry
/// in <c>PIDINDID</c>, so it — not the printed number — is what an indent is matched on.
/// </param>
public sealed record IndentReference(
    long IndentId,
    string Year,
    string Group,
    string Number,
    int SiteId,
    string SiteCode,
    IndentType IndentType)
{
    /// <summary>The number as a person reads it, for logs and messages.</summary>
    public string DisplayNumber => string.Join('/', Year, Group, SiteCode, Number);
}

/// <summary>An authorised indent that still has something left to order.</summary>
/// <param name="DocumentStatus">
/// The ERP's own word for it — "Open", "Close", "Short Closed". Only Open is offered; the others
/// are indents the ERP has already finished with.
/// </param>
public sealed record EligibleIndent(
    IndentReference Indent,
    string DocumentStatus,
    DateOnly? IndentDate,
    string RequestedBy);

/// <summary>Which authorised indents to look for.</summary>
public sealed record IndentDiscoveryRequest
{
    /// <summary>Sites to sweep. Empty falls back to <see cref="IndentPoOptions.Sites"/>.</summary>
    public IReadOnlyList<int>? Sites { get; init; }

    /// <summary>Empty means Regular, Capital and Service.</summary>
    public IReadOnlyList<IndentType>? IndentTypes { get; init; }

    /// <summary>
    /// Only these indents, by whole document number or bare running number — see
    /// <see cref="IndentNumberSelection"/>. Null or empty means no filter: every eligible indent
    /// of the selected types.
    /// </summary>
    /// <remarks>
    /// Applied after the ERP has answered, like <see cref="IndentId"/>, so a named indent that
    /// lies beyond <see cref="MaxScannedIndents"/> is not found. It is then reported as unmatched
    /// rather than silently converting nothing.
    /// </remarks>
    public IReadOnlyList<string>? IndentNumbers { get; init; }

    /// <summary>Stop once this many eligible indents have been found.</summary>
    public int MaxResults { get; init; } = 100;

    /// <summary>
    /// Narrows the ERP-side search, the same box the indent list screen has. Passing the indent
    /// number turns a scan of tens of thousands of rows into a single page, which is how one named
    /// indent is looked up.
    /// </summary>
    public string? SearchValue { get; init; }

    /// <summary>Keep only this indent (<c>XINDID</c>). Applied after the ERP has answered.</summary>
    public long? IndentId { get; init; }

    /// <summary>
    /// How far back through the authorised list to look, newest first. The ERP cannot filter its
    /// indent list by "still open", so this bounds the scan rather than the answer: a freshly
    /// authorised indent always has the highest id and is therefore found immediately, while a
    /// years-old one that was never ordered may sit thousands of rows down. Zero means no limit,
    /// for a deliberate one-off sweep of the backlog.
    /// </summary>
    public int MaxScannedIndents { get; init; } = 2000;
}

public sealed partial class IndentToPoService
{
    private const int DiscoveryPageSize = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<EligibleIndent>> FindEligibleAsync(
        IndentDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sites = ResolveSites(request);
        var wantedTypes = IndentTypeSelection.Normalise(request.IndentTypes);

        // Two lists, because there are two: material indents come from indentEntryList and service
        // indents from the service module's own, and neither knows about the other's rows.
        //
        // A type nobody selected is not filtered out of the answer, it is never asked about: an
        // unselected half of this pair costs no ERP call at all.
        var materialTypes = wantedTypes.Where(IndentPoProfile.MaterialIndentTypes.Contains).ToList();
        var wantsService = wantedTypes.Contains(IndentType.Service);

        // The budget is split before either half is asked, rather than being handed to the material
        // list first and whatever survives passed on. Selecting two families and getting only one
        // is the failure this prevents: the material half runs first, so a site with more than
        // MaxResults outstanding material indents would fill the whole cap every pass and the
        // service indents behind it would never be reached — not "next sweep", never. A caller who
        // asked for both is entitled to both.
        var materialBudget = materialTypes.Count == 0
            ? 0
            : wantsService
                // Rounded up, so a cap of 1 across both families still gives the material half a
                // look rather than none.
                ? (request.MaxResults + 1) / 2
                : request.MaxResults;

        var eligible = materialBudget <= 0
            ? []
            : await FindEligibleMaterialIndentsAsync(
                request with { MaxResults = materialBudget },
                sites,
                materialTypes,
                cancellationToken);

        if (!wantsService)
        {
            return eligible;
        }

        // Whatever the material half did not use is the service half's, on top of its own share —
        // so a quiet material list does not shrink the sweep.
        var serviceBudget = request.MaxResults - eligible.Count;

        if (serviceBudget <= 0)
        {
            return eligible;
        }

        var serviceIndents = await FindEligibleServiceIndentsAsync(
            request with { MaxResults = serviceBudget },
            sites,
            cancellationToken);

        return serviceIndents.Count == 0 ? eligible : [.. eligible, .. serviceIndents];
    }

    private async Task<IReadOnlyList<EligibleIndent>> FindEligibleMaterialIndentsAsync(
        IndentDiscoveryRequest request,
        IReadOnlyList<int> sites,
        IReadOnlyList<IndentType> wantedTypes,
        CancellationToken cancellationToken)
    {
        var locationIds = string.Join(',', sites);
        var wantedNumbers = IndentNumberSelection.Normalise(request.IndentNumbers);

        _logger.LogInformation(
            "Looking for authorised {IndentTypes} indents at site(s) {Sites}{Named}",
            string.Join(", ", wantedTypes),
            locationIds,
            wantedNumbers is null
                ? string.Empty
                : $", limited to {IndentNumberSelection.Describe(wantedNumbers)}");

        var eligible = new List<EligibleIndent>();
        var scanned = 0;
        var page = 1;

        while (eligible.Count < request.MaxResults)
        {
            var body = new JsonObject
            {
                // "A" is the ERP's code for authorised: XINDHAUBY and XINDHAUDT both set.
                ["Status"] = "A",
                ["LocationIds"] = locationIds,
                ["PageSize"] = DiscoveryPageSize,
                ["PageNumber"] = page,
                // Newest first, so an indent authorised moments ago is on the first page.
                ["SortField"] = "autoId",
                ["SortDirection"] = "desc",
                ["SearchValue"] = request.SearchValue ?? string.Empty,
                ["UserId"] = await _tokenProvider.GetUserIdAsync(cancellationToken),
            };

            var rows = await PostJsonArrayAsync(_endpoints.IndentEntryList, body, cancellationToken);
            var batch = rows.OfType<JsonObject>().ToList();

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var candidate = ReadEligible(row, wantedTypes);

                if (candidate is null)
                {
                    continue;
                }

                if (request.IndentId is { } wantedId && candidate.Indent.IndentId != wantedId)
                {
                    continue;
                }

                // Named indents only, when the caller named any. Dropped here rather than after
                // conversion, so an indent nobody asked for is never even a candidate.
                if (!IndentNumberSelection.Allows(wantedNumbers, candidate.Indent))
                {
                    continue;
                }

                eligible.Add(candidate);

                if (eligible.Count >= request.MaxResults)
                {
                    break;
                }
            }

            scanned += batch.Count;

            var totalRows = batch[0].Int("totalRows");

            if (scanned >= totalRows
                || batch.Count < DiscoveryPageSize
                || (request.MaxScannedIndents > 0 && scanned >= request.MaxScannedIndents))
            {
                break;
            }

            page++;
        }

        _logger.LogInformation(
            "Found {Eligible} authorised indent(s) still awaiting a purchase order, from {Scanned} scanned",
            eligible.Count,
            scanned);

        return eligible;
    }

    private IReadOnlyList<int> ResolveSites(IndentDiscoveryRequest request)
    {
        var sites = request.Sites is { Count: > 0 } requested ? requested : _options.Sites;

        if (sites.Count > 0)
        {
            return sites;
        }

        // The pending-indent query filters on exactly one site, so "no site" cannot mean "all
        // sites" further down; something has to name them. The configured PO site is the only
        // defensible fallback, and saying so beats silently sweeping one site out of five.
        _logger.LogWarning(
            "AutomationAgent:PurchaseOrder:Sites is empty, so only site {LocationId} is swept for "
            + "authorised indents. Authorised indents raised at any other site will not be found.",
            _options.LocationId);

        return [_options.LocationId];
    }

    /// <summary>Reads one indent-list row, or null when it is not something this flow can order.</summary>
    private static EligibleIndent? ReadEligible(JsonObject row, IReadOnlyList<IndentType> wantedTypes)
    {
        // Belt and braces: the request already asked for authorised rows only.
        if (!row.String("status").Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Close and Short Closed are indents the ERP has finished with; ordering against them would
        // be refused, and there is nothing outstanding to order anyway.
        var documentStatus = row.String("indentStatus");

        if (!documentStatus.Equals("Open", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var indentType = IndentPoProfile.Parse(row.String("indentTypeCode"));

        if (indentType is null || !wantedTypes.Contains(indentType.Value))
        {
            return null;
        }

        var reference = new IndentReference(
            IndentId: row.Int("autoId"),
            Year: row.String("indentYear"),
            Group: row.String("groupCode"),
            Number: row.String("indentNumber"),
            SiteId: row.Int("indentLocationId"),
            SiteCode: row.String("siteCode"),
            IndentType: indentType.Value);

        return new EligibleIndent(
            reference,
            documentStatus,
            ReadDate(row, "indentDate"),
            row.String("requestedByFullName"));
    }

    private static DateOnly? ReadDate(JsonObject? row, string property)
    {
        var raw = row.String(property);

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }
}
