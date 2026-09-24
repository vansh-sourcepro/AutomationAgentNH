namespace NewHorizon.Automation.Application.Abstractions;

/// <summary>
/// Injected time. Working-hours gating and retention windows are date-sensitive, so tests must be
/// able to move the clock rather than sleep.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Server-local time, used only for the working-hours window: planners configure "08:00–18:00"
    /// in their own time, not UTC.
    /// </summary>
    TimeOnly LocalTimeOfDay { get; }

    /// <summary>
    /// Server-local date. The PO Automation scheduler compares it against
    /// <c>IndentPoAutomationConfig.LastScheduledRunDate</c> so a daily slot fires once and a missed
    /// slot catches up once. A default member so the existing test clocks need no change; a clock
    /// that must control the date overrides it.
    /// </summary>
    DateOnly LocalDate => DateOnly.FromDateTime(DateTime.Now);
}
