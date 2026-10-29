using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Flows.QuotationToSo;

/// <summary>
/// No-op implementation of <see cref="IQuotationToSoTracker"/> when database is not usable.
/// </summary>
public sealed class NullQuotationToSoTracker : IQuotationToSoTracker
{
    public static readonly NullQuotationToSoTracker Instance = new();

    public bool IsEnabled => false;

    public Guid? RunId => null;

    public Guid? ExecutionId => null;

    public Task StartRunAsync(
        TriggerSource triggerSource,
        string? triggeredBy,
        string? triggerReference,
        string? quotationNumbers,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartExecutionAsync(TrackedQuotation quotation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteExecutionAsync(
        string salesOrderNumber,
        long? salesOrderId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SkipExecutionAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteRunAsync(int quotationsExamined, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailRunAsync(string reason, int quotationsExamined, CancellationToken cancellationToken) => Task.CompletedTask;
}

