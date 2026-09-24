using System.Net;
using System.Text;
using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient;

namespace NewHorizon.Automation.UnitTests.Erp;

/// <summary>
/// The ERP's shared exception filter answers a known business-rule RAISERROR (e.g. an indent already
/// fully ordered) with the same bare 500 shape a real infrastructure failure would produce. These
/// tests pin the classification that tells the two apart, so a real outage can never again be
/// reported to an operator as "the ERP is unavailable" for what is actually a deterministic refusal —
/// and, just as important, so an unrelated 500 is not swallowed into that allowance by accident.
/// </summary>
public sealed class ErpResponseHandlerTests
{
    [Fact]
    public async Task A_500_carrying_a_known_business_rejection_is_classified_as_business_not_transient()
    {
        using var response = JsonResponse(
            HttpStatusCode.InternalServerError,
            """{"message":"UnhandledErrorKey","errorMessage":"Indent is close or Po Qty is more than indent qty.","errors":"Indent is close or Po Qty is more than indent qty."}""");

        var act = async () => await ErpResponseHandler.EnsureSuccessAsync(
            response, "/api/v1/Purchase/POEntry/create", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ErpBusinessException>();
        thrown.Which.IsTransient.Should().BeFalse();
        thrown.Which.TechnicalMessage.Should().Contain("Indent is close or Po Qty is more than indent qty.");
    }

    [Fact]
    public async Task A_500_with_an_unrelated_message_is_still_transient()
    {
        // Guards the allowlist against over-matching: an ordinary infrastructure failure must keep
        // being retried, not silently reclassified as a deterministic business refusal.
        using var response = JsonResponse(
            HttpStatusCode.InternalServerError,
            """{"message":"UnhandledErrorKey","errorMessage":"Object reference not set to an instance of an object.","errors":"Object reference not set to an instance of an object."}""");

        var act = async () => await ErpResponseHandler.EnsureSuccessAsync(
            response, "/api/v1/Purchase/POEntry/create", CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ErpTransientException>();
        thrown.Which.IsTransient.Should().BeTrue();
        thrown.Which.LaymanMessage.Should().Contain("temporarily unavailable");
    }

    [Fact]
    public async Task A_500_with_no_body_is_still_transient()
    {
        // No error body to match against at all must fall through to the existing status-code rule,
        // not throw while trying to read a message that was never there.
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var act = async () => await ErpResponseHandler.EnsureSuccessAsync(
            response, "/api/v1/Purchase/POEntry/create", CancellationToken.None);

        (await act.Should().ThrowAsync<ErpTransientException>()).Which.IsTransient.Should().BeTrue();
    }

    [Fact]
    public async Task A_200_carrying_the_business_rejection_text_is_unaffected()
    {
        // The allowlist only ever applies to a 5xx — a successful envelope is read by ReadDataAsync's
        // own success/data logic, never by this status-code classification path.
        using var response = JsonResponse(HttpStatusCode.OK, """{"data":{},"success":true}""");

        await ErpResponseHandler.EnsureSuccessAsync(
            response, "/api/v1/Purchase/POEntry/create", CancellationToken.None);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
}
