using System.Security.Cryptography;
using System.Text;

namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// Deterministic identity of "this document, this workflow". Layer one of idempotency: the
/// unique filtered index on this value stops the API push and the reconciliation poll from
/// ever creating two live jobs for the same document.
/// </summary>
/// <remarks>
/// The agent is deployed once per client, on that client's own server against that client's own
/// ERP, so a document identifier is already unique within a deployment. There is no tenant
/// component here and none is needed.
/// </remarks>
public sealed record IdempotencyKey
{
    /// <summary>Length of the hex-encoded SHA-256 value; the column is fixed at this width.</summary>
    public const int Length = 64;

    private IdempotencyKey(string value) => Value = value;

    public string Value { get; }

    /// <summary>
    /// Case- and whitespace-insensitive on the inputs: "SO-123" enqueued by the ERP push and
    /// " so-123 " returned by the reconciliation query must produce the same key, otherwise the
    /// safety net would duplicate every job it re-reported.
    /// </summary>
    public static IdempotencyKey For(string documentType, string documentId, string workflowType)
    {
        var canonical = string.Join(
            '|',
            Canonicalise(documentType, nameof(documentType)),
            Canonicalise(documentId, nameof(documentId)),
            Canonicalise(workflowType, nameof(workflowType)));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return new IdempotencyKey(Convert.ToHexStringLower(hash));
    }

    /// <summary>Rehydrates a key already computed and stored. Does not re-derive it.</summary>
    public static IdempotencyKey FromStoredValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length != Length)
        {
            throw new DomainException(
                $"Stored idempotency key must be {Length} characters; got {value.Length}.");
        }

        return new IdempotencyKey(value);
    }

    public override string ToString() => Value;

    private static string Canonicalise(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim().ToLowerInvariant();
    }
}
