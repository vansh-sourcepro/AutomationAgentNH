using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.Worker.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.QuotationToSo.Services;

/// <summary>
/// Background timer scheduler for Quotation to Sales Order automation.
/// Checks once a minute if the scheduled time has arrived and triggers conversion if active.
/// </summary>
public sealed class QuotationToSoSchedulerService : BackgroundService
{
    public const string HttpClientName = "QuotationToSoLoopback";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AutomationAgentOptions _options;
    private readonly ILogger<QuotationToSoSchedulerService> _logger;

    public QuotationToSoSchedulerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        AutomationAgentOptions options,
        ILogger<QuotationToSoSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    private Uri ConvertEndpoint => new(
        $"http://127.0.0.1:{_options.Host.ManagementApiPort}/api/automation/quotation-to-so?trigger=timer");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Quotation → Sales Order automation scheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Quotation → Sales Order automation scheduler tick failed; next tick will retry");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Quotation → Sales Order automation scheduler stopped");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var configs = scope.ServiceProvider.GetRequiredService<IQuotationSoAutomationConfigRepository>();

        var localNow = clock.LocalTimeOfDay;
        var localToday = clock.LocalDate;

        var config = await configs.GetAsync(cancellationToken);

        if (!config.ShouldRunOnSchedule(localNow, localToday))
        {
            return;
        }

        _logger.LogInformation(
            "Quotation → Sales Order scheduled slot reached ({ScheduleTime}). Triggering conversion for {Target}...",
            config.ScheduleTime,
            string.IsNullOrWhiteSpace(config.QuotationNumbers) ? "all eligible quotations" : config.QuotationNumbers);

        // Pre-claim the run date so the next tick in the same minute will not re-trigger
        config.RecordRunStarted(clock.UtcNow, localToday);
        try
        {
            await configs.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not claim today's scheduled slot for Quotation to SO automation; will retry next tick");
            return;
        }

        var body = new Dictionary<string, object?>
        {
            ["customerCode"] = config.CustomerCode,
            ["siteCode"] = config.SiteCode,
        };

        if (!string.IsNullOrWhiteSpace(config.QuotationNumbers))
        {
            body["quotationNumber"] = config.QuotationNumbers;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = ConvertTimeout;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ConvertEndpoint)
            {
                Content = JsonContent.Create(body),
            };
            httpRequest.Headers.Add(ApiKeyFilter.HeaderName, _options.Host.InboundApiKey);

            using var response = await client.SendAsync(httpRequest, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Quotation → Sales Order scheduled run succeeded: {StatusCode}",
                    response.StatusCode);
            }
            else
            {
                _logger.LogWarning(
                    "Quotation → Sales Order scheduled run returned non-success: {StatusCode} - {Payload}",
                    response.StatusCode,
                    payload);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Quotation → Sales Order scheduled HTTP call failed");
        }
    }
}
