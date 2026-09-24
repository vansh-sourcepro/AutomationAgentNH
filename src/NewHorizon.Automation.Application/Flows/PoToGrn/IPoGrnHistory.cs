using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.PoToGrn;

public sealed record StartGrnRunRequest(
    TriggerSource Trigger,
    string? TriggeredBy,
    string? TriggerReference,
    GrnReceiptMode ReceiptMode,
    string? RequestedSites);

/// <summary>What happened to one PO at one warehouse, in the shape the history stores.</summary>
public sealed record PoGrnOutcome(
    PoGrnReceiptSubject Subject,
    PoGrnReceiptStatus Status,
    long? GrnId,
    string? GrnNumber,
    int LinesReceived,
    int LinesSkipped,
    string? Reason);

/// <summary>
/// Where PO → GRN runs and their per-PO results are recorded. Scoped: one instance follows one run.
/// </summary>
/// <remarks>
/// Best-effort by contract. A GRN that exists in the ERP must never be reported as a failure because
/// writing its history failed, so implementations log and swallow their own errors.
/// </remarks>
public interface IPoGrnHistory
{
    bool IsEnabled { get; }

    Guid? RunId { get; }

    Task StartRunAsync(StartGrnRunRequest request, CancellationToken cancellationToken);

    Task RecordAsync(PoGrnOutcome outcome, CancellationToken cancellationToken);

    Task CompleteRunAsync(CancellationToken cancellationToken);

    Task FailRunAsync(string reason, CancellationToken cancellationToken);
}

public sealed class NullPoGrnHistory : IPoGrnHistory
{
    public bool IsEnabled => false;

    public Guid? RunId => null;

    public Task StartRunAsync(StartGrnRunRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordAsync(PoGrnOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailRunAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}
