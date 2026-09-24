using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewHorizon.Automation.IntegrationTests.Erp;

/// <summary>
/// A real HTTP server standing in for the ERP. Deliberately a genuine Kestrel host rather than a
/// stubbed message handler, so the tests exercise the actual pipeline: sockets, status codes,
/// headers, JSON, and the auth handler's 401 replay.
/// </summary>
public sealed class StubErpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubErpServer(WebApplication app) => _app = app;

    /// <summary>Base address the agent should be pointed at.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Every request the stub received, for asserting on retries and replays.</summary>
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Number of tokens issued — proves caching prevents an auth call per request.</summary>
    public int TokensIssued;

    /// <summary>Raw login bodies received, for asserting the agent posts the ERP's own contract.</summary>
    public ConcurrentQueue<string> LoginBodies { get; } = new();

    /// <summary>Session-establishment calls received, keyed by the bearer token that carried them.</summary>
    public ConcurrentQueue<(string? BearerToken, string Body)> UserSessionCalls { get; } = new();

    /// <summary>When set, the session-establishment endpoint fails with this status.</summary>
    public int? UserSessionEndpointStatusOverride { get; set; }

    /// <summary>When set, the login endpoint fails with this status instead of issuing a token.</summary>
    public int? TokenEndpointStatusOverride { get; set; }

    /// <summary>Lifetime the stub reports; short values exercise proactive refresh.</summary>
    public int TokenExpiresInSeconds { get; set; } = 3600;

    /// <summary>
    /// Issue time the stub bases <c>validTo</c> on. The real ERP states an absolute expiry, so a
    /// test driving a <see cref="MutableClock"/> must point this at the same clock — otherwise the
    /// stub would date its tokens by wall time while the agent judges them by the test's.
    /// </summary>
    public Func<DateTimeOffset> NowUtc { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Tokens the stub will reject with 401, simulating a restarted ERP.</summary>
    public HashSet<string> RevokedTokens { get; } = [];

    /// <summary>Whether the ERP's own flag-driven automation is switched on for the transition.</summary>
    public bool ErpAutomationEnabled { get; set; } = true;

    /// <summary>Whether the ERP has finished the transition it automates internally.</summary>
    public bool ErpAutomationCompleted { get; set; } = true;

    /// <summary>Queued statuses for the next calls to an endpoint, to script failures.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<int>> ScriptedStatuses { get; } = new();

    public static async Task<StubErpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<StubErpState>();

        var app = builder.Build();
        var server = new StubErpServer(app);

        server.MapEndpoints(app);

        await app.StartAsync();

        server.BaseUrl = app.Urls.First();

        return server;
    }

    private void MapEndpoints(WebApplication app)
    {
        // Shaped like the ERP's real login: its envelope, its token object, its absolute expiry.
        app.MapPost("/api/v1/auth/login", async (HttpContext context) =>
        {
            using (var reader = new StreamReader(context.Request.Body))
            {
                LoginBodies.Enqueue(await reader.ReadToEndAsync());
            }

            if (TokenEndpointStatusOverride is { } status)
            {
                return Results.Json(
                    new { data = (object?)null, success = false, message = "InvalidUsernamePasswordKey" },
                    statusCode: status);
            }

            var issued = Interlocked.Increment(ref TokensIssued);

            return Results.Ok(new
            {
                data = new
                {
                    token = new
                    {
                        value = $"stub-token-{issued}",
                        validTo = NowUtc() + TimeSpan.FromSeconds(TokenExpiresInSeconds),
                    },
                    uid = "stub-uid",
                },
                success = true,
                message = (string?)null,
            });
        });

        // Mirrors the ERP UI's addSession call, which the agent must also make right after login —
        // see ErpEndpointOptions.AddUserSession.
        app.MapPost("/api/v1/setting/addusersession", async (HttpContext context) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                ? authorization["Bearer ".Length..]
                : null;

            using (var reader = new StreamReader(context.Request.Body))
            {
                UserSessionCalls.Enqueue((token, await reader.ReadToEndAsync()));
            }

            if (UserSessionEndpointStatusOverride is { } status)
            {
                return Results.Json(
                    new { data = (object?)null, success = false, message = "SessionRejected" },
                    statusCode: status);
            }

            return Results.Ok(new { data = 0, success = true, message = "SUCCESS" });
        });

        // One handler serves every ERP endpoint: the tests care about the pipeline's behaviour,
        // not about distinct ERP business logic, which lives on the ERP side.
        app.Map("/api/automation/{**path}", async (HttpContext context, string path) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                ? authorization["Bearer ".Length..]
                : null;

            Requests.Enqueue(new RecordedRequest(
                context.Request.Method,
                "/api/automation/" + path,
                token,
                context.Request.Headers["X-Correlation-Id"].FirstOrDefault(),
                context.Request.Headers["X-Idempotency-Key"].FirstOrDefault()));

            if (token is null || RevokedTokens.Contains(token))
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (ScriptedStatuses.TryGetValue(path, out var queue) && queue.TryDequeue(out var scripted))
            {
                return scripted >= 400
                    ? Results.Json(
                        new { message = $"scripted {scripted}", userMessage = "Vendor missing for item X" },
                        statusCode: scripted)
                    : Results.Ok(new { erpDocumentRef = "WO-OK", alreadyExisted = false });
            }

            await Task.Yield();

            return path switch
            {
                "existing-document" => Results.Ok(new { exists = false, erpDocumentRef = (string?)null }),
                "verify-automation" => Results.Ok(new
                {
                    completed = ErpAutomationCompleted,
                    erpDocumentRef = ErpAutomationCompleted ? "OAF-9001" : null,
                    erpAutomationEnabled = ErpAutomationEnabled,
                    inProgress = ErpAutomationEnabled && !ErpAutomationCompleted,
                    detail = (string?)null,
                }),
                "net-shortage" => Results.Ok(new { netShortage = 5m, detail = "5 units short" }),
                "mil-shortage" => Results.Ok(new { netShortage = 0m, detail = "no shortage" }),
                "allocation-status" => Results.Ok(new { childrenAllocated = true, detail = (string?)null }),
                "pending-documents" => Results.Ok(new[]
                {
                    new
                    {
                        documentType = "SalesOrder",
                        documentId = "SO-STUB-1",
                        workflowType = "SJO",
                        documentDateUtc = DateTimeOffset.UtcNow,
                    },
                }),
                _ => Results.Ok(new { erpDocumentRef = $"DOC-{path}", alreadyExisted = false }),
            };
        });
    }

    /// <summary>Scripts the next responses for an endpoint, in order.</summary>
    public void ScriptStatuses(string path, params int[] statuses)
    {
        var queue = ScriptedStatuses.GetOrAdd(path, _ => new ConcurrentQueue<int>());
        foreach (var status in statuses)
        {
            queue.Enqueue(status);
        }
    }

    public int RequestCountFor(string path) =>
        Requests.Count(request => request.Path.EndsWith(path, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed record RecordedRequest(
        string Method,
        string Path,
        string? BearerToken,
        string? CorrelationId,
        string? IdempotencyKey);

    private sealed class StubErpState;
}
