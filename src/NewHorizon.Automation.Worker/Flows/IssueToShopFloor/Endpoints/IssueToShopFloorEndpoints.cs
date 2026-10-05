using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Endpoints;

/// <summary>
/// Issue to Shop Floor from one SJO, Work Order or Sales OAF: one document in, one issue out — or the
/// reason there is none.
/// </summary>
/// <remarks>
/// HTTP only: validate, call the service, shape the answer. The answer is the service's result as-is,
/// so a caller always sees every check that ran.
/// <list type="bullet">
/// <item>200 — created, or on a dry run every check passed and the planned lines are listed.</item>
/// <item>400 — refused: <c>reason</c>, the failed check, and any <c>shortages</c>. Nothing was created.</item>
/// <item>503 — the ERP could not be reached. Nothing was created; asking again is safe.</item>
/// </list>
/// </remarks>
public static class IssueToShopFloorEndpoints
{
    public static IEndpointRouteBuilder MapIssueToShopFloorApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Machine or ERP browser callers: shared API key or valid ERP bearer token.
        endpoints.MapPost("/api/automation/issue-to-shop-floor", ConvertAsync)
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .WithName("CreateIssueToShopFloor");

        // The path the flow shipped under when it was SJO-only. Same handler, kept so a caller built
        // against it keeps working.
        endpoints.MapPost("/api/automation/sjo-to-issue", ConvertAsync)
            .AddEndpointFilter<ErpUserOrApiKeyFilter>()
            .WithName("CreateIssueToShopFloorLegacy");

        return endpoints;
    }

    private static async Task<IResult> ConvertAsync(
        IssueToShopFloorRequest? request,
        IIssueToShopFloorService service,
        IIssueToShopFloorTracker tracker,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Number))
        {
            return Results.Problem(
                title: "documentNumber is required, e.g. \"26-27/SJ/NF1/000123\" with issueType \"sjo\".",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!request.TryGetSource(out var source))
        {
            return Results.Problem(
                title: $"issueType '{request.IssueType}' is not understood. Use one of: {IssueToShopFloorRequest.AcceptedIssueTypes}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        bool shouldTrackRun = !request.DryRun && tracker.IsEnabled;
        if (shouldTrackRun)
        {
            await tracker.StartRunAsync(
                new StartIssueRunRequest(
                    TriggerSource.Api,
                    source.ToString(),
                    TriggeredBy: "Api",
                    TriggerReference: request.Number,
                    RequestedSites: null),
                cancellationToken);
        }

        try
        {
            var result = await service.ConvertAsync(source, request.Number, request.DryRun, cancellationToken);

            if (shouldTrackRun)
            {
                await tracker.CompleteRunAsync(
                    documentsExamined: 1,
                    issuesCreated: result.Created ? 1 : 0,
                    cancellationToken);
            }

            return result.Ready ? Results.Ok(result) : Results.BadRequest(result);
        }
        catch (ErpException erp)
        {
            if (shouldTrackRun)
            {
                await tracker.FailRunAsync(erp.Message, documentsExamined: 1, cancellationToken);
            }

            return Results.Problem(
                title: erp.LaymanMessage,
                detail: erp.TechnicalMessage,
                statusCode: erp.IsTransient
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            if (shouldTrackRun)
            {
                await tracker.FailRunAsync(ex.Message, documentsExamined: 1, cancellationToken);
            }

            throw;
        }
    }
}
