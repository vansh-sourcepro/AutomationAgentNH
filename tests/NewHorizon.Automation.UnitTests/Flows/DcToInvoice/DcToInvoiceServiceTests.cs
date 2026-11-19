using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.ErpClient.Authentication;
using NewHorizon.Automation.ErpClient.Flows.DcToInvoice;
using Xunit;

namespace NewHorizon.Automation.UnitTests.Flows.DcToInvoice;

public sealed class DcToInvoiceServiceTests
{
    private readonly FakeInvoiceErp _fakeErp = new();
    private readonly FakeHttpClientFactory _clientFactory;
    private readonly FakeTokenProvider _tokenProvider = new();
    private readonly DcToInvoiceOptions _options = new()
    {
        SiteId = 8,
        SiteCode = "CT2",
        CustomerCode = "A001",
        Currency = "INR",
        DocType = "SI",
        DocSubType = "DS"
    };

    public DcToInvoiceServiceTests()
    {
        _clientFactory = new FakeHttpClientFactory(new HttpClient(_fakeErp)
        {
            BaseAddress = new Uri("http://erp.local/")
        });
        SetupDefaultRoutes();
    }

    private DcToInvoiceService CreateService(DcToInvoiceOptions? options = null) =>
        new(
            _clientFactory,
            _tokenProvider,
            options ?? _options,
            NullLogger<DcToInvoiceService>.Instance,
            new InMemoryConvertedDcTracker());

