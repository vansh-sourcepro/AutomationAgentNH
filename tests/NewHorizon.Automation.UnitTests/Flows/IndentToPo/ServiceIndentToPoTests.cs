using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// Authorised Service Indent → Service PO, against a stand-in ERP.
/// </summary>
/// <remarks>
/// The cases worth pinning are the ones that differ from the material flow: the site and the
/// open/closed status come from the indent's own record rather than from the list, the quantity is
/// the outstanding one, the order is narrowed to this indent, and a second pass over an indent the
/// ERP has closed orders nothing.
/// </remarks>
public class ServiceIndentToPoTests
{
    // ---- Discovery ---------------------------------------------------------

    [Fact]
    public async Task An_authorised_open_service_indent_is_eligible()
    {
        var erp = ServiceErpFixtures.HappyPath();

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        eligible.Should().ContainSingle();
        eligible[0].Indent.IndentType.Should().Be(IndentType.Service);
        eligible[0].Indent.IndentId.Should().Be(ServiceErpFixtures.IndentId);
        eligible[0].DocumentStatus.Should().Be("Open");
    }

    [Fact]
    public async Task The_site_comes_from_the_indents_own_record_not_from_the_list()
    {
        // The service list returns the site's code but never its id, and the pending-lines query
        // filters on the id. Reading it from the detail call is the whole reason that call exists.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(siteId: 7));

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        eligible.Should().ContainSingle();
        eligible[0].Indent.SiteId.Should().Be(7);
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("Short Closed")]
    [InlineData("Cancelled")]
    [InlineData("Deleted")]
    [InlineData("C")]
    [InlineData("D")]
    public async Task A_service_indent_the_erp_has_finished_with_is_not_eligible(string documentStatus)
    {
        // "A fully converted service indent is still 'authorised'": the list cannot tell them
        // apart, so the status on the indent's own record is what decides.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(documentStatus));

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        eligible.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Open")]
    [InlineData("O")]
    public async Task Both_spellings_of_open_are_understood(string documentStatus)
    {
        // The ERP says it two ways: XINDSHSTATUS holds "O", but getSerIndentDetail translates it to
        // "Open" under a column still called XINDSHSTATUS. Reading only the letter is how this
        // feature first found nothing at all to convert against the live ERP.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(documentStatus));

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        eligible.Should().ContainSingle().Which.DocumentStatus.Should().Be("Open");
    }

    [Fact]
    public async Task An_unauthorised_service_indent_is_not_eligible()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(authorised: false));

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        eligible.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_with_no_type_filter_looks_for_service_indents_too()
    {
        // This is what makes authorising a service indent enough: the timer sweeps with no filter.
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest(),
            CancellationToken.None);

        erp.CallCount("ServiceIndentCont/indentEntryList").Should().BeGreaterThan(0);
        erp.CallCount("indententry/indentEntryList").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Service_discovery_asks_the_erp_for_every_configured_site()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { Sites = [1, 2, 4], IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        erp.Requests
            .Select(request => request.Path)
            .Should()
            .Contain(path => path.Contains("ServiceIndentCont/indentEntryList/1,2,4", StringComparison.Ordinal));
    }

    // ---- Conversion --------------------------------------------------------

    [Fact]
    public async Task A_converted_service_indent_reports_the_purchase_order_the_erp_created()
    {
        var erp = ServiceErpFixtures.HappyPath();

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();
        result.PurchaseOrders.Should().ContainSingle();
        result.PurchaseOrders[0].PoNumber.Should().Be("26-27/SP/SP1/000031");
        result.PurchaseOrders[0].PoId.Should().Be(90212);
        result.PurchaseOrders[0].VendorCode.Should().Be(ServiceErpFixtures.VendorCode);
        result.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_order_goes_to_the_service_create_endpoint_not_the_material_one()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        erp.CallCount("createServicePOEntry").Should().Be(1);
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task Document_control_is_asked_for_the_service_po_sub_type()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        erp.Requests
            .Select(request => request.Path)
            .Should()
            .Contain(path => path.Contains("getDefaultDocumentDetail/26-27/PR/SP/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pending_lines_are_asked_for_at_the_indents_own_site()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(siteId: 7));

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var body = erp.LastBodyFor("getItemDetailForSerPO");

        body.Should().NotBeNull();
        body!["LocId"]!.GetValue<int>().Should().Be(7);
        body["poType"]!.GetValue<string>().Should().Be("IN");
        body["vendorCode"]!.GetValue<string>().Should().Be(ServiceErpFixtures.VendorCode);
        body["rateCode"]!.GetValue<string>().Should().Be(ServiceErpFixtures.RateStructure);
    }

    [Fact]
    public async Task The_order_carries_the_outstanding_quantity_and_the_indent_line_it_belongs_to()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(
            new JsonArray(ServiceErpFixtures.PendingLine(alreadyOrdered: 4m, indentLineNo: 3)),
            new JsonArray(ServiceErpFixtures.PendingDelivery())));

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var item = ItemsOf(erp).Should().ContainSingle().Subject;

        item["quantity"]!.GetValue<decimal>().Should().Be(6m);
        item["indentLineNo"]!.GetValue<int>().Should().Be(3);
        item["indentId"]!.GetValue<long>().Should().Be(ServiceErpFixtures.IndentId);
        // PIDSBPURRT is the rate before the discount; the amounts carry the discount.
        item["basicprice"]!.GetValue<decimal>().Should().Be(ServiceErpFixtures.BasicPrice);
    }

