using System.Net.Http.Json;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.PoToGrn.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.PoToGrn.Services;

/// <summary>
/// Runs PO → GRN once a day at the settings row's Schedule Time, by calling the agent's own
/// <c>POST /api/automation/po-to-grn?trigger=timer</c> — the same endpoint, gate and code a manual
/// run uses, so there is one conversion path.
/// </summary>
/// <remarks>
/// Inert as shipped: the row is seeded switched off, PO-based and with no schedule. Registered only
/// with a usable automation database, like the Indent → PO scheduler it mirrors.
/// </remarks>
public sealed class GrnAutomationSchedulerService : BackgroundService
{
    public const string HttpClientName = "GrnAgentLoopback";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AutomationAgentOptions _options;
    private readonly ILogger<GrnAutomationSchedulerService> _logger;

    private bool _schemaMissingWarned;

    public GrnAutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        AutomationAgentOptions options,
        ILogger<GrnAutomationSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    private Uri Endpoint => new($"http://127.0.0.1:{_options.Host.ManagementApiPort}{PoToGrnEndpoints.Route}?trigger=timer");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PO → GRN automation scheduler started");

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
                        "PoGrnAutomationConfig does not exist — the AddPoToGrn migration has not been applied. "
                        + "Run 'dotnet ef database update'. The GRN scheduler stays idle until then; logged once.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GRN automation scheduler tick failed; the next tick will try again");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _logger.LogInformation("PO → GRN automation scheduler stopped");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var configs = scope.ServiceProvider.GetRequiredService<IPoGrnAutomationConfigRepository>();

        var config = await configs.GetAsync(cancellationToken);
        var today = clock.LocalDate;

        if (!config.ShouldRunOnSchedule(clock.LocalTimeOfDay, today))
        {
            return;
        }

        // Claim today's slot before calling, so a slow run cannot be fired twice by the next tick.
        config.MarkScheduledRun(today, clock.UtcNow, RunStatus.Running, runId: null);

        try
        {
            await configs.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GRN scheduler could not claim today's slot; it will try again on the next tick");
            return;
        }

        _logger.LogInformation("GRN scheduler calling {Endpoint} (dryRun {DryRun})", Endpoint, config.DryRun);

        try
        {
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = RunTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new { dryRun = config.DryRun }),
            };
            request.Headers.Add(ApiKeyFilter.HeaderName, _options.Host.InboundApiKey);

            using var response = await client.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Scheduled PO → GRN run returned {Status}: {Payload}", (int)response.StatusCode, payload);
            }
            else
            {
                _logger.LogWarning("Scheduled PO → GRN run returned {Status}: {Payload}", (int)response.StatusCode, payload);
                await StampFailedAsync(configs, clock, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Scheduled PO → GRN run call failed");
            await StampFailedAsync(configs, clock, cancellationToken);
        }
    }

    /// <summary>
    /// The endpoint stamps runs it actually started; a refusal before that (toggle off, no invoice
    /// number) would otherwise leave the row saying "Running" for the day.
    /// </summary>
    private async Task StampFailedAsync(IPoGrnAutomationConfigRepository configs, IClock clock, CancellationToken cancellationToken)
    {
        try
        {
            var fresh = await configs.GetAsync(cancellationToken);

            if (fresh.LastRunStatus == nameof(RunStatus.Running))
            {
                fresh.MarkRun(clock.UtcNow, RunStatus.Failed, fresh.LastRunReference);
                await configs.SaveAsync(fresh, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GRN scheduler could not record the failed outcome; today's slot is still claimed");
        }
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
}
