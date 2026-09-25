using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.UnitTests.Erp;

/// <summary>
/// A stand-in ERP over HTTP, answering whatever paths a flow's service calls.
/// </summary>
/// <remarks>
/// Hand-written rather than a mocking framework, matching the rest of the suite — there is no
/// mocking library in this solution and the ERP's shape is better expressed as canned JSON anyway.
/// Every response body here is the shape the live ERP actually returns, trimmed to the properties
/// the service reads. Requests are recorded so a test can assert what was sent, which is the only
/// way to check that the right vendor, the right site and the right indent reached the ERP.
/// </remarks>
internal sealed class FakeErp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<string, JsonObject?, JsonNode?>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _messages = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, JsonObject? Body)> Requests { get; } = [];

    /// <summary>Paths answered with an ERP business refusal — HTTP 200, success false.</summary>
    public Dictionary<string, string> Refusals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Paths answered with a raw non-2xx response — the shape a real infrastructure failure or an
    /// unhandled ERP exception takes (status, error message), as opposed to <see cref="Refusals"/>'s
    /// HTTP 200 business-refusal envelope. Exercises the real <c>ErpResponseHandler</c> classification
    /// rather than throwing a canned exception, so a test using this sees exactly what production code
    /// would do with the same response.
    /// </summary>
    public Dictionary<string, (int Status, string ErrorMessage)> Faults { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Like <see cref="Faults"/>, but the decision can look at the posted body (e.g. which vendor a
    /// create call is for) or close over call-order state, and can decline to fault by returning
    /// null — falling through to <see cref="Refusals"/>/the normal routes for that call.
    /// </summary>
    public Dictionary<string, Func<JsonObject?, (int Status, string ErrorMessage)?>> FaultRoutes { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<JsonObject> BodiesFor(string pathFragment) =>
        Requests
            .Where(request => request.Path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase))
            .Select(request => request.Body)
            .OfType<JsonObject>();

    public JsonObject? LastBodyFor(string pathFragment) => BodiesFor(pathFragment).LastOrDefault();

    public int CallCount(string pathFragment) =>
        Requests.Count(request => request.Path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Answers any path containing <paramref name="pathFragment"/> with this node.</summary>
    public FakeErp Route(string pathFragment, JsonNode? data)
    {
        _routes[pathFragment] = (_, _) => data?.DeepClone();
        return this;
    }

    /// <summary>Answers using the posted body, for endpoints whose reply depends on the request.</summary>
    public FakeErp Route(string pathFragment, Func<JsonObject?, JsonNode?> respond)
    {
        _routes[pathFragment] = (_, body) => respond(body);
        return this;
    }

    /// <summary>
    /// Answers using the request path, for the endpoints whose parameters travel in the URL —
    /// Document Control's financial year among them.
    /// </summary>
    public FakeErp RouteByPath(string pathFragment, Func<string, JsonNode?> respond)
    {
        _routes[pathFragment] = (path, _) => respond(path);
        return this;
    }

    /// <summary>
    /// Answers with a <c>message</c> alongside the data, for the endpoints that report their result
    /// there — Issue to Shop Floor's create puts the new document number in it.
    /// </summary>
    public FakeErp RouteWithMessage(string pathFragment, JsonNode? data, string message)
    {
        _messages[pathFragment] = message;
        return Route(pathFragment, data);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;

        JsonObject? body = null;

        if (request.Content is not null)
        {
            var raw = await request.Content.ReadAsStringAsync(cancellationToken);
            body = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw) as JsonObject;
        }

        Requests.Add((path, body));

        var faultRoute = FaultRoutes.FirstOrDefault(entry => path.Contains(entry.Key, StringComparison.OrdinalIgnoreCase));

        if (faultRoute.Key is not null && faultRoute.Value(body) is { } dynamicFault)
        {
            return FaultResponse(dynamicFault.Status, dynamicFault.ErrorMessage);
        }

        var fault = Faults.FirstOrDefault(entry => path.Contains(entry.Key, StringComparison.OrdinalIgnoreCase));

        if (fault.Key is not null)
        {
            return FaultResponse(fault.Value.Status, fault.Value.ErrorMessage);
        }

        var refusal = Refusals.FirstOrDefault(entry => path.Contains(entry.Key, StringComparison.OrdinalIgnoreCase));

        if (refusal.Key is not null)
        {
            // The ERP's own way of saying no: 200 with success false. It is a business refusal, not
            // a transport failure, and must never be retried.
            return Json(new JsonObject
            {
                ["data"] = null,
                ["success"] = false,
                ["message"] = refusal.Value,
                ["errorMessage"] = refusal.Value,
            });
        }

        // Longest fragment wins, so that "ItemVendorPurchase/list" is not shadowed by a shorter
        // fragment that happens to be a prefix of the same ERP controller's other routes.
        var route = _routes
            .Where(entry => path.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Key.Length)
            .FirstOrDefault();

        if (route.Key is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"FakeErp has no route for '{path}'.", Encoding.UTF8, "text/plain"),
            };
        }

        var envelope = new JsonObject
        {
            ["data"] = route.Value(path, body),
            ["success"] = true,
        };

        if (_messages.TryGetValue(route.Key, out var message))
        {
            envelope["message"] = message;
        }

        return Json(envelope);
    }

    private static HttpResponseMessage FaultResponse(int status, string errorMessage) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["message"] = errorMessage,
                    ["errorMessage"] = errorMessage,
                    ["errors"] = errorMessage,
                }.ToJsonString(new JsonSerializerOptions()),
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage Json(JsonNode envelope) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                envelope.ToJsonString(new JsonSerializerOptions()),
                Encoding.UTF8,
                "application/json"),
        };
}

internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://erp.test") };
}

internal sealed class StubTokenProvider : IErpTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");

    public Task<int> GetUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(1);

    public void Invalidate()
    {
    }
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; init; } = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    public TimeOnly LocalTimeOfDay => TimeOnly.FromDateTime(UtcNow.LocalDateTime);
}

/// <summary>
/// Keeps what was logged, so a test can assert on the one thing an operator actually sees. The
/// sweep records why an indent produced nothing in its result; the log line is the only place that
/// reaches anybody when the timer, and not a caller, started it.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));

    public IEnumerable<string> MessagesAt(LogLevel level) =>
        Entries.Where(entry => entry.Level == level).Select(entry => entry.Message);
}