    [Fact]
    public async Task Another_indents_outstanding_lines_are_left_alone()
    {
        // The ERP answers per vendor and site, not per indent: it hands back everything the vendor
        // has outstanding across every authorised service indent. Ordering the wrong one would be
        // silent, so the narrowing is on the indent's own id.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(
            new JsonArray(
                ServiceErpFixtures.PendingLine(pendingRow: 1),
                ServiceErpFixtures.PendingLine(indentId: 70999, pendingRow: 2)),
            new JsonArray(
                ServiceErpFixtures.PendingDelivery(pendingRow: 1),
                ServiceErpFixtures.PendingDelivery(pendingRow: 2))));

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        ItemsOf(erp).Should().ContainSingle()
            .Which["indentId"]!.GetValue<long>().Should().Be(ServiceErpFixtures.IndentId);
    }

    [Fact]
    public async Task The_delivery_dates_are_the_indents_own_renumbered_within_the_line()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(
            new JsonArray(ServiceErpFixtures.PendingLine()),
            new JsonArray(
                ServiceErpFixtures.PendingDelivery(deliveryLine: 4, date: "2026-09-05T00:00:00"),
                ServiceErpFixtures.PendingDelivery(deliveryLine: 9, date: "2026-10-05T00:00:00"))));

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var deliveries = ItemsOf(erp).Single()["DeliveryDetail"]!.AsArray();

        deliveries.Should().HaveCount(2);
        deliveries.Select(row => row!["delsrno"]!.GetValue<int>()).Should().Equal(1, 2);
        deliveries.Select(row => row!["srno"]!.GetValue<int>()).Should().AllBeEquivalentTo(1);
        deliveries[0]!["deldate"]!.GetValue<string>().Should().Be("09/05/2026");
        deliveries[1]!["deldate"]!.GetValue<string>().Should().Be("10/05/2026");
    }

    [Fact]
    public async Task The_erp_prices_the_line_and_the_agent_only_multiplies_by_quantity()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var payload = erp.LastBodyFor("createServicePOEntry")!;
        var header = payload["HeaderDetail"]!;

        // 500 x 10 basic, 45 x 10 per tax component, 590 landed per unit.
        header["poBasicValAftDis"]!.GetValue<decimal>().Should().Be(5000m);
        header["taxesDomesticCurr"]!.GetValue<decimal>().Should().Be(900m);
        header["taxesForeignCurr"]!.GetValue<decimal>().Should().Be(0m);

        var item = ItemsOf(erp).Single();
        item["taxvalue"]!.GetValue<decimal>().Should().Be(900m);
        item["landedPrice"]!.GetValue<decimal>().Should().Be(590m);
    }

    [Fact]
    public async Task Every_tax_component_becomes_a_row_carrying_its_own_amount_and_tax_type()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var taxRows = erp.LastBodyFor("createServicePOEntry")!["rsGrid"]!.AsArray();

        taxRows.Should().HaveCount(2);
        taxRows.Select(row => row!["rateCode"]!.GetValue<string>()).Should().Equal("P0005", "P0004");
        taxRows.Select(row => row!["rateAmount"]!.GetValue<decimal>()).Should().AllBeEquivalentTo(450m);
        // The tax type is the one field the pricing call does not return; it comes from the rate
        // structure's own detail rows, matched on the rate code.
        taxRows.Select(row => row!["taxTyp"]!.GetValue<string>()).Should().Equal("M", "N");
        taxRows.Select(row => row!["docSubType"]!.GetValue<string>()).Should().AllBeEquivalentTo("SP");
        taxRows.Select(row => row!["refLine"]!.GetValue<int>()).Should().AllBeEquivalentTo(1);
    }

    [Fact]
    public async Task The_header_carries_the_document_control_flags_and_no_document_number()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var payload = erp.LastBodyFor("createServicePOEntry")!;

        payload["Mode"]!.GetValue<string>().Should().Be("A");
        payload["AutoNoFlag"]!.GetValue<string>().Should().Be("Y");
        payload["LocReqFlag"]!.GetValue<string>().Should().Be("Y");
        payload["AuthFlag"]!.GetValue<string>().Should().Be("Y");
        payload["HeaderDetail"]!["poNumber"]!.GetValue<string>().Should().BeEmpty();
        payload["HeaderDetail"]!["docType"]!.GetValue<string>().Should().Be("PR");
        payload["HeaderDetail"]!["docSubtype"]!.GetValue<string>().Should().Be("SP");
        payload["HeaderDetail"]!["selectOdrType"]!.GetValue<string>().Should().Be("S");
        payload["HeaderDetail"]!["poType"]!.GetValue<string>().Should().Be("IN");
    }

    [Fact]
    public async Task The_vendors_own_terms_and_codes_reach_the_header()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var header = erp.LastBodyFor("createServicePOEntry")!["HeaderDetail"]!;

        header["poVendor"]!.GetValue<string>().Should().Be(ServiceErpFixtures.VendorCode);
        header["poCurrency"]!.GetValue<string>().Should().Be(ServiceErpFixtures.Currency);
        header["paymentControl"]!.GetValue<string>().Should().Be("P01");
        header["deliveryControl"]!.GetValue<string>().Should().Be("D01");
        // The service header takes the vendor's real octroi terms; the material one deliberately
        // carries the GSTIN there instead, because that is what its screen posts.
        header["octroiTermControl"]!.GetValue<string>().Should().Be("O01");
        header["poRateStructure"]!.GetValue<string>().Should().Be(ServiceErpFixtures.RateStructure);
        header["poBuyer"]!.GetValue<string>().Should().Be("001");
    }

    [Fact]
    public async Task A_vendor_in_the_same_state_with_a_valid_gstin_is_not_reverse_charge()
    {
        var erp = ServiceErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        var header = erp.LastBodyFor("createServicePOEntry")!["HeaderDetail"]!;

        header["isRCMunderGST"]!.GetValue<bool>().Should().BeFalse();
        header["poRCMType"]!.GetValue<string>().Should().Be("SGST");
    }

    [Fact]
    public async Task A_vendor_without_a_gstin_is_reverse_charge_the_way_the_screen_decides_it()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("GetVendorInformation", _ => ServiceErpFixtures.ServiceVendor(gstNumber: string.Empty));

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        erp.LastBodyFor("createServicePOEntry")!["HeaderDetail"]!["isRCMunderGST"]!
            .GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_vendor_in_another_state_is_igst()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("GetVendorInformation", _ => ServiceErpFixtures.ServiceVendor(stateCode: "MH"));

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        erp.LastBodyFor("createServicePOEntry")!["HeaderDetail"]!["poRCMType"]!
            .GetValue<string>().Should().Be("IGST");
    }

    [Fact]
    public async Task Two_vendors_become_two_orders()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(
            itemCodes: ["SRV-CAL-01", "SRV-AMC-02"]));
        erp.Route("getItemServiceDetail", body => ServiceErpFixtures.ItemMaster(
            body!["itemCode"]!.GetValue<string>(),
            body["itemCode"]!.GetValue<string>() == "SRV-CAL-01" ? 4411 : 4412));
        erp.RouteByPath("getItemServiceVendorDetail", path => new JsonArray(
            ServiceErpFixtures.ItemVendorRow(path.EndsWith("4411", StringComparison.Ordinal) ? "0009" : "0011")));
        erp.Route("getItemDetailForSerPO", body => ServiceErpFixtures.Pending(
            new JsonArray(ServiceErpFixtures.PendingLine(
                body!["vendorCode"]!.GetValue<string>() == "0009" ? "SRV-CAL-01" : "SRV-AMC-02")),
            new JsonArray(ServiceErpFixtures.PendingDelivery(
                body["vendorCode"]!.GetValue<string>() == "0009" ? "SRV-CAL-01" : "SRV-AMC-02"))));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.PurchaseOrders.Should().HaveCount(2);
        erp.CallCount("createServicePOEntry").Should().Be(2);
    }

    [Fact]
    public async Task A_transient_failure_on_one_vendor_does_not_lose_the_other_vendors_order()
    {
        // Regression test: mirrors IndentToPoServiceTests' material-side equivalent — a transient
        // failure on vendor #2 (a real 500, not a business refusal) must not discard vendor #1's
        // already-created service purchase order by unwinding out of ConvertServiceIndentAsync's
        // per-vendor-group loop uncaught.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail(
            itemCodes: ["SRV-CAL-01", "SRV-AMC-02"]));
        erp.Route("getItemServiceDetail", body => ServiceErpFixtures.ItemMaster(
            body!["itemCode"]!.GetValue<string>(),
            body["itemCode"]!.GetValue<string>() == "SRV-CAL-01" ? 4411 : 4412));
        erp.RouteByPath("getItemServiceVendorDetail", path => new JsonArray(
            ServiceErpFixtures.ItemVendorRow(path.EndsWith("4411", StringComparison.Ordinal) ? "0009" : "0011")));
        erp.Route("getItemDetailForSerPO", body => ServiceErpFixtures.Pending(
            new JsonArray(ServiceErpFixtures.PendingLine(
                body!["vendorCode"]!.GetValue<string>() == "0009" ? "SRV-CAL-01" : "SRV-AMC-02")),
            new JsonArray(ServiceErpFixtures.PendingDelivery(
                body["vendorCode"]!.GetValue<string>() == "0009" ? "SRV-CAL-01" : "SRV-AMC-02"))));

        erp.FaultRoutes["createServicePOEntry"] = body =>
            body!["HeaderDetail"]!["poVendor"]!.GetValue<string>() == "0011"
                ? (500, "Database connection lost.")
                : null;

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();
        result.PurchaseOrders.Should().ContainSingle();
        result.Notes.Should().ContainSingle().Which.Should().Contain("temporarily unavailable");
        erp.CallCount("createServicePOEntry").Should().Be(2);
    }

    // ---- Refusals ----------------------------------------------------------

    [Fact]
    public async Task A_service_item_no_vendor_supplies_is_named_rather_than_invented()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemServiceVendorDetail", new JsonArray());

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle()
            .Which.Should().Contain(ServiceErpFixtures.ItemCode).And.Contain("item/vendor record");
        erp.CallCount("createServicePOEntry").Should().Be(0);
    }

    [Fact]
    public async Task A_vendor_row_with_no_rate_structure_is_a_note_not_a_crash()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemServiceVendorDetail", new JsonArray(
            ServiceErpFixtures.ItemVendorRow(rateStructure: string.Empty)));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("rate structure");
    }

    [Fact]
    public async Task A_line_with_no_delivery_date_is_named_rather_than_posted()
    {
        // The ERP refuses an order line with no delivery row, and the only dates available are the
        // indent's own. Naming the gap beats posting something the ERP will reject.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(
            new JsonArray(ServiceErpFixtures.PendingLine()),
            new JsonArray()));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("delivery date");
        erp.CallCount("createServicePOEntry").Should().Be(0);
    }

    [Fact]
    public async Task A_line_with_no_rate_for_the_vendor_is_named_rather_than_ordered_at_zero()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(
            new JsonArray(ServiceErpFixtures.PendingLine(basicPrice: 0m)),
            new JsonArray(ServiceErpFixtures.PendingDelivery())));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("no rate");
        erp.CallCount("createServicePOEntry").Should().Be(0);
    }

    [Fact]
    public async Task A_site_that_does_not_auto_number_service_orders_is_refused_not_guessed_at()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getDefaultDocumentDetail", ServiceErpFixtures.DocumentControl(autoNumber: false));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("automatically");
        erp.CallCount("createServicePOEntry").Should().Be(0);
    }

    [Fact]
    public async Task An_erp_refusal_of_the_create_call_is_reported_with_its_reason()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Refusals["createServicePOEntry"] = "Insufficient Service indent quantities for PO conversion.";

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle()
            .Which.Should().Contain("Insufficient Service indent quantities");
    }

    [Fact]
    public async Task A_refusal_is_logged_so_an_unattended_sweep_leaves_the_reason_somewhere()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Refusals["createServicePOEntry"] = "Service po date can not be less than service indent date";

        var logger = new RecordingLogger<IndentToPoService>();

        await ServiceUnderTest.Build(erp, logger: logger)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        logger.MessagesAt(LogLevel.Warning)
            .Should()
            .Contain(message => message.Contains("produced no purchase order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Converting_a_closed_service_indent_is_refused()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail("Closed"));

        var act = () => ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        await act.Should().ThrowAsync<ErpBusinessException>()
            .Where(exception => exception.LaymanMessage.Contains("close", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Second sweep ------------------------------------------------------

    [Fact]
    public async Task A_second_sweep_over_a_converted_indent_orders_nothing()
    {
        // What the ERP does after the first order: XINDSDPOQTY reaches XINDSDINDQTY, the line
        // closes, the indent closes, and the pending-lines query stops returning it.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail("Closed"));
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(new JsonArray(), new JsonArray()));

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentTypes = [IndentType.Service] },
            CancellationToken.None);

        sweep.Examined.Should().Be(0);
        sweep.PurchaseOrdersCreated.Should().Be(0);
        erp.CallCount("createServicePOEntry").Should().Be(0);
    }

    [Fact]
    public async Task A_fully_ordered_line_on_a_still_open_indent_is_passed_over_quietly()
    {
        // The other half of the same story: one line of a two-line indent was ordered already, so
        // the indent is still open but that line has nothing left.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(
            new JsonArray(
                ServiceErpFixtures.PendingLine(pendingRow: 1, indentLineNo: 1, alreadyOrdered: 10m),
                ServiceErpFixtures.PendingLine(pendingRow: 2, indentLineNo: 2)),
            new JsonArray(
                ServiceErpFixtures.PendingDelivery(pendingRow: 1),
                ServiceErpFixtures.PendingDelivery(pendingRow: 2))));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ServiceErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();

        var item = ItemsOf(erp).Should().ContainSingle().Subject;
        item["indentLineNo"]!.GetValue<int>().Should().Be(2);
        item["srno"]!.GetValue<int>().Should().Be(1);
    }

    // ---- Dry run -----------------------------------------------------------

    [Fact]
    public async Task A_dry_run_reports_the_order_it_would_place_and_places_none()
    {
        var erp = ServiceErpFixtures.HappyPath();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentTypes = [IndentType.Service], DryRun = true },
            CancellationToken.None);

        sweep.PurchaseOrdersPlanned.Should().Be(1);
        sweep.PurchaseOrdersCreated.Should().Be(0);
        erp.CallCount("createServicePOEntry").Should().Be(0);
        sweep.Results.Should().ContainSingle()
            .Which.Notes.Should().ContainSingle().Which.Should().Contain("Would raise");
    }

    // ---- The material flow is untouched ------------------------------------

    [Fact]
    public async Task The_vendor_driven_entry_point_refuses_service_indents()
    {
        // It runs the material PO sequence, which has no service counterpart: the ERP has no
        // vendor-first question for service indents that is not already scoped to one.
        var act = () => ServiceUnderTest.Build(ServiceErpFixtures.HappyPath()).CreateAsync(
            new IndentPoRequest { IndentType = IndentType.Service, VendorCode = "0009" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ErpBusinessException>()
            .Where(exception => exception.LaymanMessage.Contains("per indent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_id_that_is_both_a_material_and_a_service_indent_is_refused_not_guessed_at()
    {
        // XINDID and XINDAUTOID are keys into different tables; the same number is a plausible
        // value for both, and raising an order against the wrong document would be silent.
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(indentId: ServiceErpFixtures.IndentId)));
        erp.Route("getIndentDetail", ErpFixtures.IndentDetail());

        var act = () => ServiceUnderTest.Build(erp).ConvertIndentAsync(
            ServiceErpFixtures.IndentId,
            indentNumber: null,
            sites: null,
            dryRun: true,
            CancellationToken.None);

        await act.Should().ThrowAsync<ErpBusinessException>()
            .Where(exception => exception.LaymanMessage.Contains("passing indentType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Naming_the_type_resolves_the_clash()
    {
        var erp = ServiceErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(indentId: ServiceErpFixtures.IndentId)));
        erp.Route("getIndentDetail", ErpFixtures.IndentDetail());

        var result = await ServiceUnderTest.Build(erp).ConvertIndentAsync(
            ServiceErpFixtures.IndentId,
            indentNumber: null,
            sites: null,
            indentTypes: [IndentType.Service],
            dryRun: false,
            CancellationToken.None);

        result.Indent.IndentType.Should().Be(IndentType.Service);
        result.Converted.Should().BeTrue();
    }

    private static IEnumerable<JsonNode> ItemsOf(FakeErp erp) =>
        erp.LastBodyFor("createServicePOEntry")!["HeaderDetail"]!["ItemDetail"]!
            .AsArray()
            .Where(node => node is not null)!;
}
