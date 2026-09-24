using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Decisions;

/// <summary>
/// Advisory only, and deliberately outside the execution path: a recommendation may inform a
/// payload or a priority, but every ERP mutation remains a deterministic API call. An
/// unavailable or wrong recommendation must never change whether a document is created.
/// </summary>
public interface IDecisionService
{
    Task<VendorRecommendation?> RecommendVendorAsync(OperationContext context, CancellationToken cancellationToken);

    Task<int> RecommendPriorityAsync(OperationContext context, CancellationToken cancellationToken);

    Task<RiskAssessment> AssessRiskAsync(OperationContext context, CancellationToken cancellationToken);
}

/// <summary>A suggested vendor with the confidence and reasoning behind it, for audit.</summary>
public sealed record VendorRecommendation(string VendorCode, double Confidence, string Rationale);

/// <summary>Advisory risk signal surfaced in the UI; never gates execution on its own.</summary>
public sealed record RiskAssessment(RiskLevel Level, string Rationale);

public enum RiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}
