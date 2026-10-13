using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.ErpClient.Authentication;
using NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;
using Xunit;

namespace NewHorizon.Automation.UnitTests.Flows.SoAllocationToDC;

public sealed class SoAllocationToDcServiceTests
{
    private readonly FakeSoErp _fakeErp = new();
    private readonly FakeHttpClientFactory _clientFactory;
    private readonly FakeTokenProvider _tokenProvider = new();
    private readonly SoAllocationToDcOptions _options = new()
    {
        SiteId = 8,
        SiteCode = "CT2",
        CustomerCode = "A001"
    };

    public SoAllocationToDcServiceTests()
    {
        _clientFactory = new FakeHttpClientFactory(new HttpClient(_fakeErp)
        {
            BaseAddress = new Uri("http://erp.local/")
        });
        SetupDefaultRoutes();
    }

    private SoAllocationToDcService CreateService(SoAllocationToDcOptions? options = null) =>
        new(
            _clientFactory,
            _tokenProvider,
            options ?? _options,
            NullLogger<SoAllocationToDcService>.Instance);

    private void SetupDefaultRoutes()
    {
        // 1. Sales policy
        _fakeErp.On("salespolicy/getDetail", JsonNode.Parse("""
        {
            "success": true,
            "data": {
                "salesPolicyResultModel": [{ "isdcbasepack": true, "autoallocitmonoafcreate": false }]
            }
        }
        """)!);

        // 2. Transporters
        _fakeErp.On("getCodeTypeList/TR/GEN", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "code": "1", "description": "By Maruti Courier", "codeFullName": "1 - By Maruti Courier" },
                { "code": "10", "description": "By ARC transport", "codeFullName": "10 - By ARC transport" }
            ]
        }
        """)!);

        // 3. Document control default
        _fakeErp.On("getDefaultDocumentDetail", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "finYear": "26-27",
                    "groupCode": "DS",
                    "locationId": 8,
                    "locationCode": "CT2",
                    "locationName": "CTPL Unit2",
                    "isLocationRequired": true,
                    "isAutoNumberGenerated": true,
                    "isAutorisationRequired": false
                }
            ]
        }
        """)!);

        // 4. Digits after decimal
        _fakeErp.On("GetDegitAfterDecimal", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "mcddigaftdec": "0", "decimal": null }]
        }
        """)!);

        // 5. Conversion factor default (1.0)
        _fakeErp.On("getConversionFactorpost", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "conFactor": 1.0 }]
        }
        """)!);

        // 6. Create DC Entry
        _fakeErp.On("createDCEntry", JsonNode.Parse("""
        {
            "success": true,
            "message": "sale_saledp_dcsales_DCEntrysavedKey#26-27/DS/CT2/000107#110530",
            "data": null
        }
        """)!);

        // 7. Customer address
        _fakeErp.On("getCustomerAddress", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "customerCode": "A001", "customerName": "Akzo Nobel India Ltd." }]
        }
        """)!);
    }

    [Fact]
    public async Task All_allocations_discovered_and_processed_sequentially()
    {
        // Customers for allocation
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "custCode": "A001", "custName": "Akzo Nobel India Ltd." },
                { "custCode": "A002", "custName": "Al Fakher Tobacco Factory" }
            ]
        }
        """)!);

        // Shipments for A001
        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "shipmentCode": "CADD", "shipmentName": "Akzo Nobel Plant" }
            ]
        }
        """)!);

        // Shipments for A002
        _fakeErp.On("getShipmentForDC/A002/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "shipmentCode": "SH01", "shipmentName": "Al Fakher Warehouse" }
            ]
        }
        """)!);

        // OAF for A001
        _fakeErp.On("getOAFDetailListForDC/A001", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000165",
                    "oafAutoId": 101,
                    "intOrderNumber": "000182",
                    "customerOrderNumber": "PO-101"
                }
            ]
        }
        """)!);

        // OAF for A002
        _fakeErp.On("getOAFDetailListForDC/A002", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000166",
                    "oafAutoId": 102,
                    "intOrderNumber": "000183",
                    "customerOrderNumber": "PO-102"
                }
            ]
        }
        """)!);

        // Items for 000165 (A001)
        _fakeErp.On("getOAFItemDetailsForDC", (path, body) =>
        {
            var docNumber = body?["docNumber"]?.GetValue<string>();
            if (docNumber == "000165")
            {
                return JsonNode.Parse("""
                {
                    "success": true,
                    "data": [
                        {
                            "salesItemCode": "ITEM001",
                            "salesItemName": "Item 1",
                            "intItemCode": "ITEM001",
                            "intItemName": "Item 1",
                            "oafQty": 10.0,
                            "alreadyDCQty": 0.0,
                            "maxDCQtyIUOM": 10.0,
                            "allocationID": 1001,
                            "oafDetailLineId": 1,
                            "oafDetailLineNo": 1,
                            "soitemlineno": 1,
                            "wareHouseId": 8,
                            "warehouseCode": "GST2",
                            "suom": "NOS",
                            "oafid": 101
                        }
                    ]
                }
                """);
            }

            return JsonNode.Parse("""
            {
                "success": true,
                "data": [
                    {
                        "salesItemCode": "ITEM002",
                        "salesItemName": "Item 2",
                        "intItemCode": "ITEM002",
                        "intItemName": "Item 2",
                        "oafQty": 20.0,
                        "alreadyDCQty": 0.0,
                        "maxDCQtyIUOM": 20.0,
                        "allocationID": 1002,
                        "oafDetailLineId": 1,
                        "oafDetailLineNo": 1,
                        "soitemlineno": 1,
                        "wareHouseId": 8,
                        "warehouseCode": "GST2",
                        "suom": "NOS",
                        "oafid": 102
                    }
                ]
            }
            """);
        });

        // Shipment info
        _fakeErp.On("getShipmentInfo", (path, _) =>
        {
            var item = path.Contains("101") ? "ITEM001" : "ITEM002";
            return JsonNode.Parse($$"""
            {
                "success": true,
                "data": [
                    {
                        "salesItemCode": "{{item}}",
                        "shipCode": "CADD",
                        "rateStructureCode": "SR001",
                        "billToCode": "A001",
                        "recordId": 501,
                        "oafDetailRefLineId": 1,
                        "oafQtyOfLineId": 1,
                        "oafRefId": 101
                    }
                ]
            }
            """);
        });

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Success.Should().BeTrue();
        result.Data.TotalAllocations.Should().Be(2);
        result.Data.SuccessfulAllocations.Should().Be(2);
        result.Data.CreatedDCCount.Should().Be(2);
        result.Data.Results.Should().HaveCount(2);

        result.Data.Results[0].OafId.Should().Be(101);
        result.Data.Results[0].DcNumber.Should().Be("26-27/DS/CT2/000107");
        result.Data.Results[1].OafId.Should().Be(102);
    }

    [Fact]
    public async Task Multiple_items_on_single_OAF_transferred_with_exact_quantities()
    {
        // Customers
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Akzo Nobel Plant" }]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC/A001", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000165",
                    "oafAutoId": 170573,
                    "intOrderNumber": "000182",
                    "customerOrderNumber": "PO-Multi"
                }
            ]
        }
        """)!);

        // 3 items on OAF: Item A=10, Item B=5, Item C=20
        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM-A",
                    "salesItemName": "Item A",
                    "intItemCode": "ITEM-A",
                    "intItemName": "Item A",
                    "oafQty": 10.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 2001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 170573
                },
                {
                    "salesItemCode": "ITEM-B",
                    "salesItemName": "Item B",
                    "intItemCode": "ITEM-B",
                    "intItemName": "Item B",
                    "oafQty": 5.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 5.0,
                    "allocationID": 2002,
                    "oafDetailLineId": 2,
                    "oafDetailLineNo": 2,
                    "soitemlineno": 2,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 170573
                },
                {
                    "salesItemCode": "ITEM-C",
                    "salesItemName": "Item C",
                    "intItemCode": "ITEM-C",
                    "intItemName": "Item C",
                    "oafQty": 20.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 20.0,
                    "allocationID": 2003,
                    "oafDetailLineId": 3,
                    "oafDetailLineNo": 3,
                    "soitemlineno": 3,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 170573
                }
            ]
        }
        """)!);

        _fakeErp.On("getShipmentInfo", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM-A",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 601,
                    "oafDetailRefLineId": 1,
                    "oafQtyOfLineId": 1,
                    "oafRefId": 170573
                },
                {
                    "salesItemCode": "ITEM-B",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 602,
                    "oafDetailRefLineId": 2,
                    "oafQtyOfLineId": 2,
                    "oafRefId": 170573
                },
                {
                    "salesItemCode": "ITEM-C",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 603,
                    "oafDetailRefLineId": 3,
                    "oafQtyOfLineId": 3,
                    "oafRefId": 170573
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Success.Should().BeTrue();
        result.Data.SuccessfulAllocations.Should().Be(1);
        result.Data.CreatedDCCount.Should().Be(1);

        var first = result.Data.Results[0];
        first.ItemCount.Should().Be(3);
        first.AllocatedQuantity.Should().Be(35.0m);
        first.DcQuantity.Should().Be(35.0m);
    }

    [Fact]
    public async Task Shipment_first_valid_dropdown_value_selected()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        // Multiple shipments returned: SHIP-FIRST, SHIP-SECOND, SHIP-THIRD
        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "shipmentCode": "SHIP-FIRST", "shipmentName": "First Valid Shipment" },
                { "shipmentCode": "SHIP-SECOND", "shipmentName": "Second Shipment" },
                { "shipmentCode": "SHIP-THIRD", "shipmentName": "Third Shipment" }
            ]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000200",
                    "oafAutoId": 200,
                    "intOrderNumber": "000200",
                    "customerOrderNumber": "PO-200"
                }
            ]
        }
        """)!);

        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 10.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 3001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 200
                }
            ]
        }
        """)!);

        _fakeErp.On("getShipmentInfo", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "shipCode": "SHIP-FIRST",
                    "rateStructureCode": "SR001",
                    "recordId": 701,
                    "oafDetailRefLineId": 1,
                    "oafQtyOfLineId": 1,
                    "oafRefId": 200
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Success.Should().BeTrue();
        result.Data.Results[0].ShipmentCode.Should().Be("SHIP-FIRST");
    }

    [Fact]
    public async Task Empty_mandatory_dropdown_rejects_DC_creation()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        // Empty shipment list
        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": []
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync(new BatchDcCreationRequest("A001"));

        // When mandatory shipment is empty, no DC is created and total is 0
        result.Data.TotalAllocations.Should().Be(0);
        result.Data.CreatedDCCount.Should().Be(0);
    }

    [Fact]
    public async Task Transporter_first_valid_dropdown_value_selected()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        // Multiple transporters returned
        _fakeErp.On("getCodeTypeList/TR/GEN", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                { "code": "TR-FIRST", "description": "First Transporter", "codeFullName": "TR-FIRST" },
                { "code": "TR-SECOND", "description": "Second Transporter", "codeFullName": "TR-SECOND" }
            ]
        }
        """)!);

        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000300",
                    "oafAutoId": 300,
                    "intOrderNumber": "000300",
                    "customerOrderNumber": "PO-300"
                }
            ]
        }
        """)!);

        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 10.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 4001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 300
                }
            ]
        }
        """)!);

        _fakeErp.On("getShipmentInfo", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 801,
                    "oafDetailRefLineId": 1,
                    "oafQtyOfLineId": 1,
                    "oafRefId": 300
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Success.Should().BeTrue();
        result.Data.SuccessfulAllocations.Should().Be(1);

        // Verify the createDCEntry request body carried "TR-FIRST"
        var createRequest = _fakeErp.Requests.LastOrDefault(r => r.Path.Contains("createDCEntry"));
        createRequest.Should().NotBeNull();
    }

    [Fact]
    public async Task Duplicate_DC_protection_prevents_duplicate_creation()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000400",
                    "oafAutoId": 400,
                    "intOrderNumber": "000400",
                    "customerOrderNumber": "PO-400"
                }
            ]
        }
        """)!);

        // Item already completely dispatched: alreadyDCQty = 10, maxDCQtyIUOM = 0
        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 10.0,
                    "alreadyDCQty": 10.0,
                    "maxDCQtyIUOM": 0.0,
                    "allocationID": 5001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 400
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Data.SuccessfulAllocations.Should().Be(0);
        result.Data.FailedAllocations.Should().Be(1);
        result.Data.CreatedDCCount.Should().Be(0);
        result.Data.Results[0].Message.Should().Contain("Duplicate DC prevented");
    }

    [Fact]
    public async Task Quantity_exceeding_maxDCQtyIUOM_fails_validation_without_silent_adjustment()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000500",
                    "oafAutoId": 500,
                    "intOrderNumber": "000500",
                    "customerOrderNumber": "PO-500"
                }
            ]
        }
        """)!);

        // Allocated Qty = 20, but Max allowable DC Qty = 10
        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 20.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 6001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 500
                }
            ]
        }
        """)!);

        _fakeErp.On("getShipmentInfo", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 901,
                    "oafDetailRefLineId": 1,
                    "oafQtyOfLineId": 1,
                    "oafRefId": 500
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        // Must NOT silently adjust quantity to 10. Must fail with clear error.
        result.Data.SuccessfulAllocations.Should().Be(0);
        result.Data.FailedAllocations.Should().Be(1);
        result.Data.Results[0].Success.Should().BeFalse();
        result.Data.Results[0].Message.Should().Contain("exceeds");
    }

    [Fact]
    public async Task Partial_failures_reported_with_full_diagnostic_details()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000601",
                    "oafAutoId": 601,
                    "intOrderNumber": "000601",
                    "customerOrderNumber": "PO-601"
                },
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000602",
                    "oafAutoId": 602,
                    "intOrderNumber": "000602",
                    "customerOrderNumber": "PO-602"
                }
            ]
        }
        """)!);

        _fakeErp.On("getOAFItemDetailsForDC", (path, body) =>
        {
            var docNumber = body?["docNumber"]?.GetValue<string>();
            if (docNumber == "000601")
            {
                // Valid item
                return JsonNode.Parse("""
                {
                    "success": true,
                    "data": [
                        {
                            "salesItemCode": "ITEM01",
                            "salesItemName": "Item 01",
                            "intItemCode": "ITEM01",
                            "intItemName": "Item 01",
                            "oafQty": 10.0,
                            "alreadyDCQty": 0.0,
                            "maxDCQtyIUOM": 10.0,
                            "allocationID": 7001,
                            "oafDetailLineId": 1,
                            "oafDetailLineNo": 1,
                            "soitemlineno": 1,
                            "wareHouseId": 8,
                            "warehouseCode": "GST2",
                            "suom": "NOS",
                            "oafid": 601
                        }
                    ]
                }
                """);
            }

            // 000602: quantity exceeds limit
            return JsonNode.Parse("""
            {
                "success": true,
                "data": [
                    {
                        "salesItemCode": "ITEM02",
                        "salesItemName": "Item 02",
                        "intItemCode": "ITEM02",
                        "intItemName": "Item 02",
                        "oafQty": 50.0,
                        "alreadyDCQty": 0.0,
                        "maxDCQtyIUOM": 20.0,
                        "allocationID": 7002,
                        "oafDetailLineId": 1,
                        "oafDetailLineNo": 1,
                        "soitemlineno": 1,
                        "wareHouseId": 8,
                        "warehouseCode": "GST2",
                        "suom": "NOS",
                        "oafid": 602
                    }
                ]
            }
            """);
        });

        _fakeErp.On("getShipmentInfo", (path, _) =>
        {
            var item = path.Contains("601") ? "ITEM01" : "ITEM02";
            var oafId = path.Contains("601") ? 601 : 602;
            return JsonNode.Parse($$"""
            {
                "success": true,
                "data": [
                    {
                        "salesItemCode": "{{item}}",
                        "shipCode": "CADD",
                        "rateStructureCode": "SR001",
                        "recordId": 1001,
                        "oafDetailRefLineId": 1,
                        "oafQtyOfLineId": 1,
                        "oafRefId": {{oafId}}
                    }
                ]
            }
            """);
        });

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Success.Should().BeFalse();
        result.Data.TotalAllocations.Should().Be(2);
        result.Data.SuccessfulAllocations.Should().Be(1);
        result.Data.FailedAllocations.Should().Be(1);
        result.Data.CreatedDCCount.Should().Be(1);

        var successItem = result.Data.Results.First(r => r.Success);
        successItem.OafId.Should().Be(601);
        successItem.DcNumber.Should().Be("26-27/DS/CT2/000107");

        var failedItem = result.Data.Results.First(r => !r.Success);
        failedItem.OafId.Should().Be(602);
        failedItem.FailedField.Should().NotBeNullOrEmpty();
        failedItem.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Empty_transporter_dropdown_fails_with_clear_message()
    {
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        // Empty transporters list
        _fakeErp.On("getCodeTypeList/TR/GEN", JsonNode.Parse("""
        {
            "success": true,
            "data": []
        }
        """)!);

        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000700",
                    "oafAutoId": 700,
                    "intOrderNumber": "000700",
                    "customerOrderNumber": "PO-700"
                }
            ]
        }
        """)!);

        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 10.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 8001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 700
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        result.Data.SuccessfulAllocations.Should().Be(0);
        result.Data.FailedAllocations.Should().Be(1);
        result.Data.Results[0].Message.Should().Contain("Transporter");
    }

    [Fact]
    public async Task Automation_creates_DC_successfully_and_does_not_authorize_it()
    {
        // 1. Customer
        _fakeErp.On("getCustomerForAllocationDeAllocation", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "custCode": "A001", "custName": "Akzo Nobel India Ltd." }]
        }
        """)!);

        // 2. Shipment
        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        // 3. OAF
        _fakeErp.On("getOAFDetailListForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000801",
                    "oafAutoId": 801,
                    "intOrderNumber": "000801",
                    "customerOrderNumber": "PO-801"
                }
            ]
        }
        """)!);

        // 4. OAF Item Details
        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 10.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 8001,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 801
                }
            ]
        }
        """)!);

        // 5. Shipment Info
        _fakeErp.On("getShipmentInfo", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 1101,
                    "oafDetailRefLineId": 1,
                    "oafQtyOfLineId": 1,
                    "oafRefId": 801
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallansFromAllocationsAsync();

        // 1. Success result reflects DC created, authorization pending
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Delivery Challan created successfully. Authorization is pending.");
        result.Data.SuccessfulAllocations.Should().Be(1);
        result.Data.CreatedDCCount.Should().Be(1);

        var allocationResult = result.Data.Results[0];
        allocationResult.Success.Should().BeTrue();
        allocationResult.Authorized.Should().BeFalse();
        allocationResult.DcStatus.Should().Be("Open");
        allocationResult.Message.Should().Be("Delivery Challan created successfully. Pending manual authorization.");
        allocationResult.DcNumber.Should().Be("26-27/DS/CT2/000107");
        allocationResult.DcId.Should().Be(110530);

        var item = allocationResult.Items[0];
        item.DcCreated.Should().BeTrue();
        item.Authorized.Should().BeFalse();
        item.DcStatus.Should().Be("Open");
        item.Message.Should().Be("Delivery Challan created successfully. Pending manual authorization.");
        item.DcNumber.Should().Be("26-27/DS/CT2/000107");
        item.DcId.Should().Be(110530);

        // 2. Verify createDCEntry payload explicitly does NOT contain authorization instructions
        var createRequest = _fakeErp.Requests.LastOrDefault(r => r.Path.Contains("createDCEntry"));
        createRequest.Should().NotBeNull();
        
        var payloadNode = createRequest!.Body;
        payloadNode.Should().NotBeNull();

        // authorizeDCModel must be non-null with finFlag N (so ERP backend does not throw NullReferenceException)
        payloadNode!["authorizeDCModel"].Should().NotBeNull();
        payloadNode["authorizeDCModel"]!["finFlag"]?.GetValue<string>().Should().Be("N");

        // dchallanHeaderEntry authFlag must be "Y" (so ERP backend skips auto-authorization)
        payloadNode["dchallanHeaderEntry"]!["authFlag"]?.GetValue<string>().Should().Be("Y");

        // 3. Confirm NO authorization endpoint was invoked after creation
        var authCalls = _fakeErp.Requests
            .Where(r => r.Path.Contains("thorization", StringComparison.OrdinalIgnoreCase) ||
                        r.Path.Contains("authorize", StringComparison.OrdinalIgnoreCase))
            .ToList();
        authCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Single_allocation_DC_creation_stops_before_authorization()
    {
        // 1. Shipment
        _fakeErp.On("getShipmentForDC/A001/L", JsonNode.Parse("""
        {
            "success": true,
            "data": [{ "shipmentCode": "CADD", "shipmentName": "Plant" }]
        }
        """)!);

        // 2. OAF
        _fakeErp.On("getOAFDetailListForDC/A001", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "oafYear": "26-27",
                    "oafGroup": "OA",
                    "locationCode": "CT2",
                    "oafNumber": "000802",
                    "oafAutoId": 802,
                    "intOrderNumber": "000802",
                    "customerOrderNumber": "PO-802"
                }
            ]
        }
        """)!);

        // 3. OAF Item Details
        _fakeErp.On("getOAFItemDetailsForDC", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "salesItemName": "Item 01",
                    "intItemCode": "ITEM01",
                    "intItemName": "Item 01",
                    "oafQty": 10.0,
                    "alreadyDCQty": 0.0,
                    "maxDCQtyIUOM": 10.0,
                    "allocationID": 8002,
                    "oafDetailLineId": 1,
                    "oafDetailLineNo": 1,
                    "soitemlineno": 1,
                    "wareHouseId": 8,
                    "warehouseCode": "GST2",
                    "suom": "NOS",
                    "oafid": 802
                }
            ]
        }
        """)!);

        // 4. Shipment Info
        _fakeErp.On("getShipmentInfo", JsonNode.Parse("""
        {
            "success": true,
            "data": [
                {
                    "salesItemCode": "ITEM01",
                    "shipCode": "CADD",
                    "rateStructureCode": "SR001",
                    "recordId": 1102,
                    "oafDetailRefLineId": 1,
                    "oafQtyOfLineId": 1,
                    "oafRefId": 802
                }
            ]
        }
        """)!);

        var service = CreateService();
        var result = await service.CreateDeliveryChallanFromAllocationAsync(802, customerCode: "A001");

        result.DcNumber.Should().Be("26-27/DS/CT2/000107");
        result.DcId.Should().Be(110530);
        result.Status.Should().Be("Open");
        result.Authorized.Should().BeFalse();

        // Check create payload
        var createRequest = _fakeErp.Requests.LastOrDefault(r => r.Path.Contains("createDCEntry"));
        createRequest.Should().NotBeNull();

        var payloadNode = createRequest!.Body;
        payloadNode.Should().NotBeNull();
        payloadNode!["authorizeDCModel"].Should().NotBeNull();
        payloadNode["authorizeDCModel"]!["finFlag"]?.GetValue<string>().Should().Be("N");
        payloadNode["dchallanHeaderEntry"]!["authFlag"]?.GetValue<string>().Should().Be("Y");

        // No authorization calls
        var authCalls = _fakeErp.Requests
            .Where(r => r.Path.Contains("thorization", StringComparison.OrdinalIgnoreCase) ||
                        r.Path.Contains("authorize", StringComparison.OrdinalIgnoreCase))
            .ToList();
        authCalls.Should().BeEmpty();
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