    private void SetupDefaultRoutes(bool enableLineLevelRateStructure = false)
    {
        // 1. Sales policy: MSCENABLELINELEVELRTSTR
        _fakeErp.On("salespolicy/getDetail", JsonNode.Parse($$"""
        {
            "success": true,
            "data": {
                "salesPolicyResultModel": [{ "enablelinelevelratestructure": {{(enableLineLevelRateStructure ? "true" : "false")}} }]
            }
        }
        """)!);

        // 2. Document control default
        _fakeErp.On("getDefaultDocumentDetail", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "groupCode": "SI",
                    "locationCode": "CT2",
                    "locationId": 8,
                    "isAutoNumberGenerated": true,
                    "isLocationRequired": true,
                    "isAutorisationRequired": true
                }
            ]
        }
        """)!);

        // 3. Fetch pending Delivery Challans (CSP_SALINV_DCDTLS)
        _fakeErp.On("FetchDCDtlsForSalInv", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "dcid": 110530,
                    "dcNo": "000107",
                    "dcDate": "2026-10-15T00:00:00",
                    "dcYear": "26-27",
                    "dcGroup": "DS",
                    "dcSite": "CT2",
                    "oafNo": "000045",
                    "oafYear": "22-23",
                    "oafGroup": "OA",
                    "oafSite": "CT2",
                    "xoafhautoid": 10169,
                    "ordid": 10166,
                    "intOdrNo": "000045",
                    "intOdrYear": "22-23",
                    "intOdrGroup": "OA",
                    "intOdrSite": "CT2",
                    "custPOno": "PO-9912",
                    "custPOdate": "2026-09-10T00:00:00",
                    "shipCode": "CADD",
                    "billTo": "A001",
                    "billToName": "Akzo Nobel India Ltd.",
                    "xobcurrcd": "INR",
                    "exchangeRate": 1.0,
                    "xdcrtstrcd": "GST18",
                    "deliveryName": "Akzo Nobel Plant",
                    "deliveryCityCode": "AHM",
                    "creditDays": 30,
                    "creditLimit": 1500000.0,
                    "crLimitInvflg": "W",
                    "creditLimitflag": "A",
                    "creditLimitdays": 30
                }
            ]
        }
        """)!);

        // 4. DC shipment logistics
        _fakeErp.On("DCShopmentDetails", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "xdcmlrno": "LR-110530",
                    "xdcmvno": "GJ01AB1234",
                    "xdcmgrsw": 1200.0,
                    "xdcmnetw": 1150.0,
                    "xdcmtrnpcd": "0 - By Road",
                    "xdcmrpno": "RP-9981"
                }
            ]
        }
        """)!);

        // 5. Shipment details
        _fakeErp.On("GetShipment", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "shipmentCode": "CADD",
                    "shipmentName": "Akzo Factory",
                    "shipmentCityCode": "AHM",
                    "shipmentCityName": "Ahmedabad",
                    "shipmentStateCode": "GUJ",
                    "shipmentStateName": "Gujarat",
                    "shipmentGstNo": "24AAACA1234F1Z5"
                }
            ]
        }
        """)!);

        // 6. Rate structures
        _fakeErp.On("FetchDCRateStructure", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "rateStructureCode": "GST18", "rateStructureDescription": "GST 18%", "rateStructureFullName": "GST18 - GST 18%", "isGSTApplicable": true },
                { "rateStructureCode": "IGST18", "rateStructureDescription": "IGST 18%", "rateStructureFullName": "IGST18 - IGST 18%", "isGSTApplicable": true }
            ]
        }
        """)!);

        // 7. Item details & Taxes
        _fakeErp.On("FetchItemDtls", JsonNode.Parse("""
        {
            "success": true,
            "data": {
                "orderdtlListModel": [
                    {
                        "salesItemCode": "0RAP01870203005",
                        "salesItemName": "VALVE 2 INCH",
                        "intItmCode": "0RAP01870203005",
                        "dcQty": 10.0,
                        "invoiceQty": 10.0,
                        "ordRateDis": 2500.0,
                        "orderRate": 2500.0,
                        "assessableRate": 2500.0,
                        "amount": 25000.0,
                        "amountWithTax": 29500.0,
                        "suom": "NOS",
                        "iuom": "NOS",
                        "convFact": 1.0,
                        "oafId": 10169,
                        "oafDetailLineNo": 1,
                        "dcoaflineno": 1,
                        "dcRndNo": "1105301",
                        "hsnCode": "84818030",
                        "ratestructurecd": "GST18"
                    }
                ],
                "ratestructureModel": [
                    { "rateCode": "CGST9", "rateDesc": "CGST 9%", "taxValue": 9.0, "appOnDisplay": "Gross Value", "ie": "I", "pv": "P", "mprtaxtyp": "C", "rateAmount": 2250.0, "postNonPost": 1, "uniqueno": 0, "itemCode": "0RAP01870203005", "seqNo": 1 },
                    { "rateCode": "SGST9", "rateDesc": "SGST 9%", "taxValue": 9.0, "appOnDisplay": "Gross Value", "ie": "I", "pv": "P", "mprtaxtyp": "S", "rateAmount": 2250.0, "postNonPost": 1, "uniqueno": 0, "itemCode": "0RAP01870203005", "seqNo": 2 }
                ]
            }
        }
        """)!);

        // 8. Dispatch-from address
        _fakeErp.On("fetchDispatchFrmAddress", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "Address1": "Naroda GIDC",
                    "City": "AHM",
                    "PinCode": "382330",
                    "GstNo": "24AAACN1234E1Z1"
                }
            ]
        }
        """)!);

        // 9. Credit days
        _fakeErp.On("FetchCreditDays", JsonNode.Parse("""
        {
            "success": true,
            "data": 30
        }
        """)!);

        // 10. Save invoice create
        _fakeErp.On("salesinvoice/create", JsonNode.Parse("""
        {
            "success": true,
            "message": "sales_salesdispatch_salesinvoice_siCreated#26-27/DS/CT2/000045#110582",
            "errors": null,
            "data": null
        }
        """)!);
    }

    [Fact]
    public async Task CreateInvoice_WhenLineLevelRateStructureFalse_SelectsFirstHeaderRateStructure()
    {
        // Arrange: MSCENABLELINELEVELRTSTR = 0
        SetupDefaultRoutes(enableLineLevelRateStructure: false);
        var service = CreateService();

        // Act
        var result = await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 110530);

        // Assert
        result.Should().NotBeNull();
        result.InvoiceNumber.Should().Be("000045");
        result.InvoiceId.Should().Be(110582);
        result.DcId.Should().Be(110530);
        result.ItemCount.Should().Be(1);
        result.TotalInvoiceAmount.Should().Be(29500.0m);
        result.LineLevelRateStructureApplied.Should().BeFalse();
        result.RateStructureCode.Should().Be("GST18"); // First valid option in FetchDCRateStructure

        // Verify save request payload sent to ERP
        var createRequest = _fakeErp.Requests.FirstOrDefault(r => r.Path.Contains("salesinvoice/create"));
        createRequest.Should().NotBeNull();
        var body = createRequest.Body;
        body.Should().NotBeNull();

        var dtInvHdr = body!["dtInvHdr"] as JsonArray;
        dtInvHdr.Should().NotBeNull();
        dtInvHdr![0]!["XIHRTSCD"]!.ToString().Should().Be("GST18");

        var dtDCINVRef = body["dtDCINVRef"] as JsonArray;
        dtDCINVRef.Should().NotBeNull();
        dtDCINVRef!.Count.Should().Be(1);
        dtDCINVRef[0]!["XDCDIDCID"]!.ToString().Should().Be("110530");
        dtDCINVRef[0]!["XDCDIINVQTY"]!.ToString().Should().Be("10.0000");
    }

    [Fact]
    public async Task CreateInvoice_WhenLineLevelRateStructureTrue_AppliesLineLevelRateStructure()
    {
        // Arrange: MSCENABLELINELEVELRTSTR = 1
        SetupDefaultRoutes(enableLineLevelRateStructure: true);
        var service = CreateService();

        // Act
        var result = await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 110530);

        // Assert
        result.Should().NotBeNull();
        result.InvoiceNumber.Should().Be("000045");
        result.InvoiceId.Should().Be(110582);
        result.LineLevelRateStructureApplied.Should().BeTrue();

        var createRequest = _fakeErp.Requests.FirstOrDefault(r => r.Path.Contains("salesinvoice/create"));
        createRequest.Should().NotBeNull();
        var body = createRequest.Body;
        body.Should().NotBeNull();

        var dtInvDtl = body!["dtInvDtl"] as JsonArray;
        dtInvDtl.Should().NotBeNull();
        dtInvDtl![0]!["XIDRTSCD"]!.ToString().Should().Be("GST18");
    }

    [Fact]
    public async Task CreateInvoice_WhenDcNotFound_ThrowsValidationException()
    {
        // Arrange
        SetupDefaultRoutes();
        var service = CreateService();

        // Act & Assert: DC 999999 is not in pending DC list
        Func<Task> act = async () => await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 999999);
        await act.Should().ThrowAsync<InvoiceValidationException>()
            .WithMessage("*not found in the list of eligible*");
    }

    [Fact]
    public async Task BatchCreateInvoices_ProcessesMultipleDcs_AndReportsSuccessAndFailureSeparately()
    {
        // Arrange: Return 2 pending DCs, one will succeed and one will fail in create
        _fakeErp.On("FetchDCDtlsForSalInv", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "dcid": 110530,
                    "dcNo": "000107",
                    "dcDate": "2026-10-15T00:00:00",
                    "dcYear": "26-27",
                    "dcGroup": "DS",
                    "dcSite": "CT2",
                    "oafNo": "000045",
                    "ordid": 10166,
                    "shipCode": "CADD",
                    "billTo": "A001",
                    "billToName": "Akzo Nobel India Ltd.",
                    "xobcurrcd": "INR",
                    "xdcrtstrcd": "GST18"
                },
                {
                    "dcid": 110531,
                    "dcNo": "000108",
                    "dcDate": "2026-10-15T00:00:00",
                    "dcYear": "26-27",
                    "dcGroup": "DS",
                    "dcSite": "CT2",
                    "oafNo": "000046",
                    "ordid": 10167,
                    "shipCode": "CADD",
                    "billTo": "A001",
                    "billToName": "Akzo Nobel India Ltd.",
                    "xobcurrcd": "INR",
                    "xdcrtstrcd": "GST18"
                }
            ]
        }
        """)!);

        // Make second DC fail on ERP create
        _fakeErp.On("salesinvoice/create", (_, body) =>
        {
            var dtInvHdr = body?["dtInvHdr"] as JsonArray;
            var lotDcIds = dtInvHdr?[0]?["LOTQuantyBasedDCIds"]?.ToString();
            if (lotDcIds == "110531")
            {
                return JsonNode.Parse("""
                {
                    "success": false,
                    "message": "Field validation error.",
                    "errors": "Financial period closed for site CT2.",
                    "data": null
                }
                """);
            }
            return JsonNode.Parse("""
            {
                "success": true,
                "message": "sales_salesdispatch_salesinvoice_siCreated#26-27/DS/CT2/000045#110582",
                "errors": null,
                "data": null
            }
            """);
        });

        var service = CreateService();

        // Act
        var result = await service.CreateInvoicesFromDeliveryChallansAsync(
            new BatchInvoiceCreationRequest(CustomerCode: "A001"));

        // Assert
        result.Should().NotBeNull();
        result.Data.TotalDeliveryChallans.Should().Be(2);
        result.Data.SuccessfulDeliveryChallans.Should().Be(1);
        result.Data.FailedDeliveryChallans.Should().Be(1);
        result.Data.CreatedInvoiceCount.Should().Be(1);

        // Verify DC 110530 succeeded
        var successResult = result.Data.Results.FirstOrDefault(r => r.DcId == 110530);
        successResult.Should().NotBeNull();
        successResult!.Status.Should().Be("SUCCESS");
        successResult.InvoiceNumber.Should().Be("000045");
        successResult.InvoiceId.Should().Be(110582);

        // Verify DC 110531 failed with preserved error message
        var failResult = result.Data.Results.FirstOrDefault(r => r.DcId == 110531);
        failResult.Should().NotBeNull();
        failResult!.Status.Should().Be("FAILED");
        failResult.Message.Should().Contain("Financial period closed for site CT2");
    }

    [Fact]
    public async Task BatchCreateInvoices_ProcessesAndCreatesInvoice()
    {
        // Arrange
        SetupDefaultRoutes();
        var service = CreateService();

        // Act
        var result = await service.CreateInvoicesFromDeliveryChallansAsync(
            new BatchInvoiceCreationRequest(CustomerCode: "A001"));

        // Assert
        result.Should().NotBeNull();
        result.Data.TotalDeliveryChallans.Should().Be(1);
        result.Data.Results[0].Status.Should().Be("SUCCESS");
        result.Data.CreatedInvoiceCount.Should().Be(1);

        // Verify save call was dispatched
        _fakeErp.Requests.Any(r => r.Path.Contains("salesinvoice/create")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateInvoice_WhenNoRateStructureAvailable_ThrowsValidationExceptionAndPreventsCreation()
    {
        // Arrange
        SetupDefaultRoutes(enableLineLevelRateStructure: false);
        // Clear rate structure options and DC rate structure
        _fakeErp.On("FetchDCRateStructure", JsonNode.Parse("""
        {
            "success": true,
            "data": []
        }
        """)!);

        _fakeErp.On("FetchDCDtlsForSalInv", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "dcid": 110530,
                    "dcNo": "000107",
                    "dcDate": "2026-10-15T00:00:00",
                    "dcYear": "26-27",
                    "dcGroup": "DS",
                    "dcSite": "CT2",
                    "oafNo": "000045",
                    "ordid": 10166,
                    "shipCode": "CADD",
                    "billTo": "A001",
                    "billToName": "Akzo Nobel India Ltd.",
                    "xobcurrcd": "INR",
                    "xdcrtstrcd": null
                }
            ]
        }
        """)!);

        _fakeErp.On("FetchItemDtls", JsonNode.Parse("""
        {
            "success": true,
            "data": {
                "orderdtlListModel": [
                    {
                        "salesItemCode": "ITEM01",
                        "intItmCode": "ITEM01",
                        "salesItemName": "Test Item",
                        "dcQty": 10.0,
                        "invoiceQty": 10.0,
                        "ordRateDis": 100.0,
                        "suom": "NOS",
                        "iuom": "NOS",
                        "convFact": 1.0,
                        "oafId": 1,
                        "oafDetailLineNo": 1,
                        "ratestructurecd": null
                    }
                ],
                "ratestructureModel": []
            }
        }
        """)!);

        var service = CreateService(new DcToInvoiceOptions { DefaultRateStructureCode = "" });

        // Act & Assert
        Func<Task> act = async () => await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 110530);
        await act.Should().ThrowAsync<InvoiceValidationException>()
            .WithMessage("*No valid Rate Structure could be resolved*");

        // Verify create endpoint was never called
        _fakeErp.Requests.Any(r => r.Path.Contains("salesinvoice/create")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateInvoice_WhenZeroPendingItems_ThrowsValidationExceptionAndPreventsCreation()
    {
        // Arrange
        SetupDefaultRoutes();
        _fakeErp.On("FetchItemDtls", JsonNode.Parse("""
        {
            "success": true,
            "data": {
                "orderdtlListModel": [],
                "ratestructureModel": []
            }
        }
        """)!);

        var service = CreateService();

        // Act & Assert
        Func<Task> act = async () => await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 110530);
        await act.Should().ThrowAsync<InvoiceValidationException>()
            .WithMessage("*0 pending items*");

        // Verify create endpoint was never called
        _fakeErp.Requests.Any(r => r.Path.Contains("salesinvoice/create")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateInvoice_WhenDcItemHasZeroDcAutoId_CreatesInvoiceBasedOnDcId_AndPassesZeroRndNo()
    {
        // Arrange: ERP returns items where dcautoid / dcRndNo is 0 (standard ERP behavior per TFS 25436)
        SetupDefaultRoutes();
        _fakeErp.On("FetchItemDtls", JsonNode.Parse("""
        {
            "success": true,
            "data": {
                "orderdtlListModel": [
                    {
                        "salesItemCode": "0RAP01870203005",
                        "salesItemName": "VALVE 2 INCH",
                        "intItmCode": "0RAP01870203005",
                        "dcQty": 10.0,
                        "invoiceQty": 10.0,
                        "ordRateDis": 2500.0,
                        "dcautoid": 0,
                        "dcRndNo": null,
                        "oafId": 10169,
                        "convFact": 1.0,
                        "amount": 25000.0,
                        "amountWithTax": 29500.0
                    }
                ],
                "ratestructureModel": []
            }
        }
        """)!);

        var service = CreateService();

        // Act
        var result = await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 110530);

        // Assert: Creation succeeds based on DCId (matching ERP WebApp)
        result.Should().NotBeNull();
        result.InvoiceNumber.Should().Be("000045");
        result.DcId.Should().Be(110530);

        var createRequest = _fakeErp.Requests.FirstOrDefault(r => r.Path.Contains("salesinvoice/create"));
        createRequest.Should().NotBeNull();
        var dtInvDtl = createRequest!.Body!["dtInvDtl"] as JsonArray;
        dtInvDtl.Should().NotBeNull();
        dtInvDtl![0]!["XIDDCID"]!.ToString().Should().Be("110530");
        dtInvDtl[0]!["XIDDCRNDNO"]!.ToString().Should().Be("0");
    }

    [Fact]
    public async Task CreateInvoice_WhenAllItemsHaveZeroInvoiceQty_ThrowsValidationException_AndNeverCallsCreate()
    {
        // Arrange: DC has items, but none have pending quantity to invoice
        SetupDefaultRoutes();
        _fakeErp.On("FetchItemDtls", JsonNode.Parse("""
        {
            "success": true,
            "data": {
                "orderdtlListModel": [
                    {
                        "salesItemCode": "0RAP01870203005",
                        "salesItemName": "VALVE 2 INCH",
                        "intItmCode": "0RAP01870203005",
                        "dcQty": 10.0,
                        "invoiceQty": 0.0,
                        "maxInvQty": 0.0,
                        "ordRateDis": 2500.0,
                        "dcautoid": 0,
                        "oafId": 10169,
                        "convFact": 1.0
                    }
                ],
                "ratestructureModel": []
            }
        }
        """)!);

        var service = CreateService();

        // Act & Assert
        Func<Task> act = async () => await service.CreateInvoiceFromDeliveryChallanAsync(dcId: 110530);
        await act.Should().ThrowAsync<InvoiceValidationException>()
            .WithMessage("*no items with valid Delivery Challan ID and pending invoice quantity > 0*");

        // Verify create endpoint was strictly NEVER called
        _fakeErp.Requests.Any(r => r.Path.Contains("salesinvoice/create")).Should().BeFalse();
    }

    [Fact]
    public async Task GetHistory_and_Dashboard_and_DailyStats_return_expected_metrics()
    {
        var tracker = new InMemoryConvertedDcTracker();
        tracker.RecordConversion(new ConvertedDcRecord(
            DcId: 101,
            DcNo: "000001",
            CustomerCode: "CUST1",
            InvoiceNumber: "INV001",
            InvoiceId: 501,
            ItemCount: 2,
            TotalQuantity: 10m,
            TotalAmount: 5000m,
            RateStructureCode: "S0001",
            ConvertedAtUtc: DateTime.UtcNow.Date.AddHours(2),
            Status: "SUCCESS",
            CustomerName: "Customer 1"));

        tracker.RecordConversion(new ConvertedDcRecord(
            DcId: 102,
            DcNo: "000002",
            CustomerCode: "CUST2",
            InvoiceNumber: "INV002",
            InvoiceId: 502,
            ItemCount: 1,
            TotalQuantity: 5m,
            TotalAmount: 2500m,
            RateStructureCode: "S0002",
            ConvertedAtUtc: DateTime.UtcNow.Date.AddDays(-1).AddHours(4),
            Status: "SUCCESS",
            CustomerName: "Customer 2"));

        tracker.RecordHistory(new ConvertedDcRecord(
            DcId: 103,
            DcNo: "000003",
            CustomerCode: "CUST1",
            InvoiceNumber: null!,
            InvoiceId: 0,
            ItemCount: 0,
            TotalQuantity: 0m,
            TotalAmount: 0m,
            RateStructureCode: null,
            ConvertedAtUtc: DateTime.UtcNow.Date.AddHours(5),
            Status: "FAILED",
            FailureReason: "No items pending for invoice."));

        var service = new DcToInvoiceService(
            _clientFactory,
            _tokenProvider,
            _options,
            NullLogger<DcToInvoiceService>.Instance,
            tracker);

        // 1. History test
        var history = await service.GetHistoryAsync(new DcToInvoiceHistoryQuery());
        history.TotalCount.Should().Be(3);
        history.Items.Should().HaveCount(3);
        history.Items[0].ConvertedAtUtc.Should().BeOnOrAfter(history.Items[1].ConvertedAtUtc);

        // Search test
        var filtered = await service.GetHistoryAsync(new DcToInvoiceHistoryQuery { Search = "000001" });
        filtered.TotalCount.Should().Be(1);
        filtered.Items[0].InvoiceNumber.Should().Be("INV001");

        // Status test
        var failedOnly = await service.GetHistoryAsync(new DcToInvoiceHistoryQuery { Status = "FAILED" });
        failedOnly.TotalCount.Should().Be(1);
        failedOnly.Items[0].DcNo.Should().Be("000003");

        // 2. Single DC History test
        var dcHistory = await service.GetDcHistoryAsync(dcNo: "000002");
        dcHistory.IsConverted.Should().BeTrue();
        dcHistory.LatestRecord.Should().NotBeNull();
        dcHistory.LatestRecord!.InvoiceNumber.Should().Be("INV002");

        // 3. Dashboard test
        var dashboard = await service.GetDashboardSummaryAsync();
        dashboard.TotalProcessed.Should().Be(3);
        dashboard.InvoicesCreated.Should().Be(2);
        dashboard.FailedCount.Should().Be(1);
        dashboard.TotalInvoiceAmount.Should().Be(7500m);
        dashboard.TotalQuantity.Should().Be(15m);
        dashboard.TopCustomers.Should().HaveCount(2);
        dashboard.TopCustomers[0].CustomerCode.Should().Be("CUST1");
        dashboard.TopCustomers[0].TotalAmount.Should().Be(5000m);

        // 4. Daily Stats test
        var daily = await service.GetDailyStatsAsync(days: 7);
        daily.Should().HaveCount(7);
        var todayStat = daily.Last();
        todayStat.Date.Should().Be(DateTime.UtcNow.ToString("yyyy-MM-dd"));
        todayStat.InvoicesCreated.Should().Be(1);
        todayStat.Failures.Should().Be(1);
    }
}

internal sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

internal sealed class FakeTokenProvider : IErpTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("fake-jwt-token");

    public Task<int> GetUserIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(2);

    public void Invalidate()
    {
    }
}
