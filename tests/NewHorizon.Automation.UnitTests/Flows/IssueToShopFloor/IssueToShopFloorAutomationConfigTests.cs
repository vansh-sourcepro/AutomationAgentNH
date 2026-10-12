using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class IssueToShopFloorAutomationConfigTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 7);

    [Fact]
    public void A_new_row_is_api_based_and_inert()
    {
        var config = IssueToShopFloorAutomationConfig.CreateDefault(IssueToShopFloorSource.Sjo, Now);

        config.RunMode.Should().Be(PoAutomationRunMode.Api);
        config.ScheduleTime.Should().BeNull();
        config.DocumentNumbers.Should().BeNull();
        config.IsActive.Should().BeFalse();

        // Api-based + no schedule ⇒ scheduler never fires it
        config.ShouldRunOnSchedule(new TimeOnly(23, 59), Today).Should().BeFalse();
    }

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
    [InlineData(PoAutomationRunMode.Api, false)]
    [InlineData(PoAutomationRunMode.Disabled, false)]
    public void Only_timer_or_both_modes_fire_on_a_schedule(PoAutomationRunMode mode, bool expected)
    {
        var config = IssueToShopFloorAutomationConfig.CreateDefault(IssueToShopFloorSource.Sjo, Now);
        config.Update(new IssueToShopFloorAutomationConfigUpdate { RunMode = PoAutomationRunMode.Both, ScheduleTime = new TimeOnly(8, 0), IsActive = true }, Now, "admin");
        config.Update(new IssueToShopFloorAutomationConfigUpdate { RunMode = mode }, Now, "admin");

        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().Be(expected);
    }

    [Fact]
    public void Inactive_row_never_fires_on_schedule()
    {
        var config = Scheduled(new TimeOnly(8, 0));
        config.Update(new IssueToShopFloorAutomationConfigUpdate { IsActive = false }, Now, "admin");

        config.ShouldRunOnSchedule(new TimeOnly(9, 0), Today).Should().BeFalse();
    }

    [Fact]
    public void Clearing_the_schedule_time_disarms_the_scheduler()
    {
        var config = Scheduled(new TimeOnly(8, 0));

        config.Update(new IssueToShopFloorAutomationConfigUpdate { ClearScheduleTime = true }, Now, "admin");

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

        config.Update(new IssueToShopFloorAutomationConfigUpdate { ScheduleTime = new TimeOnly(14, 0) }, Now, "admin");

        config.LastScheduledRunDate.Should().BeNull();
        config.ShouldRunOnSchedule(new TimeOnly(15, 0), Today).Should().BeTrue("the new time gets a fresh slot today");
    }

    [Fact]
    public void Document_numbers_and_sites_are_normalised()
    {
        var config = IssueToShopFloorAutomationConfig.CreateDefault(IssueToShopFloorSource.Sjo, Now);
        config.Update(
            new IssueToShopFloorAutomationConfigUpdate
            {
                DocumentNumbers = " 26-27/SJ/NF1/000123 , 26-27/SJ/NF1/000123 , 26-27/SJ/NF1/000124 ",
                Sites = " 1, 2 , 1 ",
                MaxDocumentsPerRun = 10,
            },
            Now,
            "admin");

        config.DocumentNumbers.Should().Be("26-27/SJ/NF1/000123,26-27/SJ/NF1/000124");
        config.Sites.Should().Be("1,2");
        config.SiteIds().Should().Equal([1, 2]);
        config.DocumentNumberList().Should().Equal(["26-27/SJ/NF1/000123", "26-27/SJ/NF1/000124"]);
        config.MaxDocumentsPerRun.Should().Be(10);
    }

    [Fact]
    public void Clear_flags_remove_document_numbers_and_max_ceiling()
    {
        var config = IssueToShopFloorAutomationConfig.CreateDefault(IssueToShopFloorSource.Sjo, Now);
        config.Update(
            new IssueToShopFloorAutomationConfigUpdate
            {
                DocumentNumbers = "26-27/SJ/NF1/000123",
                MaxDocumentsPerRun = 5,
            },
            Now,
            "admin");

        config.Update(
            new IssueToShopFloorAutomationConfigUpdate
            {
                ClearDocumentNumbers = true,
                ClearMaxDocumentsPerRun = true,
            },
            Now,
            "admin");

        config.DocumentNumbers.Should().BeNull();
        config.MaxDocumentsPerRun.Should().BeNull();
    }

    private static IssueToShopFloorAutomationConfig Scheduled(TimeOnly slot)
    {
        var config = IssueToShopFloorAutomationConfig.CreateDefault(IssueToShopFloorSource.Sjo, Now);
        config.Update(
            new IssueToShopFloorAutomationConfigUpdate
            {
                RunMode = PoAutomationRunMode.Timer,
                ScheduleTime = slot,
                IsActive = true,
            },
            Now,
            "admin");
        return config;
    }
}
