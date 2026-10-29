using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.QuotationToSo;

/// <summary>
/// A customer quotation to be converted and tracked.
/// </summary>
public sealed record TrackedQuotation(
    long QuotationId,
    string QuotationNumber,
    int SiteId,
    string? CustomerCode,
    DateOnly? QuotationDate);

/// <summary>
/// Process tracking interface for Quotation → Sales Order automation:
/// records runs, executions, stages, and outcomes.
/// </summary>
public interface IQuotationToSoTracker
{
    bool IsEnabled { get; }

    Guid? RunId { get; }

    Guid? ExecutionId { get; }

    Task StartRunAsync(
        TriggerSource triggerSource,
        string? triggeredBy,
        string? triggerReference,
        string? quotationNumbers,
        CancellationToken cancellationToken);

    Task StartExecutionAsync(TrackedQuotation quotation, CancellationToken cancellationToken);

    Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken);

    Task CompleteExecutionAsync(
        string salesOrderNumber,
        long? salesOrderId,
        CancellationToken cancellationToken);

    Task SkipExecutionAsync(string reason, CancellationToken cancellationToken);

    Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken);

    Task CompleteRunAsync(int quotationsExamined, CancellationToken cancellationToken);

    Task FailRunAsync(string reason, int quotationsExamined, CancellationToken cancellationToken);
}

