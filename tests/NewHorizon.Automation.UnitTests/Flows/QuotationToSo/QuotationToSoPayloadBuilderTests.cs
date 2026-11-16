using System.Text.Json;
using System.Text.Json.Nodes;
using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;
using Xunit;

namespace NewHorizon.Automation.UnitTests.Flows.QuotationToSo;

public sealed class QuotationToSoPayloadBuilderTests
{
    [Fact]
    public void BuildSavePayload_PopulatesLoiStrategyAndAvoidsDuplicatePoChecks()
    {
        // Arrange
        var qtnJson = """
        {
            "quotationDetails": [
                {
                    "customerCode": "C001",
                    "customerName": "Acme Industries",
                    "billToCustomerCode": "C001-BILL",
                    "salesPersonCode": "SP01",
                    "currencyCode": "INR",
                    "exchangeRate": 1.0,
                    "quotationTypeSalesOrder": "REG",
                    "totalAmountAfterDiscountCustomerCurrency": 50000.00,
                    "totalAmountAfterDiscountDomesticCurrency": 50000.00,
                    "totalAmounttAfterTaxDomesticCurrency": 59000.00,
                    "totalAmountAfterTaxCustomerCurrency": 59000.00,
                    "discountType": "P",
                    "discountAmount": 500.00
                }
            ],
            "modelDetails": [
                {
                    "itemLineNo": 1,
                    "salesItemCode": "ITEM001",
                    "salesItemDesc": "Standard Widget",
                    "qtyIUOM": 10.0,
                    "basicPriceIUOM": 5000.00,
                    "discountAmt": 500.00
                }
            ],
            "termDetails": [
                {
                    "termCode": "TRM01",
                    "subTermOrChargeCode": "SUB01"
                }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(qtnJson);

        var summary = new OpenQuotationSummary
        {
            QuotationId = 1234,
            QuotationNumber = "26-27/QT/AHM/000999",
            QtnYear = "26-27",
            QtnGroup = "QT",
            QtnNum = "000999",
            QuotationSiteCode = "AHM",
            QuotationSiteId = 1,
            CustomerCode = "C001",
            CustomerName = "Acme Industries",
            QuotationDate = new DateTime(2026, 10, 1),
            RevisionNo = 0,
            QuotationCurrency = "INR",
        };

        var orderDate = new DateTime(2026, 10, 19);

        // Act
        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            doc.RootElement,
            summary,
            companyId: 1,
            companyCode: "NH1",
            domesticCurrency: "INR",
            orderDate: orderDate);

        // Assert
        Assert.NotNull(payload);
        var soDetails = payload["salesOrderDetails"]?.AsObject();
        Assert.NotNull(soDetails);

        // Verify LOI Strategy
        Assert.StartsWith("LOI-", soDetails["customerLOINumber"]?.GetValue<string>() ?? string.Empty);
        Assert.Equal("2026-10-19", soDetails["customerLOIDate"]?.GetValue<string>());
        Assert.Equal(string.Empty, soDetails["customerPONumber"]?.GetValue<string>());
        Assert.Null(soDetails["customerPODate"]);

        // Verify Header Information
        Assert.Equal(1234, soDetails["quotationId"]?.GetValue<int>());
        Assert.Equal("26-27", soDetails["ioYear"]?.GetValue<string>());
        Assert.Equal("SW", soDetails["ioGroup"]?.GetValue<string>());
        Assert.Equal(1, soDetails["ioSiteId"]?.GetValue<int>());
        Assert.Equal("AHM", soDetails["ioSiteCode"]?.GetValue<string>());
        Assert.Equal("2026-10-19", soDetails["ioDate"]?.GetValue<string>());
        Assert.Equal("C001", soDetails["customerCode"]?.GetValue<string>());
        Assert.Equal("C001-BILL", soDetails["billToCode"]?.GetValue<string>());
        Assert.Equal("INR", soDetails["currencyCode"]?.GetValue<string>());
        Assert.Equal(1m, soDetails["exchangeRate"]?.GetValue<decimal>());
        Assert.Equal(50000m, soDetails["totalAmountAfterDiscountCustomerCurrency"]?.GetValue<decimal>());
        Assert.Equal(59000m, soDetails["totalAmounttAfterTaxDomesticCurrency"]?.GetValue<decimal>());

        // Verify Model Details
        var modelDetails = payload["modelDetails"]?.AsArray();
        Assert.NotNull(modelDetails);
        Assert.Single(modelDetails);
        Assert.Equal("ITEM001", modelDetails[0]?["salesItemCode"]?.GetValue<string>());

        // Verify Delivery Details generated
        var deliveryDetails = payload["deliveryDetails"]?.AsArray();
        Assert.NotNull(deliveryDetails);
        Assert.Single(deliveryDetails);
        Assert.Equal("ITEM001", deliveryDetails[0]?["itemCode"]?.GetValue<string>());
        Assert.Equal(10m, deliveryDetails[0]?["itemOrderQty"]?.GetValue<decimal>());
        Assert.Equal("2026-10-19", deliveryDetails[0]?["deliveryDate"]?.GetValue<string>());

        // Verify Root Metadata
        Assert.Equal("Y", payload["authorizationRequired"]?.GetValue<string>());
        Assert.Equal("Y", payload["autoNumberRequired"]?.GetValue<string>());
        Assert.Equal("NH1", payload["companyCode"]?.GetValue<string>());
        Assert.Equal(1, payload["companyId"]?.GetValue<int>());
    }

    [Fact]
    public void BuildSavePayload_DefaultsToNf1AndResolvesCustomerFromHeader_WhenSummaryCustomerAndSiteAreEmpty()
    {
        // Arrange
        var qtnJson = """
        {
            "quotationDetails": [
                {
                    "customerCode": "A001",
                    "customerName": "ABC LIMITED",
                    "siteCode": "NF1",
                    "siteId": 1,
                    "currencyCode": "INR"
                }
            ],
            "modelDetails": [
                {
                    "itemLineNo": 1,
                    "salesItemCode": "#TYRE18",
                    "qtyIUOM": 34.0,
                    "basicPriceIUOM": 345.00
                }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(qtnJson);

        var summary = new OpenQuotationSummary
        {
            QuotationId = 167,
            QuotationNumber = "000041",
            QtnYear = "26-27",
            QtnGroup = "TE",
            QtnNum = "000041",
            QuotationSiteCode = "",
            QuotationSiteId = 1,
            CustomerCode = "",
            CustomerName = null,
        };

        var orderDate = new DateTime(2026, 11, 13);

        // Act
        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            doc.RootElement,
            summary,
            companyId: 1,
            companyCode: "1",
            domesticCurrency: "INR",
            orderDate: orderDate);

        // Assert
        Assert.NotNull(payload);
        var soDetails = payload["salesOrderDetails"]?.AsObject();
        Assert.NotNull(soDetails);

        Assert.Equal("A001", soDetails["customerCode"]?.GetValue<string>());
        Assert.Equal("ABC LIMITED", soDetails["customerName"]?.GetValue<string>());
        Assert.Equal("NF1", soDetails["ioSiteCode"]?.GetValue<string>());
        Assert.Equal(1, soDetails["ioSiteId"]?.GetValue<int>());
    }

    [Fact]
    public void BuildSavePayload_MapsTechnicalSpecifications_WhenPresentInQuotationData()
    {
        // Arrange
        var qtnJson = """
        {
            "quotationDetails": [
                {
                    "customerCode": "C002",
                    "customerName": "Bajaj Auto",
                    "siteCode": "AHM",
                    "siteId": 2,
                    "currencyCode": "INR"
                }
            ],
            "modelDetails": [
                {
                    "itemLineNo": 1,
                    "salesItemCode": "#BAJAJDOMINAR400",
                    "qtyIUOM": 1.0,
                    "basicPriceIUOM": 250000.00
                }
            ],
            "technicalspec": [
                {
                    "xtstrantype": "QITEM",
                    "xtstscode": "COLOR",
                    "xtsitemcode": "#BAJAJDOMINAR400",
                    "xtstssubcode": "BLK",
                    "xtstssubdesc": "Midnight Black",
                    "xtsvariable": false,
                    "xtsprintseq": 1,
                    "termgroupcode": "SPEC",
                    "termgroupdesc": "Specifications",
                    "checkoptional": false,
                    "optional": "",
                    "itmModelRefNo": 1
                }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(qtnJson);

        var summary = new OpenQuotationSummary
        {
            QuotationId = 500,
            QuotationNumber = "000045",
            QtnYear = "26-27",
            QtnGroup = "TE",
            QtnNum = "000045",
            QuotationSiteCode = "AHM",
            QuotationSiteId = 2,
            CustomerCode = "C002",
            CustomerName = "Bajaj Auto",
        };

        // Act
        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            doc.RootElement,
            summary,
            companyId: 1,
            companyCode: "1",
            domesticCurrency: "INR",
            orderDate: new DateTime(2026, 11, 14));

        // Assert
        Assert.NotNull(payload);
        Assert.True(payload["msctechspecifications"]?.GetValue<bool>());

        var specs = payload["technicalspec"]?.AsArray();
        Assert.NotNull(specs);
        Assert.Single(specs);

        var firstSpec = specs[0]?.AsObject();
        Assert.NotNull(firstSpec);
        Assert.Equal("OITEM", firstSpec["xtstrantype"]?.GetValue<string>());
        Assert.Equal("#BAJAJDOMINAR400", firstSpec["xtsitemcode"]?.GetValue<string>());
        Assert.Equal("COLOR", firstSpec["xtstscode"]?.GetValue<string>());
        Assert.Equal("BLK", firstSpec["xtstssubcode"]?.GetValue<string>());
        Assert.Equal("Midnight Black", firstSpec["xtstssubdesc"]?.GetValue<string>());
        Assert.Equal(1, firstSpec["itmModelRefNo"]?.GetValue<int>());
        Assert.Equal(1, firstSpec["xtscreuserid"]?.GetValue<int>());
    }

    [Fact]
    public void BuildSavePayload_UsesExplicitTechnicalSpecs_WhenProvided()
    {
        // Arrange
        var qtnJson = """
        {
            "quotationDetails": [
                {
                    "customerCode": "C003",
                    "customerName": "Test Corp",
                    "siteCode": "NF1",
                    "siteId": 1
                }
            ],
            "modelDetails": [
                {
                    "itemLineNo": 1,
                    "salesItemCode": "#BAJAJDOMINAR400"
                }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(qtnJson);

        var summary = new OpenQuotationSummary
        {
            QuotationId = 501,
            QuotationNumber = "000046",
            QtnYear = "26-27",
            QtnGroup = "TE",
            QtnNum = "000046",
            QuotationSiteCode = "NF1",
            QuotationSiteId = 1,
            CustomerCode = "C003",
        };

        var fallbackSpecs = new JsonArray
        {
            new JsonObject
            {
                ["xtstrantype"] = "SITEM",
                ["xtstscode"] = "ENGINE",
                ["xtsitemcode"] = "#BAJAJDOMINAR400",
                ["xtstssubcode"] = "400CC",
                ["xtstssubdesc"] = "373cc DOHC Liquid Cooled",
                ["itmModelRefNo"] = 1,
            }
        };

        // Act
        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            doc.RootElement,
            summary,
            companyId: 1,
            companyCode: "1",
            domesticCurrency: "INR",
            orderDate: new DateTime(2026, 11, 14),
            explicitTechnicalSpecs: fallbackSpecs);

        // Assert
        Assert.NotNull(payload);
        Assert.True(payload["msctechspecifications"]?.GetValue<bool>());

        var specs = payload["technicalspec"]?.AsArray();
        Assert.NotNull(specs);
        Assert.Single(specs);

        var firstSpec = specs[0]?.AsObject();
        Assert.NotNull(firstSpec);
        Assert.Equal("OITEM", firstSpec["xtstrantype"]?.GetValue<string>());
        Assert.Equal("ENGINE", firstSpec["xtstscode"]?.GetValue<string>());
        Assert.Equal("#BAJAJDOMINAR400", firstSpec["xtsitemcode"]?.GetValue<string>());
        Assert.Equal(1, firstSpec["xtscreuserid"]?.GetValue<int>());
    }

    [Fact]
    public void BuildSavePayload_SetsMsctechspecificationsFalse_WhenNoSpecs()
    {
        // Arrange
        var qtnJson = """
        {
            "quotationDetails": [
                {
                    "customerCode": "C004",
                    "siteCode": "NF1",
                    "siteId": 1
                }
            ],
            "modelDetails": [
                {
                    "itemLineNo": 1,
                    "salesItemCode": "STANDARDITEM"
                }
            ]
        }
        """;

        using var doc = JsonDocument.Parse(qtnJson);

        var summary = new OpenQuotationSummary
        {
            QuotationId = 502,
            QuotationNumber = "000047",
            QtnYear = "26-27",
            QtnGroup = "TE",
            QtnNum = "000047",
            QuotationSiteCode = "NF1",
            QuotationSiteId = 1,
            CustomerCode = "C004",
        };

        // Act
        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            doc.RootElement,
            summary,
            companyId: 1,
            companyCode: "1",
            domesticCurrency: "INR",
            orderDate: new DateTime(2026, 11, 14));

        // Assert
        Assert.NotNull(payload);
        Assert.False(payload["msctechspecifications"]?.GetValue<bool>());
        var specs = payload["technicalspec"]?.AsArray();
        Assert.NotNull(specs);
        Assert.Empty(specs);
    }

    [Fact]
    public void FormatCanonicalQuotationNumber_Returns4PartCanonical_WhenAlreadyFormatted()
    {
        // Arrange
        const string input = "26-27/TF/NF1/000051";

        // Act
        var result = QuotationToSoService.FormatCanonicalQuotationNumber(input, "26-27", "TF", "NF1", "000051");

        // Assert
        Assert.Equal("26-27/TF/NF1/000051", result);
    }

    [Fact]
    public void FormatCanonicalQuotationNumber_Assembles4PartCanonical_FromConstituentParts()
    {
        // Arrange: Short number "000051" with separate metadata
        const string rawNumber = "000051";

        // Act
        var result = QuotationToSoService.FormatCanonicalQuotationNumber(rawNumber, "26-27", "TF", "NF1", "000051");

        // Assert
        Assert.Equal("26-27/TF/NF1/000051", result);
    }

    [Fact]
    public void FormatCanonicalQuotationNumber_Upgrades3PartQuotation_WhenSiteCodeProvided()
    {
        // Arrange: 3-part quotation "26-27/TF/000051"
        const string raw3Part = "26-27/TF/000051";

        // Act
        var result = QuotationToSoService.FormatCanonicalQuotationNumber(raw3Part, "26-27", "TF", "NF1", "000051");

        // Assert
        Assert.Equal("26-27/TF/NF1/000051", result);
    }

    [Fact]
    public void FormatCanonicalQuotationNumber_FormatsQuotation000045_Correctly()
    {
        // Arrange: Quotation 000045 from recent sweep error
        const string rawNumber = "000045";

        // Act
        var result = QuotationToSoService.FormatCanonicalQuotationNumber(rawNumber, "26-27", "TF", "NF1", "000045");

        // Assert
        Assert.Equal("26-27/TF/NF1/000045", result);
    }

    [Fact]
    public void FormatCanonicalQuotationNumber_FallsBackGracefully_WhenSiteCodeMissing()
    {
        // Arrange: Group & year present, but site code empty
        const string rawNumber = "000052";

        // Act
        var result = QuotationToSoService.FormatCanonicalQuotationNumber(rawNumber, "26-27", "TF", null, "000052");

        // Assert
        Assert.Equal("26-27/TF/000052", result);
    }
}
