using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using NewHorizon.Automation.UnitTests.Erp;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

/// <summary>
/// The Work Order and Sales OAF tabs: found through the Issue screen's own validation, and a document
/// that spans several SJOs is stocked per line, for the line's own SJO.
/// </summary>
public sealed class WorkOrderAndOafIssueTests
{
    private const string CreatePath = "createIssuetoShopFloor";

    [Fact]
    public async Task A_work_order_is_issued_as_the_work_order_tab_saves_it()
    {
        var erp = DocumentErp.Ready(documentId: 900);

        var result = await Build(erp).ConvertAsync(IssueSource.WorkOrder, "26-27/WO/NF1/10", false, CancellationToken.None);

        result.Created.Should().BeTrue(result.Reason);
        result.IssueType.Should().Be(IssueSource.WorkOrder);
        result.DocumentNumber.Should().Be("26-27/WO/NF1/000010");

        var body = erp.LastBodyFor(CreatePath)!;
        body["issueType"]!.GetValue<string>().Should().Be("W");
        body["sjowoType"]!.GetValue<string>().Should().Be("S");
        body["docId"]!.GetValue<long>().Should().Be(5341, "the screen sends the first line's SJO id on every tab");

        var warehouseLookup = erp.LastBodyFor("getWarehouseCodeForIssue")!;
        warehouseLookup["docType"]!.GetValue<string>().Should().Be("WO");
        warehouseLookup["docID"]!.GetValue<string>().Should().Be("900");
        warehouseLookup["whType"]!.GetValue<string>().Should().BeEmpty();

        erp.LastBodyFor("getListOfItems4IssueEntry")!["docType"]!.GetValue<string>().Should().Be("WO");
    }

    [Fact]
    public async Task A_sales_oaf_is_issued_as_the_oaf_tab_saves_it()
    {
        var erp = DocumentErp.Ready(documentId: 42, number: "000042");

        var result = await Build(erp).ConvertAsync(IssueSource.SalesOaf, "26-27/OF/NF1/000042", false, CancellationToken.None);

        result.Created.Should().BeTrue(result.Reason);

        var body = erp.LastBodyFor(CreatePath)!;
        body["issueType"]!.GetValue<string>().Should().Be("O");
        body["sjowoType"]!.GetValue<string>().Should().Be("M");

        var warehouseLookup = erp.LastBodyFor("getWarehouseCodeForIssue")!;
        warehouseLookup["docType"]!.GetValue<string>().Should().Be("OA");
        warehouseLookup["whType"]!.GetValue<string>().Should().Be("S", "the warehouse procedure only knows OA in its single-warehouse branch");
    }

    [Fact]
    public async Task Each_line_is_stocked_for_its_own_sjo()
    {
        var erp = DocumentErp.Ready(documentId: 900);

        var result = await Build(erp).ConvertAsync(IssueSource.WorkOrder, "26-27/WO/NF1/000010", false, CancellationToken.None);

        result.Lines.Select(line => line.SjoId).Distinct().Should().BeEquivalentTo([5341L, 5342L]);
        erp.Requests.Where(request => request.Path.Contains("getItemCombinations4IssueEntry", StringComparison.OrdinalIgnoreCase))
            .Select(request => request.Path.Split('/')[6])
            .Should().BeEquivalentTo(["5341", "5342"]);
    }

    [Fact]
    public async Task A_work_order_the_erp_would_not_issue_is_refused_and_nothing_is_created()
    {
        var erp = DocumentErp.Ready(documentId: 900);
        erp.Route("getIssueOfItmDocNo/", new JsonArray());

        var result = await Build(erp).ConvertAsync(IssueSource.WorkOrder, "26-27/WO/NF1/000010", false, CancellationToken.None);

        result.Created.Should().BeFalse();
        result.Checks.Should().ContainSingle(check => !check.Passed).Which.Name.Should().Be("DocumentFound");
        result.Reason.Should().Contain("not open and authorised");
        erp.CallCount(CreatePath).Should().Be(0);
    }

