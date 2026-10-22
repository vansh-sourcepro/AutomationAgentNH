using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewHorizon.Automation.ErpClient.Flows.QuotationToSo;

/// <summary>
/// Constructs the complete <c>SalesOrderReceiptEntrySaveModel</c> JSON payload from Quotation details.
/// </summary>
public static class QuotationToSoPayloadBuilder
{
    public static JsonObject BuildSavePayload(
        JsonElement qtnData,
        OpenQuotationSummary summary,
        int companyId,
        string companyCode,
        string domesticCurrency,
        DateTime orderDate,
        string soGroup = "SW")
    {
        ArgumentNullException.ThrowIfNull(summary);

        var qtnHeader = GetFirstElementOrSelf(qtnData, "quotationDetails");

        var customerCode = summary.CustomerCode;
        var customerName = summary.CustomerName ?? GetString(qtnHeader, "customerName") ?? customerCode;
        var billToCode = GetString(qtnHeader, "billToCustomerCode") ?? customerCode;
        var salesmanCode = GetString(qtnHeader, "salesPersonCode") ?? summary.SalesmanCode ?? string.Empty;
        var consultantCode = GetString(qtnHeader, "consultantCode") ?? summary.ConsultantCode ?? string.Empty;
        var currencyCode = GetString(qtnHeader, "currencyCode") ?? summary.QuotationCurrency ?? domesticCurrency;
        var bookCode = GetString(qtnHeader, "xqdbookcd") ?? GetString(qtnHeader, "bookCode") ?? string.Empty;
        var agentCode = GetString(qtnHeader, "agentCode") ?? summary.AgentCode ?? string.Empty;
        var isAgent = GetBool(qtnHeader, "isAgentAssociated") || !string.IsNullOrWhiteSpace(agentCode);
        var soType = GetString(qtnHeader, "quotationTypeSalesOrder") ?? "REG";
        var oafGroup = GetString(qtnHeader, "oafGroup") ?? string.Empty;
        var projectItemCode = GetString(qtnHeader, "projectItemCode") ?? string.Empty;

        var exchangeRate = GetDecimal(qtnHeader, "exchangeRate");
        if (exchangeRate <= 0)
        {
            exchangeRate = 1m;
        }

        var totalAfterDiscountCust = GetDecimal(qtnHeader, "totalAmountAfterDiscountCustomerCurrency");
        var totalAfterDiscountDom = GetDecimal(qtnHeader, "totalAmountAfterDiscountDomesticCurrency");
        var totalAfterTaxDom = GetDecimal(qtnHeader, "totalAmounttAfterTaxDomesticCurrency");
        var totalAfterTaxCust = GetDecimal(qtnHeader, "totalAmountAfterTaxCustomerCurrency");
        var discountType = GetString(qtnHeader, "discountType") ?? string.Empty;
        var discountAmount = GetDecimal(qtnHeader, "discountAmount");

        var formattedOrderDate = orderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var effectiveSoGroup = !string.IsNullOrWhiteSpace(soGroup) ? soGroup : "SW";

        var custPoNo = GetString(qtnHeader, "customerPONumber") ?? GetString(qtnHeader, "poNumber") ?? GetString(qtnHeader, "customerPoNo") ?? string.Empty;
        var custLoiNo = GetString(qtnHeader, "customerLOINumber") ?? GetString(qtnHeader, "loiNumber") ?? string.Empty;
        var effectiveLoiNo = !string.IsNullOrWhiteSpace(custLoiNo)
            ? custLoiNo
            : (!string.IsNullOrWhiteSpace(custPoNo) ? custPoNo : $"LOI/{summary.QtnNum}");

        // SalesOrderDetails section
        var salesOrderDetails = new JsonObject
        {
            ["orderId"] = 0,
            ["customerPONumber"] = custPoNo,
            ["customerPODate"] = !string.IsNullOrWhiteSpace(custPoNo) ? formattedOrderDate : null,
            ["quotationId"] = summary.QuotationId,
            ["quotationYear"] = summary.QtnYear,
            ["quotationGroup"] = summary.QtnGroup,
            ["quotationNumber"] = summary.QtnNum,
            ["quotationDate"] = summary.QuotationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["customerCode"] = customerCode,
            ["customerName"] = customerName,
            ["salesManCode"] = salesmanCode,
            ["consultantCode"] = consultantCode,
            ["attachFlag"] = string.Empty,
            ["totalAmountAfterDiscountCustomerCurrency"] = totalAfterDiscountCust,
            ["totalAmountAfterDiscountDomesticCurrency"] = totalAfterDiscountDom,
            ["totalAmounttAfterTaxDomesticCurrency"] = totalAfterTaxDom,
            ["totalAmountAfterTaxCustomerCurrency"] = totalAfterTaxCust,
            ["discountType"] = discountType,
            ["discountAmount"] = discountAmount,
            ["exchangeRate"] = exchangeRate,
            ["orderStatus"] = "O",
            ["xobCredit"] = string.Empty,
            ["xobcrauth"] = string.Empty,
            ["amendSrNo"] = 0,
            ["ioYear"] = summary.QtnYear,
            ["ioGroup"] = effectiveSoGroup,
            ["ioSiteId"] = summary.QuotationSiteId,
            ["ioSiteCode"] = summary.QuotationSiteCode,
            ["ioDate"] = formattedOrderDate,
            ["custType"] = "CU",
            ["lcDetail"] = "F",
            ["bgDetail"] = "F",
            ["salesOrderType"] = soType,
            ["isAgentAssociated"] = isAgent,
            ["agentCode"] = agentCode,
            ["salesOrderRefNo"] = string.Empty,
            ["buyerCode"] = 0,
            ["currencyCode"] = currencyCode,
            ["bookCode"] = bookCode,
            ["ioNumber"] = string.Empty,
            ["modOfDispatchCode"] = string.Empty,
            ["isFreeSupply"] = false,
            ["isReturnable"] = false,
            ["isRoadPermitReceived"] = false,
            // LOI Strategy: Fulfills "Either PO or LOI is required" rule without colliding with duplicate PO checks.
            ["customerLOINumber"] = effectiveLoiNo,
            ["customerLOIDate"] = formattedOrderDate,
            ["isInterBranchTransfer"] = false,
            ["customerPOId"] = 0,
            ["billToCode"] = billToCode,
            ["billToCreditLimit"] = 0,
            ["billToAccBalance"] = 0,
            ["config"] = "OB",
            ["oafGroup"] = oafGroup,
            ["projectItemCode"] = projectItemCode,
            ["projectName"] = string.Empty,
            ["custContactPersonId"] = string.Empty,
            ["expancebudgetamt"] = 0,
            ["userId"] = 1,
            ["loginUser"] = 1,
        };

        // Root payload
        var payload = new JsonObject
        {
            ["authorizationRequired"] = "Y",
            ["autoNumberRequired"] = "Y",
            ["siteRequired"] = "Y",
            ["fromLocationId"] = summary.QuotationSiteId,
            ["fromLocationCode"] = summary.QuotationSiteCode,
            ["fromLocationName"] = summary.QuotationSiteCode,
            ["companyId"] = companyId,
            ["companyCode"] = companyCode,
            ["domesticCurrencyCode"] = domesticCurrency,
            ["docType"] = "OB",
            ["docSubType"] = "OB",
            ["amdSrNo"] = 0,
            ["userId"] = 1,
            ["loginUser"] = 1,
            ["msctechspecifications"] = true,
            ["salesOrderDetails"] = salesOrderDetails,
            ["modelDetails"] = CopyArrayProperty(qtnData, "modelDetails"),
            ["discountDetails"] = CopyArrayProperty(qtnData, "discountDetails"),
            ["rateStructureDetails"] = CopyArrayProperty(qtnData, "rateStructureDetails"),
            ["deliveryDetails"] = BuildDeliveryDetails(qtnData, orderDate),
            ["paymentDetails"] = CopyArrayProperty(qtnData, "paymentDetails"),
            ["termDetails"] = CopyArrayProperty(qtnData, "termDetails"),
            ["specificationDetails"] = new JsonArray(),
            ["optionalItemDetails"] = new JsonArray(),
            ["textDetails"] = BuildTextDetails(qtnData),
            ["standardTerms"] = CopyArrayProperty(qtnData, "standardTerms"),
            ["historyDetails"] = new JsonArray(),
            ["addOnDetails"] = new JsonArray(),
            ["subItemDetails"] = CopyArrayProperty(qtnData, "subItemDetails"),
            ["noteDetails"] = CopyArrayProperty(qtnData, "noteDetails"),
            ["projectLotDetails"] = new JsonArray(),
            ["equipmentAttributeDetails"] = CopyArrayProperty(qtnData, "equipmentAttributeDetails"),
            ["deletedSalesItemsDetails"] = new JsonArray(),
            ["technicalspec"] = BuildTechnicalSpec(qtnData),
            ["agentcommisiondata"] = CopyArrayProperty(qtnData, "agentcommisiondata"),
        };

        return payload;
    }

