using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// Turns an HTTP response into either a result or the correctly classified exception. This is the
/// one place where "retry" versus "ask a human" is decided, so the rule lives here rather than
/// being re-implemented per operation.
/// </summary>
internal static class ErpResponseHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Business rejections the ERP's shared exception filter answers as a bare 500 — indistinguishable
    /// from a real infrastructure failure by status code alone. Matched against the technical message
    /// (the ERP's raw exception/RAISERROR text) rather than the status code, so these are never sent to
    /// human review — or reported to an operator — as "the ERP is unavailable". Keep this list short and
    /// auditable: every entry here is a known, catalogued ERP business rule, not a guess.
    /// </summary>
    private static readonly string[] KnownBusinessRejectionPhrases =
    [
        // WebAPICore NH1_Create_Procedures.sql (ResponseMessages.pur_purchase_pomaint_indentclose):
        // POEntryRepository.CreateAsync has no special handling for this RAISERROR, so it falls
        // through to GlobalExceptionFilter's generic default -> 500 branch, the same shape a real
        // outage would produce. The indent genuinely has nothing left to order; retrying will not help.
        "Indent is close or Po Qty is more than indent qty.",
    ];

    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(response);

        await EnsureSuccessAsync(response, endpoint, cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken);

        // A 200 with an unreadable body is a broken contract, not a business refusal; treating it
        // as transient lets a deploy that is mid-rollout recover on its own.
        return payload ?? throw new ErpTransientException(
            "The ERP returned an empty response. Automation will retry.",
            $"'{endpoint}' returned success with a body that could not be read as {typeof(T).Name}.",
            endpoint);
    }

    /// <summary>
    /// Reads an enveloped response — <c>{ data, success, message, errorMessage }</c> — and returns
    /// the <c>data</c> section.
    /// </summary>
    /// <remarks>
    /// Two things the plain <see cref="ReadAsync{T}"/> cannot do. First, it unwraps <c>data</c>.
    /// Second, and the reason it exists at all: these endpoints answer a refusal with HTTP 200 and
    /// <c>success: false</c>, so the status code alone would read that as a success and hand the
    /// caller a null payload. A refused call is a business failure, never retried.
    /// </remarks>
    public static async Task<T> ReadDataAsync<T>(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(response);

        await EnsureSuccessAsync(response, endpoint, cancellationToken);

        ErpEnvelope<T>? envelope;
        try
        {
            envelope = await ErpEnvelopeReader.ReadAsync<T>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A 200 whose body is not the ERP envelope is a broken contract rather than a refusal;
            // treating it as transient lets a mid-rollout deploy recover on its own.
            throw new ErpTransientException(
                "The ERP returned a response the agent could not read. Automation will retry.",
                $"'{endpoint}' returned success with a body that is not the ERP envelope: {ex.Message}",
                endpoint,
                ex);
        }

        if (envelope is null)
        {
            throw new ErpTransientException(
                "The ERP returned an empty response. Automation will retry.",
                $"'{endpoint}' returned success with an empty body.",
                endpoint);
        }

        if (!envelope.Success)
        {
            throw new ErpBusinessException(
                envelope.Reason ?? "The ERP rejected this request. Please review the document and try again.",
                $"'{endpoint}' returned success=false: {envelope.Reason ?? "no reason given"}.",
                endpoint);
        }

        return envelope.Data ?? throw new ErpBusinessException(
            "The ERP returned no data for this request.",
            $"'{endpoint}' reported success but carried no 'data' section.",
            endpoint);
    }

    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await TryReadErrorAsync(response, cancellationToken);
        var status = (int)response.StatusCode;

        // A 5xx whose message text is a known business rejection is not an outage: the ERP's shared
        // exception filter gives a business RAISERROR the same 500 shape as an infrastructure failure,
        // so the status code alone cannot tell them apart here — the message can.
        if (status >= 500 && MatchesKnownBusinessRejection(error))
        {
            throw new ErpBusinessException(
                error!.BestLaymanMessage ?? "The ERP rejected this request. Please review the document and try again.",
                $"'{endpoint}' returned {status} (classified as a business rejection, not an outage). {error.BestTechnicalMessage}".TrimEnd(),
                endpoint);
        }

        // 5xx, 408 and 429: the ERP is unwell or overloaded, and a later attempt may succeed.
        if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            throw new ErpTransientException(
                "The ERP is temporarily unavailable. Automation will retry automatically.",
                $"'{endpoint}' returned {status}. {error?.BestTechnicalMessage}".TrimEnd(),
                endpoint);
        }

        // 401 reaching here means the auth handler already re-authenticated and was refused
        // again — the service account genuinely lacks access, which no amount of retrying fixes.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ErpBusinessException(
                "Automation is not permitted to perform this action in the ERP. Check the AUTOMATION_AGENT role.",
                $"'{endpoint}' returned {status} after re-authentication. {error?.BestTechnicalMessage}".TrimEnd(),
                endpoint);
        }

        // Everything else in 4xx is the ERP understanding the request and refusing it.
        throw new ErpBusinessException(
            error?.BestLaymanMessage ?? "The ERP rejected this request. Please review the document and try again.",
            $"'{endpoint}' returned {status}. {error?.BestTechnicalMessage}".TrimEnd(),
            endpoint);
    }

    private static bool MatchesKnownBusinessRejection(ErpErrorResponse? error) =>
        error is not null
        && KnownBusinessRejectionPhrases.Any(phrase =>
            (error.BestTechnicalMessage ?? string.Empty).Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static async Task<ErpErrorResponse?> TryReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErpErrorResponse>(SerializerOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // A non-JSON error body (an IIS HTML error page, typically) must not mask the real
            // status code, which is the part that actually drives the retry decision.
            return null;
        }
    }
}
