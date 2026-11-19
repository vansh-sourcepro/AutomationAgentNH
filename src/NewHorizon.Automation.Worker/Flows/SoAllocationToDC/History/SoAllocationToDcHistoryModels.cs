using System;
using System.Collections.Generic;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.History;

/// <summary>
/// Detailed execution record for a single allocation converted (or attempted) to a Delivery Challan.
/// </summary>
public sealed class SoAllocationToDcExecutionRecord
{
    public Guid JobId { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public int AllocationId { get; set; }
    public int OafId { get; set; }
    public string? OafNo { get; set; }
    public string? IntOrdNo { get; set; }
    public string? CustomerCode { get; set; }
    public string? ShipmentCode { get; set; }

    /// <summary>
    /// Execution status: "Completed", "Failed", "Skipped"
    /// </summary>
    public string Status { get; set; } = "Completed";

    public bool DcCreated { get; set; }
    public string? DcNumber { get; set; }
    public long? DcId { get; set; }
    public string? DcStatus { get; set; }

    public int LineCount { get; set; }
    public decimal AllocatedQty { get; set; }
    public decimal DcQty { get; set; }

    public string Trigger { get; set; } = "Manual"; // "Manual", "Timer", "Api"
    public string? TriggeredBy { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }

    public string? FailureReason { get; set; }
    public string? Reason => FailureReason;
    public string? FailedField { get; set; }
    public string? FailedApi { get; set; }

    public List<DcItemResultRecord> Items { get; set; } = new();
}

public sealed class DcItemResultRecord
{
    public string ItemCode { get; set; } = "";
    public string Status { get; set; } = "";
    public bool DcCreated { get; set; }
    public long? DcId { get; set; }
    public string? DcNumber { get; set; }
    public decimal? AllocatedQuantity { get; set; }
    public decimal? DcQuantity { get; set; }
    public string? Message { get; set; }
    public string? FailedField { get; set; }
    public string? FailedApi { get; set; }
}

/// <summary>
/// Summary record for a batch or single conversion run trigger invocation.
/// </summary>
public sealed class SoAllocationToDcRunRecord
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public string Trigger { get; set; } = "Manual";
    public string? TriggeredBy { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }

    public string Status { get; set; } = "Completed"; // "Completed", "Failed", "Partial"

    public int TotalAllocations { get; set; }
    public int ProcessedAllocations { get; set; }
    public int SuccessfulAllocations { get; set; }
    public int FailedAllocations { get; set; }
    public int SkippedAllocations { get; set; }
    public int CreatedDcCount { get; set; }

    public decimal TotalAllocatedQty { get; set; }
    public decimal TotalDcQty { get; set; }

    public string? FailureReason { get; set; }
    public List<Guid> JobIds { get; set; } = new();
}

/// <summary>
/// KPI summary metrics matching the Automation Dashboard requirements.
/// </summary>
public sealed class SoAllocationToDcSummaryDto
{
    public int TotalProcesses { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public double SuccessRate { get; set; }

    public int TotalDcsCreated { get; set; }
    public decimal TotalAllocatedQty { get; set; }
    public decimal TotalDcQty { get; set; }

    public int TotalTriggerAttempts { get; set; }
    public int TriggerAttemptsWithoutEligibleAllocation { get; set; }
    public int BusinessRefusalCount { get; set; }
    public int TechnicalFailureCount { get; set; }
    public int TotalJobs { get; set; }
    public double? AverageDurationMs { get; set; }

    public Dictionary<string, int> CountsByStatus { get; set; } = new();
}

/// <summary>
/// Daily trend point for trailing chart days.
/// </summary>
public sealed class SoAllocationToDcDailyStatDto
{
    public string Date { get; set; } = "";
    public int DcsCreated { get; set; }
    public int AllocationsConverted { get; set; }
    public int AllocationsFailed { get; set; }
    public int AllocationsSkipped { get; set; }

    // Chart compatibility aliases for shared UI chart components
    public int PurchaseOrdersCreated => DcsCreated;
    public int IndentsConverted => AllocationsConverted;
    public int IndentsFailed => AllocationsFailed;
}

/// <summary>
/// Composite dashboard response returning KPI summary, daily trends, last execution, and recent jobs.
/// </summary>
public sealed class SoAllocationToDcDashboardDto
{
    public SoAllocationToDcSummaryDto Summary { get; set; } = new();
    public List<SoAllocationToDcDailyStatDto> DailyStats { get; set; } = new();
    public List<SoAllocationToDcExecutionRecord> RecentJobs { get; set; } = new();
    public SoAllocationToDcExecutionRecord? LastCompletedJob { get; set; }
    public SoAllocationToDcExecutionRecord? LastExecution { get; set; }
}

/// <summary>
/// Paged conversion history grid response.
/// </summary>
public sealed class SoAllocationToDcHistoryPagedDto
{
    public List<SoAllocationToDcExecutionRecord> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
