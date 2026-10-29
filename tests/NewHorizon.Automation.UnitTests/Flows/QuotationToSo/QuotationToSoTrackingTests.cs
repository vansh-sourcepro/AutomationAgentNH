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
        ]);
    }

    [Fact]
    public void QuotationToSoTasks_Maps_Stages_To_Tasks()
    {
        QuotationToSoTasks.For(QuotationToSoStages.Discovery).Should().Be(QuotationToSoTasks.ListOpenQuotations);
        QuotationToSoTasks.For(QuotationToSoStages.QuotationDetail).Should().Be(QuotationToSoTasks.GetQuotationDetail);
        QuotationToSoTasks.For(QuotationToSoStages.Validation).Should().Be(QuotationToSoTasks.VerifyExistingSalesOrder);
        QuotationToSoTasks.For(QuotationToSoStages.CreateSalesOrder).Should().Be(QuotationToSoTasks.CreateSalesOrderEntry);
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
}

