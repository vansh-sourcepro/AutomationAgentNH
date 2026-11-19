using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.PoToGrn;

/// <summary>
/// One PO → GRN trigger invocation — a Run click, a scheduler tick or an API call. Kept even when
/// it received nothing, because "looked and found nothing" is worth knowing.
/// </summary>
public sealed class PoGrnRun
{
    private PoGrnRun()
    {
    }

    public Guid Id { get; private set; }

    public TriggerSource Trigger { get; private set; }

    public string? TriggeredBy { get; private set; }

    /// <summary>The request's trace id — the way back to its line in the request log.</summary>
    public string? TriggerReference { get; private set; }

    public GrnReceiptMode ReceiptMode { get; private set; }

    public string? RequestedSites { get; private set; }

    public RunStatus Status { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public int PosExamined { get; private set; }

    public int GrnsCreated { get; private set; }

    public int PosSkipped { get; private set; }

    public int PosFailed { get; private set; }

    public string? FailureReason { get; private set; }

    public static PoGrnRun Start(
        TriggerSource trigger,
        string? triggeredBy,
        string? triggerReference,
        GrnReceiptMode receiptMode,
        string? requestedSites,
        DateTimeOffset nowUtc) => new()
        {
            Id = Guid.NewGuid(),
            Trigger = trigger,
            TriggeredBy = Truncate(triggeredBy, 100),
            TriggerReference = Truncate(triggerReference, 100),
            ReceiptMode = receiptMode,
            RequestedSites = Truncate(requestedSites, 200),
            Status = RunStatus.Running,
            StartedAtUtc = nowUtc,
        };

    public void Count(PoGrnReceiptStatus outcome)
    {
        PosExamined++;

        switch (outcome)
        {
            case PoGrnReceiptStatus.Created:
                GrnsCreated++;
                break;
            case PoGrnReceiptStatus.Skipped:
                PosSkipped++;
                break;
            default:
                PosFailed++;
                break;
        }
    }

    public void Complete(DateTimeOffset nowUtc)
    {
        EnsureRunning();
        Status = RunStatus.Completed;
        CompletedAtUtc = nowUtc;
    }

    /// <summary>The run itself broke — the ERP was unreachable before discovery finished, say.</summary>
    public void Fail(string reason, DateTimeOffset nowUtc)
    {
        EnsureRunning();
        Status = RunStatus.Failed;
        FailureReason = Truncate(reason, 2000);
        CompletedAtUtc = nowUtc;
    }

    private void EnsureRunning()
    {
        if (Status != RunStatus.Running)
        {
            throw new DomainException($"PO → GRN run {Id} is already {Status}.");
        }
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
