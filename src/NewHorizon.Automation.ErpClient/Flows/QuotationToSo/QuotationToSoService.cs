using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;

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
    private readonly IQuotationToSoTracker _tracker;
    private readonly ILogger<QuotationToSoService> _logger;

    public QuotationToSoService(
        IHttpClientFactory httpClientFactory,
        IOptions<AutomationAgentOptions> options,
        IClock clock,
        IQuotationToSoTracker tracker,
        ILogger<QuotationToSoService> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClientFactory.CreateClient(DependencyInjection.ErpHttpClientName);
        _companyId = options.Value.ErpApi.CompanyId;
        _companyCode = "NH1";
        _clock = clock;
        _tracker = tracker;
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
            var summary = ParseOpenQuotationSummaryItem(item, trimmedCust, trimmedSite);
            if (summary is not null)
            {
                results.Add(summary);
            }
        }

        // Fallback: If site was specified and returned 0, try wildcard location %20 so location parameter format differences don't hide quotations
        if (results.Count == 0 && locParam != "%20")
        {
            try
            {
                var fbUrl = $"api/v1/sales/salesorderentry/getOpenQuotationDetailsForSO/{Uri.EscapeDataString(trimmedCust)}/%20/%20/0/false";
                using var fbResp = await _httpClient.GetAsync(fbUrl, cancellationToken);
                if (fbResp.IsSuccessStatusCode)
                {
                    using var fbDoc = await JsonDocument.ParseAsync(
                        await fbResp.Content.ReadAsStreamAsync(cancellationToken),
                        cancellationToken: cancellationToken);

                    var fbRoot = fbDoc.RootElement;
                    if (fbRoot.TryGetProperty("data", out var fbData) && fbData.ValueKind == JsonValueKind.Object)
                    {
                        if (fbData.TryGetProperty("quotationDetails", out var fbArr) && fbArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in fbArr.EnumerateArray())
                            {
                                var summary = ParseOpenQuotationSummaryItem(item, trimmedCust, trimmedSite);
                                if (summary is not null && MatchesSiteFilter(trimmedSite, summary.QuotationSiteId, summary.QuotationSiteCode, summary.QuotationNumber))
                                {
                                    results.Add(summary);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query wildcard location fallback for customer {Customer}", trimmedCust);
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

        await _tracker.StartExecutionAsync(new TrackedQuotation(
            quotation.QuotationId,
            quotation.QuotationNumber,
            quotation.QuotationSiteId,
            quotation.CustomerCode,
            quotation.QuotationDate.HasValue ? DateOnly.FromDateTime(quotation.QuotationDate.Value) : null), cancellationToken);

        try
        {
            await _tracker.EnterStageAsync(
                QuotationToSoStages.QuotationDetail,
                QuotationToSoTasks.GetQuotationDetail,
                cancellationToken);

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

            var errorMsg = $"Failed to fetch quotation details from ERP (HTTP {(int)qtnResponse.StatusCode}): {err}";
            await _tracker.FailExecutionAsync(errorMsg, err, transient: false, cancellationToken);

            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = false,
                ErrorMessage = errorMsg,
            };
        }

        using var qtnJson = await JsonDocument.ParseAsync(
            await qtnResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = qtnJson.RootElement;
        if (!root.TryGetProperty("data", out var qtnData) || qtnData.ValueKind != JsonValueKind.Object)
        {
            const string emptyMsg = "ERP returned empty quotation data structure.";
            await _tracker.FailExecutionAsync(emptyMsg, emptyMsg, transient: false, cancellationToken);

            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = false,
                ErrorMessage = emptyMsg,
            };
        }

        // Step 2: Build the SalesOrderReceiptEntrySaveModel JSON payload
        var orderDate = _clock.UtcNow.LocalDateTime;
        var domesticCurrency = "INR";
        var soYear = !string.IsNullOrWhiteSpace(quotation.QtnYear) ? quotation.QtnYear : GetCurrentFinancialYear();
        var soSiteId = quotation.QuotationSiteId > 0 ? quotation.QuotationSiteId : 1;
        var soGroup = await GetDefaultSalesOrderGroupAsync(soYear, soSiteId, cancellationToken);

        var payload = QuotationToSoPayloadBuilder.BuildSavePayload(
            qtnData,
            quotation,
            _companyId,
            _companyCode,
            domesticCurrency,
            orderDate,
            soGroup);

        // Step 2.5: Pre-check if quotation is already converted or closed to avoid duplicate orders
        await _tracker.EnterStageAsync(
            QuotationToSoStages.Validation,
            QuotationToSoTasks.VerifyExistingSalesOrder,
            cancellationToken);

        var preVerified = await VerifyCreatedSalesOrderAsync(quotation, soYear, soGroup, cancellationToken);
        if (preVerified is not null)
        {
            _logger.LogInformation(
                "Quotation {QuotationNumber} is already converted to Sales Order {SoNumber} (Id: {SoId}). Returning existing order without duplicate creation.",
                quotation.QuotationNumber,
                preVerified.Value.SoNumber,
                preVerified.Value.SoId);

            await _tracker.SkipExecutionAsync(
                $"Quotation '{quotation.QuotationNumber}' is already converted to Sales Order '{preVerified.Value.SoNumber}'.",
                cancellationToken);

            return new QuotationConversionItemResult
            {
                QuotationId = quotation.QuotationId,
                QuotationNumber = quotation.QuotationNumber,
                Success = true,
                SalesOrderNumber = preVerified.Value.SoNumber,
                SalesOrderId = preVerified.Value.SoId,
            };
        }

        // Step 3: Call ERP Create Sales Order API (Non-retryable to prevent duplicates on timeout)
        await _tracker.EnterStageAsync(
            QuotationToSoStages.CreateSalesOrder,
            QuotationToSoTasks.CreateSalesOrderEntry,
            cancellationToken);

        const string createUrl = "api/v1/sales/salesorderentry/createSalesOrderEntry";

        _logger.LogInformation(
            "Creating Sales Order Receipt Entry for Quotation {QuotationNumber} (Quotation Group: {QtnGroup}) with Sales Order Group {SoGroup}",
            quotation.QuotationNumber,
            quotation.QtnGroup,
            soGroup);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, createUrl)
        {
            Content = JsonContent.Create(payload),
        };
        createRequest.Options.Set(DependencyInjection.NonRetryableKey, true);

        HttpResponseMessage? createResponse = null;
        string responseContent = string.Empty;

        try
        {
            createResponse = await _httpClient.SendAsync(createRequest, cancellationToken);
            responseContent = await createResponse.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation(
                "ERP createSalesOrderEntry response for Quotation {QuotationNumber} (HTTP {StatusCode}): {Response}",
                quotation.QuotationNumber,
                (int)createResponse.StatusCode,
                responseContent);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or Polly.ExecutionRejectedException)
        {
            _logger.LogWarning(
                ex,
                "createSalesOrderEntry encountered a transport/timeout error for Quotation {QuotationNumber}. Checking database if Sales Order was already created...",
                quotation.QuotationNumber);
        }

        // Step 4: Parse created Sales Order confirmation
        var (soNumber, soId, error) = !string.IsNullOrWhiteSpace(responseContent)
            ? ParseCreateResponse(responseContent)
            : (null, null, null);

        // If ERP reported an error or 500 or timed out, verify if the Sales Order was actually created in the database
        // (ERP's CreateSalesOrderEntry commits the order, but often throws 500 or times out when attempting external notification)
        if (string.IsNullOrWhiteSpace(soNumber) || createResponse is null || !createResponse.IsSuccessStatusCode)
        {
            var verified = await VerifyCreatedSalesOrderAsync(quotation, soYear, soGroup, cancellationToken);
            if (verified is not null)
            {
                soNumber = verified.Value.SoNumber;
                soId = verified.Value.SoId;
                error = null;
            }
            else if (createResponse is null)
            {
                error = "Connection to ERP server timed out and no Sales Order was created.";
            }
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning(
                "ERP returned an error for quotation {QuotationNumber}: {Error}",
                quotation.QuotationNumber,
                error);

            await _tracker.FailExecutionAsync(error, error, transient: false, cancellationToken);

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

        await _tracker.CompleteExecutionAsync(soNumber!, soId, cancellationToken);

        return new QuotationConversionItemResult
        {
            QuotationId = quotation.QuotationId,
            QuotationNumber = quotation.QuotationNumber,
            Success = true,
            SalesOrderNumber = soNumber,
            SalesOrderId = soId,
        };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _tracker.FailExecutionAsync(ex.Message, ex.ToString(), transient: true, cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<QuotationToSoSweepResult> ConvertQuotationsForCustomerAsync(
        string customerCode,
        string? siteCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerCode);

        var trimmedCust = customerCode.Trim();
        var trimmedSite = siteCode?.Trim() ?? string.Empty;

        var openQuotations = await GetOpenAuthorisedQuotationsAsync(trimmedCust, trimmedSite, cancellationToken);

        // Fallback: If no quotations were found via SO discovery API for this customer,
        // query quotationEntryList directly for this customer so location differences or discovery gaps don't drop quotations.
        if (openQuotations.Count == 0)
        {
            _logger.LogInformation("No quotations returned by SO discovery API for customer {Customer}. Checking quotationEntryList fallback...", trimmedCust);
            var entryListQuotations = await DiscoverQuotationsFromEntryListAsync(trimmedCust, trimmedSite, cancellationToken);
            openQuotations = entryListQuotations;
        }

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
                // Fallback retry via numerical search if single conversion needed numerical lookup
                if (!itemResult.Success && !string.IsNullOrWhiteSpace(qtn.QuotationNumber))
                {
                    _logger.LogInformation("Retrying quotation {QuotationNumber} via numerical search fallback...", qtn.QuotationNumber);
                    var fallbackResult = await ConvertQuotationByNumberAsync(qtn.QuotationNumber, qtn.CustomerCode, qtn.QuotationSiteCode, cancellationToken);
                    if (fallbackResult.Success)
                    {
                        itemResult = fallbackResult;
                    }
                }
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

    /// <inheritdoc/>
    public async Task<QuotationToSoSweepResult> ConvertAllEligibleQuotationsAsync(
        string? customerCode,
        string? siteCode,
        CancellationToken cancellationToken)
    {
        var trimmedCust = customerCode?.Trim();
        var trimmedSite = siteCode?.Trim();

        var isAllCustomers = string.IsNullOrWhiteSpace(trimmedCust)
            || string.Equals(trimmedCust, "ALL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmedCust, "MULTIPLE", StringComparison.OrdinalIgnoreCase);

        // If a specific customer is specified, delegate to ConvertQuotationsForCustomerAsync
        if (!isAllCustomers)
        {
            return await ConvertQuotationsForCustomerAsync(trimmedCust!, trimmedSite, cancellationToken);
        }

        _logger.LogInformation("Discovering all eligible open authorised quotations across all customers (Site: {SiteCode})", trimmedSite ?? "ALL");

        var currentFy = GetCurrentFinancialYear();
        var openQuotations = new List<OpenQuotationSummary>();
        var seenQuotationIds = new HashSet<int>();
        var candidateCustomerCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Strategy 1: Direct sweep via quotationEntryList (with authStatus = "" so ERP returns all records)
        try
        {
            var fromEntryList = await DiscoverQuotationsFromEntryListAsync(null, trimmedSite, cancellationToken);
            foreach (var q in fromEntryList)
            {
                if (seenQuotationIds.Add(q.QuotationId))
                {
                    openQuotations.Add(q);
                    if (!string.IsNullOrWhiteSpace(q.CustomerCode))
                    {
                        candidateCustomerCodes.Add(q.CustomerCode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query quotationEntryList while discovering quotations.");
        }

        // Strategy 2: Query selectQtnFieldsOnSearchCriteria for all quotations in the current FY
        try
        {
            const string searchCriteriaUrl = "api/v1/sales/QuotationEntry/selectQtnFieldsOnSearchCriteria";
            var criteriaModel = new
            {
                customerCode = string.Empty,
                salesPersonCode = string.Empty,
                siteId = "0",
                qtnYear = currentFy,
                qtnGrp = string.Empty,
                qtnNum = string.Empty,
                userLocations = "0",
                criteria = "QTNDETAILS",
                mode = "SEARCH",
                companyId = _companyId.ToString(CultureInfo.InvariantCulture),
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
                        if (!IsAuthorizedQuotation(item) || !IsOpenQuotation(item))
                        {
                            continue;
                        }

                        var custCode = GetStringProperty(item, "customerCode", "custCode", "custCD", "xqdCustCd");
                        if (!string.IsNullOrWhiteSpace(custCode))
                        {
                            candidateCustomerCodes.Add(custCode.Trim());
                        }

                        var qtnId = GetIntProperty(item, "quotationId", "qtnID", "autoID", "id");
                        var qNumber = GetStringProperty(item, "qtnNumber", "quotationNumber", "qtn Number");
                        var qYear = GetStringProperty(item, "qtnYear", "quotationYear", "year") ?? currentFy;
                        var qGroup = GetStringProperty(item, "qtnGroup", "quotationGroup", "group") ?? string.Empty;
                        var qSiteId = GetIntProperty(item, "quotationSiteId", "siteID", "siteId");
                        var qSiteCode = GetStringProperty(item, "locationCode", "siteCode", "quotationSiteCode") ?? string.Empty;

                        if (qtnId > 0 && !seenQuotationIds.Contains(qtnId) && MatchesSiteFilter(trimmedSite, qSiteId, qSiteCode, qNumber))
                        {
                            var (yr, grp, num) = ParseQuotationParts(qNumber ?? string.Empty, qYear, qGroup);
                            if (seenQuotationIds.Add(qtnId))
                            {
                                openQuotations.Add(new OpenQuotationSummary
                                {
                                    QuotationId = qtnId,
                                    QuotationNumber = !string.IsNullOrWhiteSpace(qNumber) ? qNumber : $"{yr}/{grp}/{qSiteCode}/{num}",
                                    QtnYear = yr,
                                    QtnGroup = grp,
                                    QtnNum = num,
                                    QuotationSiteCode = qSiteCode,
                                    QuotationSiteId = qSiteId > 0 ? qSiteId : 1,
                                    CustomerCode = custCode ?? string.Empty,
                                    CustomerName = GetStringProperty(item, "customerName"),
                                    QuotationCurrency = "INR",
                                });
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query selectQtnFieldsOnSearchCriteria while discovering candidate customers.");
        }

        // Strategy 3: Query getOpenQuotationDetailsForSO for all discovered candidate customers with wildcard location
        foreach (var cust in candidateCustomerCodes)
        {
            try
            {
                var custQuotations = await GetOpenAuthorisedQuotationsAsync(cust, string.Empty, cancellationToken);
                foreach (var q in custQuotations)
                {
                    if (MatchesSiteFilter(trimmedSite, q.QuotationSiteId, q.QuotationSiteCode, q.QuotationNumber) && seenQuotationIds.Add(q.QuotationId))
                    {
                        openQuotations.Add(q);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error fetching open quotations for customer {CustomerCode}", cust);
            }
        }

        _logger.LogInformation("Discovered {Total} eligible open authorised quotation(s) for conversion", openQuotations.Count);

        if (openQuotations.Count == 0)
        {
            return new QuotationToSoSweepResult
            {
                CustomerCode = "ALL",
                SiteCode = trimmedSite ?? "ALL",
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
                // Fallback retry via numerical search if single conversion needed numerical lookup
                if (!itemResult.Success && !string.IsNullOrWhiteSpace(qtn.QuotationNumber))
                {
                    _logger.LogInformation("Retrying quotation {QuotationNumber} via numerical search fallback...", qtn.QuotationNumber);
                    var fallbackResult = await ConvertQuotationByNumberAsync(qtn.QuotationNumber, qtn.CustomerCode, qtn.QuotationSiteCode, cancellationToken);
                    if (fallbackResult.Success)
                    {
                        itemResult = fallbackResult;
                    }
                }
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
            CustomerCode = "ALL",
            SiteCode = trimmedSite ?? "ALL",
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
                var detailedError = ParseDetailedError(root);
                return (null, null, detailedError);
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

    private static string ParseDetailedError(JsonElement root)
    {
        string? errorMessage = null;
        if (root.TryGetProperty("errorMessage", out var em) && em.ValueKind == JsonValueKind.String)
        {
            var s = em.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s)) errorMessage = s;
        }

        string? errors = null;
        if (root.TryGetProperty("errors", out var errElem))
        {
            if (errElem.ValueKind == JsonValueKind.String)
            {
                var s = errElem.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) errors = s;
            }
            else if (errElem.ValueKind == JsonValueKind.Array && errElem.GetArrayLength() > 0)
            {
                var items = errElem.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x));
                var joined = string.Join("; ", items).Trim();
                if (!string.IsNullOrEmpty(joined)) errors = joined;
            }
        }

        string? userMessage = null;
        if (root.TryGetProperty("userMessage", out var um) && um.ValueKind == JsonValueKind.String)
        {
            var s = um.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s)) userMessage = s;
        }

        string? rawMessage = null;
        if (root.TryGetProperty("message", out var msgElem) && msgElem.ValueKind == JsonValueKind.String)
        {
            var s = msgElem.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s) && !string.Equals(s, "UnhandledErrorKey", StringComparison.OrdinalIgnoreCase))
            {
                rawMessage = s;
            }
        }

        return errorMessage
            ?? errors
            ?? userMessage
            ?? rawMessage
            ?? "ERP rejected the request (UnhandledErrorKey).";
    }

    private static string? ParseErpErrorMessage(string responseContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            return ParseDetailedError(doc.RootElement);
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
        var currentFy = GetCurrentFinancialYear();
        var (rawNum, paddedNum) = NormalizeQuotationNumber(trimmedQtnNo);

        _logger.LogInformation(
            "Processing conversion for Quotation numerical number '{PaddedNum}' (Raw: '{RawNum}') in current financial year '{FinancialYear}'",
            paddedNum,
            rawNum,
            currentFy);

        // Step 1: Look up all candidate quotations matching this number in the financial year
        var candidates = await FindQuotationsAsync(trimmedQtnNo, cancellationToken);
        if (candidates.Count == 0)
        {
            return new QuotationConversionItemResult
            {
                QuotationId = 0,
                QuotationNumber = trimmedQtnNo,
                Success = false,
                ErrorMessage = $"Quotation numerical number '{paddedNum}' for financial year '{currentFy}' was not found in the ERP.",
            };
        }

        _logger.LogInformation(
            "Found {Count} candidate quotation(s) for number '{PaddedNum}' in FY '{FinancialYear}': {Groups}",
            candidates.Count,
            paddedNum,
            currentFy,
            string.Join(", ", candidates.Select(c => $"[Id: {c.QuotationId}, Group: '{c.QtnGroup}', Status: '{c.QtnStatus}']")));

        var candidateNotes = new List<string>();

        // Step 2: Iterate through candidate quotations in priority order
        foreach (var qtnInfo in candidates)
        {
            var candidateDesc = !string.IsNullOrWhiteSpace(qtnInfo.QuotationNumber)
                ? qtnInfo.QuotationNumber
                : $"{currentFy}/{qtnInfo.QtnGroup}/{qtnInfo.QuotationSiteCode}/{qtnInfo.QtnNum}";

            // Check A: Is quotation authorized?
            if (!qtnInfo.IsAuthorized)
            {
                var note = $"Quotation '{candidateDesc}' (Id: {qtnInfo.QuotationId}, Group: '{qtnInfo.QtnGroup}') is not authorised.";
                _logger.LogInformation("{Note}. Moving to next candidate group if available.", note);
                candidateNotes.Add(note);
                continue;
            }

            // Check B: Is quotation already converted or closed?
            if (string.Equals(qtnInfo.QtnStatus, "R", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(qtnInfo.QtnStatus, "C", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackCust = !string.IsNullOrWhiteSpace(qtnInfo.CustomerCode) ? qtnInfo.CustomerCode : fallbackCustomerCode?.Trim();
                var soGroup = await GetDefaultSalesOrderGroupAsync(currentFy, qtnInfo.QuotationSiteId > 0 ? qtnInfo.QuotationSiteId : 1, cancellationToken);

                var summary = new OpenQuotationSummary
                {
                    QuotationId = qtnInfo.QuotationId,
                    QuotationNumber = qtnInfo.QuotationNumber,
                    QtnYear = qtnInfo.QtnYear,
                    QtnGroup = qtnInfo.QtnGroup,
                    QtnNum = qtnInfo.QtnNum,
                    QuotationSiteCode = qtnInfo.QuotationSiteCode,
                    QuotationSiteId = qtnInfo.QuotationSiteId,
                    CustomerCode = fallbackCust ?? string.Empty,
                    CustomerName = qtnInfo.CustomerName,
                    QuotationDate = qtnInfo.QuotationDate,
                    QuotationCurrency = "INR",
                };

                var existingSo = await VerifyCreatedSalesOrderAsync(summary, currentFy, soGroup, cancellationToken);
                if (existingSo is not null)
                {
                    _logger.LogInformation(
                        "Quotation '{CandidateDesc}' (Id: {QuotationId}, Group: '{QtnGroup}') is already converted to Sales Order '{SoNumber}'. Returning existing order.",
                        candidateDesc,
                        qtnInfo.QuotationId,
                        qtnInfo.QtnGroup,
                        existingSo.Value.SoNumber);

                    return new QuotationConversionItemResult
                    {
                        QuotationId = qtnInfo.QuotationId,
                        QuotationNumber = candidateDesc,
                        Success = true,
                        SalesOrderNumber = existingSo.Value.SoNumber,
                        SalesOrderId = existingSo.Value.SoId,
                    };
                }

                var alreadyMsg = $"Quotation '{candidateDesc}' (Id: {qtnInfo.QuotationId}, Group: '{qtnInfo.QtnGroup}') is already converted or closed (ERP Status: {qtnInfo.QtnStatus}).";
                _logger.LogInformation("{Note}. Moving to next candidate group if available.", alreadyMsg);
                candidateNotes.Add(alreadyMsg);
                continue;
            }

            // Check C: Resolve customer code
            var customerCode = !string.IsNullOrWhiteSpace(qtnInfo.CustomerCode)
                ? qtnInfo.CustomerCode
                : fallbackCustomerCode?.Trim();

            var siteCode = !string.IsNullOrWhiteSpace(qtnInfo.QuotationSiteCode)
                ? qtnInfo.QuotationSiteCode
                : fallbackSiteCode?.Trim();

            if (string.IsNullOrWhiteSpace(customerCode))
            {
                var note = $"Could not determine Customer Code for Quotation '{candidateDesc}' (Id: {qtnInfo.QuotationId}).";
                _logger.LogInformation("{Note}. Moving to next candidate group if available.", note);
                candidateNotes.Add(note);
                continue;
            }

            // Check D: Fetch open quotation details via Sales Order Entry discovery
            var openQuotations = await GetOpenAuthorisedQuotationsAsync(
                customerCode,
                siteCode ?? string.Empty,
                cancellationToken);

            var targetQuotation = openQuotations.FirstOrDefault(q =>
                q.QuotationId == qtnInfo.QuotationId
                || (IsSameFinancialYear(q.QtnYear, currentFy) && (
                    string.Equals(q.QtnNum, qtnInfo.QtnNum, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(qtnInfo.QtnGroup) || string.Equals(q.QtnGroup, qtnInfo.QtnGroup, StringComparison.OrdinalIgnoreCase)))));

            if (targetQuotation is null)
            {
                targetQuotation = new OpenQuotationSummary
                {
                    QuotationId = qtnInfo.QuotationId,
                    QuotationNumber = !string.IsNullOrWhiteSpace(qtnInfo.QuotationNumber) ? qtnInfo.QuotationNumber : paddedNum,
                    QtnYear = qtnInfo.QtnYear,
                    QtnGroup = qtnInfo.QtnGroup,
                    QtnNum = qtnInfo.QtnNum,
                    QuotationSiteCode = qtnInfo.QuotationSiteCode,
                    QuotationSiteId = qtnInfo.QuotationSiteId,
                    CustomerCode = customerCode,
                    CustomerName = qtnInfo.CustomerName,
                    QuotationDate = qtnInfo.QuotationDate,
                    QuotationCurrency = "INR",
                };
            }

            // Step 3: Attempt conversion for this candidate
            _logger.LogInformation(
                "Attempting conversion for candidate Quotation '{CandidateDesc}' (Id: {QuotationId}, Group: '{Group}')",
                candidateDesc,
                qtnInfo.QuotationId,
                qtnInfo.QtnGroup);

            var itemResult = await ConvertSingleQuotationAsync(targetQuotation, cancellationToken);
            if (itemResult.Success)
            {
                _logger.LogInformation(
                    "Successfully converted Quotation '{CandidateDesc}' to Sales Order '{SalesOrderNumber}' (Id: {SalesOrderId})",
                    candidateDesc,
                    itemResult.SalesOrderNumber,
                    itemResult.SalesOrderId);

                return itemResult;
            }

            // If conversion failed, record note and try next candidate
            var failNote = $"Quotation '{candidateDesc}' (Group: '{qtnInfo.QtnGroup}') conversion failed: {itemResult.ErrorMessage}";
            _logger.LogWarning("{FailNote}. Trying next candidate group if available.", failNote);
            candidateNotes.Add(failNote);
        }

        // All candidates failed or were already converted
        var consolidatedMsg = candidates.Count == 1
            ? candidateNotes.FirstOrDefault() ?? $"Quotation '{trimmedQtnNo}' could not be converted."
            : $"Processed {candidates.Count} candidate quotation(s) for number '{paddedNum}': {string.Join(" | ", candidateNotes)}";

        return new QuotationConversionItemResult
        {
            QuotationId = candidates[0].QuotationId,
            QuotationNumber = candidates[0].QuotationNumber,
            Success = false,
            ErrorMessage = consolidatedMsg,
        };
    }

    private async Task<IReadOnlyList<QuotationLookupInfo>> FindQuotationsAsync(
        string quotationNumber,
        CancellationToken cancellationToken)
    {
        var currentFy = GetCurrentFinancialYear();
        var (rawNum, paddedNum) = NormalizeQuotationNumber(quotationNumber);

        // Check if user specified a group in the string, e.g. "26-27/TE/NF1/000007"
        string? userSpecifiedGroup = null;
        var parts = quotationNumber.Split(['/', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 4)
        {
            userSpecifiedGroup = parts[1];
        }

        _logger.LogInformation(
            "Searching for Quotation numerical number '{PaddedNum}' (Raw: '{RawNum}', SpecifiedGroup: '{SpecifiedGroup}') in FY '{FinancialYear}'",
            paddedNum,
            rawNum,
            userSpecifiedGroup ?? "ANY",
            currentFy);

        var candidates = new List<QuotationLookupInfo>();
        var seenIds = new HashSet<int>();

        // Strategy 1: Direct database lookup via selectQtnFieldsOnSearchCriteria (QTNDETAILS)
        try
        {
            const string searchCriteriaUrl = "api/v1/sales/QuotationEntry/selectQtnFieldsOnSearchCriteria";
            var criteriaModel = new
            {
                customerCode = string.Empty,
                salesPersonCode = string.Empty,
                siteId = "0",
                qtnYear = currentFy,
                qtnGrp = string.Empty,
                qtnNum = paddedNum,
                userLocations = "0",
                criteria = "QTNDETAILS",
                mode = "SEARCH",
                companyId = _companyId.ToString(CultureInfo.InvariantCulture),
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
                        var qtnYear = GetStringProperty(item, "qtnYear", "quotationYear", "year") ?? currentFy;
                        var qtnGrp = GetStringProperty(item, "qtnGroup", "quotationGroup", "group") ?? string.Empty;
                        var qtnNum = GetStringProperty(item, "qtnNumber", "quotationNumber", "number") ?? paddedNum;
                        var siteId = GetIntProperty(item, "siteID", "siteId");
                        var locCode = GetStringProperty(item, "locationCode", "siteCode", "mloccode") ?? string.Empty;
                        var custCode = GetStringProperty(item, "customerCode", "custCode", "custCD", "xqdCustCd") ?? string.Empty;
                        var authBy = GetIntProperty(item, "authBy", "authorizerId");
                        var isAuth = authBy > 0 || GetBoolProperty(item, "isAuthorise", "isAuthorized");

                        if (!IsSameFinancialYear(qtnYear, currentFy))
                        {
                            continue;
                        }

                        if (autoId > 0 && !seenIds.Add(autoId))
                        {
                            continue;
                        }

                        var formattedQNumber = $"{qtnYear}/{qtnGrp}/{locCode}/{qtnNum}";

                        var initialInfo = new QuotationLookupInfo(
                            autoId,
                            formattedQNumber,
                            qtnYear,
                            qtnGrp,
                            qtnNum,
                            siteId,
                            locCode,
                            custCode,
                            string.Empty,
                            string.Empty,
                            isAuth,
                            null);

                        // Fetch detailed quotation header to populate CustomerCode, CustomerName, SiteCode, and Status
                        var header = await TryFetchQuotationHeaderAsync(initialInfo, cancellationToken);
                        if (header is not null)
                        {
                            candidates.Add(initialInfo with
                            {
                                CustomerCode = !string.IsNullOrWhiteSpace(header.CustomerCode) ? header.CustomerCode : initialInfo.CustomerCode,
                                CustomerName = header.CustomerName ?? string.Empty,
                                QuotationSiteCode = !string.IsNullOrWhiteSpace(header.SiteCode) ? header.SiteCode : initialInfo.QuotationSiteCode,
                                QuotationSiteId = header.SiteId > 0 ? header.SiteId : initialInfo.QuotationSiteId,
                                QtnGroup = !string.IsNullOrWhiteSpace(header.Group) ? header.Group : initialInfo.QtnGroup,
                                QtnStatus = header.QtnStatus ?? string.Empty,
                                IsAuthorized = header.IsAuthorized || initialInfo.IsAuthorized,
                                QuotationDate = header.QuotationDate,
                            });
                        }
                        else
                        {
                            candidates.Add(initialInfo);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query selectQtnFieldsOnSearchCriteria (QTNDETAILS) for {QuotationNumber}", quotationNumber);
        }

        // Strategy 2: Fallback search via quotationEntryList
        try
        {
            var searchTerms = new[] { paddedNum, rawNum }.Distinct();
            foreach (var term in searchTerms)
            {
                const string listUrl = "api/v1/sales/QuotationEntry/quotationEntryList";
                var filter = new
                {
                    pageSize = 50,
                    pageNumber = 1,
                    sortField = "Date",
                    sortDirection = "desc",
                    searchValue = term,
                    userLocationIds = "0",
                    authStatus = "",
                    loginUser = 1,
                    restcoresalestrans = false,
                    docType = "QT",
                    docSubType = "QT",
                };

                using var listResponse = await _httpClient.PostAsJsonAsync(listUrl, filter, cancellationToken);
                if (!listResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                using var jsonDoc = await JsonDocument.ParseAsync(
                    await listResponse.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = jsonDoc.RootElement;
                if (!root.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in dataElem.EnumerateArray())
                {
                    var qId = GetIntProperty(item, "qtnID", "id", "quotationID");
                    var qNumber = GetStringProperty(item, "qtnNumber", "number", "quotationNumber");
                    var qYear = GetStringProperty(item, "qtnYear", "year");
                    var qGroup = GetStringProperty(item, "qtnGroup", "group");
                    var sId = GetIntProperty(item, "siteId", "siteID");
                    var sCode = GetStringProperty(item, "siteCode");
                    var cCode = GetStringProperty(item, "customerCode", "custCode", "custCD");
                    var isAuth = GetBoolProperty(item, "isAuthorized", "isAuthorise");
                    var status = GetStringProperty(item, "status", "qtnStatus") ?? string.Empty;

                    if (!IsSameFinancialYear(qYear, currentFy))
                    {
                        continue;
                    }

                    if (qId > 0 && !seenIds.Add(qId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(qNumber) &&
                        (string.Equals(qNumber, quotationNumber, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(qNumber, paddedNum, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(qNumber, rawNum, StringComparison.OrdinalIgnoreCase)
                         || qNumber.EndsWith("/" + paddedNum, StringComparison.OrdinalIgnoreCase)
                         || qNumber.EndsWith("-" + paddedNum, StringComparison.OrdinalIgnoreCase)
                         || qNumber.EndsWith("/" + rawNum, StringComparison.OrdinalIgnoreCase)
                         || qNumber.EndsWith("-" + rawNum, StringComparison.OrdinalIgnoreCase)
                         || qNumber.EndsWith(paddedNum, StringComparison.OrdinalIgnoreCase)))
                    {
                        var info = new QuotationLookupInfo(
                            qId,
                            qNumber,
                            qYear ?? currentFy,
                            qGroup ?? string.Empty,
                            paddedNum,
                            sId,
                            sCode ?? string.Empty,
                            cCode ?? string.Empty,
                            string.Empty,
                            status,
                            isAuth,
                            null);

                        var header = await TryFetchQuotationHeaderAsync(info, cancellationToken);
                        if (header is not null)
                        {
                            candidates.Add(info with
                            {
                                CustomerCode = !string.IsNullOrWhiteSpace(header.CustomerCode) ? header.CustomerCode : info.CustomerCode,
                                CustomerName = header.CustomerName ?? string.Empty,
                                QuotationSiteCode = !string.IsNullOrWhiteSpace(header.SiteCode) ? header.SiteCode : info.QuotationSiteCode,
                                QuotationSiteId = header.SiteId > 0 ? header.SiteId : info.QuotationSiteId,
                                QtnGroup = !string.IsNullOrWhiteSpace(header.Group) ? header.Group : info.QtnGroup,
                                QtnStatus = !string.IsNullOrWhiteSpace(header.QtnStatus) ? header.QtnStatus : status,
                                IsAuthorized = header.IsAuthorized || info.IsAuthorized,
                                QuotationDate = header.QuotationDate,
                            });
                        }
                        else
                        {
                            candidates.Add(info);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query quotationEntryList for {QuotationNumber}", quotationNumber);
        }

        // Sort candidates:
        // 1. If user specified group, place matching group first
        // 2. Open quotations (not 'R' and not 'C') before closed/converted ones
        return candidates
            .OrderByDescending(c => !string.IsNullOrWhiteSpace(userSpecifiedGroup) && string.Equals(c.QtnGroup, userSpecifiedGroup, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => !string.Equals(c.QtnStatus, "R", StringComparison.OrdinalIgnoreCase) && !string.Equals(c.QtnStatus, "C", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private string GetCurrentFinancialYear()
    {
        var now = _clock.UtcNow.LocalDateTime;
        var startYear = now.Month >= 4 ? now.Year : now.Year - 1;
        return string.Create(CultureInfo.InvariantCulture, $"{startYear % 100:00}-{(startYear + 1) % 100:00}");
    }

    private static (string Raw, string Padded) NormalizeQuotationNumber(string input)
    {
        var trimmed = input.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
        {
            return (parsedLong.ToString(CultureInfo.InvariantCulture), parsedLong.ToString("D6", CultureInfo.InvariantCulture));
        }
        return (trimmed, trimmed);
    }

    private static string NormalizeFinancialYear(string? fy)
    {
        if (string.IsNullOrWhiteSpace(fy))
        {
            return string.Empty;
        }

        var parts = fy.Trim().Split(['-', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            var p1 = parts[0].Length > 2 ? parts[0][^2..] : parts[0].PadLeft(2, '0');
            var p2 = parts[1].Length > 2 ? parts[1][^2..] : parts[1].PadLeft(2, '0');
            return $"{p1}-{p2}";
        }

        return fy.Trim();
    }

    private static bool IsSameFinancialYear(string? fy1, string? fy2)
    {
        if (string.IsNullOrWhiteSpace(fy1) || string.IsNullOrWhiteSpace(fy2))
        {
            return false;
        }

        return string.Equals(NormalizeFinancialYear(fy1), NormalizeFinancialYear(fy2), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<QuotationHeaderDetails?> TryFetchQuotationHeaderAsync(
        QuotationLookupInfo qtnInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            var yearParam = !string.IsNullOrWhiteSpace(qtnInfo.QtnYear) ? qtnInfo.QtnYear : "0";
            var grpParam = !string.IsNullOrWhiteSpace(qtnInfo.QtnGroup) ? qtnInfo.QtnGroup : "0";
            var numParam = !string.IsNullOrWhiteSpace(qtnInfo.QtnNum) ? qtnInfo.QtnNum : qtnInfo.QuotationNumber;
            var siteIdParam = qtnInfo.QuotationSiteId > 0 ? qtnInfo.QuotationSiteId : 0;

            // Try standard getQuotationDetail endpoint first, and if site is 0 or it fails, try getQuotationDetail_MOB
            var endpointsToTry = new[]
            {
                $"api/v1/sales/QuotationEntry/getQuotationDetail/{Uri.EscapeDataString(yearParam)}/{Uri.EscapeDataString(grpParam)}/{Uri.EscapeDataString(numParam)}/{siteIdParam}/0/SEARCH/10",
                $"api/v1/sales/QuotationEntry/getQuotationDetail_MOB/{Uri.EscapeDataString(yearParam)}/{Uri.EscapeDataString(grpParam)}/{Uri.EscapeDataString(numParam)}/{siteIdParam}/0/SEARCH/10",
            };

            foreach (var detailsUrl in endpointsToTry)
            {
                using var response = await _httpClient.PostAsJsonAsync(detailsUrl, new { }, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data))
                {
                    continue;
                }

                JsonElement headerElem = default;
                if (data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("quotationDetails", out var qd))
                    {
                        if (qd.ValueKind == JsonValueKind.Array)
                        {
                            headerElem = qd.EnumerateArray().FirstOrDefault();
                        }
                        else if (qd.ValueKind == JsonValueKind.Object)
                        {
                            headerElem = qd;
                        }
                    }
                    else
                    {
                        headerElem = data;
                    }
                }
                else if (data.ValueKind == JsonValueKind.Array)
                {
                    headerElem = data.EnumerateArray().FirstOrDefault();
                }

                if (headerElem.ValueKind == JsonValueKind.Object)
                {
                    var custCode = GetStringProperty(headerElem, "customerCode", "custCode", "custCD", "billToCustomerCode");
                    var custName = GetStringProperty(headerElem, "customerName");
                    var siteCode = GetStringProperty(headerElem, "siteCode", "quotationSiteCode", "locCode", "locationCode");
                    var siteId = GetIntProperty(headerElem, "quotationSiteId", "siteId", "siteID");
                    var grp = GetStringProperty(headerElem, "quotationGroup", "group");
                    var qtnStat = GetStringProperty(headerElem, "qtnStatus", "quotationStatus");
                    var authBy = GetIntProperty(headerElem, "authby", "authBy", "authorizerId");
                    var isAuth = authBy > 0 || GetBoolProperty(headerElem, "isAuthorized", "isAuthorise");

                    DateTime? qDate = null;
                    if (headerElem.TryGetProperty("quotationDate", out var qdProp) && qdProp.ValueKind == JsonValueKind.String)
                    {
                        if (DateTime.TryParse(qdProp.GetString(), CultureInfo.InvariantCulture, out var dt))
                        {
                            qDate = dt;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(custCode) || siteId > 0)
                    {
                        return new QuotationHeaderDetails(
                            custCode,
                            custName,
                            siteCode,
                            siteId,
                            grp,
                            qtnStat,
                            isAuth,
                            qDate);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch header for quotation {QuotationNumber}", qtnInfo.QuotationNumber);
        }

        return null;
    }

    private async Task<string> GetDefaultSalesOrderGroupAsync(
        string year,
        int siteId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"api/v1/admin/documentControl/getDefaultDocumentDetail/{Uri.EscapeDataString(year)}/OB/OB/{siteId}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    var group = GetStringProperty(data, "groupCode", "docGroup", "group");
                    if (!string.IsNullOrWhiteSpace(group))
                    {
                        return group;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query default document group for Sales Order at site {SiteId}", siteId);
        }

        return "SW";
    }

    private async Task<(string SoNumber, int SoId)?> VerifyCreatedSalesOrderAsync(
        OpenQuotationSummary quotation,
        string soYear,
        string soGroup,
        CancellationToken cancellationToken)
    {
        try
        {
            // Give ERP a moment to commit any active transaction
            await Task.Delay(500, cancellationToken);

            var custCode = quotation.CustomerCode?.Trim() ?? string.Empty;

            // 1. Check getCloseQuotationDetailsForSO
            if (!string.IsNullOrWhiteSpace(custCode))
            {
                try
                {
                    var closedUrl = $"api/v1/sales/salesorderentry/getCloseQuotationDetailsForSO/{Uri.EscapeDataString(custCode)}";
                    using var closedResponse = await _httpClient.GetAsync(closedUrl, cancellationToken);
                    if (closedResponse.IsSuccessStatusCode)
                    {
                        using var closedDoc = await JsonDocument.ParseAsync(
                            await closedResponse.Content.ReadAsStreamAsync(cancellationToken),
                            cancellationToken: cancellationToken);

                        if (closedDoc.RootElement.TryGetProperty("data", out var closedArray) && closedArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in closedArray.EnumerateArray())
                            {
                                var qId = GetIntProperty(item, "quotationId", "quotationID", "id");
                                var qNum = GetStringProperty(item, "quotationNumber", "qtnNumber", "quotationNo");

                                var isClosedMatch = (qId > 0 && qId == quotation.QuotationId)
                                    || (!string.IsNullOrWhiteSpace(qNum) && (
                                        string.Equals(qNum, quotation.QuotationNumber, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(qNum, quotation.QtnNum, StringComparison.OrdinalIgnoreCase)));

                                if (isClosedMatch)
                                {
                                    var soNumFromClosed = GetStringProperty(item, "salesOrderNumber", "orderNumber", "orderNo", "soNumber", "ioNumber");
                                    var soIdFromClosed = GetIntProperty(item, "orderId", "orderID", "salesOrderId", "soId");
                                    if (!string.IsNullOrWhiteSpace(soNumFromClosed))
                                    {
                                        _logger.LogInformation(
                                            "Found linked Sales Order {SoNumber} (Id: {SoId}) for Quotation {QuotationNumber} in getCloseQuotationDetailsForSO",
                                            soNumFromClosed,
                                            soIdFromClosed,
                                            quotation.QuotationNumber);

                                        return (soNumFromClosed, soIdFromClosed);
                                    }
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to check getCloseQuotationDetailsForSO for customer {CustomerCode}", custCode);
                }
            }

            // 2. Query selectOrderFieldsOnSearchCriteria for newly created order that explicitly matches this quotation
            const string searchUrl = "api/v1/sales/salesorderentry/selectOrderFieldsOnSearchCriteria";
            var searchCriteria = new
            {
                CustomerCode = custCode,
                SalesPersonCode = string.Empty,
                SiteCode = string.Empty,
                IOYear = soYear,
                IOGroup = soGroup,
                IOOrderNo = string.Empty,
                UserLocations = quotation.QuotationSiteId > 0 ? quotation.QuotationSiteId.ToString(CultureInfo.InvariantCulture) : "1",
                Criteria = "ORDDETAILS",
                Mode = "EDIT",
                AuthorizationRequired = "Y",
                IsInterBranchTransfer = false,
            };

            using var searchResponse = await _httpClient.PostAsJsonAsync(searchUrl, searchCriteria, cancellationToken);
            if (searchResponse.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(
                    await searchResponse.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);

                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    var matchingOrders = new List<(string SoNumber, int SoId)>();
                    foreach (var item in data.EnumerateArray())
                    {
                        var orderId = GetIntProperty(item, "orderId", "orderID", "autoId", "autoID");
                        var ioNumber = GetStringProperty(item, "ioNumber", "number", "orderNo");
                        var siteCode = GetStringProperty(item, "siteCode") ?? quotation.QuotationSiteCode;
                        var year = GetStringProperty(item, "ioYear") ?? soYear;
                        var group = GetStringProperty(item, "ioGroup") ?? soGroup;
                        var qtnId = GetIntProperty(item, "quotationId", "quotationID", "qtnId", "qtnID");
                        var qtnNumber = GetStringProperty(item, "quotationNumber", "qtnNumber", "quotationNo", "qtnNo");
                        var loiNo = GetStringProperty(item, "customerLOINumber", "loiNumber", "loiNo", "custLOINo");

                        if (orderId > 0 && !string.IsNullOrWhiteSpace(ioNumber))
                        {
                            var fullSoNumber = $"{year}/{group}/{siteCode}/{ioNumber}";

                            // Strictly match by quotationId, quotationNumber, or LOI reference
                            var isMatch = (qtnId > 0 && qtnId == quotation.QuotationId)
                                || (!string.IsNullOrWhiteSpace(qtnNumber) && (
                                    string.Equals(qtnNumber, quotation.QuotationNumber, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(qtnNumber, quotation.QtnNum, StringComparison.OrdinalIgnoreCase) ||
                                    qtnNumber.EndsWith("/" + quotation.QtnNum, StringComparison.OrdinalIgnoreCase) ||
                                    qtnNumber.EndsWith("-" + quotation.QtnNum, StringComparison.OrdinalIgnoreCase) ||
                                    qtnNumber.EndsWith(quotation.QtnNum, StringComparison.OrdinalIgnoreCase)))
                                || (!string.IsNullOrWhiteSpace(loiNo) && (
                                    loiNo.Contains($"LOI/{quotation.QuotationNumber}", StringComparison.OrdinalIgnoreCase) ||
                                    loiNo.Contains(quotation.QuotationNumber, StringComparison.OrdinalIgnoreCase) ||
                                    loiNo.Contains(quotation.QtnNum, StringComparison.OrdinalIgnoreCase)));

                            if (isMatch)
                            {
                                matchingOrders.Add((fullSoNumber, orderId));
                            }
                        }
                    }

                    if (matchingOrders.Count > 0)
                    {
                        var latest = matchingOrders.OrderByDescending(o => o.SoId).First();
                        _logger.LogInformation(
                            "Verified linked Sales Order {SoNumber} (Id: {SoId}) for Quotation {QuotationNumber}",
                            latest.SoNumber,
                            latest.SoId,
                            quotation.QuotationNumber);

                        return latest;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify created Sales Order for Quotation {QuotationId}", quotation.QuotationId);
        }

        return null;
    }

    private sealed record QuotationHeaderDetails(
        string? CustomerCode,
        string? CustomerName,
        string? SiteCode,
        int SiteId,
        string? Group,
        string? QtnStatus,
        bool IsAuthorized,
        DateTime? QuotationDate);

    private sealed record QuotationLookupInfo(
        int QuotationId,
        string QuotationNumber,
        string QtnYear,
        string QtnGroup,
        string QtnNum,
        int QuotationSiteId,
        string QuotationSiteCode,
        string CustomerCode,
        string CustomerName,
        string QtnStatus,
        bool IsAuthorized,
        DateTime? QuotationDate);

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

    private async Task<List<OpenQuotationSummary>> DiscoverQuotationsFromEntryListAsync(
        string? targetCustomer,
        string? targetSite,
        CancellationToken cancellationToken)
    {
        var currentFy = GetCurrentFinancialYear();
        var results = new List<OpenQuotationSummary>();
        const string listUrl = "api/v1/sales/QuotationEntry/quotationEntryList";

        var filter = new
        {
            pageSize = 100,
            pageNumber = 1,
            sortField = "Date",
            sortDirection = "desc",
            searchValue = targetCustomer ?? string.Empty,
            userLocationIds = "0",
            authStatus = "",
            loginUser = 1,
            restcoresalestrans = false,
            docType = "QT",
            docSubType = "QT",
        };

        using var listResponse = await _httpClient.PostAsJsonAsync(listUrl, filter, cancellationToken);
        if (!listResponse.IsSuccessStatusCode)
        {
            return results;
        }

        using var jsonDoc = await JsonDocument.ParseAsync(
            await listResponse.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = jsonDoc.RootElement;
        if (!root.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in dataElem.EnumerateArray())
        {
            if (!IsAuthorizedQuotation(item) || !IsOpenQuotation(item))
            {
                continue;
            }

            var qId = GetIntProperty(item, "qtnID", "id", "quotationID");
            var qNumber = GetStringProperty(item, "qtnNumber", "number", "quotationNumber");
            var qYear = GetStringProperty(item, "qtnYear", "year", "quotationYear") ?? currentFy;
            var qGroup = GetStringProperty(item, "qtnGroup", "group", "quotationGroup");
            var sId = GetIntProperty(item, "siteId", "siteID");
            var sCode = GetStringProperty(item, "siteCode", "locationCode");
            var cCode = GetStringProperty(item, "customerCode", "custCode", "custCD");
            var cName = GetStringProperty(item, "customerName");

            if (!string.IsNullOrWhiteSpace(targetCustomer) && !string.Equals(targetCustomer, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(cCode, targetCustomer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!MatchesSiteFilter(targetSite, sId, sCode, qNumber))
            {
                continue;
            }

            if (qId > 0 && !string.IsNullOrWhiteSpace(qNumber))
            {
                var (yr, grp, num) = ParseQuotationParts(qNumber, qYear, qGroup);

                DateTime? qDate = null;
                if (item.TryGetProperty("quotationDate", out var qdElem) || item.TryGetProperty("date", out qdElem))
                {
                    if (qdElem.ValueKind == JsonValueKind.String && DateTime.TryParse(qdElem.GetString(), CultureInfo.InvariantCulture, out var dt))
                    {
                        qDate = dt;
                    }
                }

                results.Add(new OpenQuotationSummary
                {
                    QuotationId = qId,
                    QuotationNumber = qNumber,
                    QtnYear = yr,
                    QtnGroup = grp,
                    QtnNum = num,
                    QuotationSiteCode = sCode ?? string.Empty,
                    QuotationSiteId = sId > 0 ? sId : 1,
                    CustomerCode = cCode ?? string.Empty,
                    CustomerName = cName,
                    QuotationDate = qDate,
                    QuotationCurrency = "INR",
                });
            }
        }

        return results;
    }

    private static bool IsAuthorizedQuotation(JsonElement item)
    {
        var authBy = GetIntProperty(item, "authBy", "authorizerId", "authby");
        if (authBy > 0)
        {
            return true;
        }

        if (GetBoolProperty(item, "isAuthorized", "isAuthorise", "isAuthorised"))
        {
            return true;
        }

        var authStatus = GetStringProperty(item, "authStatus", "authorizationStatus", "authorizeStatus");
        if (string.Equals(authStatus, "A", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authStatus, "Authorized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(authStatus, "Y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsOpenQuotation(JsonElement item)
    {
        var status = GetStringProperty(item, "status", "qtnStatus", "quotationStatus");
        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        var trimmed = status.Trim();
        if (string.Equals(trimmed, "C", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "R", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Closed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Rejected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesSiteFilter(string? targetSite, int siteId, string? siteCode, string? quotationNumber)
    {
        if (string.IsNullOrWhiteSpace(targetSite) || targetSite == "0" || string.Equals(targetSite, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var cleanTarget = targetSite.Trim();

        if (siteId > 0 && int.TryParse(cleanTarget, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetId) && siteId == targetId)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(siteCode) && string.Equals(siteCode.Trim(), cleanTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(quotationNumber))
        {
            var parts = quotationNumber.Split(['/', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Any(p => string.Equals(p, cleanTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static (string Year, string Group, string Num) ParseQuotationParts(
        string quotationNumber,
        string? fallbackYear,
        string? fallbackGroup)
    {
        var clean = quotationNumber.Trim();
        var parts = clean.Split(['/', '-'], StringSplitOptions.TrimEntries);
        if (parts.Length >= 4)
        {
            var year = parts[0];
            if (parts.Length >= 5 && parts[1].Length == 2 && char.IsDigit(parts[1][0]))
            {
                year = $"{parts[0]}-{parts[1]}";
                var grp = parts[2];
                var num = parts[^1];
                return (year, grp, num);
            }
            return (parts[0], parts[1], parts[^1]);
        }
        if (parts.Length == 3)
        {
            return (parts[0], parts[1], parts[2]);
        }

        return (fallbackYear ?? string.Empty, fallbackGroup ?? string.Empty, clean);
    }

    private static OpenQuotationSummary? ParseOpenQuotationSummaryItem(JsonElement item, string defaultCustomer, string defaultSite)
    {
        var qtnId = GetIntProperty(item, "quotationID", "quotationId", "qtnID");
        var qtnNumFormatted = GetStringProperty(item, "qtnNumber", "quotationNumber", "qtn Number");
        var qtnYear = GetStringProperty(item, "quotationYear", "qtnYear");
        var qtnGroup = GetStringProperty(item, "quotationGroup", "qtnGroup");
        var qtnNum = GetStringProperty(item, "quotationNumber", "qtnNum");
        var qtnSiteCode = GetStringProperty(item, "quotationSiteCode", "qtnLocCode") ?? defaultSite;
        var qtnSiteId = GetIntProperty(item, "quotationSiteId", "qtnSiteId");
        var custCode = GetStringProperty(item, "customerCode", "custCD") ?? defaultCustomer;
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
            return new OpenQuotationSummary
            {
                QuotationId = qtnId,
                QuotationNumber = qtnNumFormatted,
                QtnYear = qtnYear ?? string.Empty,
                QtnGroup = qtnGroup ?? string.Empty,
                QtnNum = qtnNum ?? string.Empty,
                QuotationSiteCode = qtnSiteCode,
                QuotationSiteId = qtnSiteId > 0 ? qtnSiteId : 1,
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
            };
        }

        return null;
    }
}
