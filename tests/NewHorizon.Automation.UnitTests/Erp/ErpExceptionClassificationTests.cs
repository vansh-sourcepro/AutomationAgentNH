using FluentAssertions;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.UnitTests.Erp;

/// <summary>
/// The retry decision hangs entirely on this classification, so it is asserted directly rather
/// than only through the paths that happen to produce each exception.
/// </summary>
public sealed class ErpExceptionClassificationTests
{
    [Fact]
    public void Transient_failures_are_retried()
    {
        var exception = new ErpTransientException("layman", "technical");

        exception.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Business_failures_are_never_retried()
    {
        // Retrying a rejection produces the same rejection and delays the human who must fix it.
        var exception = new ErpBusinessException("Vendor missing for item X", "400 from /purchase-requisition");

        exception.IsTransient.Should().BeFalse();
    }

    [Fact]
    public void Authentication_failures_are_retried()
    {
        // A restarting ERP must not push otherwise-healthy jobs into human review.
        var exception = new ErpAuthenticationException("layman", "technical");

        exception.IsTransient.Should().BeTrue();
    }

    [Fact]
    public void Both_messages_are_always_carried()
    {
        var exception = new ErpBusinessException(
            "Vendor missing for item X",
            "POST /api/automation/purchase-requisition returned 400",
            "/api/automation/purchase-requisition");

        // The ERP UI shows the layman text by default and expands the technical one for admins;
        // an error must never arrive with only one of the two.
        exception.LaymanMessage.Should().Be("Vendor missing for item X");
        exception.TechnicalMessage.Should().Contain("400");
        exception.Message.Should().Be(exception.TechnicalMessage);
        exception.ApiEndpoint.Should().Be("/api/automation/purchase-requisition");
    }
}
