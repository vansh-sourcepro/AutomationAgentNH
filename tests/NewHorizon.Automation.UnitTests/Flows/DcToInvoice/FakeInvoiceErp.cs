using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NewHorizon.Automation.UnitTests.Flows.DcToInvoice;

internal sealed class FakeInvoiceErp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<string, JsonObject?, JsonNode?>> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    public List<(string Method, string Path, JsonObject? Body)> Requests { get; } = [];

    public Dictionary<string, (int Status, string ErrorMessage)> Faults { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void On(string pathFragment, Func<string, JsonObject?, JsonNode?> handler)
    {
        _routes[pathFragment] = handler;
    }

    public void On(string pathFragment, JsonNode cannedResponse)
    {
        _routes[pathFragment] = (_, _) => cannedResponse;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? "";
        JsonObject? body = null;

        if (request.Content is not null)
        {
            var contentString = await request.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(contentString) &&
                (contentString.TrimStart().StartsWith('{') || contentString.TrimStart().StartsWith('[')))
            {
                try
                {
                    var node = JsonNode.Parse(contentString);
                    if (node is JsonObject jo) body = jo;
                }
                catch
                {
                }
            }
        }

        Requests.Add((request.Method.Method, path, body));

        foreach (var (faultPath, fault) in Faults)
        {
            if (path.Contains(faultPath, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage((HttpStatusCode)fault.Status)
                {
                    Content = new StringContent(
                        $"{{\"success\":false,\"message\":\"{fault.ErrorMessage}\",\"errors\":null,\"data\":null}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }

        foreach (var (pathFragment, handler) in _routes)
        {
            if (path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase))
            {
                var responseNode = handler(path, body);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        responseNode?.ToJsonString() ?? "{\"success\":true,\"data\":null}",
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                $"{{\"success\":false,\"message\":\"Route not mocked in FakeInvoiceErp: {path}\"}}",
                Encoding.UTF8,
                "application/json")
        };
    }
}
