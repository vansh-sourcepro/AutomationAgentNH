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
        Assert.Equal("QT", soDetails["ioGroup"]?.GetValue<string>());
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
}
