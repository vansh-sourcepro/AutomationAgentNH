using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.UnitTests.Erp;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// The indent-type allow-list, at the level where it decides what the ERP is asked.
/// </summary>
/// <remarks>
/// The assertion that matters throughout is not "the results were filtered" but "the ERP was never
/// asked about the unselected type". Filtering after the fact would pass a results-shaped test and
/// still read, price and very nearly order documents nobody selected.
/// </remarks>
public class IndentTypeSelectionTests
{
    /// <summary>The ERP list behind each type, so a test can assert one was never called.</summary>
    private const string MaterialList = "indententry/indentEntryList";
    private const string ServiceList = "ServiceIndentCont/indentEntryList";

    // ---- Normalising -------------------------------------------------------

    [Fact]
    public void No_selection_means_every_type()
    {
        IndentTypeSelection.Normalise(null).Should().Equal(
            IndentType.Regular, IndentType.Capital, IndentType.Service);

        IndentTypeSelection.Normalise([]).Should().Equal(
            IndentType.Regular, IndentType.Capital, IndentType.Service);
    }

    [Fact]
    public void A_type_named_twice_is_swept_once()
    {
        IndentTypeSelection
            .Normalise([IndentType.Service, IndentType.Regular, IndentType.Service])
            .Should()
            .Equal(IndentType.Regular, IndentType.Service);
    }

    [Fact]
    public void The_order_is_canonical_not_the_order_it_was_typed_in()
    {
        // Two callers who asked for the same set get the same sweep, in the same order.
        IndentTypeSelection.Normalise([IndentType.Service, IndentType.Capital])
            .Should()
            .Equal(
                IndentTypeSelection.Normalise([IndentType.Capital, IndentType.Service]));
    }

    // ---- Every combination, at the ERP boundary ----------------------------

    public static TheoryData<IndentType[]> EveryCombination() =>
    [
        [IndentType.Regular],
        [IndentType.Capital],
        [IndentType.Service],
        [IndentType.Regular, IndentType.Capital],
        [IndentType.Regular, IndentType.Service],
        [IndentType.Capital, IndentType.Service],
        [IndentType.Regular, IndentType.Capital, IndentType.Service],
    ];

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task Discovery_asks_only_about_the_selected_types(IndentType[] selected)
    {
        var erp = BothFamilies();

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest { IndentTypes = selected },
            CancellationToken.None);

        var wantsMaterial = selected.Any(IndentPoProfile.MaterialIndentTypes.Contains);
        var wantsService = selected.Contains(IndentType.Service);

        // The unselected family's list is not called at all — not called and discarded, not called.
        erp.CallCount(MaterialList).Should().Be(wantsMaterial ? 1 : 0);
        erp.CallCount(ServiceList).Should().Be(wantsService ? 1 : 0);

