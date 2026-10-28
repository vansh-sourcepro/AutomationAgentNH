using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Configuration;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Contracts;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice.Services;

/// <summary>
/// Background timer scheduler for Delivery Challan to Sales Invoice automation.
/// Runs once per day at the configured AutomationTime.
/// </summary>
public sealed class DcToInvoiceSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AutomationAgentOptions _options;
    private readonly ILogger<DcToInvoiceSchedulerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public DcToInvoiceSchedulerService(
        IServiceScopeFactory scopeFactory,
        AutomationAgentOptions options,
        ILogger<DcToInvoiceSchedulerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DcToInvoice automation scheduler started");

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
                _logger.LogError(ex, "DcToInvoice automation scheduler tick failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("DcToInvoice automation scheduler stopped");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IDcToInvoiceConfigRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var config = await configs.GetConfigAsync(cancellationToken);

        if (!config.AutomationEnabled)
        {
            return;
        }

        // Mode INDIVIDUAL does not execute on timer
        if (config.AutomationMode == "INDIVIDUAL")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.AutomationTime) || !TimeOnly.TryParse(config.AutomationTime, out var scheduledTime))
        {
            return;
        }

        var localNow = clock.LocalTimeOfDay;
        var localToday = clock.LocalDate;

        // Check if scheduled time has arrived today
        if (localNow < scheduledTime)
        {
            return;
        }

        // Check if already run today
        if (config.LastScheduledRunDate.HasValue && config.LastScheduledRunDate.Value >= localToday)
        {
            return;
        }

        // Mark as run BEFORE triggering to prevent duplicate execution
        config.LastScheduledRunDate = localToday;
        config.UpdatedAtUtc = clock.UtcNow;
        await configs.SaveConfigAsync(config, cancellationToken);

        _logger.LogInformation("DcToInvoice scheduler triggered automation run for {Time}", config.AutomationTime);

        await RunMainApiAsync(config, cancellationToken);
    }

    private async Task RunMainApiAsync(DcToInvoiceAutomationConfig config, CancellationToken cancellationToken)
    {
        var requestPayload = new BatchInvoiceCreationRequestDto();

        if (config.AutomationMode != "TIMER" && config.DcNumbers != null && config.DcNumbers.Count > 0)
        {
            requestPayload = requestPayload with { DcNumbers = config.DcNumbers.Select(x => x.Trim()).ToList() };
        }

        var endpoint = $"http://127.0.0.1:{_options.Host.ManagementApiPort}/api/v1/sales/salesinvoice/CreateInvoiceFromDeliveryChallan";

        try
        {
            using var client = _httpClientFactory.CreateClient("AgentLoopback");
            client.Timeout = TimeSpan.FromMinutes(30);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(requestPayload)
            };

            if (!string.IsNullOrEmpty(_options.Host.InboundApiKey))
            {
                httpRequest.Headers.Add("X-Automation-Api-Key", _options.Host.InboundApiKey);
            }

            var response = await client.SendAsync(httpRequest, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("DcToInvoice Automation Run Completed Successfully: {Payload}", payload);
            }
            else
            {
                _logger.LogWarning("DcToInvoice Automation Run Failed ({StatusCode}): {Payload}", (int)response.StatusCode, payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call existing Main Automation API CreateInvoicesFromDeliveryChallans");
        }
    }
}