    private static JsonArray BuildTechnicalSpec(JsonElement qtnData)
    {
        var result = new JsonArray();
        if (qtnData.TryGetProperty("technicalspec", out var specArray) && specArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in specArray.EnumerateArray())
            {
                var node = JsonNode.Parse(elem.GetRawText());
                if (node is JsonObject obj)
                {
                    obj["xtstrantype"] = "OITEM";
                    result.Add(obj);
                }
            }
        }
        return result;
    }

    private static JsonArray BuildDeliveryDetails(JsonElement qtnData, DateTime orderDate)
    {
        var formattedDelDate = orderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (qtnData.TryGetProperty("deliveryDetails", out var delArray) && delArray.ValueKind == JsonValueKind.Array && delArray.GetArrayLength() > 0)
        {
            var arr = JsonNode.Parse(delArray.GetRawText())?.AsArray() ?? new JsonArray();
            foreach (var node in arr)
            {
                if (node is JsonObject obj)
                {
                    if (string.IsNullOrWhiteSpace(obj["shipmentCode"]?.GetValue<string>()))
                    {
                        obj["shipmentCode"] = "CADD";
                    }
                    if (string.IsNullOrWhiteSpace(obj["expectedInstallationDate"]?.GetValue<string>()))
                    {
                        var delDate = obj["deliveryDate"]?.GetValue<string>();
                        obj["expectedInstallationDate"] = !string.IsNullOrWhiteSpace(delDate) ? delDate : formattedDelDate;
                    }
                    if (string.IsNullOrWhiteSpace(obj["deliveryDate"]?.GetValue<string>()))
                    {
                        obj["deliveryDate"] = formattedDelDate;
                    }
                }
            }
            return arr;
        }

        // If quotation had no explicit delivery schedules, create one default line per model item.
        var result = new JsonArray();
        if (qtnData.TryGetProperty("modelDetails", out var modelArray) && modelArray.ValueKind == JsonValueKind.Array)
        {
            var lineId = 1;

            foreach (var item in modelArray.EnumerateArray())
            {
                var itemCode = GetString(item, "salesItemCode") ?? GetString(item, "itemCode") ?? string.Empty;
                var qty = GetDecimal(item, "qtyIUOM");
                var lineNo = GetInt(item, "itemLineNo");

                result.Add(new JsonObject
                {
                    ["modelNo"] = string.Empty,
                    ["itemCode"] = itemCode,
                    ["itemOrderQty"] = qty,
                    ["orderType"] = string.Empty,
                    ["qtySUOM"] = qty,
                    ["deliveryDate"] = formattedDelDate,
                    ["expectedInstallationDate"] = formattedDelDate,
                    ["amendSrNo"] = 0,
                    ["commitedDelDate"] = null,
                    ["shipmentCode"] = "CADD",
                    ["amendYear"] = string.Empty,
                    ["amendGroup"] = string.Empty,
                    ["amendSiteId"] = 0,
                    ["amendNumber"] = string.Empty,
                    ["amendDate"] = null,
                    ["amendAuthDate"] = null,
                    ["oafQty"] = 0,
                    ["sjoQty"] = 0,
                    ["lineId"] = lineId++,
                    ["itemLineNo"] = lineNo,
                });
            }
        }

        return result;
    }

    private static JsonArray BuildTextDetails(JsonElement qtnData)
    {
        var result = new JsonArray();
        if (qtnData.TryGetProperty("quotationTextDetails", out var textArr) && textArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in textArr.EnumerateArray())
            {
                result.Add(new JsonObject
                {
                    ["termDocType"] = "SO",
                    ["termId"] = GetString(t, "termId") ?? string.Empty,
                    ["remark"] = GetString(t, "remark") ?? string.Empty,
                    ["termLineId"] = GetInt(t, "termLineId"),
                    ["termAmendSrNo"] = 0,
                });
            }
        }
        return result;
    }

    private static JsonArray CopyArrayProperty(JsonElement root, string propName)
    {
        if (root.TryGetProperty(propName, out var elem) && elem.ValueKind == JsonValueKind.Array)
        {
            return JsonNode.Parse(elem.GetRawText())?.AsArray() ?? new JsonArray();
        }
        return new JsonArray();
    }

    private static JsonElement? GetFirstElementOrSelf(JsonElement root, string propName)
    {
        if (root.TryGetProperty(propName, out var elem))
        {
            if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
            {
                return elem[0];
            }
            if (elem.ValueKind == JsonValueKind.Object)
            {
                return elem;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement? elem, string propName)
    {
        if (elem.HasValue && elem.Value.TryGetProperty(propName, out var p) && p.ValueKind == JsonValueKind.String)
        {
            return p.GetString()?.Trim();
        }
        return null;
    }

    private static decimal GetDecimal(JsonElement? elem, string propName)
    {
        if (elem.HasValue && elem.Value.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var d))
            {
                return d;
            }
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds))
            {
                return ds;
            }
        }
        return 0m;
    }

    private static int GetInt(JsonElement? elem, string propName)
    {
        if (elem.HasValue && elem.Value.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i))
            {
                return i;
            }
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var isStr))
            {
                return isStr;
            }
        }
        return 0;
    }

    private static bool GetBool(JsonElement? elem, string propName)
    {
        if (elem.HasValue && elem.Value.TryGetProperty(propName, out var p))
        {
            if (p.ValueKind is JsonValueKind.True) return true;
            if (p.ValueKind is JsonValueKind.False) return false;
            if (p.ValueKind == JsonValueKind.String)
            {
                var s = p.GetString();
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "Y", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s, "1", StringComparison.Ordinal);
            }
        }
        return false;
    }
}
