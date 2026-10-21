using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;

namespace NewHorizon.Automation.ErpClient.Flows.QuotationToSo;

/// <summary>
/// Implements Quotation to Sales Order automation by calling the ERP's existing API endpoints.
/// </summary>
public sealed class QuotationToSoService : IQuotationToSoService
{
    private readonly HttpClient _httpClient;
    private readonly int _companyId;
    private readonly string _companyCode;
    private readonly IClock _clock;
    private readonly ILogger<QuotationToSoService> _logger;

    public QuotationToSoService(
        IHttpClientFactory httpClientFactory,
        IOptions<AutomationAgentOptions> options,
        IClock clock,
        ILogger<QuotationToSoService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _companyId = options.Value.ErpApi.CompanyId;
        _companyCode = "NH1";
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OpenQuotationSummary>> GetOpenAuthorisedQuotationsAsync(
        string customerCode,
        string siteCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerCode))
        {
            _logger.LogWarning("GetOpenAuthorisedQuotationsAsync called with empty customerCode.");
            return [];
        }

        var trimmedCust = customerCode.Trim();
        var trimmedSite = siteCode?.Trim() ?? string.Empty;
        var locParam = !string.IsNullOrWhiteSpace(trimmedSite) ? Uri.EscapeDataString(trimmedSite) : "%20";

        var url = $"api/v1/sales/salesorderentry/getOpenQuotationDetailsForSO/{Uri.EscapeDataString(trimmedCust)}/{locParam}/%20/0/false";

        _logger.LogInformation(
            "Querying open authorised quotations from ERP for customer {CustomerCode} and site {SiteCode}",
            trimmedCust,
            trimmedSite);

