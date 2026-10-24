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

        var siteId = summary.QuotationSiteId > 0
            ? summary.QuotationSiteId
            : (GetInt(qtnHeader, "siteId") > 0 ? GetInt(qtnHeader, "siteId") : (GetInt(qtnHeader, "locationId") > 0 ? GetInt(qtnHeader, "locationId") : 1));
        var siteCode = !string.IsNullOrWhiteSpace(summary.QuotationSiteCode)
            ? summary.QuotationSiteCode
            : (GetString(qtnHeader, "siteCode") ?? GetString(qtnHeader, "locationCode") ?? "NF1");

        var randomLoiNo = $"LOI-{Random.Shared.Next(100000, 999999)}";

        var rawQtnDate = summary.QuotationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ?? GetString(qtnHeader, "quotationDate")
            ?? GetString(qtnHeader, "qtnDate");
        var qtnDateStr = (!string.IsNullOrWhiteSpace(rawQtnDate) && !rawQtnDate.StartsWith("0001-01-01", StringComparison.Ordinal))
            ? rawQtnDate
            : formattedOrderDate;

        // SalesOrderDetails section - only mandatory fields with red * in ERP UI
        var salesOrderDetails = new JsonObject
        {
            ["orderId"] = 0,
            ["customerPONumber"] = string.Empty,
            ["customerPODate"] = null,
            ["quotationId"] = summary.QuotationId,
            ["quotationYear"] = summary.QtnYear,
            ["quotationGroup"] = summary.QtnGroup,
            ["quotationNumber"] = summary.QtnNum,
            ["quotationDate"] = qtnDateStr,
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
            ["ioSiteId"] = siteId,
            ["ioSiteCode"] = siteCode,
            ["ioDate"] = formattedOrderDate,
            ["custType"] = "CU",
            ["lcDetail"] = "F",
            ["bgDetail"] = "F",
            ["salesOrderType"] = soType,
            ["isAgentAssociated"] = isAgent,
            ["agentCode"] = agentCode,
            ["salesOrderRefNo"] = string.Empty,
            ["buyerCode"] = 0,
            ["soDeliveryDate"] = formattedOrderDate,
            ["sodeliveryDate"] = formattedOrderDate,
            ["currencyCode"] = currencyCode,
            ["bookCode"] = bookCode,
            ["ioNumber"] = string.Empty,
            ["modOfDispatchCode"] = string.Empty,
            ["isFreeSupply"] = false,
            ["isReturnable"] = false,
            ["isRoadPermitReceived"] = false,
            // LOI Strategy: Fulfills "Either PO or LOI is required" rule with a random LOI number and current date
            ["customerLOINumber"] = randomLoiNo,
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

        // Root payload: Populates only mandatory details (salesOrderDetails, modelDetails, deliveryDetails, rateStructureDetails)
        // Skips all optional non-mandatory collections (termDetails, paymentDetails, textDetails, notes, specs, etc.)
        var payload = new JsonObject
        {
            ["authorizationRequired"] = "Y",
            ["autoNumberRequired"] = "Y",
            ["siteRequired"] = "Y",
            ["fromLocationId"] = siteId,
            ["fromLocationCode"] = siteCode,
            ["fromLocationName"] = siteCode,
            ["companyId"] = companyId,
            ["companyCode"] = companyCode,
            ["domesticCurrencyCode"] = domesticCurrency,
            ["docType"] = "OB",
            ["docSubType"] = "OB",
            ["amdSrNo"] = 0,
            ["userId"] = 1,
            ["loginUser"] = 1,
            ["msctechspecifications"] = false,
            ["salesOrderDetails"] = salesOrderDetails,
            ["modelDetails"] = BuildModelDetails(qtnData, orderDate),
            ["discountDetails"] = CopyArrayProperty(qtnData, "discountDetails"),
            ["rateStructureDetails"] = CopyArrayProperty(qtnData, "rateStructureDetails"),
            ["deliveryDetails"] = BuildDeliveryDetails(qtnData, orderDate),
            ["paymentDetails"] = new JsonArray(),
            ["termDetails"] = new JsonArray(),
            ["specificationDetails"] = new JsonArray(),
            ["optionalItemDetails"] = new JsonArray(),
            ["textDetails"] = new JsonArray(),
            ["standardTerms"] = new JsonArray(),
            ["historyDetails"] = new JsonArray(),
            ["addOnDetails"] = new JsonArray(),
            ["subItemDetails"] = new JsonArray(),
            ["noteDetails"] = new JsonArray(),
            ["projectLotDetails"] = new JsonArray(),
            ["equipmentAttributeDetails"] = new JsonArray(),
            ["deletedSalesItemsDetails"] = new JsonArray(),
            ["technicalspec"] = new JsonArray(),
            ["agentcommisiondata"] = new JsonArray(),
        };

        return payload;
    }

    private static JsonArray BuildModelDetails(JsonElement qtnData, DateTime orderDate)
    {
        var formattedDate = orderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (qtnData.TryGetProperty("modelDetails", out var modelArray) && modelArray.ValueKind == JsonValueKind.Array)
        {
            var arr = JsonNode.Parse(modelArray.GetRawText())?.AsArray() ?? new JsonArray();
            foreach (var node in arr)
            {
                if (node is JsonObject obj)
                {
                    obj["docType"] = "O";

                    EnsureValidDate(obj, "costdate", formattedDate);
                    EnsureValidDate(obj, "amendmentDate", formattedDate);
                    EnsureValidDate(obj, "amendmentAuthDate", formattedDate);
                }
            }
            return arr;
        }

        return new JsonArray();
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
                    EnsureValidDate(obj, "deliveryDate", formattedDelDate);
                    EnsureValidDate(obj, "expectedInstallationDate", formattedDelDate);
                    EnsureValidDate(obj, "commitedDelDate", formattedDelDate);
                    EnsureValidDate(obj, "amendDate", formattedDelDate);
                    EnsureValidDate(obj, "amendAuthDate", formattedDelDate);
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
                    ["commitedDelDate"] = formattedDelDate,
                    ["shipmentCode"] = "CADD",
                    ["amendYear"] = string.Empty,
                    ["amendGroup"] = string.Empty,
                    ["amendSiteId"] = 0,
                    ["amendNumber"] = string.Empty,
                    ["amendDate"] = formattedDelDate,
                    ["amendAuthDate"] = formattedDelDate,
                    ["oafQty"] = 0,
                    ["sjoQty"] = 0,
                    ["lineId"] = lineId++,
                    ["itemLineNo"] = lineNo,
                });
            }
        }

        return result;
    }

    private static void EnsureValidDate(JsonObject obj, string propName, string fallbackDate)
    {
        try
        {
            if (!obj.ContainsKey(propName) || obj[propName] == null)
            {
                obj[propName] = fallbackDate;
                return;
            }

            var val = obj[propName]?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(val)
                || val.StartsWith("0001-01-01", StringComparison.Ordinal)
                || val.StartsWith("1900-01-01", StringComparison.Ordinal)
                || val.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                obj[propName] = fallbackDate;
            }
        }
        catch
        {
            obj[propName] = fallbackDate;
        }
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
