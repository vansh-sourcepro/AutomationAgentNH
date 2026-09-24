using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.ErpClient.Flows.IndentToPo;

/// <summary>
/// The bits of a service indent's own record the conversion needs, read straight from the ERP
/// rather than from a list row that may be minutes old.
/// </summary>
/// <param name="SiteId">
/// <c>XINDSHSITE</c>. The list does not carry it — it filters on the raising site
/// (<c>XINDFRSITEID</c>) and returns only the indent site's code — and the pending-lines query
/// filters on this one, so it has to be read here or nothing is ever found.
/// </param>
/// <param name="DocumentStatus">
/// <c>XINDSHSTATUS</c>, and the ERP is not consistent about how it spells it: the detail call
/// hands back the display word ("Open", "Closed", "Cancelled", "Short Closed", "Deleted") under a
/// column still named <c>XINDSHSTATUS</c>, while the underlying column holds one letter. Both are
/// accepted — see <see cref="IsOpen"/> — because reading "Closed" as "not O" is how this feature
/// first found nothing at all to convert. The ERP closes an indent itself once every line is fully
/// ordered, which is what makes a second conversion a no-op.
/// </param>
internal sealed record ServiceIndentRecord(
    long IndentId,
    string Year,
    string Group,
    string Number,
    int SiteId,
    string SiteCode,
    string DocumentStatus,
    bool IsAuthorised,
    DateOnly? IndentDate,
    string RequestedBy,
    // The item codes on the indent, in the order the ERP lists them. This is what a vendor is
    // resolved for; which of them still has something outstanding is the pending-lines query's
    // answer, not this one's.
    IReadOnlyList<string> ItemCodes)
{
    public IndentReference ToReference() =>
        new(IndentId, Year, Group, Number, SiteId, SiteCode, IndentType.Service);

    /// <summary>
    /// Still open, and therefore still worth ordering against. Both spellings the ERP uses are
    /// accepted; anything else — closed, short closed, cancelled, deleted — is a document it has
    /// finished with.
    /// </summary>
    public bool IsOpen =>
        DocumentStatus.Trim() is var status
        && (status.Equals("O", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Open", StringComparison.OrdinalIgnoreCase));

    /// <summary>The status in words, whichever spelling the ERP used to say it.</summary>
    public string DocumentStatusDisplay => DocumentStatus.Trim().ToUpperInvariant() switch
    {
        "O" => "Open",
        "C" => "Closed",
        "N" => "Cancelled",
        "S" => "Short Closed",
        "D" => "Deleted",
        _ => DocumentStatus.Trim(),
    };
}

public sealed partial class IndentToPoService
{
    /// <summary>
    /// One scope's worth of service-indent records, keyed by <c>XINDAUTOID</c>.
    /// </summary>
    /// <remarks>
    /// Discovery has to read each candidate's own record to learn its site and whether it is still
    /// open — the list carries neither — and the conversion that follows needs exactly the same
    /// record. Caching it means one read per indent per sweep rather than two.
    /// </remarks>
    private readonly Dictionary<long, ServiceIndentRecord> _serviceIndentsById = [];

    /// <summary>
    /// The authorised service indents that still have something left to order.
    /// </summary>
    /// <remarks>
    /// Two calls per candidate, not one. <c>indentEntryList</c> with <c>Status=A</c> answers "which
    /// service indents are authorised and not deleted" and nothing more: no site id, and no notion
    /// of whether the indent is still open — an indent fully converted last week is still on it.
    /// <c>getSerIndentDetail</c> supplies both. The material list happens to carry them, which is
    /// the only reason its discovery is a single call.
    /// </remarks>
    private async Task<IReadOnlyList<EligibleIndent>> FindEligibleServiceIndentsAsync(
        IndentDiscoveryRequest request,
        IReadOnlyList<int> sites,
        CancellationToken cancellationToken)
    {
        var endpoint = _endpoints.ServiceIndentList(sites);
        var wantedNumbers = IndentNumberSelection.Normalise(request.IndentNumbers);

        _logger.LogInformation(
            "Looking for authorised service indents at site(s) {Sites}{Named}",
            string.Join(',', sites),
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
                ["PageSize"] = DiscoveryPageSize,
                ["PageNumber"] = page,
                // Left blank on purpose: the ERP's own default ordering for this list is
                // XINDAUTOID descending, so a service indent authorised moments ago is on the
                // first page. Naming a sort field would replace that with an alphabetical one.
                ["SortField"] = string.Empty,
                ["SortDirection"] = string.Empty,
                ["SearchValue"] = request.SearchValue ?? string.Empty,
                ["UserId"] = await _tokenProvider.GetUserIdAsync(cancellationToken),
            };

            var rows = await PostJsonArrayAsync(endpoint, body, cancellationToken);
            var batch = rows.OfType<JsonObject>().ToList();

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var indentId = (long)row.Decimal("id");

                if (indentId <= 0)
                {
                    continue;
                }

                if (request.IndentId is { } wantedId && indentId != wantedId)
                {
                    continue;
                }

                // Belt and braces: the request already asked for authorised rows only.
                if (!row.String("status").Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var record = await GetServiceIndentAsync(
                    indentId,
                    row.String("year"),
                    row.String("groupCode"),
                    row.String("siteCode"),
                    row.String("indentNumber"),
                    row,
                    cancellationToken);

                if (record is null || !record.IsAuthorised)
                {
                    continue;
                }

                // Closed and deleted indents are ones the ERP has finished with; there is nothing
                // outstanding on them and ordering against them would be refused.
                if (!record.IsOpen)
                {
                    continue;
                }

                // Named indents only, when the caller named any. The reference is built here
                // rather than earlier because the service list carries no number of its own —
                // getSerIndentDetail is what supplies it.
                var reference = record.ToReference();

                if (!IndentNumberSelection.Allows(wantedNumbers, reference))
                {
                    continue;
                }

                eligible.Add(new EligibleIndent(
                    reference,
                    record.DocumentStatusDisplay,
                    record.IndentDate,
                    record.RequestedBy));

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
            "Found {Eligible} authorised service indent(s) still awaiting a purchase order, from {Scanned} scanned",
            eligible.Count,
            scanned);

        return eligible;
    }

    /// <summary>
    /// One service indent's own record. Cached for the life of the scope — see
    /// <see cref="_serviceIndentsById"/>.
    /// </summary>
    /// <param name="listRow">
    /// The list row this candidate came from, if there is one. Only its date and requester are
    /// taken from it: the ERP's detail call does not return the raiser's display name, and the
    /// list's own copy is the same value the screen shows.
    /// </param>
    private async Task<ServiceIndentRecord?> GetServiceIndentAsync(
        long indentId,
        string year,
        string group,
        string siteCode,
        string number,
        JsonObject? listRow,
        CancellationToken cancellationToken)
    {
        if (_serviceIndentsById.TryGetValue(indentId, out var cached))
        {
            return cached;
        }

        var body = new JsonObject
        {
            ["ServiceIndentId"] = indentId,
            ["Year"] = year,
            ["GroupCode"] = group,
            ["SiteCode"] = siteCode,
            ["ServiceIndentNumber"] = number,
            // "S" is what the indent screen sends when it is only reading, same as the material one.
            ["Mode"] = "S",
        };

        var detail = await PostJsonObjectAsync(_endpoints.ServiceIndentDetail, body, cancellationToken);

        if (detail["headerDetail"] is not JsonObject header)
        {
            _logger.LogWarning(
                "Service indent {IndentId} has no header in the ERP's answer and cannot be converted",
                indentId);

            return null;
        }

        var record = new ServiceIndentRecord(
            IndentId: indentId,
            Year: Fallback(header.String("indentYear"), year),
            Group: Fallback(header.String("indentGroupCode"), group),
            Number: Fallback(header.String("indentNumber"), number),
            SiteId: header.Int("indentSiteId"),
            SiteCode: Fallback(header.String("siteCode"), siteCode),
            DocumentStatus: header.String("indentStatus"),
            // Either half of the ERP's own authorisation pair is enough: the list already filtered
            // on XINDSHAUTHBY being set, and the detail exposes the user and the date separately.
            IsAuthorised: !string.IsNullOrWhiteSpace(header.String("authUserId"))
                || !string.IsNullOrWhiteSpace(header.String("authDate")),
            IndentDate: ReadDate(header, "indentDate") ?? ReadDate(listRow, "indentDate"),
            RequestedBy: Fallback(
                listRow.String("requestedByFullName"),
                header.String("indentDoneFullName")),
            // The ERP nests the lines beside the header, not inside it. Distinct, because an indent
            // may list the same service on two lines and the vendor for it is resolved once.
            ItemCodes: detail["itemDetail"].Objects()
                .Select(item => item.String("itemCode").Trim())
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList());

        _serviceIndentsById[indentId] = record;

        return record;

        static string Fallback(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>
    /// Looks one service indent up by id alone, for the caller who has an id and nothing else.
    /// </summary>
    /// <remarks>
    /// It goes through the list rather than straight to the detail call because the detail call
    /// alone cannot say whether this is an indent the automation may touch: only the list applies
    /// the authorisation filter and the site restriction.
    /// </remarks>
    private async Task<EligibleIndent?> FindServiceIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        CancellationToken cancellationToken)
    {
        var matches = await FindEligibleServiceIndentsAsync(
            new IndentDiscoveryRequest
            {
                IndentId = indentId,
                SearchValue = indentNumber,
                MaxResults = 1,
            },
            ResolveSites(new IndentDiscoveryRequest { Sites = sites }),
            cancellationToken);

        return matches.FirstOrDefault();
    }

    /// <summary>
    /// The site the order must be raised from, and a refusal when the ERP did not name one — an
    /// indent with no site cannot be matched by the pending-lines query, which filters on it.
    /// </summary>
    private static int RequireSite(ServiceIndentRecord record)
    {
        if (record.SiteId > 0)
        {
            return record.SiteId;
        }

        throw new ErpBusinessException(
            $"Service indent {record.Year}/{record.Group}/{record.SiteCode}/{record.Number} has no site "
            + "in the ERP, so no purchase order can be raised for it.",
            $"getSerIndentDetail returned indentSiteId 0 for XINDAUTOID {record.IndentId}.");
    }
}
