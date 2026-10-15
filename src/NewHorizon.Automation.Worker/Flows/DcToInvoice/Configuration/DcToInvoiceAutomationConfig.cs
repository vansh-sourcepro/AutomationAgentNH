using System;
using System.Collections.Generic;

namespace NewHorizon.Automation.Worker.Flows.DcToInvoice.Configuration;

/// <summary>
/// Configuration for the DC-to-Invoice automation flow.
/// </summary>
public class DcToInvoiceAutomationConfig
{
    public bool AutomationEnabled { get; set; } = false;
    public string? AutomationTime { get; set; } // e.g. "15:00"
    
    // Support for multiple DC numbers (or empty for all eligible)
    public List<string>? DcNumbers { get; set; }
    
    // Mode: INDIVIDUAL, TIMER, BOTH
    public string? AutomationMode { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateOnly? LastScheduledRunDate { get; set; }
}

public class DcToInvoiceAutomationConfigDto
{
    public bool AutomationEnabled { get; set; }
    public string? AutomationTime { get; set; }
    
    public string? DcNumber { get; set; }
    public string? AutomationMode { get; set; }
    public List<string>? DcNumbers { get; set; }
}
