using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// Error body the ERP returns on a rejected request. Every field is optional — the agent must
/// still produce a usable layman message when the ERP sends nothing but a status code.
/// </summary>
public sealed record ErpErrorResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Business-friendly text, when the ERP supplies one.</summary>
    [JsonPropertyName("userMessage")]
    public string? UserMessage { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>
    /// Where the ERP actually puts the cause. Its own envelope answers a failure with a resource key
    /// in <c>message</c> ("UnhandledErrorKey") and the real explanation — a constraint violation, a
    /// timeout, a stack message — here. Without it the technical message says nothing an operator
    /// can act on.
    /// </summary>
    [JsonPropertyName("errors")]
    public JsonElement? Errors { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    public string? BestLaymanMessage =>
        FirstNonBlank(UserMessage, ErrorMessage, Message);

    public string? BestTechnicalMessage =>
        FirstNonBlank(ErrorText, Detail, ErrorMessage, Message, UserMessage);

    private string? ErrorText => Errors is { } errors
        && errors.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        ? (errors.ValueKind == JsonValueKind.String ? errors.GetString() : errors.ToString())
        : null;

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
