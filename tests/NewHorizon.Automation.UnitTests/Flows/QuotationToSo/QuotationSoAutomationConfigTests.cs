using FluentAssertions;
using NewHorizon.Automation.Domain.Flows.QuotationToSo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Flows.QuotationToSo;

public sealed class QuotationSoAutomationConfigTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 26);

    [Fact]
    public void Default_config_is_active_with_both_run_mode()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);

        config.IsActive.Should().BeTrue();
        config.RunMode.Should().Be(SoAutomationRunMode.Both);
        config.ScheduleTime.Should().BeNull();
        config.QuotationNumbers.Should().BeNull();
    }

    [Fact]
    public void Inactive_toggle_never_fires_on_schedule()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);
        config.Update(new QuotationSoAutomationConfigUpdate
        {
            ScheduleTime = new TimeOnly(10, 0),
            IsActive = false
        }, Now, "tester");

        config.ShouldRunOnSchedule(new TimeOnly(11, 0), Today).Should().BeFalse();
    }

    [Fact]
    public void Active_toggle_with_schedule_time_fires_at_or_after_slot()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);
        config.Update(new QuotationSoAutomationConfigUpdate
        {
            ScheduleTime = new TimeOnly(10, 30),
            IsActive = true
        }, Now, "tester");

        config.ShouldRunOnSchedule(new TimeOnly(10, 29), Today).Should().BeFalse();
        config.ShouldRunOnSchedule(new TimeOnly(10, 30), Today).Should().BeTrue();
        config.ShouldRunOnSchedule(new TimeOnly(11, 0), Today).Should().BeTrue();
    }

    [Fact]
    public void Once_run_today_scheduler_does_not_fire_again_today()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);
        config.Update(new QuotationSoAutomationConfigUpdate
        {
            ScheduleTime = new TimeOnly(10, 30),
            IsActive = true
        }, Now, "tester");

        config.RecordRunStarted(Now, Today);

        config.ShouldRunOnSchedule(new TimeOnly(11, 0), Today).Should().BeFalse();
    }

    [Theory]
    [InlineData(SoAutomationRunMode.Both, TriggerSource.Api, true)]
    [InlineData(SoAutomationRunMode.Both, TriggerSource.Timer, true)]
    [InlineData(SoAutomationRunMode.Both, TriggerSource.Manual, true)]
    [InlineData(SoAutomationRunMode.Api, TriggerSource.Api, true)]
    [InlineData(SoAutomationRunMode.Api, TriggerSource.Manual, true)]
    [InlineData(SoAutomationRunMode.Api, TriggerSource.Timer, false)]
    [InlineData(SoAutomationRunMode.Timer, TriggerSource.Timer, true)]
    [InlineData(SoAutomationRunMode.Timer, TriggerSource.Api, false)]
    [InlineData(SoAutomationRunMode.Disabled, TriggerSource.Api, false)]
    [InlineData(SoAutomationRunMode.Disabled, TriggerSource.Timer, false)]
    public void Run_mode_governs_trigger_permissions(SoAutomationRunMode mode, TriggerSource trigger, bool expected)
    {
        mode.Allows(trigger).Should().Be(expected);
    }

    [Fact]
    public void Quotation_numbers_are_normalised_and_deduped()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);
        config.Update(new QuotationSoAutomationConfigUpdate
        {
            QuotationNumbers = " 26-27/TE//000008, 000009, 26-27/TE//000008 \n 000010 ; 000009 "
        }, Now, "tester");

        config.QuotationNumbers.Should().Be("26-27/TE//000008, 000009, 000010");
    }

    [Fact]
    public void Clearing_quotation_numbers_reverts_to_null()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);
        config.Update(new QuotationSoAutomationConfigUpdate
        {
            QuotationNumbers = "26-27/TE//000008"
        }, Now, "tester");

        config.Update(new QuotationSoAutomationConfigUpdate
        {
            ClearQuotationNumbers = true
        }, Now, "tester");

        config.QuotationNumbers.Should().BeNull();
    }

    [Fact]
    public void Master_toggle_can_be_set_directly()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);
        config.IsActive.Should().BeTrue();

        config.SetActive(false, Now, "admin");
        config.IsActive.Should().BeFalse();
        config.UpdatedBy.Should().Be("admin");

        config.SetActive(true, Now, "admin");
        config.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Run_outcome_is_recorded_accurately()
    {
        var config = QuotationSoAutomationConfig.CreateDefault(Now);

        config.RecordRunStarted(Now, Today);
        config.LastTriggeredAtUtc.Should().Be(Now);
        config.LastScheduledRunDate.Should().Be(Today);

        config.RecordRunCompleted("Success", "1 quotation converted to Sales Order 1001", Now);
        config.LastRunStatus.Should().Be("Success");
        config.LastRunMessage.Should().Be("1 quotation converted to Sales Order 1001");
    }

    [Fact]
    public void FromSnapshot_reconstructs_all_properties_accurately()
    {
        var id = Guid.NewGuid();
        var time = new TimeOnly(15, 30);
        var snapshot = QuotationSoAutomationConfig.FromSnapshot(
            id: id,
            isActive: true,
            runMode: SoAutomationRunMode.Timer,
            scheduleTime: time,
            quotationNumbers: "QTN-001, QTN-002",
            customerCode: "CUST-99",
            siteCode: "SITE-1",
            dryRun: true,
            lastScheduledRunDate: Today,
            lastTriggeredAtUtc: Now,
            lastRunStatus: "Success",
            lastRunMessage: "All processed",
            updatedAtUtc: Now,
            updatedBy: "automation-test");

        snapshot.Id.Should().Be(id);
        snapshot.IsActive.Should().BeTrue();
        snapshot.RunMode.Should().Be(SoAutomationRunMode.Timer);
        snapshot.ScheduleTime.Should().Be(time);
        snapshot.QuotationNumbers.Should().Be("QTN-001, QTN-002");
        snapshot.CustomerCode.Should().Be("CUST-99");
        snapshot.SiteCode.Should().Be("SITE-1");
        snapshot.DryRun.Should().BeTrue();
        snapshot.LastScheduledRunDate.Should().Be(Today);
        snapshot.LastTriggeredAtUtc.Should().Be(Now);
        snapshot.LastRunStatus.Should().Be("Success");
        snapshot.LastRunMessage.Should().Be("All processed");
        snapshot.UpdatedAtUtc.Should().Be(Now);
        snapshot.UpdatedBy.Should().Be("automation-test");
    }

    [Fact]
    public void IndentPoAutomationConfig_quotation_helpers_function_correctly()
    {
        var row = NewHorizon.Automation.Domain.Flows.IndentToPo.IndentPoAutomationConfig.CreateQuotationDefault(Now);

        row.IndentKind.Should().Be(NewHorizon.Automation.Domain.Flows.IndentToPo.IndentKind.Quotation);
        row.IsActive.Should().BeTrue();
        row.RunMode.Should().Be(NewHorizon.Automation.Domain.Flows.IndentToPo.PoAutomationRunMode.Both);
        row.DryRun.Should().BeFalse();

        row.UpdateFromQuotation(
            runMode: NewHorizon.Automation.Domain.Flows.IndentToPo.PoAutomationRunMode.Timer,
            scheduleTime: new TimeOnly(14, 0),
            clearScheduleTime: false,
            quotationNumbers: "00001, 00002",
            clearQuotationNumbers: false,
            sites: "SITE-ALPHA",
            isActive: false,
            dryRun: true,
            nowUtc: Now,
            updatedBy: "admin");

        row.RunMode.Should().Be(NewHorizon.Automation.Domain.Flows.IndentToPo.PoAutomationRunMode.Timer);
        row.ScheduleTime.Should().Be(new TimeOnly(14, 0));
        row.IndentNumbers.Should().Be("00001, 00002");
        row.Sites.Should().Be("SITE-ALPHA");
        row.IsActive.Should().BeFalse();
        row.DryRun.Should().BeTrue();
        row.UpdatedBy.Should().Be("admin");

        row.SetActiveForQuotation(true, Now, "lead");
        row.IsActive.Should().BeTrue();
        row.UpdatedBy.Should().Be("lead");

        row.RecordQuotationRunStarted(Now, Today);
        row.LastTriggeredAtUtc.Should().Be(Now);
        row.LastScheduledRunDate.Should().Be(Today);

        row.RecordQuotationRunCompleted("StatusThatIsWayTooLongAndExceedsTwentyCharacters", Now);
        row.LastRunStatus.Should().HaveLength(20);
        row.LastRunStatus.Should().Be("StatusThatIsWayTooLo");
    }

    [Fact]
    public void IndentDocumentTypes_maps_quotation_kind_correctly()
    {
        NewHorizon.Automation.Domain.Flows.IndentToPo.IndentDocumentTypes.For(
            NewHorizon.Automation.Domain.Flows.IndentToPo.IndentKind.Quotation)
            .Should().Be("Quotation");
    }
}
