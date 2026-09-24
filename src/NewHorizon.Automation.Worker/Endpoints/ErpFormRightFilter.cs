using Microsoft.AspNetCore.Http.HttpResults;
using NewHorizon.Automation.Worker.Services;

namespace NewHorizon.Automation.Worker.Endpoints;

/// <summary>
/// Requires the ERP user behind a browser-facing request to hold a given right on a given ERP form
/// (a Role Management checkbox). Machine callers — the inbound API key, the scheduler's own
/// self-call — carry no ERP user identity and are left to <see cref="ErpUserOrApiKeyFilter"/> /
/// <see cref="ApiKeyFilter"/>; form rights are a user concept.
/// </summary>
/// <remarks>
/// The rights come from the ERP (<see cref="IErpUserRightsService"/>), asked with the caller's own
/// token, so unchecking the box in Role Management takes effect here without the agent keeping its
/// own copy of who may do what. Unreachable ERP ⇒ denied (fail closed).
/// </remarks>
public static class ErpFormRightFilter
{
    /// <summary>PO Automation Configuration — Role Management form id.</summary>
    public const string PoAutomationConfigForm = "011171";

    /// <summary>PO Automation Run History — Role Management form id.</summary>
    public const string PoAutomationHistoryForm = "011172";

    /// <summary>Open / view the screen.</summary>
    public const char Inquiry = 'I';

    /// <summary>Change anything (edit a row, flip the master toggle, Run now).</summary>
    public const char Edit = 'E';

    public static TBuilder RequireErpFormRight<TBuilder>(this TBuilder builder, string formId, char right)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(formId);

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var rights = http.RequestServices.GetRequiredService<IErpUserRightsService>();

            var denied = await DenyAsync(http, rights, formId, right);

            return denied ?? await next(context);
        });
    }

    /// <summary>
    /// The decision, lifted out of the filter delegate so it can be tested without a route: returns
    /// a 403 result when an authenticated ERP user lacks <paramref name="right"/> on
    /// <paramref name="formId"/>, or <see langword="null"/> to let the request through — both for a
    /// user who holds the right and for a machine caller with no user identity at all.
    /// </summary>
    public static async ValueTask<ProblemHttpResult?> DenyAsync(
        HttpContext http,
        IErpUserRightsService rights,
        string formId,
        char right)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var wanted = char.ToUpperInvariant(right);
        var letters = await rights.RightsForAsync(http, formId, http.RequestAborted);

        // ERP right letters are stored uppercase (MROOPRID / ModuleInfo.xml Rights); compare
        // case-insensitively anyway so a stray lowercase grant can never silently lock a user out.
        if (letters.Contains(wanted.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (ProblemHttpResult)Results.Problem(
            title: "You do not have permission for this screen.",
            detail: $"This action needs the '{wanted}' right on form {formId}, which your role does not grant.",
            statusCode: StatusCodes.Status403Forbidden);
    }
}
