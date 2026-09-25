using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using NewHorizon.Automation.UnitTests.Erp;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class IssueToShopFloorServiceTests
{
    private const string CreatePath = "createIssuetoShopFloor";

    [Fact]
    public async Task Creates_one_issue_split_across_warehouses_when_every_check_passes()
    {
        var erp = SjoIssueErp.Ready();

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", dryRun: false, CancellationToken.None);

        result.Created.Should().BeTrue();
        result.Reason.Should().BeNull();
        result.IssueNumber.Should().Be("26-27/IS/NF1/000045");
        result.Checks.Should().OnlyContain(check => check.Passed);
        result.Lines.Select(line => (line.WarehouseCode, line.Quantity)).Should().Equal(("WH1", 12m), ("WH2", 10m));

        erp.CallCount(CreatePath).Should().Be(1);
    }

    [Fact]
    public async Task The_create_payload_is_the_screens_sjo_wise_save()
    {
        var erp = SjoIssueErp.Ready();

        await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", dryRun: false, CancellationToken.None);

        var body = erp.LastBodyFor(CreatePath)!;
        body["issueType"]!.GetValue<string>().Should().Be("S");
        body["sjowoType"]!.GetValue<string>().Should().Be("S");
        body["whType"]!.GetValue<string>().Should().Be("M");
        body["docType"]!.GetValue<string>().Should().Be("IS");
        body["docSubType"]!.GetValue<string>().Should().Be("NI");
        body["docId"]!.GetValue<long>().Should().Be(5341);
        body["issueNo"]!.GetValue<string>().Should().BeEmpty();
        body["issueTo"]!.GetValue<string>().Should().Be("SHOP");
        body["issueBy"]!.GetValue<string>().Should().Be("STORES");

        var lines = body["itemDetails"]!.AsArray().Select(node => node!.AsObject()).ToList();
        lines.Select(line => (line["stockId"]!.GetValue<long>(), line["issueQuantity"]!.GetValue<decimal>()))
            .Should().Equal((1L, 12m), (2L, 10m));
        lines.Should().OnlyContain(line => line["woId"]!.GetValue<long>() == 900 && line["randomNumber"]!.GetValue<int>() == 11);
    }

    [Fact]
    public async Task A_dry_run_plans_the_lines_and_creates_nothing()
    {
        var erp = SjoIssueErp.Ready();

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", dryRun: true, CancellationToken.None);

        result.Ready.Should().BeTrue();
        result.Created.Should().BeFalse();
        result.Lines.Should().HaveCount(2);
        erp.CallCount(CreatePath).Should().Be(0);
    }

    [Fact]
    public async Task An_unauthorised_sjo_is_refused_before_anything_else_is_asked()
    {
        var erp = SjoIssueErp.Ready().WithSjoStatus("P", "Pending for authorisation");

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", dryRun: false, CancellationToken.None);

        ShouldRefuse(result, erp, "Authorised", "not authorised");
        erp.CallCount("GetSJOItemCode4Allocation").Should().Be(0);
    }

    [Fact]
    public async Task No_cbom_is_refused()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("GetSJOItemCode4Allocation", new JsonArray());

        ShouldRefuse(await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None), erp, "Cbom", "CBOM");
    }

    [Fact]
    public async Task No_work_order_is_refused()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("GetWODtl4AllocationWoCreation", new JsonArray());

        ShouldRefuse(await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None), erp, "WorkOrder", "No Work Order");
    }

    [Fact]
    public async Task Only_closed_work_orders_are_refused()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("GetWODtl4AllocationWoCreation", new JsonArray(new JsonObject { ["wonumber"] = "26-27/WO/NF1/000010", ["wostatus"] = "CLOSE" }));

        ShouldRefuse(await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None), erp, "WorkOrder", "no open Work Order");
    }

    [Fact]
    public async Task No_allocated_warehouse_is_refused_as_allocation_not_done()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("getWarehouseCodeForIssue", new JsonArray());

        ShouldRefuse(await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None), erp, "WorkAllocation", "Work Allocation");
    }

    [Fact]
    public async Task The_erps_own_eligibility_check_has_the_last_word()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("getIssueOfItmDocNoLOV", new JsonArray());

        ShouldRefuse(await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None), erp, "ErpEligibility", "does not offer");
    }

    [Fact]
    public async Task A_shortage_on_any_item_refuses_the_whole_issue_and_lists_it()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("getItemCombinations4IssueEntry", new JsonArray(StockRow(1, 10, "WH1", 12), StockRow(2, 20, "WH2", 3)));

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        ShouldRefuse(result, erp, "Stock", "not have enough stock");
        result.Shortages.Should().ContainSingle().Which.Should().Be(new IssueShortage("RM-100", 11, 22m, 15m));
        result.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task A_site_that_does_not_auto_number_issues_is_refused()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("getDefaultDocumentDetail", new JsonArray(DocumentControl(autoNumber: false)));

        ShouldRefuse(await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None), erp, "IssueNumbering", "auto-numbered");
    }

    [Fact]
    public async Task A_bare_number_that_fits_two_sjos_is_refused_with_both()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("sjoentryList", new JsonArray(IssueToShopFloorServiceTests.SjoRow(5341, "26-27"), SjoRow(4001, "25-26")));

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "123", false, CancellationToken.None);

        ShouldRefuse(result, erp, "SjoFound", "25-26/SJ/NF1/000123");
    }

    [Fact]
    public async Task Missing_issue_to_or_issue_by_is_refused_naming_the_setting()
    {
        var erp = SjoIssueErp.Ready();
        var options = Options(issueTo: string.Empty);

        var result = await Build(erp, options).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        ShouldRefuse(result, erp, "Configuration", "IssueTo");
        erp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_erp_refusal_on_create_surfaces_as_an_erp_error_not_a_success()
    {
        var erp = SjoIssueErp.Ready();
        erp.Refusals[CreatePath] = "Issue quantity is more than pending quantity.";

        var act = () => Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        await act.Should().ThrowAsync<NewHorizon.Automation.Application.Erp.ErpBusinessException>();
    }

    [Fact]
    public async Task The_sjo_list_is_paged_never_asked_for_every_row()
    {
        // pageSize 0 is the ERP list's "every match" mode, and CSP_XSJO_List_EM's SQL for it is broken
        // (the ERP answers 500). The flow must page instead.
        var erp = SjoIssueErp.Ready();

        await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000135", true, CancellationToken.None);

        var request = erp.BodiesFor("sjoentryList").First();
        request["pageSize"]!.GetValue<int>().Should().BePositive();
        request["pageNumber"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task An_sjo_on_the_second_page_of_candidates_is_found()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("sjoentryList", body =>
        {
            var page = body!["pageNumber"]!.GetValue<int>();
            var rows = new JsonArray();
            var count = page == 1 ? 100 : 50;
            for (var i = 0; i < count; i++)
            {
                // Other SJOs the LIKE '%000123%' search also returns, e.g. 26-27/SJ/NF1/0001230.
                var row = SjoRow(10_000 + (page * 1000) + i, "26-27");
                row["number"] = "0001230";
                row["totalRows"] = 150;
                rows.Add(row);
            }

            if (page == 2)
            {
                var target = SjoRow(5341, "26-27");
                target["totalRows"] = 150;
                rows[0] = target;
            }

            return rows;
        });

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", true, CancellationToken.None);

        result.Ready.Should().BeTrue(result.Reason);
        result.Checks.First().Detail.Should().Contain("id 5341");
        erp.CallCount("sjoentryList").Should().Be(2);
    }

    [Fact]
    public async Task An_empty_first_page_is_not_found_without_asking_for_more()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("sjoentryList", new JsonArray());

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", true, CancellationToken.None);

        ShouldRefuse(result, erp, "SjoFound", "No SJO");
        erp.CallCount("sjoentryList").Should().Be(1);
    }

    [Fact]
    public async Task An_inward_tracked_item_takes_the_oldest_inward_first_when_inward_wise_allocation_is_off()
    {
        // The screen's Fill would skip this item; the agent picks the inwards itself, oldest first.
        var erp = InwardTrackedErp(policy: "N");

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        result.Created.Should().BeTrue(result.Reason);
        result.Lines.Select(line => (line.InwardNo, line.StockId, line.Quantity))
            .Should().Equal(("INW05022024013943I1", 9119L, 5m), ("INW03022025063350I1", 27740L, 7m));
        result.Checks.Should().Contain(check => check.Name == "PendingItems" && check.Detail.Contains("oldest inward first"));

        erp.LastBodyFor(CreatePath)!["itemDetails"]!.AsArray()
            .Select(line => line!["stockId"]!.GetValue<long>())
            .Should().Equal(9119, 27740);
    }

    [Fact]
    public async Task With_inward_wise_allocation_on_the_erps_own_order_is_kept()
    {
        var erp = InwardTrackedErp(policy: "Y");

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", true, CancellationToken.None);

        result.Lines.Select(line => line.StockId).Should().Equal(27740, 9119);
    }

    [Fact]
    public async Task Inwards_that_together_fall_short_still_refuse_the_whole_issue()
    {
        var erp = InwardTrackedErp(policy: "N", required: 20);

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        ShouldRefuse(result, erp, "Stock", "not have enough stock");
        result.Shortages.Should().ContainSingle().Which.Available.Should().Be(12m);
    }

    /// <summary>
    /// An SJO whose one line is inward-tracked: 12 needed, the ERP listing a 2025 inward (7) before a
    /// 2024 one (5) — its text order, not its date order.
    /// </summary>
    private static FakeErp InwardTrackedErp(string policy, decimal required = 12)
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("getListOfItems4IssueEntry", new JsonArray(new JsonObject
        {
            ["itemCode"] = "BATTERY7854",
            ["sjoId"] = 5341,
            ["woId"] = 900,
            ["randomNumber"] = 2,
            ["requiredQuantity"] = required.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pendingQuantity"] = required,
            ["inwardRequired"] = "True",
            ["barcodereqflag"] = false,
            ["remarks"] = string.Empty,
        }));
        erp.Route("getItemCombinations4IssueEntry", new JsonArray(
            InwardRow(27740, "INW03022025063350I1", 7),
            InwardRow(9119, "INW05022024013943I1", 5)));
        erp.Route("mrpPolicy/getDetail", new JsonObject { ["useInwardNoWiseAllocation"] = policy });
        return erp;
    }

    private static JsonObject InwardRow(long stockId, string inwardNo, decimal quantity)
    {
        var row = StockRow(stockId, 727, "ABCD", quantity);
        row["inwardNo"] = inwardNo;
        return row;
    }

    [Fact]
    public async Task Authorises_with_today_inside_the_finance_period()
    {
        var erp = SjoIssueErp.Ready();

        await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        var body = erp.LastBodyFor(CreatePath)!;
        body["authorizationDate"]!.GetValue<string>().Should().Be(body["issueDate"]!.GetValue<string>());
    }

    [Fact]
    public async Task Authorises_with_the_period_end_when_today_is_past_it()
    {
        // The screen's rule: a closed-out period still takes the issue, dated its last day.
        var erp = SjoIssueErp.Ready();
        erp.Route("getCommomSystemConfig", FinancePeriod(siteId: 1, "2020-04-01T00:00:00", "2020-04-30T00:00:00"));

        var result = await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        erp.LastBodyFor(CreatePath)!["authorizationDate"]!.GetValue<string>().Should().Be("2020-04-30");
        erp.LastBodyFor(CreatePath)!["issueDate"]!.GetValue<string>().Should().NotBe("2020-04-30", "only the authorisation date moves");
        result.Checks.Should().Contain(check => check.Name == "AuthorisationDate" && check.Detail.Contains("outside"));
    }

    [Fact]
    public async Task Authorises_with_today_when_the_site_has_no_finance_period()
    {
        var erp = SjoIssueErp.Ready();
        erp.Route("getCommomSystemConfig", FinancePeriod(siteId: 99, "2020-04-01T00:00:00", "2020-04-30T00:00:00"));

        await Build(erp).ConvertAsync(IssueSource.Sjo, "26-27/SJ/NF1/000123", false, CancellationToken.None);

        var body = erp.LastBodyFor(CreatePath)!;
        body["authorizationDate"]!.GetValue<string>().Should().Be(body["issueDate"]!.GetValue<string>());
    }

    internal static JsonObject FinancePeriod(int siteId, string start, string end) => new()
    {
        ["financePeriodSetting"] = new JsonArray(new JsonObject
        {
            ["siteId"] = siteId,
            ["periodSDt"] = start,
            ["periodEDt"] = end,
            ["currentPeriod"] = 1,
        }),
    };

    private static void ShouldRefuse(IssueToShopFloorResult result, FakeErp erp, string failedCheck, string reasonFragment)
    {
        result.Created.Should().BeFalse();
        result.Ready.Should().BeFalse();
        result.Reason.Should().Contain(reasonFragment);
        result.Checks.Should().ContainSingle(check => !check.Passed).Which.Name.Should().Be(failedCheck);
        erp.CallCount(CreatePath).Should().Be(0, "nothing may be created when a check fails");
    }

    private static IssueToShopFloorService Build(FakeErp erp, IssueToShopFloorOptions? options = null) =>
        new(
            new SingleClientFactory(erp),
            new StubTokenProvider(),
            Microsoft.Extensions.Options.Options.Create(new ErpEndpointOptions()),
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            new FixedClock(),
            NullLogger<IssueToShopFloorService>.Instance);

    private static IssueToShopFloorOptions Options(string issueTo = "SHOP") => new()
    {
        CompanyId = 1,
        CompanyCode = "C01",
        LocationId = 1,
        Sites = [1],
        IssueTo = issueTo,
        IssueBy = "STORES",
    };

    internal static JsonObject SjoRow(long id, string year, string status = "A", string statusDescription = "Authorized") => new()
    {
        ["sjoEntryId"] = id,
        ["entryYear"] = year,
        ["groupCode"] = "SJ",
        ["siteId"] = 1,
        ["siteCode"] = "NF1",
        ["number"] = "000123",
        ["itemCode"] = "FG-1",
        ["status"] = status,
        ["statusDescription"] = statusDescription,
    };

    internal static JsonObject DocumentControl(bool autoNumber = true) => new()
    {
        ["groupCode"] = "IS",
        ["locationId"] = 1,
        ["locationCode"] = "NF1",
        ["isDefault"] = "Y",
        ["isLocationRequired"] = false,
        ["isAutoNumberGenerated"] = autoNumber,
        ["isAutorisationRequired"] = true,
    };

    internal static JsonObject StockRow(long stockId, long lineNo, string warehouse, decimal quantity) => new()
    {
        ["stockId"] = stockId,
        ["lineNo"] = lineNo,
        ["wareHouseCode"] = warehouse,
        ["quantity"] = quantity,
    };
}

