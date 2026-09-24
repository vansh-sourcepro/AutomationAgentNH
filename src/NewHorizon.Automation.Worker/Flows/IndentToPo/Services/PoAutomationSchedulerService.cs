using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Worker.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Services;

/// <summary>
/// The daily trigger for indent → PO automation. Once a minute it reads the three
/// <see cref="IndentPoAutomationConfig"/> rows and, for each one whose slot has passed today and has
/// not already run, calls <c>POST /api/automation/indent-to-po/convert?trigger=timer</c> — the exact
/// same endpoint the screen's "Run now" button hits — with a request body built from that row.
/// </summary>
/// <remarks>
/// <para>
/// It converts nothing on its own: every row ships <see cref="PoAutomationRunMode.Disabled"/> and
/// inactive, so a fresh install behaves exactly as before until a person turns a type on from the
/// ERP screen and gives it a 24-hour schedule time.
/// </para>
/// <para>
/// Registered exactly once, and only when the automation database is usable — the same gate the
/// AutoShop cycle scheduler and the job dispatcher sit behind.
/// </para>
/// <para>
/// The slot is <b>claimed before the call</b> (<c>LastScheduledRunDate = today</c>, saved), so a
/// conversion that runs longer than the one-minute tick cannot be started twice.
/// "Slot passed today and not run today" is one rule for both the normal daily fire and a catch-up
/// after downtime.
/// </para>
/// </remarks>
public sealed class PoAutomationSchedulerService : BackgroundService
{
    public const string HttpClientName = "AgentLoopback";

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AutomationAgentOptions _options;
    private readonly ILogger<PoAutomationSchedulerService> _logger;

    /// <summary>
    /// True once the "IndentPoAutomationConfig is missing" warning has been logged, so it is said
    /// once rather than every minute until the migration is applied. Reset by a tick that succeeds.
    /// </summary>
    private bool _schemaMissingWarned;

    public PoAutomationSchedulerService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        AutomationAgentOptions options,
        ILogger<PoAutomationSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    private Uri ConvertEndpoint => new(
        $"http://127.0.0.1:{_options.Host.ManagementApiPort}/api/automation/indent-to-po/convert?trigger=timer");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Indent → PO automation scheduler started");

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
                        "IndentPoAutomationConfig does not exist — the PO Automation migration has not "
                        + "been applied. Run 'dotnet ef database update' (or apply deploy/sql/001_Schema.sql). "
                        + "The scheduler and the PO Automation screen stay idle until then; logged once.");
                }
            }
            catch (Exception ex)
            {
                // A failed tick must not kill the scheduler: the next one may well succeed, and a
                // dead timer would silently stop all scheduled automation.
                _logger.LogError(ex, "Automation scheduler tick failed; the next tick will try again");
            }

            await SafeDelayAsync(TickInterval, stoppingToken);
        }

        _logger.LogInformation("Indent → PO automation scheduler stopped");
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
        var configs = scope.ServiceProvider.GetRequiredService<IIndentPoAutomationConfigRepository>();

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
        IndentPoAutomationConfig config,
        IIndentPoAutomationConfigRepository configs,
        IClock clock,
        DateOnly localToday,
        CancellationToken cancellationToken)
    {
        // Claim the slot first: the next tick's ShouldRunOnSchedule then returns false, so a
        // conversion that outlasts the one-minute tick is never started twice. The outcome
        // (LastRunStatus / LastRunReference) is stamped by /convert itself once it finishes.
        config.MarkScheduledRun(localToday, clock.UtcNow, RunStatus.Running, runId: null);

        try
        {
            await configs.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Automation scheduler could not claim today's slot for {Kind}; it will try again on the next tick",
                config.IndentKind);
            return;
        }

        // The request body — the exact same shape the screen's "Run" button builds from the row:
        // the indent type, and the named indent numbers when the row has any (empty ⇒ every
        // eligible indent of that type, which is what the conversion endpoint already does).
        var body = new Dictionary<string, object?>
        {
            ["indentTypes"] = new[] { config.IndentKind.ToString() },
            ["dryRun"] = config.DryRun,
        };

        var indentNumbers = config.IndentNumberList();
        if (indentNumbers.Count > 0)
        {
            body["indentNumbers"] = indentNumbers;
        }

        var sites = config.SiteIds();
        if (sites.Count > 0)
        {
            body["sites"] = sites;
        }

        if (config.MaxIndentsPerRun is { } max)
        {
            body["maxIndents"] = max;
        }

        _logger.LogInformation(
            "Automation scheduler calling {Endpoint} for {Kind} (dryRun {DryRun}, indentNumbers {Numbers}, sites {Sites}, maxIndents {Max})",
            ConvertEndpoint,
            config.IndentKind,
            config.DryRun,
            indentNumbers.Count > 0 ? string.Join(",", indentNumbers) : "(all eligible)",
            sites.Count > 0 ? string.Join(",", sites) : "(configured default)",
            config.MaxIndentsPerRun);

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
                    "Scheduled {Kind} conversion returned {Status}: {Payload}",
                    config.IndentKind,
                    (int)response.StatusCode,
                    payload);
            }
            else
            {
                _logger.LogWarning(
                    "Scheduled {Kind} conversion returned {Status}: {Payload}",
                    config.IndentKind,
                    (int)response.StatusCode,
                    payload);
            }

            await RecordOutcomeAsync(
                config,
                configs,
                clock,
                response.IsSuccessStatusCode ? RunStatus.Completed : RunStatus.Failed,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The slot is already claimed, so this does not re-fire today. /convert may still be
            // running server-side and will stamp the outcome when it finishes.
            _logger.LogError(ex, "Scheduled {Kind} conversion call failed", config.IndentKind);
            await RecordOutcomeAsync(config, configs, clock, RunStatus.Failed, cancellationToken);
        }
    }

    /// <summary>
    /// Moves the row off the "Running" status it was claimed with, once the call has returned.
    /// A successful real run is left to <c>/convert</c>'s own stamping (it records the
    /// <c>AutomationRun</c> id the screen links to). A <b>failed</b> real run, or any dry run, is
    /// stamped here — otherwise a failed scheduled attempt sits on "Running" until tomorrow.
    /// Best-effort, exactly like the slot claim: a stamp failure must not crash the scheduler.
    /// </summary>
    private async Task RecordOutcomeAsync(
        IndentPoAutomationConfig config,
        IIndentPoAutomationConfigRepository configs,
        IClock clock,
        RunStatus status,
        CancellationToken cancellationToken)
    {
        if (!config.DryRun && status != RunStatus.Failed)
        {
            return;
        }

        try
        {
            config.MarkManualRun(clock.UtcNow, status, runId: null);
            await configs.SaveAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Automation scheduler could not record the {Status} outcome for {Kind}; the slot is still claimed for today",
                status,
                config.IndentKind);
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
