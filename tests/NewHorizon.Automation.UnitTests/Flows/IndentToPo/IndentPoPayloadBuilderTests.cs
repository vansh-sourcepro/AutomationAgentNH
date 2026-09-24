using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// Checks the create payload against the two captures of the ERP UI doing the same job.
/// </summary>
/// <remarks>
/// This is the feature's real safety net. The builder is pure, and both traces record what the ERP
/// accepted, so every expected value here is an observed fact rather than a restatement of the
/// implementation.
/// </remarks>
public sealed class IndentPoPayloadBuilderTests
{
    // ---- Structure ---------------------------------------------------------

    [Fact]
    public void The_payload_carries_every_section_the_ERP_expects()
    {
        var payload = IndentPoPayloadBuilder.Build(RecordedTrace.Regular());

        payload.Should().ContainKeys(
            "drPOITMRemark", "Poheader", "DetailDesc", "drNonStand", "drHeader", "drFooter",
            "TaxDetails", "DelDetails", "ItemDetails", "PaySchedule", "dtHstDetails",
            "clsNotiParams", "SjoDetails", "posorefdtl");
    }

    [Fact]
    public void The_sections_this_flow_never_fills_are_sent_as_empty_arrays()
    {
        // Present-but-empty, not absent. The screen posts them that way and the ERP's model binds
        // them; omitting one would be a different payload shape than the one known to work.
        var payload = IndentPoPayloadBuilder.Build(RecordedTrace.Regular());

        foreach (var section in new[]
        {
            "drPOITMRemark", "drNonStand", "drFooter", "PaySchedule", "dtHstDetails",
            "SjoDetails", "posorefdtl",
        })
        {
            payload[section].As<JsonArray>().Should().BeEmpty(because: $"{section} is unused here");
        }
    }

    // ---- Regular: the recorded header --------------------------------------

