using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Services;

namespace NewHorizon.Automation.UnitTests.Worker;

/// <summary>
/// The gate on the browser-facing PO Automation endpoints: an ERP user needs the Role Management
/// right; a machine caller (API key, scheduler) is waved through because it has no user identity.
/// </summary>
public sealed class ErpFormRightFilterTests
{
    [Fact]
    public async Task A_machine_caller_with_no_user_identity_is_let_through()
    {
        var result = await ErpFormRightFilter.DenyAsync(
            Anonymous(), Rights("does-not-matter"), ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        result.Should().BeNull();
    }

    [Fact]
    public async Task A_user_who_holds_the_right_is_let_through()
    {
        var result = await ErpFormRightFilter.DenyAsync(
            User(), Rights("EI"), ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        result.Should().BeNull();
    }

    [Fact]
    public async Task A_user_missing_the_right_is_refused_403()
    {
        var result = await ErpFormRightFilter.DenyAsync(
            User(), Rights("I"), ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_user_with_no_grant_at_all_is_refused()
    {
        var result = await ErpFormRightFilter.DenyAsync(
            User(), Rights(string.Empty), ErpFormRightFilter.PoAutomationHistoryForm, ErpFormRightFilter.Inquiry);

        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task The_required_letter_is_matched_case_insensitively()
    {
        var result = await ErpFormRightFilter.DenyAsync(
            User(), Rights("ei"), ErpFormRightFilter.PoAutomationConfigForm, ErpFormRightFilter.Edit);

        result.Should().BeNull();
    }

    private static HttpContext Anonymous() => new DefaultHttpContext();

    private static HttpContext User()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", "u1")], authenticationType: "test")),
        };

        return context;
    }

    private static IErpUserRightsService Rights(string letters) => new FixedRights(letters);

    private sealed class FixedRights(string letters) : IErpUserRightsService
    {
        public Task<string> RightsForAsync(HttpContext httpContext, string formId, CancellationToken cancellationToken) =>
            Task.FromResult(letters);
    }
}
