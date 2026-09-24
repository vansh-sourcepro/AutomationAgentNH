using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

public sealed class IndentPoAutomationConfigTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 3);

    [Fact]
    public void A_new_row_is_indent_based_and_inert()
    {
        var config = IndentPoAutomationConfig.CreateDefault(IndentKind.Regular, Now);

        config.RunMode.Should().Be(PoAutomationRunMode.Api);   // "Indent-based"
        config.ScheduleTime.Should().BeNull();
        config.IndentNumbers.Should().BeNull();

        // Indent-based + no schedule ⇒ the scheduler never fires it, whatever the wall clock says.
        config.ShouldRunOnSchedule(new TimeOnly(23, 59), Today).Should().BeFalse();
    }

    // ---- the scheduler firing rule ---------------------------------------

    [Fact]
    public void A_timer_row_with_a_schedule_time_fires_from_its_slot_onward()
    {
        var config = Scheduled(new TimeOnly(8, 0));

        config.ShouldRunOnSchedule(new TimeOnly(7, 59), Today).Should().BeFalse();
        config.ShouldRunOnSchedule(new TimeOnly(8, 0), Today).Should().BeTrue();
        config.ShouldRunOnSchedule(new TimeOnly(23, 0), Today).Should().BeTrue();
    }

    [Theory]
    [InlineData(PoAutomationRunMode.Timer, true)]
    [InlineData(PoAutomationRunMode.Both, true)]
    [InlineData(PoAutomationRunMode.Api, false)]        // Indent-based: never on a timer
    [InlineData(PoAutomationRunMode.Disabled, false)]   // legacy
    public void Only_timer_or_both_modes_fire_on_a_schedule(PoAutomationRunMode mode, bool expected)
    {
        var config = IndentPoAutomationConfig.CreateDefault(IndentKind.Regular, Now);
        // Both is used so a schedule time actually persists even for the Api / Disabled cases.
        config.Update(new IndentPoAutomationConfigUpdate { RunMode = PoAutomationRunMode.Both, ScheduleTime = new TimeOnly(8, 0), IsActive = true }, Now, "admin");
        config.Update(new IndentPoAutomationConfigUpdate { RunMode = mode }, Now, "admin");

        // Api/Disabled clear the schedule time on the mode switch, so the check is false either way;
        // this asserts the rule holds even if a stray time were present.
        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().Be(expected);
    }

    [Fact]
    public void Clearing_the_schedule_time_disarms_the_scheduler()
    {
        var config = Scheduled(new TimeOnly(8, 0));

        config.Update(new IndentPoAutomationConfigUpdate { ClearScheduleTime = true }, Now, "admin");

        config.ScheduleTime.Should().BeNull();
        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeFalse();
    }

    [Fact]
    public void A_slot_already_run_today_does_not_fire_again()
    {
        var config = Scheduled(new TimeOnly(8, 0));

        config.MarkScheduledRun(Today, Now, RunStatus.Completed, Guid.NewGuid());

        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeFalse();
        config.LastScheduledRunDate.Should().Be(Today);
    }

    [Fact]
    public void Changing_the_schedule_time_re_arms_the_row_for_today()
    {
        var config = Scheduled(new TimeOnly(8, 0));
        config.MarkScheduledRun(Today, Now, RunStatus.Completed, Guid.NewGuid());
        config.ShouldRunOnSchedule(new TimeOnly(15, 0), Today).Should().BeFalse("it already ran today");

        config.Update(new IndentPoAutomationConfigUpdate { ScheduleTime = new TimeOnly(14, 0) }, Now, "admin");

        config.LastScheduledRunDate.Should().BeNull();
        config.ShouldRunOnSchedule(new TimeOnly(15, 0), Today).Should().BeTrue("the new time gets a fresh slot today");
    }

    [Fact]
    public void Re_saving_the_same_schedule_time_does_not_re_arm()
    {
        var config = Scheduled(new TimeOnly(8, 0));
        config.MarkScheduledRun(Today, Now, RunStatus.Completed, Guid.NewGuid());

        config.Update(
            new IndentPoAutomationConfigUpdate { ScheduleTime = new TimeOnly(8, 0), IndentNumbers = "000123" },
            Now,
            "admin");

        config.LastScheduledRunDate.Should().Be(Today);
        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeFalse();
    }

    [Fact]
    public void A_missed_slot_catches_up_once_the_next_day()
    {
        var config = Scheduled(new TimeOnly(8, 0));
        config.MarkScheduledRun(new DateOnly(2026, 9, 2), Now.AddDays(-1), RunStatus.Completed, Guid.NewGuid());

        config.ShouldRunOnSchedule(new TimeOnly(10, 0), Today).Should().BeTrue();

        config.MarkScheduledRun(Today, Now, RunStatus.Completed, Guid.NewGuid());
        config.ShouldRunOnSchedule(new TimeOnly(11, 0), Today).Should().BeFalse();
    }

    // ---- the per-row mode shapes the stored data ------------------------

    [Fact]
    public void Switching_to_indent_based_clears_the_schedule_time()
    {
        var config = Scheduled(new TimeOnly(8, 0), PoAutomationRunMode.Both);
        config.Update(new IndentPoAutomationConfigUpdate { IndentNumbers = "000162" }, Now, "admin");

        config.Update(new IndentPoAutomationConfigUpdate { RunMode = PoAutomationRunMode.Api }, Now, "admin");

        config.RunMode.Should().Be(PoAutomationRunMode.Api);
        config.ScheduleTime.Should().BeNull();
        config.IndentNumbers.Should().Be("000162");
        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeFalse();
    }

    [Fact]
    public void Switching_to_timer_based_clears_the_indent_numbers()
    {
        var config = IndentPoAutomationConfig.CreateDefault(IndentKind.Regular, Now);
        config.Update(new IndentPoAutomationConfigUpdate { IndentNumbers = "000162, 000163" }, Now, "admin");

        config.Update(
            new IndentPoAutomationConfigUpdate { RunMode = PoAutomationRunMode.Timer, ScheduleTime = new TimeOnly(14, 0) },
            Now, "admin");

        config.RunMode.Should().Be(PoAutomationRunMode.Timer);
        config.IndentNumbers.Should().BeNull();
        config.ScheduleTime.Should().Be(new TimeOnly(14, 0));
    }

    [Fact]
    public void Both_mode_keeps_a_schedule_and_indent_numbers()
    {
        var config = Scheduled(new TimeOnly(8, 0), PoAutomationRunMode.Both);
        config.Update(new IndentPoAutomationConfigUpdate { IndentNumbers = "000100" }, Now, "admin");

        config.RunMode.Should().Be(PoAutomationRunMode.Both);
        config.ScheduleTime.Should().Be(new TimeOnly(8, 0));
        config.IndentNumbers.Should().Be("000100");
        config.IndentNumberList().Should().Equal("000100");
        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeTrue();
    }

    // ---- indent-number normalisation ----------------------------------

    [Fact]
    public void Indent_numbers_are_trimmed_deduplicated_and_kept_in_order()
    {
        var config = IndentPoAutomationConfig.CreateDefault(IndentKind.Regular, Now);

        config.Update(
            new IndentPoAutomationConfigUpdate { IndentNumbers = " 26-27/PI/NF1/000162 , 000163 ,26-27/PI/NF1/000162" },
            Now,
            "admin");

        config.IndentNumbers.Should().Be("26-27/PI/NF1/000162, 000163");
        config.IndentNumberList().Should().Equal("26-27/PI/NF1/000162", "000163");
    }

    [Fact]
    public void An_empty_indent_numbers_string_collapses_to_null()
    {
        var config = IndentPoAutomationConfig.CreateDefault(IndentKind.Regular, Now);

        config.Update(new IndentPoAutomationConfigUpdate { IndentNumbers = "   ,  " }, Now, "admin");

        config.IndentNumbers.Should().BeNull();
    }

    [Fact]
    public void An_update_only_changes_the_fields_it_carries()
    {
        var config = Scheduled(new TimeOnly(7, 30), PoAutomationRunMode.Both);
        config.Update(new IndentPoAutomationConfigUpdate { IndentNumbers = "000100" }, Now, "admin");

        config.Update(new IndentPoAutomationConfigUpdate { ScheduleTime = new TimeOnly(9, 0) }, Now, "admin2");

        config.ScheduleTime.Should().Be(new TimeOnly(9, 0));
        config.IndentNumbers.Should().Be("000100");
        config.UpdatedBy.Should().Be("admin2");
    }

    // ---- the master on/off toggle (IsActive) ---------------------------

    [Fact]
    public void An_inactive_row_never_fires_on_a_schedule()
    {
        var config = Scheduled(new TimeOnly(8, 0));
        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeTrue("it is active and past its slot");

        config.Update(new IndentPoAutomationConfigUpdate { IsActive = false }, Now, "admin");

        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeFalse("PO Automation is turned off");
    }

    private static IndentPoAutomationConfig Scheduled(TimeOnly time, PoAutomationRunMode mode = PoAutomationRunMode.Timer)
    {
        var config = IndentPoAutomationConfig.CreateDefault(IndentKind.Regular, Now);
        config.Update(
            new IndentPoAutomationConfigUpdate { RunMode = mode, ScheduleTime = time, IsActive = true },
            Now,
            "admin");

        return config;
    }
}
