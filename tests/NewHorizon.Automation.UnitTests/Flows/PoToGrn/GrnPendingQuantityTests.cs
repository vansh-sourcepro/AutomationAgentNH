using FluentAssertions;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;

namespace NewHorizon.Automation.UnitTests.Flows.PoToGrn;

/// <summary>
/// The ERP's <c>getposearchdetails</c> always sends <c>pendinggrniuom = 0</c> (a case-mismatched
/// column key in its repository); the agent rebuilds it from the row's own IUOM columns.
/// </summary>
public sealed class GrnPendingQuantityTests
{
    [Fact]
    public void A_zero_from_the_ERP_is_rebuilt_from_the_PO_IUOM_columns()
    {
        // PO 26-27/TE/NF1/000190 as the ERP answered it: 10 pending in PUOM, 0 in IUOM.
        var line = GrnFixtures.Line(1, "A", tweak: row => row["pendinggrniuom"] = 0m);

        GrnPendingQuantity.PendingIuom(line).Should().Be(10m);
    }

    [Fact]
    public void A_non_zero_value_from_the_ERP_wins()
    {
        var line = GrnFixtures.Line(1, "A", tweak: row => row["pendinggrniuom"] = 7m);

        GrnPendingQuantity.PendingIuom(line).Should().Be(7m);
    }

    [Fact]
    public void Received_and_short_closed_IUOM_are_taken_off()
    {
        var line = GrnFixtures.Line(1, "A", pending: 6m, tweak: row =>
        {
            row["pendinggrniuom"] = 0m;
            row["poiuomqty"] = 10m;
            row["poiuomrcvd"] = 3m;
            row["poiuomsc"] = 1m;
        });

        GrnPendingQuantity.PendingIuom(line).Should().Be(6m);
    }

    [Fact]
    public void Rejections_are_added_back_only_when_the_PUOM_figure_did_so()
    {
        static System.Text.Json.Nodes.JsonObject Line(decimal pendingPuom) =>
            GrnFixtures.Line(1, "A", pending: pendingPuom, tweak: row =>
            {
                row["pendinggrniuom"] = 0m;
                row["popuomqty"] = 10m;
                row["popuomrcvd"] = 10m;
                row["popuomrej"] = 2m;
                row["poiuomqty"] = 20m;
                row["poiuomrcvd"] = 20m;
                row["poiuomrej"] = 4m;
            });

        GrnPendingQuantity.PendingIuom(Line(2m)).Should().Be(4m);
        GrnPendingQuantity.PendingIuom(Line(0m)).Should().Be(0m);
    }

    [Fact]
    public void The_classifier_and_totals_use_the_rebuilt_quantity()
    {
        var line = GrnFixtures.Line(1, "A", tweak: row => row["pendinggrniuom"] = 0m);

        var selection = GrnLineClassifier.Select([line], GrnReceiptMode.Complete);
        var totals = GrnLineTotals.From(line, 1, []);

        selection.Receivable.Should().ContainSingle();
        totals.QuantityPuom.Should().Be(10m);
        totals.QuantityIuom.Should().Be(10m);
    }

    [Fact]
    public void A_fully_received_line_is_still_skipped()
    {
        var line = GrnFixtures.Line(1, "DONE", pending: 0m, tweak: row => row["pendinggrniuom"] = 0m);

        GrnLineClassifier.Select([line], GrnReceiptMode.Complete).Receivable.Should().BeEmpty();
    }
}
