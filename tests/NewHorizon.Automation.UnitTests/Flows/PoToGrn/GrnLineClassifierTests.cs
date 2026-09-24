using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;

namespace NewHorizon.Automation.UnitTests.Flows.PoToGrn;

public sealed class GrnLineClassifierTests
{
    [Fact]
    public void Clean_lines_are_all_receivable()
    {
        var selection = GrnLineClassifier.Select([GrnFixtures.Line(1, "A"), GrnFixtures.Line(1, "B")], GrnReceiptMode.Complete);

        selection.SkipReason.Should().BeNull();
        selection.Receivable.Should().HaveCount(2);
        selection.SkippedLines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("mimbchreqd", "batch number")]
    [InlineData("miminwdreq", "inward number")]
    [InlineData("mimheatreq", "heat number")]
    [InlineData("mimitmsrreqd", "serial numbers")]
    [InlineData("mimmfgreq", "manufacturing batch number")]
    [InlineData("isShelfLife", "shelf-life expiry date")]
    public void Each_parameter_flag_marks_the_line_and_names_the_item(string flag, string expected)
    {
        var line = GrnFixtures.Line(1, "ITEM-9", tweak: row => row[flag] = true);

        GrnLineClassifier.ParameterReason(line).Should().Contain("ITEM-9").And.Contain(expected);
    }

    [Fact]
    public void A_flag_sent_as_Y_or_1_counts_too()
    {
        GrnLineClassifier.ParameterReason(GrnFixtures.Line(1, "A", tweak: row => row["mimheatreq"] = "Y")).Should().NotBeNull();
        GrnLineClassifier.ParameterReason(GrnFixtures.Line(1, "A", tweak: row => row["mimbchreqd"] = 1)).Should().NotBeNull();
    }

    [Fact]
    public void An_LBT_line_with_a_size_is_a_parameter_line()
    {
        var line = GrnFixtures.Line(1, "PLATE", tweak: row => row["xgrndsize"] = "10x20x3");

        GrnLineClassifier.ParameterReason(line).Should().Contain("LBT");
    }

    [Fact]
    public void Complete_mode_skips_the_whole_PO_when_any_line_needs_parameters()
    {
        var selection = GrnLineClassifier.Select(
            [GrnFixtures.Line(1, "CLEAN"), GrnFixtures.Line(1, "BATCHED", tweak: row => row["mimbchreqd"] = true)],
            GrnReceiptMode.Complete);

        selection.Receivable.Should().BeEmpty();
        selection.SkipReason.Should().Contain("Complete").And.Contain("BATCHED");
    }

    [Fact]
    public void Partial_mode_receives_the_clean_lines_and_notes_the_rest()
    {
        var selection = GrnLineClassifier.Select(
            [GrnFixtures.Line(1, "CLEAN"), GrnFixtures.Line(1, "BATCHED", tweak: row => row["mimbchreqd"] = true)],
            GrnReceiptMode.Partial);

        selection.SkipReason.Should().BeNull();
        selection.Receivable.Select(line => line["itmcode"]!.GetValue<string>()).Should().Equal("CLEAN");
        selection.SkippedLines.Should().ContainSingle().Which.Should().Contain("BATCHED");
    }

    [Fact]
    public void Partial_mode_skips_a_PO_whose_every_line_needs_parameters()
    {
        var selection = GrnLineClassifier.Select(
            [GrnFixtures.Line(1, "HEAT", tweak: row => row["mimheatreq"] = true)],
            GrnReceiptMode.Partial);

        selection.Receivable.Should().BeEmpty();
        selection.SkipReason.Should().Contain("Every pending line");
    }

    [Fact]
    public void A_blanket_PO_is_skipped()
    {
        var selection = GrnLineClassifier.Select(
            [GrnFixtures.Line(1, "A", tweak: row => row["blkpotype"] = "B")],
            GrnReceiptMode.Partial);

        selection.SkipReason.Should().Contain("blanket");
    }

    [Fact]
    public void Lines_with_nothing_pending_are_ignored_and_a_PO_with_none_is_skipped()
    {
        var mixed = GrnLineClassifier.Select([GrnFixtures.Line(1, "DONE", pending: 0m), GrnFixtures.Line(1, "OPEN")], GrnReceiptMode.Complete);
        var none = GrnLineClassifier.Select([GrnFixtures.Line(1, "DONE", pending: 0m)], GrnReceiptMode.Complete);

        mixed.Receivable.Select(line => line["itmcode"]!.GetValue<string>()).Should().Equal("OPEN");

        // Says what the ERP sent, so a zero from the ERP is distinguishable from a misread field.
        none.SkipReason.Should().Contain("1 line(s)").And.Contain("none with a pending quantity")
            .And.Contain("item DONE").And.Contain("pendinggrnpuom=0");
    }

    [Fact]
    public void No_lines_at_all_reads_as_nothing_left()
    {
        GrnLineClassifier.Select([], GrnReceiptMode.Complete).SkipReason.Should().Contain("Nothing is left");
    }

    [Fact]
    public void Another_blocker_is_treated_like_a_parameter_item()
    {
        static string? NoTax(JsonObject line) =>
            line["itmcode"]!.GetValue<string>() == "UNTAXED" ? "Item UNTAXED has no tax rows." : null;

        var complete = GrnLineClassifier.Select([GrnFixtures.Line(1, "A"), GrnFixtures.Line(1, "UNTAXED")], GrnReceiptMode.Complete, NoTax);
        var partial = GrnLineClassifier.Select([GrnFixtures.Line(1, "A"), GrnFixtures.Line(1, "UNTAXED")], GrnReceiptMode.Partial, NoTax);

        complete.SkipReason.Should().Contain("UNTAXED");
        partial.Receivable.Should().ContainSingle();
    }
}
