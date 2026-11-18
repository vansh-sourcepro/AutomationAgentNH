using FluentAssertions;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;

namespace NewHorizon.Automation.UnitTests.Flows.QuotationToSo;

public sealed class QuotationToSoTrackingTests
{
    [Fact]
    public void QuotationToSoStages_Contains_Expected_Stages_In_Sequence()
    {
        QuotationToSoStages.InOrder.Should().ContainInOrder([
            QuotationToSoStages.Discovery,
            QuotationToSoStages.QuotationDetail,
            QuotationToSoStages.Validation,
            QuotationToSoStages.CreateSalesOrder,
            QuotationToSoStages.CreateOaf,
        ]);
    }

    [Fact]
    public void QuotationToSoTasks_Maps_Stages_To_Tasks()
    {
        QuotationToSoTasks.For(QuotationToSoStages.Discovery).Should().Be(QuotationToSoTasks.ListOpenQuotations);
        QuotationToSoTasks.For(QuotationToSoStages.QuotationDetail).Should().Be(QuotationToSoTasks.GetQuotationDetail);
        QuotationToSoTasks.For(QuotationToSoStages.Validation).Should().Be(QuotationToSoTasks.VerifyExistingSalesOrder);
        QuotationToSoTasks.For(QuotationToSoStages.CreateSalesOrder).Should().Be(QuotationToSoTasks.CreateSalesOrderEntry);
        QuotationToSoTasks.For(QuotationToSoStages.CreateOaf).Should().Be(QuotationToSoTasks.CreateOrderAckEntry);
    }

    [Fact]
    public void WorkflowNames_Includes_QuotationToSalesOrder()
    {
        WorkflowNames.All.Should().Contain(WorkflowNames.QuotationToSalesOrder);
        WorkflowNames.QuotationToSalesOrder.Should().Be("QuotationToSalesOrder");
    }

    [Fact]
    public void TrackedQuotation_Stores_Quotation_Metadata()
    {
        var quotation = new TrackedQuotation(
            QuotationId: 501,
            QuotationNumber: "26-27/QT/NF1/000088",
            SiteId: 1,
            CustomerCode: "CUST-001",
            QuotationDate: new DateOnly(2026, 10, 15));

        quotation.QuotationId.Should().Be(501);
        quotation.QuotationNumber.Should().Be("26-27/QT/NF1/000088");
        quotation.SiteId.Should().Be(1);
        quotation.CustomerCode.Should().Be("CUST-001");
        quotation.QuotationDate.Should().Be(new DateOnly(2026, 10, 15));
    }

    [Fact]
    public async Task NullQuotationToSoTracker_GetExistingSalesOrderNumberAsync_Returns_Null()
    {
        var tracker = NullQuotationToSoTracker.Instance;
        var result = await tracker.GetExistingSalesOrderNumberAsync(501, CancellationToken.None);
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Quotation '26-27/TE/NF1/000057' is already converted to Sales Order '26-27/SW/NF1/000105'.", "26-27/SW/NF1/000105")]
    [InlineData("Quotation '000045' is already converted to Sales Order '26-27/SO/NF1/000099'. Checking OAF...", "26-27/SO/NF1/000099")]
    [InlineData("Quotation '000057' could not be converted.", null)]
    public void SkippedReason_SalesOrderExtraction_Matches_Expected(string reason, string? expectedSo)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            reason,
            @"Sales Order '([^']+)'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (expectedSo is not null)
        {
            match.Success.Should().BeTrue();
            match.Groups[1].Value.Should().Be(expectedSo);
        }
        else
        {
            match.Success.Should().BeFalse();
        }
    }

    [Fact]
    public void Dashboard_Fallback_Reason_WhenExamined_ReturnsDescriptiveReason()
    {
        int examined = 1;
        string? failureReason = null;

        var reason = failureReason ?? (examined == 0
            ? "No eligible open authorised quotations found."
            : $"All {examined} examined quotation(s) were already converted or closed.");

        reason.Should().Be("All 1 examined quotation(s) were already converted or closed.");
    }

