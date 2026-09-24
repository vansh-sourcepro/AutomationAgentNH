using NewHorizon.Automation.Application.Abstractions;

namespace NewHorizon.Automation.Infrastructure.Time;

/// <summary>
/// Production clock. The only place the agent reads the real time.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeOnly LocalTimeOfDay => TimeOnly.FromDateTime(DateTime.Now);

    public DateOnly LocalDate => DateOnly.FromDateTime(DateTime.Now);
}