/// <summary>An ERP where SJO 26-27/SJ/NF1/000123 is ready to issue: 22 of RM-100, 12 in WH1 and 200 in WH2.</summary>
internal static class SjoIssueErp
{
    public static FakeErp Ready() => new FakeErp()
        .Route("sjoentryList", new JsonArray(IssueToShopFloorServiceTests.SjoRow(5341, "26-27")))
        .Route("GetSJOItemCode4Allocation", new JsonArray(new JsonObject { ["itemCode"] = "FG-1", ["randomno"] = 7 }))
        .Route("GetWODtl4AllocationWoCreation", new JsonArray(new JsonObject { ["wonumber"] = "26-27/WO/NF1/000010", ["wostatus"] = "OPEN" }))
        .Route("getDefaultDocumentDetail", new JsonArray(IssueToShopFloorServiceTests.DocumentControl()))
        .Route("getWarehouseCodeForIssue", new JsonArray(
            new JsonObject { ["warehouseId"] = 81, ["warehouseCode"] = "WH1" },
            new JsonObject { ["warehouseId"] = 86, ["warehouseCode"] = "WH2" }))
        .Route("getIssueOfItmDocNoLOV", new JsonArray(new JsonObject { ["id"] = 5341, ["number"] = "000123" }))
        .Route("getListOfItems4IssueEntry", new JsonArray(new JsonObject
        {
            ["itemCode"] = "RM-100",
            ["sjoId"] = 5341,
            ["woId"] = 900,
            ["randomNumber"] = 11,
            ["requiredQuantity"] = "22",
            ["pendingQuantity"] = 22,
            ["inwardRequired"] = "False",
            ["barcodereqflag"] = false,
            ["remarks"] = string.Empty,
        }))
        .Route("mrpPolicy/getDetail", new JsonObject { ["useInwardNoWiseAllocation"] = "N" })
        .Route("getCommomSystemConfig", IssueToShopFloorServiceTests.FinancePeriod(siteId: 1, "2000-04-01T00:00:00", "2099-03-31T00:00:00"))
        .Route("getItemCombinations4IssueEntry", new JsonArray(IssueToShopFloorServiceTests.StockRow(1, 10, "WH1", 12), IssueToShopFloorServiceTests.StockRow(2, 20, "WH2", 200)))
        .RouteWithMessage("createIssuetoShopFloor", 1, "Issue_details_successfully_saved_Issue_document_number_ITSFKey#26-27/IS/NF1/000045");

    public static FakeErp WithSjoStatus(this FakeErp erp, string status, string description) =>
        erp.Route("sjoentryList", new JsonArray(IssueToShopFloorServiceTests.SjoRow(5341, "26-27", status, description)));
}
