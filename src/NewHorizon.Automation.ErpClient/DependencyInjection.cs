using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Authentication;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;
using Polly;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// Wires the ERP client, its resilience pipeline and its authentication.
/// </summary>
public static class DependencyInjection
{
    public const string ErpHttpClientName = "ErpApi";
    public const string TokenHttpClientName = "ErpAuth";

    /// <summary>
    /// Set on a request that must never be replayed by the transport-level retry, because the ERP
    /// call it wraps is not safe to resend blind — the create-purchase-order endpoints, specifically.
    /// Query-before-create (RunSequenceAsync's fresh pending-items read, immediately before the
    /// create payload is built) is what makes a *fresh* attempt duplicate-safe; a raw transport retry
    /// skips straight back to replaying the already-built request and bypasses that re-verification.
    /// Recovery from a genuine transient failure here is a full re-attempt (a manual retry, or the
    /// next scheduled run) — not a transport-level resend.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<bool> NonRetryableKey = new("NewHorizon.NonRetryable");

    public static IServiceCollection AddErpClient(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The login client is deliberately plain: no auth handler (it *is* the auth call, and
        // adding one would recurse) and no retry beyond its own timeout, because a bad password
        // must fail fast and loudly rather than be retried into a backoff loop.
        services.AddHttpClient(TokenHttpClientName, ConfigureBaseAddress);

        services.AddSingleton<IErpTokenProvider>(serviceProvider =>
            ActivatorUtilities.CreateInstance<ErpTokenProvider>(
                serviceProvider,
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(TokenHttpClientName)));

        services.AddTransient<ErpAuthHandler>();

        var erpClientBuilder = services.AddHttpClient<IErpClient, HttpErpClient>(
            ErpHttpClientName,
            ConfigureBaseAddress);

        // Registration order is execution order, outermost first. Resilience wraps auth so a
        // retried attempt re-enters the auth handler and picks up a refreshed token; were it the
        // other way round, a retry after a long backoff could replay a token that has since expired.
        erpClientBuilder.AddResilienceHandler("erp", BuildPipeline);
        erpClientBuilder.AddHttpMessageHandler<ErpAuthHandler>();

        // Automation flows that drive ERP screens the generic IErpClient does not cover. Each
        // borrows the named client above. A new flow adds its own line here.
        services.AddIndentToPoErp();
        services.AddIssueToShopFloorErp();

        services.AddHealthChecks()
            .AddCheck<ErpApiHealthCheck>("erpApi", tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Signs in to the ERP as soon as the agent is running, rather than waiting for the first job.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddErpClient"/> for the same reason the timer and dispatcher are
    /// separate from the infrastructure: an integration-test host composes the ERP client without
    /// wanting a real sign-in fired at whatever it is pointed at.
    /// </remarks>
    public static IServiceCollection AddErpLoginStartup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<ErpLoginStartupService>();

        return services;
    }

    // There is deliberately no AddIndentToPoAutoConvert here any more. Indent → PO runs only when
    // POST /api/automation/indent-to-po asks for it; see the comment where that used to be called
    // in the Worker's Program.cs. IIndentToPoService is registered by AddErpClient above and is
    // reached from the endpoint, and from nothing else.

    private static void ConfigureBaseAddress(IServiceProvider serviceProvider, HttpClient client)
    {
        var options = serviceProvider.GetRequiredService<IOptions<AutomationAgentOptions>>().Value.ErpApi;

        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);

        // Timeout is enforced by the pipeline below; leaving HttpClient's own timeout infinite
        // stops it cancelling an attempt the pipeline still intends to manage.
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    private static void BuildPipeline(
        ResiliencePipelineBuilder<HttpResponseMessage> builder,
        ResilienceHandlerContext context)
    {
        var options = context.ServiceProvider
            .GetRequiredService<IOptions<AutomationAgentOptions>>().Value;

        var attemptTimeout = TimeSpan.FromSeconds(options.ErpApi.TimeoutSeconds);

        builder
            // Total budget across all attempts, so one stuck operation cannot hold a worker for
            // minutes while other jobs queue behind it.
            .AddTimeout(new Polly.Timeout.TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(60, options.ErpApi.TimeoutSeconds * 4)),
                Name = "erp-total-timeout",
            });

        // MaxRetry is legitimately configurable down to zero ("never retry at the transport
        // level"), but Polly rejects a retry strategy with no attempts, so the strategy is
        // omitted entirely rather than added with an invalid count.
        if (options.Defaults.MaxRetry > 0)
        {
            builder.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                Name = "erp-retry",
                MaxRetryAttempts = options.Defaults.MaxRetry,
                BackoffType = DelayBackoffType.Exponential,
                // Jitter keeps N parallel workers from retrying in lockstep and hammering an ERP
                // that is already struggling.
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = static arguments =>
                    ValueTask.FromResult(IsTransient(arguments.Outcome, arguments.Context)),
            });
        }

        builder
            .AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                Name = "erp-circuit-breaker",
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                // A non-retryable request's own failures never count against this breaker either —
                // acceptable, since it is one of several calls in the same PO-build sequence, so a
                // real ERP outage still trips the breaker via the surrounding (retryable) calls.
                ShouldHandle = static arguments =>
                    ValueTask.FromResult(IsTransient(arguments.Outcome, arguments.Context)),
            })
            // Per-attempt timeout, innermost, so a hung attempt is abandoned and retried rather
            // than consuming the whole budget.
            .AddTimeout(new Polly.Timeout.TimeoutStrategyOptions
            {
                Timeout = attemptTimeout,
                Name = "erp-attempt-timeout",
            });
    }

    /// <summary>
    /// Only transient conditions are retried or counted against the breaker. A business refusal
    /// (a 400 for a missing vendor) is deterministic: retrying produces the same answer and would
    /// wrongly trip the breaker against a perfectly healthy ERP.
    /// </summary>
    private static bool IsTransient(Outcome<HttpResponseMessage> outcome, ResilienceContext context)
    {
        if (context.GetRequestMessage()?.Options.TryGetValue(NonRetryableKey, out var nonRetryable) is true
            && nonRetryable)
        {
            return false;
        }

        if (outcome.Exception is { } exception)
        {
            return exception is HttpRequestException or TaskCanceledException or TimeoutException;
        }

        if (outcome.Result is not { } response)
        {
            return false;
        }

        var status = (int)response.StatusCode;

        return status >= 500
            || response.StatusCode is System.Net.HttpStatusCode.RequestTimeout
            || response.StatusCode is System.Net.HttpStatusCode.TooManyRequests;
    }
}