    [Theory]
    [InlineData("POHTYPE", "R")]
    [InlineData("POHDOCTYP", "PR")]
    [InlineData("POHDOCSUBTYP", "RP")]
    [InlineData("POHGRPCD", "TE")]
    [InlineData("POHSUBTYP", "N")]
    [InlineData("pobasis", "I")]
    [InlineData("POHORDYEAR", "26-27")]
    [InlineData("POHORDDT", "08/06/2026")]
    [InlineData("POHVNDCODE", "A032")]
    [InlineData("POHBYRCD", "001")]
    [InlineData("POHCURCD", "INR")]
    [InlineData("POHRTSTRCD", "P0002")]
    [InlineData("SiteCode", "NF1")]
    [InlineData("SITEREQ", "Y")]
    [InlineData("AUTONUMREQ", "Y")]
    [InlineData("AUTHREQ", "Y")]
    [InlineData("XATTCHTYPE", "UDP")]
    [InlineData("RCMTYPE", "IGST")]
    [InlineData("POHDISCPA", "None")]
    [InlineData("POHNETVAL", "9558.00")]
    [InlineData("POHPOVALBFDISC", "8100.00")]
    [InlineData("POHPOVALAFDISC", "8100.00")]
    [InlineData("POHPODOMCURTAX", "1458.00")]
    [InlineData("POHPOFORCURTAX", "0.00")]
    public void The_regular_header_matches_the_capture(string property, string expected)
    {
        Header(RecordedTrace.Regular())[property]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void The_regular_header_reuses_the_vendors_GSTIN_for_octroi_and_warranty()
    {
        // Not a sensible mapping, but it is what the ERP screen posts, and matching it is the point.
        var header = Header(RecordedTrace.Regular());

        header["POHOCTCD"]!.GetValue<string>().Should().Be("06AAJPK0422D1ZC");
        header["POHWRNTCD"]!.GetValue<string>().Should().Be("06AAJPK0422D1ZC");
    }

    [Fact]
    public void The_regular_header_leaves_the_PO_number_to_the_ERP()
    {
        Header(RecordedTrace.Regular())["POHORDNO"]!.GetValue<string>().Should().BeEmpty();
    }

    // ---- Capital: only the type, sub-type and group differ ------------------

    [Theory]
    [InlineData("POHTYPE", "C")]
    [InlineData("POHDOCTYP", "PR")]
    [InlineData("POHDOCSUBTYP", "CP")]
    [InlineData("POHGRPCD", "CP")]
    [InlineData("POHVNDCODE", "F005")]
    [InlineData("POHORDDT", "08/07/2026")]
    [InlineData("POHNETVAL", "743400.00")]
    [InlineData("POHPOVALBFDISC", "630000.00")]
    [InlineData("POHPODOMCURTAX", "113400.00")]
    public void The_capital_header_matches_the_capture(string property, string expected)
    {
        Header(RecordedTrace.Capital())[property]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void Regular_and_capital_headers_differ_only_in_type_subtype_and_group()
    {
        // The claim the whole two-flavour design rests on. If a future change makes Capital differ
        // somewhere else, this is the test that should fail first.
        var regular = Header(RecordedTrace.Regular());
        var capital = Header(RecordedTrace.Capital());

        var differing = regular
            .Where(entry => capital[entry.Key]?.ToJsonString() != entry.Value?.ToJsonString())
            .Select(entry => entry.Key)
            .ToList();

        differing.Should().BeEquivalentTo(
        [
            // The flavour itself.
            "POHTYPE", "POHDOCSUBTYP", "POHGRPCD",
            // Everything else is this particular capture: a different vendor, date and value.
            "POHORDDT", "POHVNDCODE", "POHOCTCD", "POHWRNTCD",
            "POHNETVAL", "POHPOVALAFDISC", "POHPOVALBFDISC", "POHPODOMCURTAX",
        ]);
    }

    [Fact]
    public void The_document_sub_type_reaches_every_section_that_carries_it()
    {
        var payload = IndentPoPayloadBuilder.Build(RecordedTrace.Capital());

        payload["DetailDesc"]![0]!["MTMDOCSUBTYP"]!.GetValue<string>().Should().Be("CP");
        payload["drHeader"]!.AsArray().Should().AllSatisfy(row =>
            row!["MTMDOCSUBTYP"]!.GetValue<string>().Should().Be("CP"));
        payload["TaxDetails"]!.AsArray().Should().AllSatisfy(row =>
            row!["XDTDSUBTYPE"]!.GetValue<string>().Should().Be("CP"));
    }

    [Fact]
    public void The_note_block_key_is_the_year_group_and_site_run_together()
    {
        // "26-27" + "TE" + "NF1". The ERP keys its header and footer text on exactly this string.
        var payload = IndentPoPayloadBuilder.Build(RecordedTrace.Regular());

        payload["drHeader"]!.AsArray().Should().HaveCount(2);
        payload["drHeader"]![0]!["MTMDOCTYPE"]!.GetValue<string>().Should().Be("HDRTX");
        payload["drHeader"]![1]!["MTMDOCTYPE"]!.GetValue<string>().Should().Be("FTRTX");
        payload["drHeader"]!.AsArray().Should().AllSatisfy(row =>
            row!["MTMID"]!.GetValue<string>().Should().Be("26-27TENF1"));

        IndentPoPayloadBuilder.Build(RecordedTrace.Capital())["drHeader"]![0]!["MTMID"]!
            .GetValue<string>().Should().Be("26-27CPNF1");
    }

    // ---- Item line ---------------------------------------------------------

    [Fact]
    public void The_regular_item_line_matches_the_capture()
    {
        var item = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["ItemDetails"]![0]!.AsObject();

        item["PIDITMLINE"]!.GetValue<int>().Should().Be(1);
        item["qtypuom"]!.GetValue<decimal>().Should().Be(20m);
        item["qtyiuom"]!.GetValue<decimal>().Should().Be(20m);
        item["PIDQTYPUOM"]!.GetValue<decimal>().Should().Be(20m);
        item["PIDQTYIUOM"]!.GetValue<decimal>().Should().Be(20m);
        item["PIDPCS"]!.GetValue<int>().Should().Be(0);
        // 450 less 10%.
        item["PIDDISCBSCRT"]!.GetValue<decimal>().Should().Be(405m);
        item["PIDNETRATE"]!.GetValue<decimal>().Should().Be(477.9m);
        item["landedPrice"]!.GetValue<decimal>().Should().Be(477.9m);
        item["itemtotalamount"]!.GetValue<decimal>().Should().Be(9000m);
        item["itemAmtDiscount"]!.GetValue<string>().Should().Be("8100.00");
        item["rateStruAmt"]!.GetValue<decimal>().Should().Be(1458m);
        item["itemAmtDiscRateStru"]!.GetValue<decimal>().Should().Be(9558m);
        item["creusrid"]!.GetValue<int>().Should().Be(2);
        item["rateStructureCode"]!.GetValue<string>().Should().Be("P0002");
    }

    [Fact]
    public void The_capital_item_line_matches_the_capture()
    {
        var item = IndentPoPayloadBuilder.Build(RecordedTrace.Capital())["ItemDetails"]![0]!.AsObject();

        item["qtypuom"]!.GetValue<decimal>().Should().Be(10m);
        // No discount, so the basic rate carries through unchanged.
        item["PIDDISCBSCRT"]!.GetValue<decimal>().Should().Be(63000m);
        item["PIDNETRATE"]!.GetValue<decimal>().Should().Be(74340m);
        item["itemtotalamount"]!.GetValue<decimal>().Should().Be(630000m);
        item["itemAmtDiscount"]!.GetValue<string>().Should().Be("630000.00");
        item["rateStruAmt"]!.GetValue<decimal>().Should().Be(113400m);
        item["itemAmtDiscRateStru"]!.GetValue<decimal>().Should().Be(743400m);
        // The warehouse marks it as capital goods, and it comes from the ERP untouched.
        item["whCode"]!.GetValue<string>().Should().Be("CG");
    }

    [Fact]
    public void The_delivery_lead_time_is_taken_from_the_item_vendor_row()
    {
        // The pending-items call reports 0 for this; only the item/vendor row knows it is 2, and
        // both captures show the corrected value being posted back.
        var item = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["ItemDetails"]![0]!.AsObject();

        item["mimdelldtm"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Item_master_properties_the_agent_does_not_understand_survive_untouched()
    {
        // The reason the row travels as the ERP's own JSON rather than a typed model.
        var item = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["ItemDetails"]![0]!.AsObject();

        item["mimadddesc"]!.GetValue<string>().Should().Be("item for testing");
        item["hsncode"]!.GetValue<string>().Should().Be("84282011");
    }

    // ---- Delivery line -----------------------------------------------------

    [Fact]
    public void The_regular_delivery_line_matches_the_capture()
    {
        var delivery = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["DelDetails"]![0]!.AsObject();

        delivery["PILDELLINE"]!.GetValue<int>().Should().Be(1);
        delivery["PILITMREFLINE"]!.GetValue<int>().Should().Be(1);
        delivery["XINDITMCD"]!.GetValue<string>().Should().Be("ITEM_IA_HNI14");
        delivery["indentNo"]!.GetValue<string>().Should().Be("26-27/PI/NF1/000002");
        delivery["PIDINDID"]!.GetValue<int>().Should().Be(899);
        delivery["srno"]!.GetValue<int>().Should().Be(23);
        delivery["PILQTYPUOM"]!.GetValue<decimal>().Should().Be(20m);
        delivery["PILQTYIUOM"]!.GetValue<decimal>().Should().Be(20m);
        // No SJO or OAF is ever linked on this flow.
        delivery["PILSJOID"]!.GetValue<int>().Should().Be(0);
        delivery["OAFID"]!.GetValue<int>().Should().Be(0);
        delivery["PILQTYPO"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public void Delivery_dates_are_reformatted_to_the_month_first_form_the_payload_uses()
    {
        // The ERP answers in ISO and expects MM/dd/yyyy back on this call.
        var regular = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["DelDetails"]![0]!.AsObject();

        regular["pildeldt"]!.GetValue<string>().Should().Be("09/05/2026");
        regular["indentdate"]!.GetValue<string>().Should().Be("08/06/2026");
        regular["indentdeliverydate"]!.GetValue<string>().Should().Be("09/05/2026");

        IndentPoPayloadBuilder.Build(RecordedTrace.Capital())["DelDetails"]![0]!["pildeldt"]!
            .GetValue<string>().Should().Be("08/18/2026");
    }

    [Fact]
    public void The_ordered_quantity_string_follows_the_items_own_precision()
    {
        // A 4-decimal UOM records "20.0000"; one with no decimals records "10". Both captures agree.
        IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["DelDetails"]![0]!["currentpoqtyiuom"]!
            .GetValue<string>().Should().Be("20.0000");

        IndentPoPayloadBuilder.Build(RecordedTrace.Capital())["DelDetails"]![0]!["currentpoqtyiuom"]!
            .GetValue<string>().Should().Be("10");
    }

    // ---- Tax ---------------------------------------------------------------

    [Fact]
    public void Every_rate_structure_component_becomes_a_tax_row_for_the_item()
    {
        var taxes = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["TaxDetails"]!.AsArray();

        taxes.Should().HaveCount(5);
        taxes.Should().AllSatisfy(row =>
        {
            row!["itemCode"]!.GetValue<string>().Should().Be("ITEM_IA_HNI14");
            row["XDTDTMCD"]!.GetValue<string>().Should().Be("ITEM_IA_HNI14");
            row["XDTDCATTYPE"]!.GetValue<string>().Should().Be("PR");
            row["XDTDSUBTYPE"]!.GetValue<string>().Should().Be("RP");
            row["XDTDAMDSRNO"]!.GetValue<int>().Should().Be(0);
            // Null in the template; the builder stamps the structure it belongs to.
            row["mspstrcd"]!.GetValue<string>().Should().Be("P0002");
        });
    }

    [Fact]
    public void Tax_amounts_are_the_ERPs_per_unit_figures_multiplied_by_quantity()
    {
        // 36.45 per unit x 20 = 729 on each of the two GST components; the expense components are
        // zero. That is 1458 of tax on 8100, exactly as captured.
        var amounts = TaxAmountsByRateCode(RecordedTrace.Regular());

        amounts["P0001"].Should().Be(0m);
        amounts["P0002"].Should().Be(0m);
        amounts["P0003"].Should().Be(0m);
        amounts["P0005"].Should().Be(729m);
        amounts["P0004"].Should().Be(729m);
    }

    [Fact]
    public void Capital_tax_amounts_match_the_capture()
    {
        var amounts = TaxAmountsByRateCode(RecordedTrace.Capital());

        amounts["P0005"].Should().Be(56700m);
        amounts["P0004"].Should().Be(56700m);
        amounts.Values.Sum().Should().Be(113400m);
    }

    [Fact]
    public void The_accounting_metadata_the_rate_structure_supplies_is_carried_through()
    {
        // These come only from getratestructuredetail. The calculation endpoint returns them blank,
        // which is why it cannot be used to build these rows.
        var cgst = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["TaxDetails"]!
            .AsArray().Single(row => row!["rateCode"]!.GetValue<string>() == "P0005")!;

        cgst["acGroup"]!.GetValue<string>().Should().Be("A21002");
        cgst["acCode"]!.GetValue<string>().Should().Be("0003");
        cgst["mprtaxtyp"]!.GetValue<string>().Should().Be("M");
        cgst["taxValue"]!.GetValue<decimal>().Should().Be(9m);
    }

    // ---- Notification parameters -------------------------------------------

    [Fact]
    public void The_notification_parameters_name_the_document_and_the_vendor()
    {
        var noti = IndentPoPayloadBuilder.Build(RecordedTrace.Regular())["clsNotiParams"]![0]!.AsObject();

        noti["FORMID"]!.GetValue<string>().Should().Be("01139");
        noti["docYear"]!.GetValue<string>().Should().Be("26-27");
        noti["docGroup"]!.GetValue<string>().Should().Be("TE");
        noti["docSite"]!.GetValue<string>().Should().Be("NF1");
        noti["VENDORCODE"]!.GetValue<string>().Should().Be("A032");
        noti["VENDORNAME"]!.GetValue<string>().Should().Be("A S BEARING COMPANY");
        noti["USRID"]!.GetValue<int>().Should().Be(2);
        noti["companyId"]!.GetValue<int>().Should().Be(1);
    }

    // ---- Guards ------------------------------------------------------------

    [Fact]
    public void A_purchase_order_with_no_lines_is_refused_rather_than_sent()
    {
        var empty = RecordedTrace.Regular() with { Lines = [] };

        var build = () => IndentPoPayloadBuilder.Build(empty);

        build.Should().Throw<InvalidOperationException>().WithMessage("*at least one item line*");
    }

    [Fact]
    public void An_item_whose_indent_lines_are_fully_ordered_is_refused()
    {
        var context = RecordedTrace.Regular();
        var line = context.Lines[0];
        var exhausted = (JsonObject)line.DeliveryRows[0].DeepClone();
        exhausted["xindiodqty"] = 0;

        var build = () => IndentPoPayloadBuilder.Build(
            context with { Lines = [line with { DeliveryRows = [exhausted] }] });

        build.Should().Throw<InvalidOperationException>().WithMessage("*no indent line*");
    }

    private static JsonObject Header(IndentPoContext context) =>
        IndentPoPayloadBuilder.Build(context)["Poheader"]![0]!.AsObject();

    private static Dictionary<string, decimal> TaxAmountsByRateCode(IndentPoContext context) =>
        IndentPoPayloadBuilder.Build(context)["TaxDetails"]!.AsArray()
            .ToDictionary(
                row => row!["rateCode"]!.GetValue<string>(),
                row => row!["rateAmount"]!.GetValue<decimal>());
}
