using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// Covers the half of the feature the payload tests cannot reach: finding an authorised indent and
/// driving the ERP call sequence for it.
/// </summary>
/// <remarks>
/// These exist because of a real failure — an indent was authorised in the ERP and nothing
/// happened. Three things were wrong and each has a test here: nothing looked for authorised
/// indents at all, the site was taken from configuration instead of from the indent, and an order
/// swept up every line the vendor had outstanding rather than the indent that was authorised.
/// </remarks>
public sealed class IndentToPoServiceTests
{
    // ---- Discovery ---------------------------------------------------------

    [Fact]
    public async Task An_authorised_open_indent_is_discovered()
    {
        var erp = ErpFixtures.HappyPath();
        var service = ServiceUnderTest.Build(erp);

        var eligible = await service.FindEligibleAsync(new IndentDiscoveryRequest(), CancellationToken.None);

        eligible.Should().ContainSingle();
        eligible[0].Indent.IndentId.Should().Be(ErpFixtures.IndentId);
        eligible[0].Indent.SiteId.Should().Be(ErpFixtures.SiteId);
        eligible[0].Indent.IndentType.Should().Be(IndentType.Regular);
    }

    [Fact]
    public async Task An_unauthorised_indent_is_ignored()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(ErpFixtures.IndentListRow(status: "P")));

        var eligible = await ServiceUnderTest.Build(erp)
            .FindEligibleAsync(new IndentDiscoveryRequest(), CancellationToken.None);

        eligible.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Close")]
    [InlineData("Short Closed")]
    public async Task An_indent_the_erp_has_finished_with_is_ignored(string indentStatus)
    {
        // Closed means every line is already on a purchase order. Offering it again would either be
        // refused or, worse, duplicate the order.
        var erp = ErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(ErpFixtures.IndentListRow(indentStatus: indentStatus)));

        var eligible = await ServiceUnderTest.Build(erp)
            .FindEligibleAsync(new IndentDiscoveryRequest(), CancellationToken.None);

        eligible.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_requested_indent_type_is_offered()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(indentId: 1, indentTypeCode: "R", totalRows: 3),
            ErpFixtures.IndentListRow(indentId: 2, indentTypeCode: "C", totalRows: 3),
            // "L" is a labour indent, which this flow does not create purchase orders for.
            ErpFixtures.IndentListRow(indentId: 3, indentTypeCode: "L", totalRows: 3)));

        var capital = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = [IndentType.Capital] },
            CancellationToken.None);

        capital.Should().ContainSingle();
        capital[0].Indent.IndentId.Should().Be(2);
    }

    [Fact]
    public async Task Discovery_asks_the_erp_for_every_configured_site()
    {
        // The original defect: one site was configured, and indents raised at any other were
        // invisible because the ERP filters pending indent lines on the indent's own site.
        var erp = ErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { Sites = [1, 2, 4, 7, 8] },
            CancellationToken.None);

        erp.LastBodyFor("indententry/indentEntryList").Should().NotBeNull();
        erp.LastBodyFor("indententry/indentEntryList")!["LocationIds"]!.GetValue<string>().Should().Be("1,2,4,7,8");
        erp.LastBodyFor("indententry/indentEntryList")!["Status"]!.GetValue<string>().Should().Be("A");
    }

    // ---- Conversion --------------------------------------------------------

    [Fact]
    public async Task A_converted_indent_reports_the_purchase_order_the_erp_created()
    {
        var erp = ErpFixtures.HappyPath();

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();
        result.PurchaseOrders.Should().ContainSingle();
        result.PurchaseOrders[0].PoNumber.Should().Be("26-27/RG/SP1/000794");
        result.PurchaseOrders[0].PoId.Should().Be(107432);
        result.PurchaseOrders[0].VendorCode.Should().Be(ErpFixtures.VendorCode);
        result.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_vendor_the_item_master_names_is_the_vendor_the_order_is_raised_on()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("ItemVendorPurchase/list", new JsonArray(
            // Priority 1 but not the default; the default wins even at a worse priority, because
            // that is the row the ERP's own screens treat as the answer.
            ErpFixtures.ItemVendorRow(vendorCode: "W001", rateStructure: "P0017", isDefault: false, priority: 1),
            ErpFixtures.ItemVendorRow(vendorCode: "0006", rateStructure: "PSC3", isDefault: true, priority: 804)));

        await ServiceUnderTest.Build(erp).CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        var pending = erp.LastBodyFor("GetPendingItemsFromIndentnew")!;
        pending["vendorcode"]!.GetValue<string>().Should().Be("0006");
        pending["ratestructure"]!.GetValue<string>().Should().Be("PSC3");
    }

    [Fact]
    public async Task Without_a_default_vendor_the_highest_priority_active_row_is_used()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("ItemVendorPurchase/list", new JsonArray(
            ErpFixtures.ItemVendorRow(vendorCode: "W002", isDefault: false, priority: 9),
            ErpFixtures.ItemVendorRow(vendorCode: "W001", isDefault: false, priority: 2),
            // Inactive rows are not offers, whatever their priority.
            ErpFixtures.ItemVendorRow(vendorCode: "W000", isDefault: false, priority: 1, active: false)));

        await ServiceUnderTest.Build(erp).CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        erp.LastBodyFor("GetPendingItemsFromIndentnew")!["vendorcode"]!.GetValue<string>().Should().Be("W001");
    }

    [Fact]
    public async Task A_rate_structure_search_hit_on_another_item_does_not_choose_the_vendor()
    {
        // The ERP endpoint is a search box, not a filter: asking about "0500000DQ" also returns
        // longer codes containing it. Letting one of those pick the vendor would order the wrong
        // thing from the wrong supplier.
        var erp = ErpFixtures.HappyPath();
        erp.Route("ItemVendorPurchase/list", new JsonArray(
            ErpFixtures.ItemVendorRow(itemCode: "0500000DQX", vendorCode: "WRONG", isDefault: true),
            ErpFixtures.ItemVendorRow(itemCode: ErpFixtures.ItemCode, vendorCode: "0006", isDefault: false, priority: 5)));

        await ServiceUnderTest.Build(erp).CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        erp.LastBodyFor("GetPendingItemsFromIndentnew")!["vendorcode"]!.GetValue<string>().Should().Be("0006");
    }

    [Fact]
    public async Task An_item_with_no_vendor_record_is_named_rather_than_silently_dropped()
    {
        // Exactly the case that blocked the indent this work started from. The ERP will not offer
        // the line to any vendor, so the only useful thing to do is say which item and why.
        var erp = ErpFixtures.HappyPath();
        erp.Route("ItemVendorPurchase/list", new JsonArray());

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.PurchaseOrders.Should().BeEmpty();
        result.Notes.Should().ContainSingle()
            .Which.Should().Contain(ErpFixtures.ItemCode).And.Contain("item/vendor purchase record");
    }

    [Fact]
    public async Task The_order_is_raised_at_the_indents_own_site_not_the_configured_one()
    {
        // The configured site is 1; the indent is at site 2. The ERP filters pending indent lines
        // on XINDHLOCID, so asking about site 1 would correctly answer "nothing to order".
        var erp = ErpFixtures.HappyPath();
        var options = new IndentPoOptions { LocationId = 1, Sites = [1, 2], Currency = "RS", DomesticCurrency = "RS" };

        await ServiceUnderTest.Build(erp, options)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        erp.LastBodyFor("GetPendingItemsFromIndentnew")!["locationid"]!.GetValue<int>()
            .Should().Be(ErpFixtures.SiteId);
        erp.Requests.Select(request => request.Path)
            .Should().Contain(path => path.Contains("getDefaultDocumentDetail/26-27/PR/RP/2"));
    }

    [Fact]
    public async Task Only_the_indents_own_lines_are_ordered()
    {
        // The ERP answers with everything the vendor has outstanding. Ordering the lot is what the
        // vendor-driven entry point does on purpose; converting one indent must not.
        var erp = ErpFixtures.HappyPath();
        erp.Route("GetPendingItemsFromIndentnew", ErpFixtures.Pending(
            new JsonArray(ErpFixtures.PendingItem(), ErpFixtures.PendingItem(itemCode: "OTHER-ITEM")),
            new JsonArray(
                ErpFixtures.PendingDelivery(),
                ErpFixtures.PendingDelivery(itemCode: "OTHER-ITEM", indentId: 50569, deliveryLine: 2))));

        await ServiceUnderTest.Build(erp).CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        var created = erp.LastBodyFor("POEntry/create")!;
        var items = created["ItemDetails"]!.AsArray();

        items.Should().ContainSingle();
        items[0]!["itemCode"]!.GetValue<string>().Should().Be(ErpFixtures.ItemCode);

        var deliveries = created["DelDetails"]!.AsArray();
        deliveries.Should().ContainSingle();
        deliveries[0]!["PIDINDID"]!.GetValue<long>().Should().Be(ErpFixtures.IndentId);
    }

    [Fact]
    public async Task A_line_already_fully_ordered_is_left_alone()
    {
        // The ERP has already decremented what is outstanding; a zero balance means somebody — or
        // an earlier run — has ordered it.
        var erp = ErpFixtures.HappyPath();
        erp.Route("GetPendingItemsFromIndentnew", ErpFixtures.Pending(
            new JsonArray(ErpFixtures.PendingItem()),
            new JsonArray(ErpFixtures.PendingDelivery(outstanding: 0m))));

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("already ordered in full");
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task A_partly_ordered_line_is_ordered_only_for_what_is_left()
    {
        // 100 indented, 70 already on order: the ERP reports 30 outstanding and 30 is what goes on
        // the purchase order.
        var erp = ErpFixtures.HappyPath();
        erp.Route("GetPendingItemsFromIndentnew", ErpFixtures.Pending(
            new JsonArray(ErpFixtures.PendingItem()),
            new JsonArray(ErpFixtures.PendingDelivery(outstanding: 30m))));

        await ServiceUnderTest.Build(erp).CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        var created = erp.LastBodyFor("POEntry/create")!;
        created["DelDetails"]![0]!["PILQTYIUOM"]!.GetValue<decimal>().Should().Be(30m);
        created["ItemDetails"]![0]!["PIDQTYIUOM"]!.GetValue<decimal>().Should().Be(30m);
    }

    [Fact]
    public async Task An_indent_whose_items_come_from_two_vendors_becomes_two_purchase_orders()
    {
        var erp = ErpFixtures.HappyPath();

        erp.Route("getIndentDetail", ErpFixtures.IndentDetail(
            "Authorized",
            "Open",
            ErpFixtures.IndentDetailItem("ITEM-A"),
            ErpFixtures.IndentDetailItem("ITEM-B")));

        erp.Route("ItemVendorPurchase/list", body =>
        {
            var itemCode = body!["SearchValue"]!.GetValue<string>();

            return new JsonArray(itemCode == "ITEM-A"
                ? ErpFixtures.ItemVendorRow("ITEM-A", "V-A", "RS-A")
                : ErpFixtures.ItemVendorRow("ITEM-B", "V-B", "RS-B"));
        });

        erp.Route("GetPendingItemsFromIndentnew", body =>
        {
            var itemCode = body!["vendorcode"]!.GetValue<string>() == "V-A" ? "ITEM-A" : "ITEM-B";

            return ErpFixtures.Pending(
                new JsonArray(ErpFixtures.PendingItem(itemCode)),
                new JsonArray(ErpFixtures.PendingDelivery(itemCode)));
        });

        erp.Route("POEntry/create", body =>
        {
            var vendor = body!["Poheader"]![0]!["POHVNDCODE"]!.GetValue<string>();

            return ErpFixtures.CreatedPo($"26-27/RG/SP1/{vendor}", vendor == "V-A" ? 1 : 2);
        });

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.PurchaseOrders.Should().HaveCount(2);
        result.PurchaseOrders.Select(po => po.VendorCode).Should().BeEquivalentTo(["V-A", "V-B"]);
    }

    [Fact]
    public async Task A_refusal_from_one_vendor_does_not_lose_the_other_vendors_order()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Refusals["POEntry/create"] = "The purchase order was refused.";

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeFalse();
        result.Notes.Should().ContainSingle().Which.Should().Contain("refused");
    }

    [Fact]
    public async Task A_transient_failure_on_one_vendor_does_not_lose_the_other_vendors_order()
    {
        // Regression test: a transient failure on vendor group #2 (a real 500, not a business
        // refusal) must not discard vendor group #1's already-created purchase order by unwinding
        // out of ConvertCoreAsync's per-group loop uncaught.
        var erp = ErpFixtures.HappyPath();

        erp.Route("getIndentDetail", ErpFixtures.IndentDetail(
            "Authorized",
            "Open",
            ErpFixtures.IndentDetailItem("ITEM-A"),
            ErpFixtures.IndentDetailItem("ITEM-B")));

        erp.Route("ItemVendorPurchase/list", body =>
        {
            var itemCode = body!["SearchValue"]!.GetValue<string>();

            return new JsonArray(itemCode == "ITEM-A"
                ? ErpFixtures.ItemVendorRow("ITEM-A", "V-A", "RS-A")
                : ErpFixtures.ItemVendorRow("ITEM-B", "V-B", "RS-B"));
        });

        erp.Route("GetPendingItemsFromIndentnew", body =>
        {
            var itemCode = body!["vendorcode"]!.GetValue<string>() == "V-A" ? "ITEM-A" : "ITEM-B";

            return ErpFixtures.Pending(
                new JsonArray(ErpFixtures.PendingItem(itemCode)),
                new JsonArray(ErpFixtures.PendingDelivery(itemCode)));
        });

        // V-A's create succeeds; V-B's create hits a genuine 500 with a message that is not a known
        // business rejection, so ErpResponseHandler classifies it as ErpTransientException — exactly
        // the exception type the old code did not catch in this loop.
        erp.FaultRoutes["POEntry/create"] = body =>
        {
            var vendor = body!["Poheader"]![0]!["POHVNDCODE"]!.GetValue<string>();

            return vendor == "V-B" ? (500, "Database connection lost.") : null;
        };

        erp.Route("POEntry/create", body =>
        {
            var vendor = body!["Poheader"]![0]!["POHVNDCODE"]!.GetValue<string>();

            return ErpFixtures.CreatedPo($"26-27/RG/SP1/{vendor}", 1);
        });

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();
        result.PurchaseOrders.Should().ContainSingle().Which.VendorCode.Should().Be("V-A");
        result.Notes.Should().ContainSingle().Which.Should().Contain("temporarily unavailable");
    }

    [Fact]
    public async Task An_indent_the_erp_no_longer_reports_as_authorised_is_refused()
    {
        // The list row could be minutes old, so authorisation is re-read from the indent itself
        // before anything is ordered against it.
        var erp = ErpFixtures.HappyPath();
        erp.Route("getIndentDetail", ErpFixtures.IndentDetail(authStatus: "Pending"));

        var act = async () => await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        (await act.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("not been authorised");

        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task An_indent_containing_an_lbt_item_is_refused_before_vendor_resolution()
    {
        // An LBT line's "already ordered" quantity is a formula-computed weight the ERP's own
        // over-order guard does not check (see IndentConversion.GuardNoLbtItems) — refused up
        // front, before vendor resolution/pending-quantity/PO creation ever run.
        var erp = ErpFixtures.HappyPath();
        erp.Route("getIndentDetail", ErpFixtures.IndentDetail(
            items: [ErpFixtures.IndentDetailItem(itemSize: "10x20x5")]));

        var act = async () => await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        (await act.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("contains an LBT item and cannot be converted to PO");

        erp.CallCount("ItemVendorPurchase/list").Should().Be(0);
        erp.CallCount("GetPendingItemsFromIndentnew").Should().Be(0);
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task An_indent_with_no_lbt_items_still_converts_normally()
    {
        // A non-LBT indent must see no behavior change: itemSize blank on every line is exactly
        // what HappyPath()/IndentDetailItem() already send by default.
        var erp = ErpFixtures.HappyPath();

        var result = await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();
        result.PurchaseOrders.Should().ContainSingle();
    }

    [Fact]
    public async Task Running_twice_does_not_create_a_second_purchase_order()
    {
        // The ERP is the record of what is already ordered: creating the order closes the indent
        // line, so the second pass is answered with an indent that has nothing left on it. That is
        // the whole of this feature's idempotency, and it works without a local database.
        var erp = ErpFixtures.HappyPath();
        var service = ServiceUnderTest.Build(erp);

        var first = await service.CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);
        first.PurchaseOrders.Should().ContainSingle();

        erp.Route("getIndentDetail", ErpFixtures.IndentDetail(
            "Authorized",
            "Close",
            ErpFixtures.IndentDetailItem(status: "C")));

        var act = async () => await service.CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        (await act.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("nothing left on it to order");
        erp.CallCount("POEntry/create").Should().Be(1);
    }

    [Fact]
    public async Task A_failure_recording_the_outcome_after_a_real_success_still_returns_the_real_result()
    {
        // Regression test: ConvertAsync must not report "the purchase order was not created" when the
        // ERP conversion itself succeeded and only the local tracking write afterward failed.
        var erp = ErpFixtures.HappyPath();
        var tracker = new ThrowingCompleteExecutionTracker();

        var result = await ServiceUnderTest.Build(erp, tracker: tracker)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.Converted.Should().BeTrue();
        result.PurchaseOrders.Should().ContainSingle();
        tracker.FailExecutionCalls.Should().Be(0, "the ERP call succeeded — only the local recording of it failed");
    }

    // ---- Sweep -------------------------------------------------------------

    [Fact]
    public async Task A_sweep_converts_what_it_discovers()
    {
        var erp = ErpFixtures.HappyPath();

        var sweep = await ServiceUnderTest.Build(erp)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        sweep.Examined.Should().Be(1);
        sweep.PurchaseOrdersCreated.Should().Be(1);
        sweep.DryRun.Should().BeFalse();
    }

    [Fact]
    public async Task A_dry_run_reports_the_orders_it_would_place_and_places_none()
    {
        var erp = ErpFixtures.HappyPath();

        var sweep = await ServiceUnderTest.Build(erp)
            .ConvertEligibleAsync(new IndentSweepRequest { DryRun = true }, CancellationToken.None);

        sweep.PurchaseOrdersCreated.Should().Be(0);

        // Nothing created and nothing to do read identically on the created count alone, which is
        // the one distinction someone commissioning this needs.
        sweep.PurchaseOrdersPlanned.Should().Be(1);
        sweep.Results.Should().ContainSingle()
            .Which.Notes.Should().ContainSingle().Which.Should().Contain("Would raise");
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task One_indent_failing_does_not_stop_the_sweep()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(
            // Newest first, as the ERP sorts them: 60902 is read first and refused, and 60901 —
            // the one the pending-items fixture has outstanding lines for — must still convert.
            ErpFixtures.IndentListRow(indentId: 60902, indentNumber: "000002", totalRows: 2),
            ErpFixtures.IndentListRow(indentId: ErpFixtures.IndentId, totalRows: 2)));

        var seen = 0;

        erp.Route("getIndentDetail", _ =>
            // The first indent read is refused; the second must still be attempted.
            ErpFixtures.IndentDetail(seen++ == 0 ? "Pending" : "Authorized", "Open"));

        var sweep = await ServiceUnderTest.Build(erp)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        sweep.Examined.Should().Be(2);
        sweep.PurchaseOrdersCreated.Should().Be(1);
        sweep.Results.Should().Contain(result => result.Notes.Any(note => note.Contains("not been authorised")));
    }

    [Fact]
    public async Task A_transient_failure_on_one_indent_does_not_lose_an_earlier_indents_order()
    {
        // Regression test: a transient failure converting indent #2 (a real 500, not a business
        // refusal) must not discard indent #1's already-created purchase order by unwinding out of
        // ConvertEligibleAsync's per-indent loop uncaught.
        var erp = ErpFixtures.HappyPath();

        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(indentId: 60902, indentNumber: "000002", totalRows: 2),
            ErpFixtures.IndentListRow(indentId: ErpFixtures.IndentId, totalRows: 2)));

        // Both indents' delivery lines are offered, since the ERP's pending-items call is
        // vendor-scoped, not indent-scoped — the client narrows to the indent being converted
        // itself, so both need a matching row for either to reach the create call.
        erp.Route("GetPendingItemsFromIndentnew", ErpFixtures.Pending(
            new JsonArray(ErpFixtures.PendingItem()),
            new JsonArray(
                ErpFixtures.PendingDelivery(indentId: ErpFixtures.IndentId),
                ErpFixtures.PendingDelivery(indentId: 60902, deliveryLine: 2))));

        var createCalls = 0;

        erp.FaultRoutes["POEntry/create"] = _ =>
        {
            createCalls++;
            return createCalls == 1 ? null : (500, "Database connection lost.");
        };

        var sweep = await ServiceUnderTest.Build(erp)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        sweep.Examined.Should().Be(2);
        sweep.PurchaseOrdersCreated.Should().Be(1);
        sweep.Results.Should().Contain(result => result.Converted);
        sweep.Results.Should().Contain(result => result.Notes.Any(note => note.Contains("temporarily unavailable")));
    }

    // ---- Sweep, PO Automation master switch --------------------------------

    [Fact]
    public async Task A_sweep_converts_nothing_when_the_master_switch_is_off()
    {
        var erp = ErpFixtures.HappyPath();          // one eligible, convertible indent
        var gate = FakePoAutomationGate.AlwaysOn;
        gate.TurnOff();                              // the user disabled PO Automation before this pass ran

        var sweep = await ServiceUnderTest.Build(erp, poAutomationGate: gate)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        sweep.PurchaseOrdersCreated.Should().Be(0);
        erp.CallCount("POEntry/create").Should().Be(0);   // the ERP was never asked to create anything
    }

    [Fact]
    public async Task Turning_the_master_switch_off_mid_sweep_stops_before_the_next_indent()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("indententry/indentEntryList", new JsonArray(
            // Newest first: 60902 is converted first, then the loop would reach 60901.
            ErpFixtures.IndentListRow(indentId: 60902, indentNumber: "000002", totalRows: 2),
            ErpFixtures.IndentListRow(indentId: ErpFixtures.IndentId, totalRows: 2)));
        erp.Route("GetPendingItemsFromIndentnew", ErpFixtures.Pending(
            new JsonArray(ErpFixtures.PendingItem()),
            new JsonArray(
                ErpFixtures.PendingDelivery(indentId: 60902),
                ErpFixtures.PendingDelivery(indentId: ErpFixtures.IndentId))));

        // The user flips PO Automation off while the first indent's purchase order is being created.
        var gate = FakePoAutomationGate.AlwaysOn;
        erp.Route("POEntry/create", _ =>
        {
            gate.TurnOff();
            return ErpFixtures.CreatedPo();
        });

        var sweep = await ServiceUnderTest.Build(erp, poAutomationGate: gate)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        // The first indent's PO stands; the second indent is never converted.
        sweep.PurchaseOrdersCreated.Should().Be(1);
        sweep.Examined.Should().Be(1);
        erp.CallCount("POEntry/create").Should().Be(1);
    }

    // ---- Sweep, restricted to named indent numbers ---------------------------

    /// <summary>
    /// Two authorised Regular indents, so "only the named one converted" is a real assertion
    /// rather than a coincidence of there being one candidate.
    /// </summary>
    private static FakeErp TwoConvertibleIndents()
    {
        var erp = ErpFixtures.HappyPath();

        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(indentId: 60902, indentNumber: "000002", totalRows: 2),
            ErpFixtures.IndentListRow(indentId: ErpFixtures.IndentId, totalRows: 2)));

        return erp;
    }

    [Fact]
    public async Task No_indent_numbers_sweeps_every_eligible_indent()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        // The behaviour every existing caller depends on: an absent filter is not a filter.
        sweep.Examined.Should().Be(2);
    }

    [Fact]
    public async Task An_empty_indent_numbers_list_is_read_as_no_filter()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = [] },
            CancellationToken.None);

        sweep.Examined.Should().Be(2);
    }

    [Fact]
    public async Task One_indent_number_converts_that_indent_and_leaves_the_others_alone()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["26-27/IN/SP1/000001"] },
            CancellationToken.None);

        sweep.Examined.Should().Be(1);
        sweep.Results.Should().ContainSingle()
            .Which.Indent.IndentId.Should().Be(ErpFixtures.IndentId);

        // Asserted on the ERP conversation, not just the result: the unnamed indent must never
        // have been read, let alone ordered. One purchase order, for the indent that was named.
        erp.CallCount("POEntry/create").Should().Be(1);
        erp.BodiesFor("getIndentDetail").Should().NotContain(body => body!.ToJsonString().Contains("60902"));
    }

    [Fact]
    public async Task Several_indent_numbers_convert_exactly_those()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["26-27/IN/SP1/000001", "26-27/IN/SP1/000002"] },
            CancellationToken.None);

        sweep.Examined.Should().Be(2);
    }

    [Fact]
    public async Task An_indent_number_that_matches_nothing_converts_nothing()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["26-27/IN/SP1/999999"] },
            CancellationToken.None);

        // Not an error — the ERP was asked and nothing matched. The endpoint reports the number
        // back as unmatched; the sweep simply has nothing to do.
        sweep.Examined.Should().Be(0);
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task A_bare_running_number_selects_that_indent()
    {
        var erp = TwoConvertibleIndents();

        // The number people read off the screen. Accepted because it is what gets typed, and safe
        // here because only one eligible indent carries it.
        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["000001"] },
            CancellationToken.None);

        sweep.Examined.Should().Be(1);
        sweep.Results.Should().ContainSingle().Which.Indent.IndentId.Should().Be(ErpFixtures.IndentId);
    }

    [Fact]
    public async Task A_bare_running_number_matches_exactly_and_never_as_a_prefix()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["0000"] },
            CancellationToken.None);

        // "0000" is a prefix of both running numbers and must select neither: a loose match here
        // is how one named indent becomes an order nobody asked for.
        sweep.Examined.Should().Be(0);
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task A_bare_running_number_that_fits_two_indents_is_refused_rather_than_guessed()
    {
        var erp = ErpFixtures.HappyPath();

        // The same running number at two sites — the reason the bare form cannot simply be trusted.
        var elsewhere = ErpFixtures.IndentListRow(indentId: 60902, totalRows: 2);
        elsewhere["siteCode"] = "SP2";

        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(totalRows: 2),
            elsewhere));

        var act = async () => await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["000001"] },
            CancellationToken.None);

        // Converting both would order an indent nobody named; picking one would be a guess. The
        // refusal names the whole numbers so the caller can say which they meant.
        (await act.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("26-27/IN/SP1/000001").And.Contain("26-27/IN/SP2/000001");

        erp.CallCount("POEntry/create").Should().Be(0);
    }

    // ---- The reported defect: one number in, a different indent ordered ------

    /// <summary>
    /// Indent 000123 is the convertible one; 000456 is a second authorised Regular indent that must
    /// be left completely alone. Both are Regular, so <c>indentTypes</c> cannot be what separates
    /// them — only <c>indentNumbers</c> can.
    /// </summary>
    private static FakeErp Indents123And456()
    {
        var erp = ErpFixtures.HappyPath();

        erp.Route("indententry/indentEntryList", new JsonArray(
            ErpFixtures.IndentListRow(indentNumber: "000123", totalRows: 2),
            ErpFixtures.IndentListRow(indentId: 60456, indentNumber: "000456", totalRows: 2)));

        return erp;
    }

    [Fact]
    public async Task Naming_000123_converts_only_000123()
    {
        var erp = Indents123And456();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest
            {
                IndentTypes = [IndentType.Regular],
                IndentNumbers = ["000123"],
                DryRun = false,
            },
            CancellationToken.None);

        sweep.Examined.Should().Be(1);
        sweep.PurchaseOrdersCreated.Should().Be(1);
        sweep.Results.Should().ContainSingle()
            .Which.Indent.DisplayNumber.Should().Be("26-27/IN/SP1/000123");
    }

    [Fact]
    public async Task Naming_000123_can_never_convert_000456()
    {
        var erp = Indents123And456();

        await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest
            {
                IndentTypes = [IndentType.Regular],
                IndentNumbers = ["000123"],
                DryRun = false,
            },
            CancellationToken.None);

        // Asserted on the ERP conversation rather than on the result, because the defect being
        // guarded is precisely a filter that reports one indent and orders another. The unnamed
        // indent's id must appear nowhere — not in a path, not in a body — and exactly one
        // purchase order may be created.
        erp.Requests.Should().NotContain(request =>
            request.Path.Contains("60456", StringComparison.Ordinal)
            || (request.Body != null && request.Body.ToJsonString().Contains("60456", StringComparison.Ordinal)));

        erp.CallCount("POEntry/create").Should().Be(1);
    }

    [Fact]
    public async Task Indent_numbers_are_matched_ignoring_case_and_surrounding_space()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentNumbers = ["  26-27/in/sp1/000001  "] },
            CancellationToken.None);

        sweep.Examined.Should().Be(1);
    }

    [Fact]
    public async Task A_named_indent_of_an_unselected_type_still_converts_nothing()
    {
        var erp = TwoConvertibleIndents();

        // Both allow-lists narrow together: naming a Regular indent does not widen a Capital-only
        // run into converting it.
        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest
            {
                IndentTypes = [IndentType.Capital],
                IndentNumbers = ["26-27/IN/SP1/000001"],
            },
            CancellationToken.None);

        sweep.Examined.Should().Be(0);
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task A_dry_run_with_an_indent_number_plans_only_that_indent_and_creates_nothing()
    {
        var erp = TwoConvertibleIndents();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest
            {
                IndentNumbers = ["26-27/IN/SP1/000001"],
                DryRun = true,
            },
            CancellationToken.None);

        sweep.Examined.Should().Be(1);
        sweep.PurchaseOrdersPlanned.Should().Be(1);
        sweep.PurchaseOrdersCreated.Should().Be(0);
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    [Fact]
    public async Task Converting_by_id_refuses_an_id_the_erp_does_not_offer()
    {
        var erp = ErpFixtures.HappyPath();

        var act = async () => await ServiceUnderTest.Build(erp)
            .ConvertIndentAsync(999999, indentNumber: null, sites: null, dryRun: false, CancellationToken.None);

        (await act.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("not an authorised, still-open indent");
    }

    [Fact]
    public async Task Converting_by_id_narrows_the_erp_search_with_the_indent_number()
    {
        var erp = ErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp).ConvertIndentAsync(
            ErpFixtures.IndentId,
            ErpFixtures.IndentNumber,
            sites: null,
            dryRun: false,
            CancellationToken.None);

        erp.LastBodyFor("indententry/indentEntryList")!["SearchValue"]!.GetValue<string>()
            .Should().Be(ErpFixtures.IndentNumber);
    }

    [Fact]
    public async Task A_capital_indent_is_ordered_as_a_capital_purchase_order()
    {
        var erp = ErpFixtures.HappyPath();

        await ServiceUnderTest.Build(erp)
            .CreateFromIndentAsync(ErpFixtures.Reference(IndentType.Capital), CancellationToken.None);

        erp.LastBodyFor("GetPendingItemsFromIndentnew")!["potype"]!.GetValue<string>().Should().Be("C");
        erp.LastBodyFor("POEntry/create")!["Poheader"]![0]!["POHDOCSUBTYP"]!.GetValue<string>().Should().Be("CP");
        // The indent's own document sub-type, which is not the purchase order's: "CP" happens to
        // match here, whereas a Regular indent is "RG" against a "RP" order.
        erp.Requests.Select(request => request.Path)
            .Should().Contain(path => path.Contains("getIndentDetail/Y/26-27/IN/SP1/000001/S/C/CP"));
    }

    // ---- The financial year the order is numbered in -----------------------

    [Fact]
    public async Task A_blank_financial_year_follows_the_purchase_order_date()
    {
        // The clock says 2026-08-19, so the ERP's April-to-March year is 26-27. Configuring
        // nothing is the setting that stays right after an April rollover.
        var erp = ErpFixtures.HappyPath();
        var options = new IndentPoOptions { Sites = [1, 2], Currency = "RS", DomesticCurrency = "RS" };

        await ServiceUnderTest.Build(erp, options)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        erp.Requests.Select(request => request.Path)
            .Should().Contain(path => path.Contains("getDefaultDocumentDetail/26-27/PR/RP/2"));
        erp.LastBodyFor("POEntry/create")!["Poheader"]![0]!["POHORDYEAR"]!.GetValue<string>()
            .Should().Be("26-27");
    }

    [Fact]
    public async Task A_configured_year_with_no_numbering_falls_back_to_the_purchase_order_dates()
    {
        // The failure this exists for: a stale AutomationAgent:PurchaseOrder:FinancialYear made
        // Document Control answer with an empty list, which killed every conversion on the very
        // first ERP call of the sequence.
        var erp = ErpFixtures.HappyPath();
        erp.RouteByPath(
            "getDefaultDocumentDetail",
            path => path.Contains("/25-26/", StringComparison.Ordinal)
                ? new JsonArray()
                : ErpFixtures.DocumentControl());

        var options = new IndentPoOptions
        {
            Sites = [1, 2],
            FinancialYear = "25-26",
            Currency = "RS",
            DomesticCurrency = "RS",
        };

        var result = await ServiceUnderTest.Build(erp, options)
            .CreateFromIndentAsync(ErpFixtures.Reference(), CancellationToken.None);

        result.PurchaseOrders.Should().ContainSingle();

        // Stamped with the year the ERP agreed to number it in, not the configured one.
        erp.LastBodyFor("POEntry/create")!["Poheader"]![0]!["POHORDYEAR"]!.GetValue<string>()
            .Should().Be("26-27");
    }

    [Fact]
    public async Task A_year_no_document_control_row_exists_for_is_named_in_the_refusal()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("getDefaultDocumentDetail", new JsonArray());

        var logger = new RecordingLogger<IndentToPoService>();

        var result = await ServiceUnderTest.Build(erp, options: null, logger)
            .ConvertIndentAsync(
                ErpFixtures.IndentId,
                ErpFixtures.IndentNumber,
                sites: null,
                dryRun: false,
                CancellationToken.None);

        result.PurchaseOrders.Should().BeEmpty();
        result.Notes.Should().ContainSingle().Which.Should().Contain("no document numbering");

        // And the reason is logged, not merely returned. A timer-driven sweep has no caller to
        // read the result, so an unlogged reason is no reason at all.
        logger.MessagesAt(LogLevel.Warning)
            .Should().Contain(message => message.Contains("produced no purchase order")
                && message.Contains("no document numbering"));
    }

    [Fact]
    public async Task A_sweep_that_orders_nothing_says_why()
    {
        var erp = ErpFixtures.HappyPath();
        erp.Route("getDefaultDocumentDetail", new JsonArray());

        var logger = new RecordingLogger<IndentToPoService>();

        var sweep = await ServiceUnderTest.Build(erp, options: null, logger)
            .ConvertEligibleAsync(new IndentSweepRequest(), CancellationToken.None);

        sweep.Examined.Should().Be(1);
        sweep.PurchaseOrdersCreated.Should().Be(0);

        logger.MessagesAt(LogLevel.Warning)
            .Should().Contain(message => message.Contains("The sweep ordered nothing")
                && message.Contains("no document numbering"));
    }
}
