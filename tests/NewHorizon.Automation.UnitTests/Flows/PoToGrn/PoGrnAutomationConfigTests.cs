using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Flows.PoToGrn;

public sealed class PoGrnAutomationConfigTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 24);

    [Fact]
    public void A_new_row_is_inert()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);

        config.IsActive.Should().BeFalse();
        config.RunMode.Should().Be(PoGrnRunMode.Api);
        config.ReceiptMode.Should().Be(GrnReceiptMode.Complete);
        config.HasInvoiceNumber.Should().BeFalse();
        config.ShouldRunOnSchedule(new TimeOnly(23, 59), Today).Should().BeFalse();
    }

    [Fact]
    public void PO_types_are_parsed_deduplicated_and_all_of_them_means_no_limit()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);

        config.PoTypeList().Should().BeEmpty();

        config.Update(new PoGrnAutomationConfigUpdate { PoTypes = [" capital ", "Capital"] }, Now, "test");
        config.PoTypes.Should().Be("Capital");
        config.PoTypeList().Should().Equal(PoGrnType.Capital);

        config.Update(new PoGrnAutomationConfigUpdate { PoTypes = ["Regular", "Capital"] }, Now, "test");
        config.PoTypes.Should().BeNull();

        config.Update(new PoGrnAutomationConfigUpdate { PoTypes = ["Capital"] }, Now, "test");
        config.Update(new PoGrnAutomationConfigUpdate { PoTypes = [] }, Now, "test");
        config.PoTypeList().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Service")]
    [InlineData("0")]
    public void Only_Regular_and_Capital_are_PO_types(string type)
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);

        var act = () => config.Update(new PoGrnAutomationConfigUpdate { PoTypes = [type] }, Now, "test");

        act.Should().Throw<DomainException>().WithMessage($"*'{type}' is not a PO type*");
    }

    [Fact]
    public void PO_numbers_are_trimmed_deduplicated_and_blank_clears_them()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);

        config.Update(new PoGrnAutomationConfigUpdate { PoNumbers = " 26-27/TE/NF1/000190 , 26-27/te/nf1/000190,000012" }, Now, "test");
        config.PoNumberList().Should().Equal("26-27/TE/NF1/000190", "000012");

        config.Update(new PoGrnAutomationConfigUpdate { PoNumbers = "  " }, Now, "test");
        config.PoNumbers.Should().BeNull();
        config.PoNumberList().Should().BeEmpty();
    }

    [Fact]
    public void Too_many_PO_numbers_are_refused()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);
        var many = string.Join(",", Enumerable.Range(1, 60).Select(n => $"26-27/TE/NF1/{n:000000}"));

        var act = () => config.Update(new PoGrnAutomationConfigUpdate { PoNumbers = many }, Now, "test");

        act.Should().Throw<DomainException>().WithMessage("*PoNumbers*");
    }

    [Fact]
    public void The_scheduler_fires_once_a_day_after_the_slot_when_on_and_timer_based()
    {
        var config = Scheduled(new TimeOnly(18, 0));

        config.ShouldRunOnSchedule(new TimeOnly(17, 59), Today).Should().BeFalse();
        config.ShouldRunOnSchedule(new TimeOnly(18, 0), Today).Should().BeTrue();

        config.MarkScheduledRun(Today, Now, RunStatus.Running, null);

        config.ShouldRunOnSchedule(new TimeOnly(20, 0), Today).Should().BeFalse();
        config.ShouldRunOnSchedule(new TimeOnly(18, 0), Today.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void Turning_it_off_stops_the_scheduler()
    {
        var config = Scheduled(new TimeOnly(18, 0));
        config.Update(new PoGrnAutomationConfigUpdate { IsActive = false }, Now, "test");

        config.ShouldRunOnSchedule(new TimeOnly(19, 0), Today).Should().BeFalse();
    }

    [Fact]
    public void PO_based_mode_has_no_schedule()
    {
        var config = Scheduled(new TimeOnly(18, 0));
        config.Update(new PoGrnAutomationConfigUpdate { RunMode = PoGrnRunMode.Api }, Now, "test");

        config.ScheduleTime.Should().BeNull();
    }

    [Fact]
    public void Changing_the_slot_gives_today_a_fresh_chance()
    {
        var config = Scheduled(new TimeOnly(9, 0));
        config.MarkScheduledRun(Today, Now, RunStatus.Completed, null);

        config.Update(new PoGrnAutomationConfigUpdate { ScheduleTime = new TimeOnly(15, 0) }, Now, "test");

        config.ShouldRunOnSchedule(new TimeOnly(15, 30), Today).Should().BeTrue();
    }

    [Fact]
    public void The_invoice_number_is_trimmed_editable_and_clearable()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);

        config.Update(new PoGrnAutomationConfigUpdate { InvoiceNumber = "  INV-01 " }, Now, "a");
        config.InvoiceNumber.Should().Be("INV-01");

        config.Update(new PoGrnAutomationConfigUpdate { InvoiceNumber = "INV-02" }, Now, "b");
        config.InvoiceNumber.Should().Be("INV-02");

        config.Update(new PoGrnAutomationConfigUpdate { ReceiptMode = GrnReceiptMode.Partial }, Now, "c");
        config.InvoiceNumber.Should().Be("INV-02", "an update that does not mention it leaves it alone");

        config.Update(new PoGrnAutomationConfigUpdate { InvoiceNumber = " " }, Now, "d");
        config.HasInvoiceNumber.Should().BeFalse();
    }

    [Fact]
    public void An_overlong_invoice_number_or_bad_site_is_refused()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);

        var tooLong = () => config.Update(new PoGrnAutomationConfigUpdate { InvoiceNumber = new string('9', 51) }, Now, "x");
        var badSite = () => config.Update(new PoGrnAutomationConfigUpdate { Sites = "1, two" }, Now, "x");

        tooLong.Should().Throw<DomainException>();
        badSite.Should().Throw<DomainException>();
    }

    [Fact]
    public void Sites_are_normalised()
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);
        config.Update(new PoGrnAutomationConfigUpdate { Sites = "4, 1,4" }, Now, "x");

        config.Sites.Should().Be("4, 1");
        config.SiteIds().Should().Equal(4, 1);
    }

    private static PoGrnAutomationConfig Scheduled(TimeOnly slot)
    {
        var config = PoGrnAutomationConfig.CreateDefault(Now);
        config.Update(
            new PoGrnAutomationConfigUpdate { IsActive = true, RunMode = PoGrnRunMode.Timer, ScheduleTime = slot },
            Now,
            "test");

        return config;
    }
}
