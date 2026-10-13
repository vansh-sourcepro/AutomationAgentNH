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
    public async Task Multiple_named_PO_numbers_are_found_on_first_page_without_unnecessary_queries()
    {
        var erp = GrnFixtures.StandardErp(
                GrnFixtures.PoRow(103, "000014"),
                GrnFixtures.PoRow(102, "000013"),
                GrnFixtures.PoRow(101, "000012"))
            .Route("getposearchdetails", path =>
            {
                var poId = path.Contains("101") ? 101 : 103;
                return GrnFixtures.PoLines(poId, [GrnFixtures.Line(poId, "A")]);
            });

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(
            GrnServiceUnderTest.Request() with { PoNumbers = ["000012", "000014"] },
            CancellationToken.None);

        sweep.Results.Select(result => result.PoId).Should().Equal(103, 101);
        sweep.NotFound.Should().BeEmpty();

        var poListSearches = erp.Requests
            .Where(r => r.Path.Contains("POEntry/List", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Body?["searchValue"]?.GetValue<string>())
            .ToList();

        // Direct search targeted the requested POs without scanning the whole list
        poListSearches.Should().Contain("000012");
        poListSearches.Should().Contain("000014");
        poListSearches.Should().NotContain(string.Empty);
    }

    [Fact]
    public async Task Named_PO_number_search_finds_targeted_po()
    {
        var erp = GrnFixtures.StandardErp()
            .Route("POEntry/List", (_, body) =>
            {
                var search = body?["searchValue"]?.GetValue<string>()?.Trim() ?? string.Empty;
                JsonObject[] rows = search switch
                {
                    "000014" => [GrnFixtures.PoRow(103, "000014")],
                    _ => [GrnFixtures.PoRow(101, "000012")],
                };
                return new JsonArray([.. rows.Select(r => (JsonNode)r.DeepClone())]);
            })
            .Route("getposearchdetails", path =>
            {
                var poId = path.Contains("101") ? 101 : 103;
                return GrnFixtures.PoLines(poId, [GrnFixtures.Line(poId, "A")]);
            });

        var sweep = await GrnServiceUnderTest.Build(erp).ReceiveEligibleAsync(
            GrnServiceUnderTest.Request() with { PoNumbers = ["000012", "000014"] },
            CancellationToken.None);

        sweep.Results.Select(result => result.PoId).Should().Equal(103, 101);
        sweep.NotFound.Should().BeEmpty();

        var poListSearches = erp.Requests
            .Where(r => r.Path.Contains("POEntry/List", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Body?["searchValue"]?.GetValue<string>())
            .ToList();

        poListSearches.Should().Contain("000012");
        poListSearches.Should().Contain("000014");
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

    [Fact]
    public async Task When_grn_authorization_is_disabled_in_document_control_the_sweep_is_blocked()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(101))
            .Route("getDefaultDocumentDetail", new JsonArray(
                new JsonObject
                {
                    ["groupCode"] = "GR",
                    ["locationCode"] = "NF1",
                    ["isLocationRequired"] = true,
                    ["isAutoNumberGenerated"] = true,
                    ["isDefault"] = "Y",
                    ["isAutorisationRequired"] = false,
                }));

        var service = GrnServiceUnderTest.Build(erp);
        var sweep = await service.ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        sweep.GrnsCreated.Should().Be(0);
        sweep.StoppedReason.Should().NotBeNull();
        sweep.StoppedReason.Should().Contain("GRN Authorization is turned OFF in ERP");
        erp.CallCount("grn/create").Should().Be(0);
    }

    [Fact]
    public async Task CheckAuthorizationRequiredAsync_reports_refusals_when_flag_is_off()
    {
        var erp = GrnFixtures.StandardErp()
            .Route("getDefaultDocumentDetail", new JsonArray(
                new JsonObject
                {
                    ["groupCode"] = "GR",
                    ["locationCode"] = "NF1",
                    ["isLocationRequired"] = true,
                    ["isAutoNumberGenerated"] = true,
                    ["isDefault"] = "Y",
                    ["isAutorisationRequired"] = false,
                }));

        var service = GrnServiceUnderTest.Build(erp);
        var result = await service.CheckAuthorizationRequiredAsync([1], [PoGrnType.Regular], CancellationToken.None);

        result.Allowed.Should().BeFalse();
        result.Refusals.Should().ContainSingle().Which.Should().Contain("Site NF1 (Regular)").And.Contain("turned OFF");
    }

    [Fact]
    public void ResolveNextInwardNumber_increments_consecutive_lines_within_same_grn()
    {
        var line1 = GrnLineTotals.From(GrnFixtures.Line(1, "ITEM1"), 1, [.. GrnFixtures.TaxRows(1, "ITEM1").OfType<JsonObject>()]);
        var line2 = GrnLineTotals.From(GrnFixtures.Line(1, "ITEM2"), 2, [.. GrnFixtures.TaxRows(1, "ITEM2").OfType<JsonObject>()]);

        var inwd1 = PoToGrnService.ResolveNextInwardNumber("2627-0000001", "2627-", "ITEM1", [line1, line2], isItemWise: false);
        inwd1.Should().Be("2627-0000001");
        line1.InwardNumber = inwd1;

        var inwd2 = PoToGrnService.ResolveNextInwardNumber("2627-0000001", "2627-", "ITEM2", [line1, line2], isItemWise: false);
        inwd2.Should().Be("2627-0000002");
    }

    [Fact]
    public void ResolveNextInwardNumber_falls_back_to_0000001_when_empty()
    {
        var line = GrnLineTotals.From(GrnFixtures.Line(1, "ITEM1"), 1, [.. GrnFixtures.TaxRows(1, "ITEM1").OfType<JsonObject>()]);
        var inwd = PoToGrnService.ResolveNextInwardNumber(null, "2627-", "ITEM1", [line], isItemWise: false);
        inwd.Should().Be("2627-0000001");
    }

    [Fact]
    public async Task Po_with_inward_item_is_received_and_stamped_when_inward_auto_gen_is_enabled()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(100))
            .Route("inventoryPolicy/getDetail", new JsonObject
            {
                ["islinelevelwhgrn"] = false,
                ["isinwardAutoGen"] = true,
                ["isinwardAutoGenItemWise"] = false,
            })
            .Route("getposearchdetails", GrnFixtures.PoLines(100,
                [GrnFixtures.Line(100, "INWD-ITEM", tweak: row => row["miminwdreq"] = true)]))
            .Route("getmaxParamValue", new JsonArray(
                new JsonObject { ["maxinwd"] = "2627-0000005" }));

        var service = GrnServiceUnderTest.Build(erp);
        var sweep = await service.ReceiveEligibleAsync(GrnServiceUnderTest.Request(), CancellationToken.None);

        sweep.GrnsCreated.Should().Be(1);
        erp.CallCount("getmaxParamValue").Should().Be(1);

        var payload = erp.CreateBodies.Should().ContainSingle().Subject;
        var item = payload["grnitemDetails"]!.AsArray().Should().ContainSingle().Subject;
        item["xgrndinwd"]!.GetValue<string>().Should().Be("2627-0000005");
    }

    [Fact]
    public async Task Po_with_inward_item_is_skipped_when_inward_auto_gen_is_disabled()
    {
        var erp = GrnFixtures.StandardErp(GrnFixtures.PoRow(100))
            .Route("inventoryPolicy/getDetail", new JsonObject
            {
                ["islinelevelwhgrn"] = false,
                ["isinwardAutoGen"] = false,
                ["isinwardAutoGenItemWise"] = false,
            })
            .Route("getposearchdetails", GrnFixtures.PoLines(100,
                [GrnFixtures.Line(100, "INWD-ITEM", tweak: row => row["miminwdreq"] = true)]));

        var service = GrnServiceUnderTest.Build(erp);
        var sweep = await service.ReceiveEligibleAsync(GrnServiceUnderTest.Request(GrnReceiptMode.Complete), CancellationToken.None);

        sweep.GrnsCreated.Should().Be(0);
        erp.CallCount("grn/create").Should().Be(0);
        sweep.Results.Should().ContainSingle().Which.Notes.Should().ContainMatch("*autogeneration of inward number is not enabled*");
    }
}
