namespace NewHorizon.Automation.Domain.Flows.QuotationToSo;

/// <summary>
/// Partial update object for Quotation to Sales Order automation configuration.
/// </summary>
public sealed class QuotationSoAutomationConfigUpdate
{
    public bool? IsActive { get; init; }

    public SoAutomationRunMode? RunMode { get; init; }

    public TimeOnly? ScheduleTime { get; init; }

    public bool ClearScheduleTime { get; init; }

    public string? QuotationNumbers { get; init; }

    public bool ClearQuotationNumbers { get; init; }

    public string? CustomerCode { get; init; }

    public bool ClearCustomerCode { get; init; }

    public string? SiteCode { get; init; }

    public bool ClearSiteCode { get; init; }

    public bool? DryRun { get; init; }
}