    [Fact]
    public async Task A_site_with_nothing_to_issue_is_refused()
    {
        var erp = DocumentErp.Ready(documentId: 42, number: "000042");
        erp.Route("getIssueOfItmSite", new JsonArray());

        var result = await Build(erp).ConvertAsync(IssueSource.SalesOaf, "26-27/OF/NF1/000042", false, CancellationToken.None);

        result.Checks.Should().ContainSingle(check => !check.Passed).Which.Name.Should().Be("DocumentFound");
        result.Reason.Should().Contain("site NF1");
        erp.CallCount(CreatePath).Should().Be(0);
    }

    [Fact]
    public async Task A_bare_work_order_number_is_refused_because_it_is_not_unique()
    {
        var erp = DocumentErp.Ready(documentId: 900);

        var result = await Build(erp).ConvertAsync(IssueSource.WorkOrder, "000010", false, CancellationToken.None);

        result.Reason.Should().Contain("full Work Order number");
        erp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Work_order_and_oaf_do_not_need_the_sjo_site_list()
    {
        // Sites only scopes the SJO list search; the Work Order and OAF lookups take the site from the number.
        var erp = DocumentErp.Ready(documentId: 900);
        var options = new IssueToShopFloorOptions { CompanyCode = "C01", IssueTo = "SHOP", IssueBy = "STORES", Sites = [] };

        var result = await Build(erp, options).ConvertAsync(IssueSource.WorkOrder, "26-27/WO/NF1/000010", true, CancellationToken.None);

        result.Ready.Should().BeTrue(result.Reason);
    }

    private static IssueToShopFloorService Build(FakeErp erp, IssueToShopFloorOptions? options = null) =>
        new(
            new SingleClientFactory(erp),
            new StubTokenProvider(),
            Microsoft.Extensions.Options.Options.Create(new ErpEndpointOptions()),
            Microsoft.Extensions.Options.Options.Create(options ?? new IssueToShopFloorOptions
            {
                CompanyCode = "C01",
                Sites = [1],
                IssueTo = "SHOP",
                IssueBy = "STORES",
            }),
            new FixedClock(),
            NullLogger<IssueToShopFloorService>.Instance);
}

/// <summary>
/// An ERP where a Work Order or Sales OAF at site NF1 is ready to issue: two lines from two SJOs, each
/// covered by its own stock.
/// </summary>
internal static class DocumentErp
{
    public static FakeErp Ready(long documentId, string number = "000010") => new FakeErp()
        .Route("getIssueOfItmSite", new JsonArray(new JsonObject { ["site"] = "NF1", ["siteName"] = "Plant 1", ["siteID"] = 1 }))
        .Route("getIssueOfItmDocNo/", new JsonArray(new JsonObject { ["number"] = number, ["id"] = documentId }))
        .Route("getDefaultDocumentDetail", new JsonArray(IssueToShopFloorServiceTests.DocumentControl()))
        .Route("getWarehouseCodeForIssue", new JsonArray(new JsonObject { ["warehouseId"] = 81, ["warehouseCode"] = "WH1" }))
        .Route("getListOfItems4IssueEntry", new JsonArray(Line("RM-100", sjoId: 5341, randomNumber: 11), Line("RM-200", sjoId: 5342, randomNumber: 3)))
        .Route("mrpPolicy/getDetail", new JsonObject { ["useInwardNoWiseAllocation"] = "N" })
        .Route("getCommomSystemConfig", IssueToShopFloorServiceTests.FinancePeriod(siteId: 1, "2000-04-01T00:00:00", "2099-03-31T00:00:00"))
        .RouteByPath("getItemCombinations4IssueEntry", path => new JsonArray(
            IssueToShopFloorServiceTests.StockRow(path.Contains("/5341/", StringComparison.Ordinal) ? 1 : 2, 10, "WH1", 50)))
        .RouteWithMessage("createIssuetoShopFloor", 1, "Issue_details_successfully_saved_Issue_document_number_ITSFKey#26-27/IS/NF1/000046");

    private static JsonObject Line(string itemCode, long sjoId, int randomNumber) => new()
    {
        ["itemCode"] = itemCode,
        ["sjoId"] = sjoId,
        ["woId"] = 900,
        ["randomNumber"] = randomNumber,
        ["requiredQuantity"] = "5",
        ["pendingQuantity"] = 5,
        ["inwardRequired"] = "False",
        ["barcodereqflag"] = false,
        ["remarks"] = string.Empty,
    };
}
