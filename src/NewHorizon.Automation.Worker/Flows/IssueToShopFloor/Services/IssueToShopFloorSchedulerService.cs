using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Services;

/// <summary>
/// The daily trigger for Issue to Shop Floor automation. Once a minute it reads the three
/// <see cref="IssueToShopFloorAutomationConfig"/> rows and, for each one whose slot has passed today and has
/// not already run, triggers conversion via <c>POST /api/automation/issue-to-shop-floor?trigger=timer</c>.
/// </summary>
public sealed class IssueToShopFloorSchedulerService : BackgroundService
{
    public const string HttpClientName = "IssueToShopFloorSchedulerLoopback";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AutomationAgentOptions _options;
    private readonly ILogger<IssueToShopFloorSchedulerService> _logger;

    private bool _schemaMissingWarned;

    public IssueToShopFloorSchedulerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        AutomationAgentOptions options,
        ILogger<IssueToShopFloorSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    private Uri ConvertEndpoint => new(
        $"http://127.0.0.1:{_options.Host.ManagementApiPort}/api/automation/issue-to-shop-floor?trigger=timer");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Issue to Shop Floor automation scheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                _schemaMissingWarned = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsSchemaMissing(ex))
            {
                if (!_schemaMissingWarned)
                {
                    _schemaMissingWarned = true;
                    _logger.LogWarning(
                        "IssueToShopFloorAutomationConfig does not exist — the Issue to Shop Floor configuration migration has not "
                        + "been applied. Run 'dotnet ef database update'. "
                        + "The scheduler and the configuration screen stay idle until then; logged once.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Issue to Shop Floor automation scheduler tick failed; the next tick will try again");
            }

            await SafeDelayAsync(TickInterval, stoppingToken);
        }

        _logger.LogInformation("Issue to Shop Floor automation scheduler stopped");
    }

    private static bool IsSchemaMissing(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name == "SqlException"
                && current.Message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var configs = scope.ServiceProvider.GetRequiredService<IIssueToShopFloorAutomationConfigRepository>();

        var localNow = clock.LocalTimeOfDay;
        var localToday = clock.LocalDate;

        foreach (var config in await configs.GetAllAsync(cancellationToken))
        {
            if (!config.ShouldRunOnSchedule(localNow, localToday))
            {
                continue;
            }

            await RunOneAsync(config, configs, clock, localToday, cancellationToken);
        }
    }

    private async Task RunOneAsync(
        IssueToShopFloorAutomationConfig config,
        IIssueToShopFloorAutomationConfigRepository configs,
        IClock clock,
        DateOnly localToday,
        CancellationToken cancellationToken)
    {
        var docList = config.DocumentNumberList();

        // Claim slot first
        config.MarkScheduledRun(localToday, clock.UtcNow, RunStatus.Running, runId: null);

        try
        {
            await configs.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Automation scheduler could not claim today's slot for {Source}; it will try again on the next tick",
                config.Source);
            return;
        }

        var overallSuccess = true;
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = ConvertTimeout;

        if (docList.Count == 0)
        {
            _logger.LogInformation(
                "Automation scheduler calling {Endpoint} for {Source} (timer-based mode: bulk discovery of all eligible documents, dryRun {DryRun})",
                ConvertEndpoint,
                config.Source,
                config.DryRun);

            try
            {
                var body = new IssueToShopFloorRequest(
                    IssueType: config.Source.ToString().ToLowerInvariant(),
                    Mode: "numberBased",
                    DocumentNumber: null,
                    DryRun: config.DryRun);

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
                        "Scheduled bulk {Source} conversion returned {Status}: {Payload}",
                        config.Source,
                        (int)response.StatusCode,
                        payload);
                }
                else
                {
                    overallSuccess = false;
                    _logger.LogWarning(
                        "Scheduled bulk {Source} conversion returned {Status}: {Payload}",
                        config.Source,
                        (int)response.StatusCode,
                        payload);
                }
            }
            catch (Exception ex)
            {
                overallSuccess = false;
                _logger.LogError(ex, "Scheduled bulk {Source} conversion failed", config.Source);
            }
        }
        else
        {
            var targets = docList.AsEnumerable();
            if (config.MaxDocumentsPerRun is { } max && max > 0)
            {
                targets = targets.Take(max);
            }

            var targetList = targets.ToList();
            _logger.LogInformation(
                "Automation scheduler calling {Endpoint} for {Source} ({Count} documents: {Docs}, dryRun {DryRun})",
                ConvertEndpoint,
                config.Source,
                targetList.Count,
                string.Join(", ", targetList),
                config.DryRun);

            foreach (var docNumber in targetList)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var body = new IssueToShopFloorRequest(
                        IssueType: config.Source.ToString().ToLowerInvariant(),
                        Mode: "numberBased",
                        DocumentNumber: docNumber,
                        DryRun: config.DryRun);

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
                            "Scheduled {Source} conversion for {Doc} returned {Status}: {Payload}",
                            config.Source,
                            docNumber,
                            (int)response.StatusCode,
                            payload);
                    }
                    else
                    {
                        overallSuccess = false;
                        _logger.LogWarning(
                            "Scheduled {Source} conversion for {Doc} returned {Status}: {Payload}",
                            config.Source,
                            docNumber,
                            (int)response.StatusCode,
                            payload);
                    }
                }
                catch (Exception ex)
                {
                    overallSuccess = false;
                    _logger.LogError(ex, "Scheduled {Source} conversion failed for document {Doc}", config.Source, docNumber);
                }
            }
        }

        await RecordOutcomeAsync(
            config,
            configs,
            clock,
            overallSuccess ? RunStatus.Completed : RunStatus.Failed,
            cancellationToken);
    }

    private async Task RecordOutcomeAsync(
        IssueToShopFloorAutomationConfig config,
        IIssueToShopFloorAutomationConfigRepository configs,
        IClock clock,
        RunStatus status,
        CancellationToken cancellationToken)
    {
        try
        {
            config.MarkManualRun(clock.UtcNow, status, runId: null);
            await configs.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Automation scheduler could not record the {Status} outcome for {Source}; the slot is still claimed for today",
                status,
                config.Source);
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }
    }
}
