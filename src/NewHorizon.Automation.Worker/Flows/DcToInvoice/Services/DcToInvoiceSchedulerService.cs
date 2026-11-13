using System;
using System.Collections.Generic;
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
using NewHorizon.Automation.ErpClient.Flows.DcToInvoice;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Configuration;
using NewHorizon.Automation.Worker.Flows.DcToInvoice.Contracts;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice.Services;

/// <summary>
/// Background timer scheduler for Delivery Challan to Sales Invoice automation.
/// Runs once per day at the configured AutomationTime for TIMER and BOTH modes.
/// </summary>
public sealed class DcToInvoiceSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

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
        _logger.LogInformation("DcToInvoice automation scheduler started (tick interval: {Interval}s)", TickInterval.TotalSeconds);

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
        var service = scope.ServiceProvider.GetRequiredService<IDcToInvoiceService>();

        var config = await configs.GetConfigAsync(cancellationToken);

        if (!config.AutomationEnabled)
        {
            return;
        }

        // Mode INDIVIDUAL does not execute on timer — it runs on demand
        var mode = config.AutomationMode?.ToUpperInvariant();
        if (mode == "INDIVIDUAL" || mode == "NONE" || mode == "DISABLED")
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.AutomationTime) || !TimeOnly.TryParse(config.AutomationTime, out var scheduledTime))
        {
            return;
        }

        var localNow = clock.LocalTimeOfDay;
        var localToday = clock.LocalDate;

        // If today's scheduled time has not yet arrived, clear any stale claim for today
        if (localNow < scheduledTime)
        {
            if (config.LastScheduledRunDate.HasValue && config.LastScheduledRunDate.Value >= localToday)
            {
                config.LastScheduledRunDate = null;
                await configs.SaveConfigAsync(config, cancellationToken);
            }
            return;
        }

        // Check if already executed today
        if (config.LastScheduledRunDate.HasValue && config.LastScheduledRunDate.Value >= localToday)
        {
            return;
        }

        // Claim slot for today BEFORE execution to prevent duplicate triggers
        config.LastScheduledRunDate = localToday;
        config.UpdatedAtUtc = clock.UtcNow;
        await configs.SaveConfigAsync(config, cancellationToken);

        _logger.LogInformation("DcToInvoice scheduler triggered automation run for {Time} (Mode: {Mode})", config.AutomationTime, mode);

        try
        {
            await RunScheduledConversionAsync(config, service, configs, clock, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DcToInvoice scheduled conversion failed with unhandled exception");
            config.StatusDescription = $"Scheduled run error at {clock.LocalTimeOfDay:HH:mm}: {ex.Message}";
            config.LastScheduledRunUtc = clock.UtcNow;
            await configs.SaveConfigAsync(config, cancellationToken);
        }
    }

    private async Task RunScheduledConversionAsync(
        DcToInvoiceAutomationConfig config,
        IDcToInvoiceService service,
        IDcToInvoiceConfigRepository configs,
        IClock clock,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? targetDcs = null;
        var mode = config.AutomationMode?.ToUpperInvariant();

        // In BOTH mode, use configured target DCs if specified; otherwise process all eligible
        if (mode == "BOTH" && config.DcNumbers != null && config.DcNumbers.Count > 0)
        {
            targetDcs = config.DcNumbers.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var batchRequest = new BatchInvoiceCreationRequest(
            DcNumbers: targetDcs,
            DryRun: false
        );

        _logger.LogInformation(
            "DcToInvoice scheduler executing conversion. Mode={Mode}, ScheduledTime={Time}, TargetDcs={Targets}",
            mode,
            config.AutomationTime,
            targetDcs != null && targetDcs.Count > 0 ? string.Join(", ", targetDcs) : "ALL eligible Delivery Challans");

        var result = await service.CreateInvoicesFromDeliveryChallansAsync(batchRequest, cancellationToken);

        string statusMsg = $"Scheduled run completed at {clock.LocalTimeOfDay:HH:mm}.";
        if (result.Data != null)
        {
            statusMsg = $"Scheduled run at {clock.LocalTimeOfDay:HH:mm}: {result.Data.CreatedInvoiceCount} invoice(s) created from {result.Data.TotalDeliveryChallans} candidate DC(s).";
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            statusMsg = $"Scheduled run at {clock.LocalTimeOfDay:HH:mm}: {result.Message}";
        }

        config.StatusDescription = statusMsg;
        config.LastScheduledRunUtc = clock.UtcNow;
        await configs.SaveConfigAsync(config, cancellationToken);

        _logger.LogInformation("DcToInvoice scheduler finished: {Message}", result.Message);
    }
}
