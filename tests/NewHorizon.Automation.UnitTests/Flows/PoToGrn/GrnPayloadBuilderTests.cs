using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;

namespace NewHorizon.Automation.UnitTests.Flows.PoToGrn;

public sealed class GrnPayloadBuilderTests
{
    private static readonly JsonArray PerUnitTax = new(
        new JsonObject { ["msprtcd"] = "CGST", ["rtamt"] = 9m },
        new JsonObject { ["msprtcd"] = "SGST", ["rtamt"] = 9m });

    [Fact]
    public void Quantity_is_the_pending_quantity_and_value_follows_the_screen()
    {
        var line = Priced(GrnFixtures.Line(42, "A", pending: 10m));

        line.QuantityPuom.Should().Be(10m);
        line.QuantityIuom.Should().Be(10m);
        line.PurchaseValue.Should().Be(1000m);
        line.UnitRate.Should().Be(100m);
        line.TaxRows.OfType<JsonObject>().Select(row => row["rateAmount"]!.GetValue<decimal>()).Should().Equal(90m, 90m);
    }

    [Fact]
    public void Challan_quantity_is_only_what_the_ERP_sent_never_the_received_quantity()
    {
        static decimal Challan(JsonObject payload) =>
            payload["grnitemDetails"]!.AsArray().OfType<JsonObject>().Single()["chalanqty"]!.GetValue<decimal>();

        var absent = Build(Priced(GrnFixtures.Line(42, "A", pending: 10m)));
        var present = Build(Priced(GrnFixtures.Line(42, "A", pending: 10m, tweak: row => row["chalanqty"] = 4m)));

        Challan(absent).Should().Be(0m);
        Challan(present).Should().Be(4m);
        absent["vendchallannoControl"]!.GetValue<string>().Should().BeEmpty();
    }

    [Fact]
    public void Item_and_PO_level_discounts_come_off_the_value()
    {
        var percent = Priced(GrnFixtures.Line(42, "A", tweak: row =>
        {
            row["disctype"] = "P";
            row["discvalue"] = 10m;
        }));

        var poLevel = Priced(GrnFixtures.Line(42, "A", tweak: row =>
        {
            row["pohdiscpa"] = "P";
            row["pohdiscval"] = 5m;
        }));

        percent.PurchaseValue.Should().Be(900m);
        percent.DiscountedRate.Should().Be(90m);
        poLevel.PurchaseValue.Should().Be(950m);
    }

    [Fact]
    public void Non_postable_exclusive_tax_is_part_of_the_unit_cost()
    {
        var line = GrnLineTotals.From(GrnFixtures.Line(42, "A"), 1, Template(42, "A", postable: false));
        line.ApplyTaxes(Template(42, "A", postable: false), PerUnitTax);

        line.UnitRate.Should().Be(118m);
    }

    [Fact]
    public void The_GRN_is_left_for_a_person_to_authorise_and_carries_the_invoice_details()
    {
        var payload = Build(Priced(GrnFixtures.Line(42, "A")));

        payload["AuthorizationRequired"]!.GetValue<string>().Should().Be("Y");
        payload["invoicenoControl"]!.GetValue<string>().Should().Be("INV-AUTO");
        payload["invoicedateControl"]!.GetValue<string>().Should().Be("2026-09-23T00:00:00");
        payload["dateControl"]!.GetValue<string>().Should().Be("2026-09-24T00:00:00");
        payload["DocType"]!.GetValue<string>().Should().Be("GR");
        payload["DocSubType"]!.GetValue<string>().Should().Be("OR");
        payload["numberControl"]!.GetValue<string>().Should().BeEmpty();
        payload["warehouseControl"]!.GetValue<int>().Should().Be(3);
        payload["poflag"]!.GetValue<string>().Should().Be("S");
    }

