using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Application.Jobs;

/// <summary>Filter for the jobs list the ERP UI renders.</summary>
public sealed record JobQuery
{
    public JobStatus? Status { get; init; }

    public string? WorkflowType { get; init; }

    public string? DocumentId { get; init; }

    public DateTimeOffset? CreatedFromUtc { get; init; }

    public DateTimeOffset? CreatedToUtc { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;
}

/// <summary>One page of results plus the total, so the UI can render a pager.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
