using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// The response shape every ERP application API uses: the payload under <c>data</c>, with the
/// outcome carried by <c>success</c> rather than by the status code alone.
/// </summary>
/// <remarks>
/// The AutoShop endpoints were specified to the agent as bare payloads, so
/// <see cref="ErpResponseHandler.ReadAsync{T}"/> deserialises the whole body. Every endpoint the PO
/// flow touches is enveloped, hence this type and
/// <see cref="ErpResponseHandler.ReadDataAsync{T}"/>. A 200 carrying <c>success: false</c> is a
/// business refusal — the same rule the login path already follows.
/// </remarks>
internal sealed record ErpEnvelope<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("errors")]
    public JsonElement? Errors { get; init; }

    /// <summary>Whichever explanation the ERP filled in.</summary>
    public string? Reason
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return ErrorMessage;
            }

            if (!string.IsNullOrWhiteSpace(Message))
            {
                return Message;
            }

            return Errors is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }
                ? Errors.Value.ToString()
                : null;
        }
    }
}

/// <summary>
/// Reads an ERP response body that could not be parsed at all. Kept separate from
/// <see cref="ErpEnvelope{T}"/> so a malformed <c>data</c> section does not hide the envelope's own
/// verdict.
/// </summary>
internal static class ErpEnvelopeReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task<ErpEnvelope<T>?> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        // JsonObject/JsonArray need the duplicate-key-tolerant path below: the ERP is not always
        // careful about aliasing columns uniquely (a query joining the same lookup twice, say),
        // and System.Text.Json's own JsonObject throws the first time a row carrying a duplicate
        // key is touched — which can be anywhere downstream, for a property that is not even the
        // duplicated one, because the whole row materialises into a dictionary at once. Every other
        // T here is a typed contract the ERP is expected to send cleanly, so it keeps the plain path.
        if (typeof(T) != typeof(JsonObject) && typeof(T) != typeof(JsonArray))
        {
            return await response.Content.ReadFromJsonAsync<ErpEnvelope<T>>(SerializerOptions, cancellationToken);
        }

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        T? data = null;
        if (root.TryGetProperty("data", out var dataElement)
            && dataElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            data = DuplicateSafeJson.FromElement(dataElement) as T;
        }

        return new ErpEnvelope<T>
        {
            Data = data,
            Success = root.TryGetProperty("success", out var successElement)
                && successElement.ValueKind == JsonValueKind.True,
            Message = root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null,
            ErrorMessage = root.TryGetProperty("errorMessage", out var errorMessageElement)
                && errorMessageElement.ValueKind == JsonValueKind.String
                ? errorMessageElement.GetString()
                : null,
            Errors = root.TryGetProperty("errors", out var errorsElement) ? errorsElement.Clone() : null,
        };
    }
}

/// <summary>
/// Rebuilds a <see cref="JsonNode"/> tree from a parsed <see cref="JsonElement"/>, tolerating
/// duplicate object keys by keeping the last occurrence. <see cref="JsonDocument"/> parses
/// duplicate keys without complaint; <see cref="JsonObject"/> does not, once anything touches it.
/// </summary>
internal static class DuplicateSafeJson
{
    public static JsonNode? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    // Indexer assignment replaces an existing key rather than throwing, unlike the
                    // Add the framework's own JsonElement-to-JsonObject conversion uses.
                    obj[property.Name] = FromElement(property.Value);
                }

                return obj;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(FromElement(item));
                }

                return array;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            default:
                return JsonNode.Parse(element.GetRawText());
        }
    }
}