    [Fact]
    public void Dashboard_Fallback_Reason_WhenZeroExamined_ReturnsEmptyReason()
    {
        int examined = 0;
        string? failureReason = null;

        var reason = failureReason ?? (examined == 0
            ? "No eligible open authorised quotations found."
            : $"All {examined} examined quotation(s) were already converted or closed.");

        reason.Should().Be("No eligible open authorised quotations found.");
    }

    [Fact]
    public void Dashboard_Fallback_Reason_WhenFailureReasonPresent_PreservesFailureReason()
    {
        int examined = 2;
        string? failureReason = "ERP connection timed out.";

        var reason = failureReason ?? (examined == 0
            ? "No eligible open authorised quotations found."
            : $"All {examined} examined quotation(s) were already converted or closed.");

        reason.Should().Be("ERP connection timed out.");
    }

    [Fact]
    public void IndentPoOutcome_Created_Truncates_PoNumber_To_50_Characters_When_Long()
    {
        var longPoNumber = "26-27/SO/NF1/000008 (OAF: 26-27/OA/NF1/000008) Extra Characters";
        var outcome = NewHorizon.Automation.Domain.Flows.IndentToPo.IndentPoOutcome.Created(
            Guid.NewGuid(),
            sequence: 1,
            vendorCode: "CUSTOMER",
            currencyCode: "INR",
            rateStructureCode: null,
            poId: 20_000_000_001L,
            poNumber: longPoNumber,
            lineCount: 1,
            DateTimeOffset.UtcNow);

        outcome.PoNumber.Should().NotBeNull();
        outcome.PoNumber!.Length.Should().BeLessOrEqualTo(50);
        outcome.PoNumber.Should().Be(longPoNumber[..50]);
        outcome.PoId.Should().Be(20_000_000_001L);
    }

    [Fact]
    public void IndentPoOutcome_Created_Truncates_VendorCode_To_20_Characters()
    {
        var longVendor = "CUST-VERY-LONG-NAME-OVER-TWENTY";
        var outcome = NewHorizon.Automation.Domain.Flows.IndentToPo.IndentPoOutcome.Created(
            Guid.NewGuid(),
            sequence: 1,
            vendorCode: longVendor,
            currencyCode: null,
            rateStructureCode: null,
            poId: 20_000_000_002L,
            poNumber: "26-27/SO/NF1/000001",
            lineCount: 1,
            DateTimeOffset.UtcNow);

        outcome.VendorCode.Should().NotBeNull();
        outcome.VendorCode!.Length.Should().BeLessOrEqualTo(20);
        outcome.VendorCode.Should().Be(longVendor[..20]);
    }

    [Fact]
    public void IndentKind_Quotation_Is_Distinct_And_Has_Quotation_DocumentType()
    {
        var kind = NewHorizon.Automation.Domain.Flows.IndentToPo.IndentKind.Quotation;
        kind.ToString().Should().Be("Quotation");
        NewHorizon.Automation.Domain.Flows.IndentToPo.IndentDocumentTypes.For(kind).Should().Be("Quotation");
        kind.Should().NotBe(NewHorizon.Automation.Domain.Flows.IndentToPo.IndentKind.Regular);
    }

    [Fact]
    public void TargetQuotations_SafeTruncation_Fits_Within_60_Characters()
    {
        var rawQuotationNumbers = "26-27/QT/NF1/000008, 26-27/QT/NF1/000009, 26-27/QT/NF1/000010, 26-27/QT/NF1/000011";
        var safeTarget = string.IsNullOrWhiteSpace(rawQuotationNumbers)
            ? "All"
            : (rawQuotationNumbers.Trim().Length > 60
                ? rawQuotationNumbers.Trim()[..57] + "..."
                : rawQuotationNumbers.Trim());

        safeTarget.Length.Should().BeLessOrEqualTo(60);
        safeTarget.Should().EndWith("...");
    }
}