    [Fact]
    public void Every_line_travels_with_its_tax_rows_keyed_by_item_and_PO()
    {
        var payload = Build(Priced(GrnFixtures.Line(42, "A")), Priced(GrnFixtures.Line(42, "B"), rowId: 2));

        var items = payload["grnitemDetails"]!.AsArray().OfType<JsonObject>().ToList();
        var taxes = payload["rateStructureDetail"]!.AsArray().OfType<JsonObject>().ToList();

        items.Select(item => item["rowid"]!.GetValue<int>()).Should().Equal(1, 2);
        items[0]["puomqt"]!.GetValue<decimal>().Should().Be(10m);
        items[0]["xgrndwhid"]!.GetValue<string>().Should().Be("3");
        taxes.Should().HaveCount(4);
        taxes.Should().OnlyContain(tax => tax["xgrndpoid"]!.GetValue<long>() == 42);
        taxes.Select(tax => tax["itemCode"]!.GetValue<string>()).Should().Equal("A", "A", "B", "B");
    }

    [Fact]
    public void A_line_without_tax_rows_is_never_sent()
    {
        // Without tax rows the ERP does not raise the PO's received quantity, and the next run
        // would receive the same line again.
        var untaxed = GrnLineTotals.From(GrnFixtures.Line(42, "A"), 1, []);

        var build = () => Build(untaxed);

        build.Should().Throw<InvalidOperationException>().WithMessage("*tax rows*");
    }

    [Fact]
    public void Inclusive_tax_that_swallows_the_value_is_refused()
    {
        var template = Template(42, "A");
        foreach (var row in template)
        {
            row["ie"] = "I";
        }

        var line = GrnLineTotals.From(GrnFixtures.Line(42, "A"), 1, template);
        var apply = () => line.ApplyTaxes(template, new JsonArray(new JsonObject { ["msprtcd"] = "CGST", ["rtamt"] = 100m }));

        apply.Should().Throw<ErpBusinessException>();
    }

    [Theory]
    [InlineData("GRNCreated#26-27/GR/NF1/000101#5001", true, 5001, "26-27/GR/NF1/000101")]
    [InlineData("GRNCreated#26-27/GR/NF1/000101", false, 0, "")]
    [InlineData("GRNCreated", false, 0, "")]
    [InlineData(null, false, 0, "")]
    public void The_created_message_is_parsed_for_the_GRN_number_and_id(string? message, bool ok, long id, string number)
    {
        PoToGrnService.TryParseCreated(message, out var grnId, out var grnNumber).Should().Be(ok);
        grnId.Should().Be(id);
        grnNumber.Should().Be(number);
    }

    private static GrnLineTotals Priced(JsonObject source, int rowId = 1)
    {
        var template = Template(source["xgrndpoid"]!.GetValue<long>(), source["itmcode"]!.GetValue<string>());
        var line = GrnLineTotals.From(source, rowId, template);
        line.ApplyTaxes(template, PerUnitTax);

        return line;
    }

    private static List<JsonObject> Template(long poId, string item, bool postable = true)
    {
        var rows = GrnFixtures.TaxRows(poId, item).OfType<JsonObject>().ToList();
        foreach (var row in rows)
        {
            row["postnonpost"] = postable;
        }

        return rows;
    }

    private static JsonObject Build(params GrnLineTotals[] lines) =>
        GrnPayloadBuilder.Build(new GrnContext
        {
            Po = new PoGrnCandidate(42, "26-27", "PR", "NF1", "000012", 1, PoGrnType.Regular, GrnFixtures.Today, "V0001"),
            VendorCode = "V0001",
            VendorName = "Acme Steel",
            Currency = "INR",
            WarehouseId = 3,
            ItemLevelWarehouse = false,
            DocumentControl = new GrnDocumentControl("26-27", "GR", "NF1", "Y", "Y"),
            GrnDate = GrnFixtures.Today,
            PeriodStart = new DateOnly(2026, 4, 1),
            PeriodEnd = new DateOnly(2027, 3, 31),
            InvoiceNumber = "INV-AUTO",
            CompanyId = 1,
            UserId = 7,
            Remark = "Created by Automation Agent",
            Lines = lines,
        });
}
