using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration;
using NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Contracts;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Services;

public sealed class SoAllocationToDcSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AutomationAgentOptions _options;
    private readonly ILogger<SoAllocationToDcSchedulerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public SoAllocationToDcSchedulerService(
        IServiceScopeFactory scopeFactory,
        AutomationAgentOptions options,
        ILogger<SoAllocationToDcSchedulerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SoAllocationToDC automation scheduler started");

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
                _logger.LogError(ex, "SoAllocationToDC automation scheduler tick failed");
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

        _logger.LogInformation("SoAllocationToDC automation scheduler stopped");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<ISoAllocationToDcConfigRepository>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var config = await configs.GetConfigAsync(cancellationToken);

        if (!config.AutomationEnabled)
        {
            return;
        }
        
        // Mode INDIVIDUAL does not run on timer
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

        // Check if the scheduled time has passed today
        if (localNow < scheduledTime)
        {
            return;
        }

        // Check if already run today (idempotency)
        if (config.LastScheduledRunDate.HasValue && config.LastScheduledRunDate.Value >= localToday)
        {
            return; // Already ran today
        }

        // Mark as run BEFORE execution to prevent duplicate triggers
        config.LastScheduledRunDate = localToday;
        config.UpdatedAtUtc = clock.UtcNow;
        await configs.SaveConfigAsync(config, cancellationToken);

        _logger.LogInformation("SoAllocationToDC scheduler triggered automation run for {Time}", config.AutomationTime);

        await RunMainApiAsync(config, cancellationToken);
    }

    private async Task RunMainApiAsync(SoAllocationToDcAutomationConfig config, CancellationToken cancellationToken)
    {
        var requestPayload = new BatchDcCreationRequestDto
        {
            DryRun = false
        };

        // If caller configured specific OAF numbers, process ONLY those OAF numbers.
        // If no OAF numbers are configured, process ALL eligible allocations (existing behavior preserved).
        if (config.OafNumbers != null && config.OafNumbers.Count > 0)
        {
            requestPayload = requestPayload with { OafNumbers = config.OafNumbers.Select(x => x.Trim()).ToList() };
        }
        // No else block needed since OafNumbers handles all OAFs now

        var endpoint = $"http://127.0.0.1:{_options.Host.ManagementApiPort}/api/v1/sales/SalesAllocationDeAllocation/CreateDeliveryChallansFromAllocations?trigger=timer";

        try
        {
            using var client = _httpClientFactory.CreateClient("AgentLoopback"); // Reuse loopback client if exists, else default
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
                _logger.LogInformation("SoAllocationToDC Automation Run Completed Successfully: {Payload}", payload);
                try
                {
                    var batchResponse = JsonSerializer.Deserialize<BatchDcCreationResponse>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    var createdCount = batchResponse?.Data?.CreatedDCCount ?? 0;
                    var successCount = batchResponse?.Data?.SuccessfulAllocations ?? 0;
                    var processedCount = batchResponse?.Data?.ProcessedAllocations ?? 0;
                    var isSuccess = (batchResponse?.Success == true && (createdCount > 0 || successCount > 0 || processedCount > 0)) || createdCount > 0 || successCount > 0;

                    using var updateScope = _scopeFactory.CreateScope();
                    var configRepo = updateScope.ServiceProvider.GetRequiredService<ISoAllocationToDcConfigRepository>();
                    var latestConfig = await configRepo.GetConfigAsync(cancellationToken);

                    latestConfig.LastScheduledRunStatus = isSuccess ? "Success" : "Failed";
                    latestConfig.LastCreatedDcCount = createdCount;
                    latestConfig.LastScheduledRunMessage = createdCount > 0
                        ? $"Created {createdCount} Delivery Challan(s) successfully at {config.AutomationTime} across {processedCount} allocation(s)."
                        : (batchResponse?.Message ?? "Scheduled DC creation completed.");
                    latestConfig.LastScheduledRunUtc = DateTimeOffset.UtcNow;
                    await configRepo.SaveConfigAsync(latestConfig, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse scheduled batch DC creation response");
                }
            }
            else
            {
                _logger.LogWarning("SoAllocationToDC Automation Run Failed ({StatusCode}): {Payload}", (int)response.StatusCode, payload);
                try
                {
                    using var updateScope = _scopeFactory.CreateScope();
                    var configRepo = updateScope.ServiceProvider.GetRequiredService<ISoAllocationToDcConfigRepository>();
                    var latestConfig = await configRepo.GetConfigAsync(cancellationToken);
                    latestConfig.LastScheduledRunStatus = "Failed";
                    latestConfig.LastScheduledRunMessage = $"Scheduled DC creation failed ({response.StatusCode}).";
                    latestConfig.LastScheduledRunUtc = DateTimeOffset.UtcNow;
                    await configRepo.SaveConfigAsync(latestConfig, cancellationToken);
                }
                catch
                {
                    // Ignore config update failure
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call existing Main Automation API CreateDeliveryChallansFromAllocations");
        }
    }
}
