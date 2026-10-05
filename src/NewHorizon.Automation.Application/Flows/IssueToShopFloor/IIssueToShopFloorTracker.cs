namespace NewHorizon.Automation.Application.Flows.IssueToShopFloor;

/// <summary>
/// The write side of Issue to Shop Floor process tracking: records runs, executions, stages and outcomes.
/// </summary>
public interface IIssueToShopFloorTracker
{
    /// <summary>False when tracking is disabled or no automation DB is configured.</summary>
    bool IsEnabled { get; }

    Guid? RunId { get; }

    Guid? ExecutionId { get; }

    Task StartRunAsync(StartIssueRunRequest request, CancellationToken cancellationToken);

    Task StartExecutionAsync(
        string issueSource,
        string documentNumber,
        long documentId,
        int siteId,
        string? siteCode,
        CancellationToken cancellationToken);

    Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken);

    Task CompleteExecutionAsync(
        string outcome,
        string? issueNumber,
        int lineCount,
        decimal totalQuantity,
        string? refusalReason,
        string? checksJson,
        string? shortagesJson,
        string? linesJson,
        CancellationToken cancellationToken);

    Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken);

    Task CompleteRunAsync(int documentsExamined, int issuesCreated, CancellationToken cancellationToken);

    Task FailRunAsync(string reason, int documentsExamined, CancellationToken cancellationToken);
}
