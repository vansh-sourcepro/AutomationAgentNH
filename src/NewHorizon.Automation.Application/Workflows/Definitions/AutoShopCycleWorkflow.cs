using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// The agent's actual unit of work: one repeating cycle, not one run per sales order.
/// </summary>
/// <remarks>
/// <para>
/// Automation deliberately begins <em>after</em> OAF creation, because SO → OAF requires manual
/// authorisation inside the ERP. SJO → CBOM is likewise the ERP's own. So the agent owns exactly
/// two transitions — OAF → SJO, and the per-site sequencing that feeds AutoShop.
/// </para>
/// <para>
/// The site stages are not in the static plan: the sites live in the ERP, so
/// <c>DiscoverSites</c> fetches them and expands the plan to one step per site. Each site is then
/// its own checkpoint, so a failure at the seventh site resumes at the seventh site.
/// </para>
/// </remarks>
public static class AutoShopCycleWorkflow
{
    public const string StageOafToSjo = "OafToSjo";
    public const string StageDiscovery = "Discovery";
    public const string StageSjoSequence = "SjoSequence";
    public const string StageAutoShop = "AutoShop";

    public const string OperationSjoSequence = "SequenceSite";
    public const string OperationAutoShop = "AutoShopSite";

    public static WorkflowDefinition Create() =>
        WorkflowBuilder.For(WorkflowNames.AutoShopCycle)
            .Stage(StageOafToSjo, stage => stage
                .Execute("CreateSjoFromPendingOaf", CreateSjoFromPendingOafAsync))

            .Stage(StageDiscovery, stage => stage
                .Execute("DiscoverSites", DiscoverSitesAsync))

            // Declared per-target, so these contribute no step to the initial plan. DiscoverSites
            // appends one real step per site; these bodies are what those steps resolve to.
            .Stage(StageSjoSequence, stage => stage
                .PerTarget(OperationSjoSequence, SequenceSiteAsync))

            .Stage(StageAutoShop, stage => stage
                .PerTarget(OperationAutoShop, AutoShopSiteAsync))
            .Build();

    /// <summary>
    /// The cycle's entry point. Nothing awaiting an SJO is the normal quiet case, not a failure.
    /// </summary>
    private static async Task<OperationResult> CreateSjoFromPendingOafAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        var pending = await erpClient.GetOafAwaitingSjoAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return OperationResult.Skip("No OAF is awaiting an SJO.");
        }

        var created = new List<string>(pending.Count);

        foreach (var oaf in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = context.ToErpRequest() with { DocumentId = oaf.OafNumber };

            // Query before create, per OAF: a re-run after a partial failure must adopt the SJOs
            // this cycle already made rather than raising a second one for the same OAF.
            var existing = await erpClient.FindExistingDocumentAsync(request, "sjo", cancellationToken);

            if (existing.Exists)
            {
                created.Add(existing.ErpDocumentRef ?? oaf.OafNumber);
                continue;
            }

            var result = await erpClient.CreateSjoFromOafAsync(request, oaf.OafNumber, cancellationToken);
            created.Add(result.ErpDocumentRef ?? oaf.OafNumber);
        }

        return OperationResult.Success(
            erpDocumentRef: created.Count == 1 ? created[0] : null,
            responsePayload: $$"""{"oafProcessed":{{pending.Count}},"sjoCreated":{{created.Count}}}""");
    }

    /// <summary>
    /// Fetches the Site IDs and turns them into real steps — one per site for each of the two
    /// per-site stages. This is what gives the loop a length at run time while keeping every site
    /// individually checkpointed and resumable.
    /// </summary>
    private static async Task<OperationResult> DiscoverSitesAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        var sites = await erpClient.GetSitesAsync(cancellationToken);

        if (sites.Count == 0)
        {
            return OperationResult.Skip("The ERP returned no sites.");
        }

        // Sequencing runs for every site first, then AutoShop for every site — the order the
        // client described. Both stages are laid down now so the whole cycle is visible up front.
        var steps = new List<PlannedOperation>(sites.Count * 2);

        steps.AddRange(sites.Select(site =>
            new PlannedOperation(StageSjoSequence, OperationSjoSequence, OperationKind.Execute, site.SiteId)));

        steps.AddRange(sites.Select(site =>
            new PlannedOperation(StageAutoShop, OperationAutoShop, OperationKind.Execute, site.SiteId)));

        return OperationResult.SuccessWithDiscoveredSteps(
            steps,
            responsePayload: $$"""{"siteCount":{{sites.Count}}}""");
    }

    private static async Task<OperationResult> SequenceSiteAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        var siteId = RequireTarget(context);

        var rows = await erpClient.GetSjoSequenceAsync(siteId, cancellationToken);

        if (rows.Count == 0)
        {
            return OperationResult.Skip($"Site {siteId} has no SJO to sequence.");
        }

        var ordered = PrepareForSubmission(rows);

        var submission = await erpClient.SubmitSjoSequenceAsync(siteId, ordered, cancellationToken);

        return OperationResult.Success(
            submission.Reference,
            requestPayload: $$"""{"siteId":"{{siteId}}","rows":{{ordered.Count}}}""",
            responsePayload: $$"""{"rowsSubmitted":{{submission.RowsSubmitted}}}""");
    }

    private static async Task<OperationResult> AutoShopSiteAsync(
        OperationContext context,
        IErpClient erpClient,
        CancellationToken cancellationToken)
    {
        var siteId = RequireTarget(context);

        var rows = await erpClient.GetAutoShopAsync(siteId, cancellationToken);

        if (rows.Count == 0)
        {
            return OperationResult.Skip($"Site {siteId} has nothing for AutoShop.");
        }

        var ordered = PrepareForSubmission(rows);

        var submission = await erpClient.SubmitAutoShopAsync(siteId, ordered, cancellationToken);

        return OperationResult.Success(
            submission.Reference,
            requestPayload: $$"""{"siteId":"{{siteId}}","rows":{{ordered.Count}}}""",
            responsePayload: $$"""{"rowsSubmitted":{{submission.RowsSubmitted}}}""");
    }

    /// <summary>
    /// The only two things the agent does to a row before handing it back: order it, and set the
    /// flag the ERP acts on. Every other property travels through exactly as the ERP sent it.
    /// </summary>
    internal static IReadOnlyList<SjoSequenceRow> PrepareForSubmission(IEnumerable<SjoSequenceRow> rows)
    {
        var ordered = SortByDeliveryDate(rows);

        foreach (var row in ordered)
        {
            row.MarkForSubmission();
        }

        return ordered;
    }

    /// <summary>
    /// Delivery date ascending, which is the sequence itself rather than a display preference.
    /// Rows with no delivery date sort last, so an incomplete record cannot silently jump the
    /// queue ahead of dated work.
    /// </summary>
    internal static IReadOnlyList<SjoSequenceRow> SortByDeliveryDate(IEnumerable<SjoSequenceRow> rows) =>
        rows
            .OrderBy(row => row.DeliveryDate is null)
            .ThenBy(row => row.DeliveryDate)
            .ThenBy(row => row.SjoNumber, StringComparer.Ordinal)
            .ToList();

    private static string RequireTarget(OperationContext context) =>
        context.Target
        ?? throw new InvalidOperationException(
            $"Operation '{context.OperationName}' is per-site but its step carries no Site ID.");
}