        using var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "ERP returned HTTP {StatusCode} when fetching open quotations for customer {CustomerCode}. Response: {Response}",
                (int)response.StatusCode,
                trimmedCust,
                errorBody);
            return [];
        }

        using var jsonDoc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = jsonDoc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (!data.TryGetProperty("quotationDetails", out var qtnDetailsArr) || qtnDetailsArr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<OpenQuotationSummary>();

        foreach (var item in qtnDetailsArr.EnumerateArray())
        {
            var qtnId = GetIntProperty(item, "quotationID", "quotationId", "qtnID");
            var qtnNumFormatted = GetStringProperty(item, "qtnNumber", "quotationNumber", "qtn Number");
            var qtnYear = GetStringProperty(item, "quotationYear", "qtnYear");
            var qtnGroup = GetStringProperty(item, "quotationGroup", "qtnGroup");
            var qtnNum = GetStringProperty(item, "quotationNumber", "qtnNum");
            var qtnSiteCode = GetStringProperty(item, "quotationSiteCode", "qtnLocCode") ?? trimmedSite;
            var qtnSiteId = GetIntProperty(item, "quotationSiteId", "qtnSiteId");
            var custCode = GetStringProperty(item, "customerCode", "custCD") ?? trimmedCust;
            var custName = GetStringProperty(item, "customerName");
            var currency = GetStringProperty(item, "quotationCurrency", "qtnCurrency");
            var salesmanCode = GetStringProperty(item, "salesmanCode");
            var salesmanName = GetStringProperty(item, "salesmanName");
            var consultantCode = GetStringProperty(item, "consultantCode");
            var consultantName = GetStringProperty(item, "consultantName");
            var agentCode = GetStringProperty(item, "agentCode");
            var inqNo = GetStringProperty(item, "inquiryNo", "inqNo");
            var gstNo = GetStringProperty(item, "gstno", "gstNo");
            var revNo = GetIntProperty(item, "revisionNo");

            DateTime? qtnDate = null;
            if (item.TryGetProperty("quotationDate", out var qdElem) && qdElem.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(qdElem.GetString(), CultureInfo.InvariantCulture, out var dt))
                {
                    qtnDate = dt;
                }
            }

            if (qtnId > 0 && !string.IsNullOrWhiteSpace(qtnNumFormatted))
            {
                results.Add(new OpenQuotationSummary
                {
                    QuotationId = qtnId,
                    QuotationNumber = qtnNumFormatted,
                    QtnYear = qtnYear ?? string.Empty,
                    QtnGroup = qtnGroup ?? string.Empty,
                    QtnNum = qtnNum ?? string.Empty,
                    QuotationSiteCode = qtnSiteCode,
                    QuotationSiteId = qtnSiteId,
                    CustomerCode = custCode,
                    CustomerName = custName,
                    QuotationDate = qtnDate,
                    RevisionNo = revNo,
                    QuotationCurrency = currency,
                    SalesmanCode = salesmanCode,
                    SalesmanName = salesmanName,
                    ConsultantCode = consultantCode,
                    ConsultantName = consultantName,
                    AgentCode = agentCode,
                    InquiryNo = inqNo,
                    GstNo = gstNo,
                });
            }
        }

        _logger.LogInformation(
            "Found {Count} open authorised quotations for customer {CustomerCode} and site {SiteCode}",
            results.Count,
            trimmedCust,
            trimmedSite);

        return results;
    }

    /// <inheritdoc/>
    public async Task<QuotationConversionItemResult> ConvertSingleQuotationAsync(
        OpenQuotationSummary quotation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotation);

        _logger.LogInformation(
            "Fetching quotation details for Quotation {QuotationNumber} (Id: {QuotationId})",
            quotation.QuotationNumber,
            quotation.QuotationId);

        // Step 1: Fetch full Quotation structure from ERP
        var detailsUrl = $"api/v1/sales/QuotationEntry/getQuotationDetail/{Uri.EscapeDataString(quotation.QtnYear)}/{Uri.EscapeDataString(quotation.QtnGroup)}/{Uri.EscapeDataString(quotation.QtnNum)}/{quotation.QuotationSiteId}/0/SEARCH/10";

        using var qtnResponse = await _httpClient.PostAsJsonAsync(detailsUrl, new { }, cancellationToken);

        if (!qtnResponse.IsSuccessStatusCode)
        {
            var err = await qtnResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to fetch details for quotation {QuotationNumber}: HTTP {StatusCode} - {Error}",
                quotation.QuotationNumber,
                (int)qtnResponse.StatusCode,
                err);

            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = false,
                ErrorMessage = $"Failed to fetch quotation details from ERP (HTTP {(int)qtnResponse.StatusCode}): {err}",
            };
        }

        using var qtnJson = await JsonDocument.ParseAsync(
            await qtnResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = qtnJson.RootElement;
        if (!root.TryGetProperty("data", out var qtnData) || qtnData.ValueKind != JsonValueKind.Object)
        {
            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = false,
                ErrorMessage = "ERP returned empty quotation data structure.",
            };
        }

        // Step 2: Build the SalesOrderReceiptEntrySaveModel JSON payload
        var orderDate = _clock.UtcNow.LocalDateTime;
        var domesticCurrency = "INR";

        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            qtnData,
            quotation,
            _companyId,
            _companyCode,
            domesticCurrency,
            orderDate);

        // Step 3: Call ERP Create Sales Order API
        const string createUrl = "api/v1/sales/salesorderentry/createSalesOrderEntry";

        _logger.LogInformation(
            "Creating Sales Order Receipt Entry for Quotation {QuotationNumber}",
            quotation.QuotationNumber);

        using var createResponse = await _httpClient.PostAsJsonAsync(createUrl, payload, cancellationToken);
        var responseContent = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ERP rejected Sales Order creation for quotation {QuotationNumber}. HTTP {StatusCode} - {Response}",
                quotation.QuotationNumber,
                (int)createResponse.StatusCode,
                responseContent);

            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = false,
                ErrorMessage = ParseErpErrorMessage(responseContent) ?? $"HTTP {(int)createResponse.StatusCode}: {responseContent}",
            };
        }

        // Step 4: Parse created Sales Order confirmation
        var (soNumber, soId, error) = ParseCreateResponse(responseContent);

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning(
                "ERP returned an error for quotation {QuotationNumber}: {Error}",
                quotation.QuotationNumber,
                error);

            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = false,
                ErrorMessage = error,
            };
        }

        _logger.LogInformation(
            "Successfully created Sales Order {SalesOrderNumber} (Id: {SalesOrderId}) for Quotation {QuotationNumber}",
            soNumber,
            soId,
            quotation.QuotationNumber);

        return new QuotationConversionItemResult
        {
            QuotationId = quotation.QuotationId,
            QuotationNumber = quotation.QuotationNumber,
            Success = true,
            SalesOrderNumber = soNumber,
            SalesOrderId = soId,
        };
    }

    /// <inheritdoc/>
    public async Task<QuotationToSoSweepResult> ConvertQuotationsForCustomerAsync(
        string customerCode,
        string siteCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteCode);

        var trimmedCust = customerCode.Trim();
        var trimmedSite = siteCode.Trim();

        var openQuotations = await GetOpenAuthorisedQuotationsAsync(trimmedCust, trimmedSite, cancellationToken);

        if (openQuotations.Count == 0)
        {
            return new QuotationToSoSweepResult
            {
                CustomerCode = trimmedCust,
                SiteCode = trimmedSite,
                TotalFound = 0,
                Succeeded = 0,
                Failed = 0,
                Details = [],
            };
        }

        var results = new List<QuotationConversionItemResult>();

        foreach (var qtn in openQuotations)
        {
            try
            {
                var itemResult = await ConvertSingleQuotationAsync(qtn, cancellationToken);
                results.Add(itemResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error converting quotation {QuotationNumber} to Sales Order",
                    qtn.QuotationNumber);

                results.Add(new QuotationConversionItemResult
                {
                    QuotationId = qtn.QuotationId,
                    QuotationNumber = qtn.QuotationNumber,
                    Success = false,
                    ErrorMessage = ex.Message,
                });
            }
        }

        return new QuotationToSoSweepResult
        {
            CustomerCode = trimmedCust,
            SiteCode = trimmedSite,
            TotalFound = openQuotations.Count,
            Succeeded = results.Count(r => r.Success),
            Failed = results.Count(r => !r.Success),
            Details = results,
        };
    }

    private static (string? SoNumber, int? SoId, string? Error) ParseCreateResponse(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;

            var success = false;
            if (root.TryGetProperty("success", out var succElem))
            {
                success = succElem.ValueKind is JsonValueKind.True;
            }

            var message = string.Empty;
            if (root.TryGetProperty("message", out var msgElem) && msgElem.ValueKind == JsonValueKind.String)
            {
                message = msgElem.GetString() ?? string.Empty;
            }

            if (!success)
            {
                return (null, null, !string.IsNullOrWhiteSpace(message) ? message : "ERP rejected the request.");
            }

            // Success message format: "Order details successfully inserted. #message#Year/Group/Site/No#OrderId"
            var parts = message.Split('#');
            string? soNumber = null;
            int? soId = null;

            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
            {
                soNumber = parts[2].Trim();
            }

            if (parts.Length > 3 && int.TryParse(parts[3].Trim(), out var parsedId))
            {
                soId = parsedId;
            }

            return (soNumber ?? message, soId, null);
        }
        catch
        {
            return (null, null, "Could not parse ERP response: " + responseContent);
        }
    }

    private static string? ParseErpErrorMessage(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString();
            }
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                return errs[0].ToString();
            }
        }
        catch
        {
            // fallback
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<QuotationConversionItemResult> ConvertQuotationByNumberAsync(
        string quotationNumber,
        string? fallbackCustomerCode,
        string? fallbackSiteCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quotationNumber);

        var trimmedQtnNo = quotationNumber.Trim();
        _logger.LogInformation("Processing conversion for specific Quotation {QuotationNumber}", trimmedQtnNo);

        // Step 1: Look up quotation metadata from ERP
        var qtnInfo = await FindQuotationAsync(trimmedQtnNo, cancellationToken);
        if (qtnInfo is null)
        {
            return new QuotationConversionItemResult
            {
                QuotationId = 0,
                QuotationNumber = trimmedQtnNo,
                Success = false,
                ErrorMessage = $"Quotation '{trimmedQtnNo}' not found in ERP.",
            };
        }

        // Step 2: Validate authorisation
        if (!qtnInfo.IsAuthorized)
        {
            return new QuotationConversionItemResult
            {
                QuotationId = qtnInfo.QuotationId,
                QuotationNumber = qtnInfo.QuotationNumber,
                Success = false,
                ErrorMessage = $"Quotation '{qtnInfo.QuotationNumber}' is not authorised. Only authorised quotations can be converted to Sales Orders.",
            };
        }

        // Step 3: Resolve CustomerCode and SiteCode
        var customerCode = !string.IsNullOrWhiteSpace(qtnInfo.CustomerCode)
            ? qtnInfo.CustomerCode
            : fallbackCustomerCode?.Trim();

        var siteCode = !string.IsNullOrWhiteSpace(qtnInfo.QuotationSiteCode)
            ? qtnInfo.QuotationSiteCode
            : fallbackSiteCode?.Trim();

        // If customer or site is still missing, attempt to fetch details directly from the quotation header
        if (string.IsNullOrWhiteSpace(customerCode) || string.IsNullOrWhiteSpace(siteCode))
        {
            var header = await TryFetchQuotationHeaderAsync(qtnInfo, cancellationToken);
            if (header is not null)
            {
                if (string.IsNullOrWhiteSpace(customerCode) && !string.IsNullOrWhiteSpace(header.Value.CustomerCode))
                {
                    customerCode = header.Value.CustomerCode;
                }
                if (string.IsNullOrWhiteSpace(siteCode) && !string.IsNullOrWhiteSpace(header.Value.SiteCode))
                {
                    siteCode = header.Value.SiteCode;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(customerCode))
        {
            return new QuotationConversionItemResult
            {
                QuotationId = qtnInfo.QuotationId,
                QuotationNumber = qtnInfo.QuotationNumber,
                Success = false,
                ErrorMessage = $"Could not determine Customer Code for Quotation '{trimmedQtnNo}'. Please ensure the quotation exists in the ERP.",
            };
        }

        // Step 4: Fetch open quotation details via Sales Order Entry's discovery endpoint
        var openQuotations = await GetOpenAuthorisedQuotationsAsync(
            customerCode,
            siteCode ?? string.Empty,
            cancellationToken);

        var normalizedInput = trimmedQtnNo.Replace('-', '/');

        var targetQuotation = openQuotations.FirstOrDefault(q =>
            (qtnInfo.QuotationId > 0 && q.QuotationId == qtnInfo.QuotationId)
            || string.Equals(q.QuotationNumber, qtnInfo.QuotationNumber, StringComparison.OrdinalIgnoreCase)
            || string.Equals(q.QtnNum, qtnInfo.QtnNum, StringComparison.OrdinalIgnoreCase)
            || string.Equals(q.QuotationNumber, trimmedQtnNo, StringComparison.OrdinalIgnoreCase)
            || string.Equals(q.QuotationNumber.Replace('-', '/'), normalizedInput, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(qtnInfo.QtnNum) && q.QuotationNumber.EndsWith(qtnInfo.QtnNum, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(qtnInfo.QtnNum) && string.Equals(q.QtnNum, qtnInfo.QtnNum, StringComparison.OrdinalIgnoreCase)));

        if (targetQuotation is null)
        {
            // If authorised but not returned by getOpenQuotationDetailsForSO, it is either already converted or closed.
            return new QuotationConversionItemResult
            {
                QuotationId = qtnInfo.QuotationId,
                QuotationNumber = qtnInfo.QuotationNumber,
                Success = false,
                ErrorMessage = $"Quotation '{qtnInfo.QuotationNumber}' is not available for Sales Order creation (it may already be converted to a Sales Order or closed).",
            };
        }

        // Step 5: Convert the quotation to a Sales Order Receipt
        return await ConvertSingleQuotationAsync(targetQuotation, cancellationToken);
    }

    private async Task<QuotationLookupInfo?> FindQuotationAsync(
        string quotationNumber,
        CancellationToken cancellationToken)
    {
        var parsedSite = string.Empty;
        var year = string.Empty;
        var grp = string.Empty;
        var num = quotationNumber.Trim();

        // Check for canonical slash format: "NF1/26-27/TE/000005"
        if (quotationNumber.Contains('/'))
        {
            var slashParts = quotationNumber.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (slashParts.Length == 4)
            {
                parsedSite = slashParts[0]; // "NF1"
                year = slashParts[1];       // "26-27"
                grp = slashParts[2];        // "TE"
                num = slashParts[3];        // "000005"
            }
            else if (slashParts.Length == 3)
            {
                year = slashParts[0];
                grp = slashParts[1];
                num = slashParts[2];
            }
        }
        else
        {
            var dashParts = quotationNumber.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dashParts.Length >= 4)
            {
                parsedSite = dashParts[0];
                num = dashParts[^1];
                grp = dashParts[^2];
                year = string.Join("-", dashParts[1..^2]);
            }
            else if (dashParts.Length == 3)
            {
                if (int.TryParse(dashParts[0], out _))
                {
                    year = dashParts[0];
                    grp = dashParts[1];
                    num = dashParts[2];
                }
                else
                {
                    parsedSite = dashParts[0];
                    grp = dashParts[1];
                    num = dashParts[2];
                }
            }
            else if (dashParts.Length == 2)
            {
                year = dashParts[0];
                num = dashParts[1];
            }
        }

        // Strategy 1: Search via selectQtnFieldsOnSearchCriteria (QTNDETAILS)
        try
        {
            const string searchCriteriaUrl = "api/v1/sales/QuotationEntry/selectQtnFieldsOnSearchCriteria";
            var criteriaModel = new
            {
                customerCode = string.Empty,
                salesPersonCode = string.Empty,
                siteId = "0",
                qtnYear = year,
                qtnGrp = grp,
                qtnNum = num,
                userLocations = "0",
                criteria = "QTNDETAILS",
                mode = "SEARCH",
                companyId = string.Empty,
                userId = "1",
            };

            using var criteriaResponse = await _httpClient.PostAsJsonAsync(searchCriteriaUrl, criteriaModel, cancellationToken);
            if (criteriaResponse.IsSuccessStatusCode)
            {
                using var jsonDoc = await JsonDocument.ParseAsync(
                    await criteriaResponse.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElem.EnumerateArray())
                    {
                        var autoId = GetIntProperty(item, "autoID", "autoId", "id");
                        var qtnYear = GetStringProperty(item, "qtnYear", "quotationYear", "year");
                        var qtnGrp = GetStringProperty(item, "qtnGroup", "quotationGroup", "group");
                        var qtnNum = GetStringProperty(item, "qtnNumber", "quotationNumber", "number") ?? num;
                        var siteId = GetIntProperty(item, "siteID", "siteId");
                        var locCode = GetStringProperty(item, "locationCode", "siteCode", "mloccode");
                        if (string.IsNullOrWhiteSpace(locCode) && !string.IsNullOrWhiteSpace(parsedSite))
                        {
                            locCode = parsedSite;
                        }
                        var custCode = GetStringProperty(item, "customerCode", "custCode", "custCD", "xqdCustCd");
                        var authBy = GetIntProperty(item, "authBy", "authorizerId");
                        var isAuth = authBy > 0 || GetBoolProperty(item, "isAuthorise", "isAuthorized");

                        return new QuotationLookupInfo(
                            autoId,
                            quotationNumber,
                            qtnYear ?? year,
                            qtnGrp ?? grp,
                            qtnNum,
                            siteId,
                            locCode ?? string.Empty,
                            custCode ?? string.Empty,
                            isAuth);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query selectQtnFieldsOnSearchCriteria (QTNDETAILS) for {QuotationNumber}", quotationNumber);
        }

        // Strategy 2: Search via selectQtnFieldsOnSearchCriteria (QTNNO)
        try
        {
            const string searchCriteriaUrl = "api/v1/sales/QuotationEntry/selectQtnFieldsOnSearchCriteria";
            var criteriaModel = new
            {
                customerCode = string.Empty,
                salesPersonCode = string.Empty,
                siteId = "0",
                qtnYear = year,
                qtnGrp = grp,
                qtnNum = num,
                userLocations = "0",
                criteria = "QTNNO",
                mode = "SEARCH",
                companyId = string.Empty,
                userId = "1",
            };

            using var criteriaResponse = await _httpClient.PostAsJsonAsync(searchCriteriaUrl, criteriaModel, cancellationToken);
            if (criteriaResponse.IsSuccessStatusCode)
            {
                using var jsonDoc = await JsonDocument.ParseAsync(
                    await criteriaResponse.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElem.EnumerateArray())
                    {
                        var qtnId = GetIntProperty(item, "qtnID", "qtnId", "id");
                        var qtnNo = GetStringProperty(item, "qtnNo", "qtnNumber", "quotationNumber") ?? quotationNumber;
                        var custCode = GetStringProperty(item, "customerCode", "custCode", "custCD");

                        return new QuotationLookupInfo(
                            qtnId,
                            qtnNo,
                            year,
                            grp,
                            num,
                            0,
                            parsedSite,
                            custCode ?? string.Empty,
                            true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query selectQtnFieldsOnSearchCriteria (QTNNO) for {QuotationNumber}", quotationNumber);
        }

        // Strategy 3: Search via quotationEntryList
        try
        {
            const string listUrl = "api/v1/sales/QuotationEntry/quotationEntryList";
            var filter = new
            {
                pageSize = 50,
                pageNumber = 1,
                sortField = "Date",
                sortDirection = "desc",
                searchValue = quotationNumber,
                userLocationIds = "0",
                authStatus = "",
                loginUser = 1,
                restcoresalestrans = false,
                docType = "QT",
                docSubType = "QT",
            };

            using var listResponse = await _httpClient.PostAsJsonAsync(listUrl, filter, cancellationToken);
            if (listResponse.IsSuccessStatusCode)
            {
                using var jsonDoc = await JsonDocument.ParseAsync(
                    await listResponse.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = jsonDoc.RootElement;
                if (root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElem.EnumerateArray())
                    {
                        var qId = GetIntProperty(item, "id", "qtnID", "quotationID");
                        var qNumber = GetStringProperty(item, "number", "qtnNumber", "quotationNumber");
                        var qYear = GetStringProperty(item, "year", "qtnYear");
                        var qGroup = GetStringProperty(item, "group", "qtnGroup");
                        var sId = GetIntProperty(item, "siteId", "siteID");
                        var sCode = GetStringProperty(item, "siteCode");
                        if (string.IsNullOrWhiteSpace(sCode) && !string.IsNullOrWhiteSpace(parsedSite))
                        {
                            sCode = parsedSite;
                        }
                        var cCode = GetStringProperty(item, "customerCode");
                        var isAuth = GetBoolProperty(item, "isAuthorise", "isAuthorized");

                        if (!string.IsNullOrWhiteSpace(qNumber) &&
                            (string.Equals(qNumber, quotationNumber, StringComparison.OrdinalIgnoreCase)
                             || qNumber.EndsWith(quotationNumber, StringComparison.OrdinalIgnoreCase)
                             || quotationNumber.EndsWith(qNumber, StringComparison.OrdinalIgnoreCase)
                             || qNumber.EndsWith(num, StringComparison.OrdinalIgnoreCase)))
                        {
                            return new QuotationLookupInfo(
                                qId,
                                qNumber,
                                qYear ?? year,
                                qGroup ?? grp,
                                qNumber,
                                sId,
                                sCode ?? string.Empty,
                                cCode ?? string.Empty,
                                isAuth);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query quotationEntryList for {QuotationNumber}", quotationNumber);
        }

        return null;
    }

    private async Task<(string? CustomerCode, string? SiteCode)?> TryFetchQuotationHeaderAsync(
        QuotationLookupInfo qtnInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var yearParam = !string.IsNullOrWhiteSpace(qtnInfo.QtnYear) ? qtnInfo.QtnYear : "0";
            var grpParam = !string.IsNullOrWhiteSpace(qtnInfo.QtnGroup) ? qtnInfo.QtnGroup : "0";
            var numParam = !string.IsNullOrWhiteSpace(qtnInfo.QtnNum) ? qtnInfo.QtnNum : qtnInfo.QuotationNumber;
            var siteIdParam = qtnInfo.QuotationSiteId > 0 ? qtnInfo.QuotationSiteId : 0;

            var detailsUrl = $"api/v1/sales/QuotationEntry/getQuotationDetail/{Uri.EscapeDataString(yearParam)}/{Uri.EscapeDataString(grpParam)}/{Uri.EscapeDataString(numParam)}/{siteIdParam}/0/SEARCH/10";

            using var response = await _httpClient.PostAsJsonAsync(detailsUrl, new { }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);

            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("quotationDetails", out var qd) && qd.ValueKind == JsonValueKind.Object)
                {
                    var custCode = GetStringProperty(qd, "customerCode", "custCode", "custCD");
                    var siteCode = GetStringProperty(qd, "quotationSiteCode", "siteCode", "locCode", "locationCode");
                    return (custCode, siteCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch header for quotation {QuotationNumber}", qtnInfo.QuotationNumber);
        }

        return null;
    }

    private sealed record QuotationLookupInfo(
        int QuotationId,
        string QuotationNumber,
        string QtnYear,
        string QtnGroup,
        string QtnNum,
        int QuotationSiteId,
        string QuotationSiteCode,
        string CustomerCode,
        bool IsAuthorized);

    private static string? GetStringProperty(JsonElement elem, params string[] names)
    {
        if (elem.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in elem.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        return prop.Value.ToString();
                    }
                }
            }
        }

        return null;
    }

    private static int GetIntProperty(JsonElement elem, params string[] names)
    {
        if (elem.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        foreach (var prop in elem.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var i))
                    {
                        return i;
                    }
                    if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var isStr))
                    {
                        return isStr;
                    }
                }
            }
        }

        return 0;
    }

    private static bool GetBoolProperty(JsonElement elem, params string[] names)
    {
        if (elem.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in elem.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind is JsonValueKind.True) return true;
                    if (prop.Value.ValueKind is JsonValueKind.False) return false;
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString()?.Trim();
                        return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(s, "Y", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(s, "1", StringComparison.OrdinalIgnoreCase);
                    }
                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var i))
                    {
                        return i > 0;
                    }
                }
            }
        }

        return false;
    }
}
