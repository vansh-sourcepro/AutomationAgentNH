using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Configuration;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Domain;

public sealed class AutomationConfigTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_config_defaults_to_full_automation_and_is_permitted()
    {
        var config = AutomationConfig.CreateDefault("SJO", Now);

        config.Mode.Should().Be(AutomationMode.Full);
        config.IsAutomationPermitted.Should().BeTrue();
        config.RetryCount.Should().Be(3);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Automation_is_blocked_when_any_gate_is_closed(bool licensed, bool agent, bool module)
    {
        // Licence, agent switch and module switch are independent; any one of them stops work.
        var config = AutomationConfig.CreateDefault("SJO", Now);
        config.Update(
            new AutomationConfigUpdate { IsLicensed = licensed, EnableAgent = agent, EnableModule = module },
            Now,
            "admin");

        config.IsAutomationPermitted.Should().BeFalse();
    }

    [Fact]
    public void An_unset_window_means_the_agent_works_at_any_hour()
    {
        var config = AutomationConfig.CreateDefault("SJO", Now);

        config.IsWithinWorkingHours(new TimeOnly(3, 0)).Should().BeTrue();
    }

    [Theory]
    [InlineData(7, 59, false)]
    [InlineData(8, 0, true)]
    [InlineData(13, 0, true)]
    [InlineData(17, 59, true)]
    [InlineData(18, 0, false)]
    public void A_daytime_window_includes_its_start_and_excludes_its_end(int hour, int minute, bool expected)
    {
        var config = WithWindow(new TimeOnly(8, 0), new TimeOnly(18, 0));

        config.IsWithinWorkingHours(new TimeOnly(hour, minute)).Should().Be(expected);
    }

    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(2, 0, true)]
    [InlineData(5, 59, true)]
    [InlineData(6, 0, false)]
    [InlineData(12, 0, false)]
    public void A_window_whose_end_precedes_its_start_crosses_midnight(int hour, int minute, bool expected)
    {
        // Night-shift plants configure 22:00–06:00; treating that as an empty range would
        // silently stop automation overnight.
        var config = WithWindow(new TimeOnly(22, 0), new TimeOnly(6, 0));

        config.IsWithinWorkingHours(new TimeOnly(hour, minute)).Should().Be(expected);
    }

    [Fact]
    public void An_update_only_changes_the_fields_it_carries()
    {
        var config = AutomationConfig.CreateDefault("SJO", Now);
        config.Update(new AutomationConfigUpdate { RetryCount = 7, ParallelWorkers = 8 }, Now, "admin");

        config.Update(new AutomationConfigUpdate { Mode = AutomationMode.Partial }, Now, "admin2");

        config.Mode.Should().Be(AutomationMode.Partial);
        config.RetryCount.Should().Be(7);
        config.ParallelWorkers.Should().Be(8);
        config.UpdatedBy.Should().Be("admin2");
    }

    [Fact]
    public void Working_hours_are_cleared_only_when_asked_explicitly()
    {
        var config = WithWindow(new TimeOnly(8, 0), new TimeOnly(18, 0));

        config.Update(new AutomationConfigUpdate { RetryCount = 5 }, Now, "admin");
        config.WorkingHoursStart.Should().NotBeNull();

        config.Update(new AutomationConfigUpdate { ClearWorkingHours = true }, Now, "admin");

        config.WorkingHoursStart.Should().BeNull();
        config.WorkingHoursEnd.Should().BeNull();
        config.IsWithinWorkingHours(new TimeOnly(3, 0)).Should().BeTrue();
    }

    [Fact]
    public void Zero_parallel_workers_is_rejected()
    {
        // Silently accepting zero would stop the agent claiming anything, with no error anywhere.
        var config = AutomationConfig.CreateDefault("SJO", Now);

        var update = () => config.Update(new AutomationConfigUpdate { ParallelWorkers = 0 }, Now, "admin");

        update.Should().Throw<DomainException>();
    }

    [Fact]
    public void Zero_retries_is_allowed_but_negative_is_not()
    {
        var config = AutomationConfig.CreateDefault("SJO", Now);

        config.Update(new AutomationConfigUpdate { RetryCount = 0 }, Now, "admin");
        config.RetryCount.Should().Be(0);

        var negative = () => config.Update(new AutomationConfigUpdate { RetryCount = -1 }, Now, "admin");
        negative.Should().Throw<DomainException>();
    }

    private static AutomationConfig WithWindow(TimeOnly start, TimeOnly end)
    {
        var config = AutomationConfig.CreateDefault("SJO", Now);
        config.Update(
            new AutomationConfigUpdate { WorkingHoursStart = start, WorkingHoursEnd = end },
            Now,
            "admin");

        return config;
    }
}