        eligible.Select(candidate => candidate.Indent.IndentType)
            .Should()
            .OnlyContain(type => selected.Contains(type));
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task A_sweep_creates_purchase_orders_only_for_the_selected_types(IndentType[] selected)
    {
        var erp = BothFamilies();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentTypes = selected },
            CancellationToken.None);

        sweep.Results.Select(result => result.Indent.IndentType)
            .Should()
            .OnlyContain(type => selected.Contains(type));

        // The two create endpoints are the last word: one order per selected type, and nothing for
        // a type that was not selected.
        erp.CallCount("POEntry/create").Should().Be(MaterialOrdersFor(selected));

        erp.CallCount("createServicePOEntry")
            .Should()
            .Be(selected.Contains(IndentType.Service) ? 1 : 0);

        sweep.PurchaseOrdersCreated.Should().Be(erp.CallCount("POEntry/create") + erp.CallCount("createServicePOEntry"));
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task A_dry_run_plans_only_the_selected_types_and_creates_nothing(IndentType[] selected)
    {
        var erp = BothFamilies();

        var sweep = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentTypes = selected, DryRun = true },
            CancellationToken.None);

        sweep.Results.Select(result => result.Indent.IndentType)
            .Should()
            .OnlyContain(type => selected.Contains(type));

        erp.CallCount("POEntry/create").Should().Be(0);
        erp.CallCount("createServicePOEntry").Should().Be(0);
        sweep.PurchaseOrdersPlanned.Should().Be(sweep.Results.Count);
    }

    [Fact]
    public async Task A_material_backlog_does_not_starve_the_service_indents()
    {
        // The material list is asked first, so a site with more outstanding material indents than
        // the cap would fill it every pass and the service indents behind it would never be
        // reached — not "next sweep", never. Selecting both families has to mean both are looked
        // at, whatever the material backlog looks like.
        var erp = BothFamilies();

        var eligible = await ServiceUnderTest.Build(erp).FindEligibleAsync(
            new IndentDiscoveryRequest
            {
                IndentTypes = [IndentType.Regular, IndentType.Capital, IndentType.Service],
                // A cap the two material indents could fill on their own. Split first, it buys the
                // material half one slot and leaves the other for the service half; handed to the
                // material half first, it would buy two material indents and nothing else.
                MaxResults = 2,
            },
            CancellationToken.None);

        erp.CallCount(ServiceList)
            .Should()
            .Be(1, "the service list is asked even when the material half could have filled the cap");

        eligible.Select(candidate => candidate.Indent.IndentType)
            .Should()
            .Contain(IndentType.Service)
            .And.HaveCount(2);
    }

    // ---- The guarantee, independent of discovery ---------------------------

    [Theory]
    [InlineData(IndentType.Regular)]
    [InlineData(IndentType.Service)]
    public async Task Converting_a_type_outside_the_selection_is_refused_at_the_last_step(IndentType excluded)
    {
        // Discovery would never have offered it. The refusal is here anyway, because a narrowed
        // search is an optimisation and this is the guarantee — no future caller can reach the
        // conversion around the filter.
        var erp = BothFamilies();
        var allowed = IndentPoProfile.AllIndentTypes.Where(type => type != excluded).ToList();

        var id = excluded == IndentType.Service ? ServiceErpFixtures.IndentId : ErpFixtures.IndentId;

        var act = () => ServiceUnderTest.Build(erp).ConvertIndentAsync(
            id,
            indentNumber: null,
            sites: null,
            indentTypes: allowed,
            dryRun: false,
            CancellationToken.None);

        await act.Should().ThrowAsync<ErpBusinessException>();

        erp.CallCount("POEntry/create").Should().Be(0);
        erp.CallCount("createServicePOEntry").Should().Be(0);
    }

    [Fact]
    public async Task Naming_one_indent_still_honours_the_selection()
    {
        var erp = BothFamilies();

        var result = await ServiceUnderTest.Build(erp).ConvertIndentAsync(
            ServiceErpFixtures.IndentId,
            indentNumber: null,
            sites: null,
            indentTypes: [IndentType.Service],
            dryRun: false,
            CancellationToken.None);

        result.Indent.IndentType.Should().Be(IndentType.Service);
        result.Converted.Should().BeTrue();
        erp.CallCount("POEntry/create").Should().Be(0);
    }

    // ---- Running it again --------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task A_second_sweep_over_converted_indents_creates_no_duplicate(IndentType[] selected)
    {
        // What the ERP does after the first pass: the indent lines it ordered are closed, so the
        // material pending-items call and the service pending-lines call both come back empty and
        // the indents themselves are no longer open. Idempotency is the ERP's, and this is the
        // shape of it.
        var erp = BothFamilies();
        var service = ServiceUnderTest.Build(erp);

        await service.ConvertEligibleAsync(
            new IndentSweepRequest { IndentTypes = selected },
            CancellationToken.None);

        var createdFirstPass = erp.CallCount("POEntry/create") + erp.CallCount("createServicePOEntry");

        ExhaustEveryIndent(erp);

        var second = await ServiceUnderTest.Build(erp).ConvertEligibleAsync(
            new IndentSweepRequest { IndentTypes = selected },
            CancellationToken.None);

        second.PurchaseOrdersCreated.Should().Be(0);
        (erp.CallCount("POEntry/create") + erp.CallCount("createServicePOEntry"))
            .Should()
            .Be(createdFirstPass, "the ERP has nothing outstanding left to order");
    }

    /// <summary>The Capital indent, alongside <see cref="ErpFixtures.IndentId"/>'s Regular one.</summary>
    private const long CapitalIndentId = 60902;

    /// <summary>
    /// A fake ERP holding exactly one convertible indent of each of the three types: a Regular and
    /// a Capital one on the material list, and a service one on the service list.
    /// </summary>
    /// <remarks>
    /// One each is what makes the counts in these tests readable — every selected type should
    /// produce exactly one order, so "created two orders" and "swept two types" are the same
    /// statement and a leak shows up as an off-by-one rather than as a shrug.
    /// </remarks>
    private static FakeErp BothFamilies()
    {
        var erp = ServiceErpFixtures.HappyPath();

        // The material list is one ERP endpoint serving both material types; the type code on each
        // row is what discovery filters on, exactly as it does against the live ERP.
        erp.Route(MaterialList, new JsonArray(
            ErpFixtures.IndentListRow(indentTypeCode: "R", totalRows: 2),
            ErpFixtures.IndentListRow(
                indentId: CapitalIndentId,
                indentNumber: "000002",
                indentTypeCode: "C",
                totalRows: 2)));

        erp.Route("getIndentDetail", ErpFixtures.IndentDetail());
        erp.Route("ItemVendorPurchase/list", new JsonArray(ErpFixtures.ItemVendorRow()));
        erp.Route("GetRateStructureLOVforItem", new JsonArray(
            new JsonObject { ["rateStructureCode"] = ErpFixtures.RateStructure, ["isDefault"] = true }));
        erp.Route("GetWarCode4POEntry", ErpFixtures.Warehouses());
        erp.Route("GetItemVndPUOMLOVForPO", new JsonArray());

        // The pending-items call is vendor-scoped, not indent-scoped — the ERP hands back every
        // outstanding line for the vendor and the agent narrows on pidindid. So both indents'
        // lines are here, and each conversion should take only its own.
        erp.Route(
            "GetPendingItemsFromIndentnew",
            ErpFixtures.Pending(
                new JsonArray(ErpFixtures.PendingItem()),
                new JsonArray(
                    ErpFixtures.PendingDelivery(indentId: ErpFixtures.IndentId),
                    ErpFixtures.PendingDelivery(indentId: CapitalIndentId, deliveryLine: 2))));

        erp.Route("POEntry/create", ErpFixtures.CreatedPo());

        return erp;
    }

    /// <summary>How many purchase orders a selection should produce against <see cref="BothFamilies"/>.</summary>
    private static int MaterialOrdersFor(IndentType[] selected) =>
        selected.Count(IndentPoProfile.MaterialIndentTypes.Contains);

    /// <summary>
    /// Leaves the fake in the state the ERP is in after a successful conversion: nothing
    /// outstanding on either family, and both indents closed.
    /// </summary>
    private static void ExhaustEveryIndent(FakeErp erp)
    {
        erp.Route(
            "GetPendingItemsFromIndentnew",
            ErpFixtures.Pending(new JsonArray(), new JsonArray()));
        erp.Route("getIndentDetail", ErpFixtures.IndentDetail(docStatus: "Close"));
        erp.Route("getSerIndentDetail", ServiceErpFixtures.IndentDetail("Closed"));
        erp.Route("getItemDetailForSerPO", ServiceErpFixtures.Pending(new JsonArray(), new JsonArray()));
    }
}
