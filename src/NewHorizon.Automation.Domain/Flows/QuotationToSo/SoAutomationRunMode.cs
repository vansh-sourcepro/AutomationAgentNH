using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.QuotationToSo;

/// <summary>
/// How Quotation to Sales Order automation may be started:
/// API/Manual only, Timer only, or Both.
/// </summary>
public enum SoAutomationRunMode
{
    /// <summary>Neither the scheduler nor the manual API may start conversion.</summary>
    Disabled = 0,

    /// <summary>An API call or manual trigger may convert quotations; the timer scheduler may not.</summary>
    Api = 1,

    /// <summary>The daily timer scheduler may convert quotations; external API calls may not.</summary>
    Timer = 2,

    /// <summary>Both the scheduler and external/manual API calls may convert quotations.</summary>
    Both = 3,
}

/// <summary>
/// Whether a run mode permits a given trigger source.
/// </summary>
public static class SoAutomationRunModeExtensions
{
    public static bool Allows(this SoAutomationRunMode mode, TriggerSource trigger) => trigger switch
    {
        TriggerSource.Timer => mode is SoAutomationRunMode.Timer or SoAutomationRunMode.Both,
        TriggerSource.Api or TriggerSource.Manual or TriggerSource.UserPrompt => mode is SoAutomationRunMode.Api or SoAutomationRunMode.Both,
        _ => true,
    };
}
