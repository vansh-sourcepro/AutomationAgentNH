using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NewHorizon.Automation.Worker.Flows.SoAllocationToDC.Configuration;

public class SoAllocationToDcAutomationConfig
{
    public bool AutomationEnabled { get; set; } = false;
    public string? AutomationTime { get; set; } // e.g. "14:00"
    
    // Support for multiple OAF numbers
    public List<string>? OafNumbers { get; set; }
    
    // Mode: INDIVIDUAL, TIMER, BOTH
    public string? AutomationMode { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateOnly? LastScheduledRunDate { get; set; }
}

public class SoAllocationToDcAutomationConfigDto
{
    public bool AutomationEnabled { get; set; }
    
    public string? AutomationTime { get; set; }
    
    // Legacy support for single OAF
    public string? AllocationNumber { get; set; }
    public string? OafNumber { get; set; }
    
    // New fields
    public string? AutomationMode { get; set; }
    public List<string>? OafNumbers { get; set; }
}
