using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Workflows;

/// <summary>
/// Result of one operation, carrying everything the engine checkpoints before advancing:
/// the ERP document that was produced and the payloads that produced it.
/// </summary>
public sealed record OperationResult
{
    private OperationResult(OperationOutcome outcome)
    {
        Outcome = outcome;
    }

    public OperationOutcome Outcome { get; private init; }

    /// <summary>The ERP document this operation created or adopted. The key to duplicate-safety.</summary>
    public string? ErpDocumentRef { get; private init; }

    public string? RequestPayload { get; private init; }

    public string? ResponsePayload { get; private init; }

    /// <summary>Why it was skipped, or the failure reason in operator language.</summary>
    public string? Reason { get; private init; }

    /// <summary>Full technical detail for the admin view; never shown to business users by default.</summary>
    public string? TechnicalDetail { get; private init; }

    /// <summary>
    /// Steps this operation discovered and the engine should append to the plan — one per Site ID,
    /// for a discovery operation. Returned rather than applied directly so an operation body stays
    /// a pure function of its context and cannot mutate job state behind the engine's back.
    /// </summary>
    public IReadOnlyList<PlannedOperation> DiscoveredSteps { get; private init; } = [];

    public bool IsSuccess => Outcome is OperationOutcome.Succeeded or OperationOutcome.Skipped;

    public static OperationResult Success(
        string? erpDocumentRef = null,
        string? requestPayload = null,
        string? responsePayload = null) =>
        new(OperationOutcome.Succeeded)
        {
            ErpDocumentRef = erpDocumentRef,
            RequestPayload = requestPayload,
            ResponsePayload = responsePayload,
        };

    /// <summary>
    /// Succeeded, and produced further steps for the engine to append — how the site loop gets its
    /// length at run time while every site still gets its own checkpoint.
    /// </summary>
    public static OperationResult SuccessWithDiscoveredSteps(
        IReadOnlyList<PlannedOperation> discoveredSteps,
        string? responsePayload = null)
    {
        ArgumentNullException.ThrowIfNull(discoveredSteps);

        return new OperationResult(OperationOutcome.Succeeded)
        {
            DiscoveredSteps = discoveredSteps,
            ResponsePayload = responsePayload,
        };
    }

    public static OperationResult Skip(string reason) =>
        new(OperationOutcome.Skipped) { Reason = Require(reason) };

    public static OperationResult BusinessFailure(
        string laymanReason,
        string? technicalDetail = null,
        string? requestPayload = null,
        string? responsePayload = null) =>
        new(OperationOutcome.BusinessFailure)
        {
            Reason = Require(laymanReason),
            TechnicalDetail = technicalDetail,
            RequestPayload = requestPayload,
            ResponsePayload = responsePayload,
        };

    public static OperationResult TransientFailure(
        string laymanReason,
        string? technicalDetail = null,
        string? requestPayload = null,
        string? responsePayload = null) =>
        new(OperationOutcome.TransientFailure)
        {
            Reason = Require(laymanReason),
            TechnicalDetail = technicalDetail,
            RequestPayload = requestPayload,
            ResponsePayload = responsePayload,
        };

    private static string Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
