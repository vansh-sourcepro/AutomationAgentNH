using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;

namespace NewHorizon.Automation.UnitTests.Flows.PoToGrn;

public sealed class PoToGrnServiceTests
{
    [Fact]
    public async Task A_clean_authorised_PO_gets_one_GRN_at_its_pending_quantity()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "A", pending: 10m)]));
        var history = new RecordingGrnHistory();

        var sweep = await GrnServiceUnderTest.Build(erp, history: history)
            .ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        sweep.GrnsCreated.Should().Be(1);
        var result = sweep.Results.Should().ContainSingle().Subject;
        result.Status.Should().Be(PoGrnReceiptStatus.Created);
        result.GrnNumber.Should().Be("26-27/GR/NF1/000101");
        result.GrnId.Should().Be(5001);

        var body = erp.CreateBodies.Should().ContainSingle().Subject;
        body["grnitemDetails"]![0]!["puomqt"]!.GetValue<decimal>().Should().Be(10m);
        body["rateStructureDetail"]!.AsArray().Should().HaveCount(2);

        history.Outcomes.Should().ContainSingle().Which.GrnNumber.Should().Be("26-27/GR/NF1/000101");
    }

    [Fact]
    public async Task The_lines_are_asked_for_by_site_warehouse_vendor_PO_and_today()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "A")]));

        await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        erp.Requests.Select(request => request.Path)
            .Should().Contain(path => path.EndsWith("getposearchdetails/1/3/V0001/101/DIS/INR/R/2026-09-24", StringComparison.Ordinal));
        erp.Requests.Select(request => request.Path)
            .Should().Contain(path => path.EndsWith("getDefaultDocumentDetail/26-27/GR/OR/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_mode_skips_a_PO_with_a_batch_item_and_creates_nothing()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101,
                [GrnFixtures.Line(101, "CLEAN"), GrnFixtures.Line(101, "BATCHED", tweak: row => row["mimbchreqd"] = true)]));
        var history = new RecordingGrnHistory();

        var sweep = await GrnServiceUnderTest.Build(erp, history: history)
            .ReceiveEligibleAsync(GrnServiceUnderTest.Request(GrnReceiptMode.Complete), CancellationToken.None);

        erp.CallCount("grn/create").Should().Be(0);
        var result = sweep.Results.Should().ContainSingle().Subject;
        result.Status.Should().Be(PoGrnReceiptStatus.Skipped);
        result.Notes.Should().ContainSingle().Which.Should().Contain("BATCHED").And.Contain("batch number");
        history.Outcomes.Should().ContainSingle().Which.Status.Should().Be(PoGrnReceiptStatus.Skipped);
    }

    [Fact]
    public async Task Partial_mode_receives_only_the_clean_lines()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101,
                [GrnFixtures.Line(101, "CLEAN"), GrnFixtures.Line(101, "SERIAL", tweak: row => row["mimitmsrreqd"] = true)]));

        var sweep = await GrnServiceUnderTest.Build(erp)
            .ReceiveEligibleAsync(GrnServiceUnderTest.Request(GrnReceiptMode.Partial), CancellationToken.None);

        var body = erp.CreateBodies.Should().ContainSingle().Subject;
        body["grnitemDetails"]!.AsArray().Select(item => item!["itmcode"]!.GetValue<string>()).Should().Equal("CLEAN");
        body["rateStructureDetail"]!.AsArray().Should().OnlyContain(tax => tax!["itemCode"]!.GetValue<string>() == "CLEAN");

        var result = sweep.Results.Should().ContainSingle().Subject;
        result.LinesReceived.Should().Be(1);
        result.LinesSkipped.Should().Be(1);
        result.Notes.Should().ContainSingle().Which.Should().Contain("SERIAL");
    }

    [Fact]
    public async Task A_dry_run_reports_what_it_would_receive_and_creates_nothing()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "A")]));
        var history = new RecordingGrnHistory();

        var sweep = await GrnServiceUnderTest.Build(erp, history: history)
            .ReceiveEligibleAsync(GrnServiceUnderTest.Request(dryRun: true) with { InvoiceNumber = null }, CancellationToken.None);

        erp.CallCount("grn/create").Should().Be(0);
        erp.CallCount("getAllRateStructureDetails").Should().Be(0);
        sweep.GrnsPlanned.Should().Be(1);
        sweep.GrnsCreated.Should().Be(0);
        history.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_PO_the_list_says_has_nothing_pending_is_not_examined()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101, isgrn: false));

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        sweep.Examined.Should().Be(0);
        erp.CallCount("getpotogrnredirectdata").Should().Be(0);
    }

    [Fact]
    public async Task One_PO_refused_by_the_ERP_does_not_stop_the_next()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(102, "000013"), GrnFixtures.PoRow(101, "000012"))
            .Route("getposearchdetails", path => path.Contains("/102/", StringComparison.Ordinal)
                ? GrnFixtures.PoLines(102, [GrnFixtures.Line(102, "A")])
                : GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "B")]));

        erp.CreateResponse = body => body["grnitemDetails"]![0]!["xgrndpoid"]!.GetValue<long>() == 102
            ? (400, false, "Field_validation_error")
            : (200, true, "GRNCreated#26-27/GR/NF1/000200#6000");

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        sweep.Results.Select(result => result.Status).Should().Equal(PoGrnReceiptStatus.Failed, PoGrnReceiptStatus.Created);
        sweep.GrnsCreated.Should().Be(1);
    }

    [Fact]
    public async Task Turning_automation_off_mid_run_stops_before_the_next_PO()
    {
        var configs = new InMemoryGrnConfigs();
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(102, "000013"), GrnFixtures.PoRow(101, "000012"))
            .Route("getposearchdetails", path => path.Contains("/102/", StringComparison.Ordinal)
                ? GrnFixtures.PoLines(102, [GrnFixtures.Line(102, "A")])
                : GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "B")]));

        // Off as soon as the first GRN is created.
        erp.CreateResponse = _ =>
        {
            configs.TurnOff();
            return (200, true, "GRNCreated#26-27/GR/NF1/000300#7000");
        };

        var sweep = await GrnServiceUnderTest.Build(erp, configs).ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        erp.CallCount("grn/create").Should().Be(1);
        sweep.StoppedReason.Should().Contain("turned off");
    }

    [Fact]
    public async Task A_run_that_would_create_GRNs_needs_the_invoice_number()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101));

        var run = () => GrnServiceUnderTest.Build(erp)
            .ReceiveEligibleAsync(GrnServiceUnderTest.Request() with { InvoiceNumber = "  " }, CancellationToken.None);

        (await run.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("invoice number");
        erp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_foreign_currency_PO_is_skipped()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getpotogrnredirectdata", new JsonArray(
                new JsonObject { ["povndcd"] = "V0009", ["powhid"] = 3, ["povndname"] = "Overseas", ["vndcurcd"] = "USD", ["potype"] = "R" }));

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        sweep.Results.Should().ContainSingle().Which.Notes.Should().ContainSingle().Which.Should().Contain("USD");
        erp.CallCount("grn/create").Should().Be(0);
    }

    [Fact]
    public async Task Named_PO_numbers_limit_the_run_and_unknown_ones_are_reported()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(102, "000013"), GrnFixtures.PoRow(101, "000012"))
            .Route("getposearchdetails", GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "B")]));

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(
            GrnServiceUnderTest.Request() with { PoNumbers = ["26-27/PR/NF1/000012", "999999"] },
            CancellationToken.None);

        sweep.Results.Select(result => result.PoId).Should().Equal(101);
        sweep.NotFound.Should().Equal("999999");
    }

    [Fact]
    public async Task No_lines_for_the_PO_is_reported_as_such_not_as_nothing_pending()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101, [GrnFixtures.Line(999, "OTHER")]));

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(GrnServiceUnderTest.Request(dryRun: true), CancellationToken.None);

        sweep.Results.Should().ContainSingle().Which.Notes.Single()
            .Should().Contain("no receivable lines").And.Contain("warehouse 3").And.Contain("1 line row(s)");
    }

    [Fact]
    public async Task A_PO_line_without_tax_rows_is_never_received()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getposearchdetails", GrnFixtures.PoLines(101, [GrnFixtures.Line(101, "A")], taxes: []));

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        erp.CallCount("grn/create").Should().Be(0);
        sweep.Results.Should().ContainSingle().Which.Notes.Single().Should().Contain("no tax rows");
    }
}
