using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

/// <summary>
/// The rules the tracking entities keep on their own, before any database is involved.
/// </summary>
public sealed class ProcessTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_outcome_that_ordered_nothing_must_name_its_reason()
    {
        // The failure this whole table exists to prevent: an indent that converted to nothing,
        // recorded with the cause nowhere.
        var withoutReason = () => IndentPoOutcome.Skipped(Guid.NewGuid(), 1, "   ", Now);

        withoutReason.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_refusal_keeps_the_vendor_it_was_refused_for_when_there_is_one()
    {
        var outcome = IndentPoOutcome.Failed(
            Guid.NewGuid(),
            2,
            "Document Control has no 26-27 row for site 3.",
            Now,
            vendorCode: "V0044");

        outcome.Outcome.Should().Be(PoOutcomeKind.Failed);
        outcome.VendorCode.Should().Be("V0044");
        outcome.PoId.Should().BeNull();
        outcome.Reason.Should().StartWith("Document Control");
    }

    [Fact]
    public void A_note_that_belongs_to_no_vendor_group_is_still_recorded()
    {
        // "Every item line on this indent is already closed" is nobody's refusal in particular,
        // and losing it because there is no vendor to hang it on would be the old bug returning.
        var outcome = IndentPoOutcome.Skipped(
            Guid.NewGuid(),
            1,
            "Every item line on this indent is already closed, so there is nothing left to order.",
            Now);

        outcome.VendorCode.Should().BeNull();
        outcome.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_over_long_reason_is_trimmed_to_the_column()
    {
        // An ERP message longer than the column would otherwise fail the save, losing both the
        // reason and the outcome row it was attached to.
        var outcome = IndentPoOutcome.Skipped(Guid.NewGuid(), 1, new string('x', 2_000), Now);

        outcome.Reason.Should().HaveLength(1_000);
    }

    [Fact]
    public void An_order_carries_the_break_key_that_made_it_a_separate_document()
    {
        var outcome = IndentPoOutcome.Created(
            Guid.NewGuid(), 1, "V0012", "RS", "P0002", 4711, "26-27/TE/NF1/000002", 4, Now);

        outcome.Outcome.Should().Be(PoOutcomeKind.Created);
        outcome.VendorCode.Should().Be("V0012");
        outcome.CurrencyCode.Should().Be("RS");
        outcome.RateStructureCode.Should().Be("P0002");
        outcome.Reason.Should().BeNull();
    }

    [Fact]
    public void A_conversion_is_identified_by_its_indent_and_its_family()
    {
        var conversion = IndentPoConversion.Open(6841, IndentKind.Service, " IN/26-27/000012 ", 3, Now);

        conversion.IndentKind.Should().Be(IndentKind.Service);
        conversion.IndentNumber.Should().Be("IN/26-27/000012");

        // The document type is derived, never stored: two copies of the same fact could drift.
        conversion.DocumentType.Should().Be(IndentDocumentTypes.Service);
    }

    [Fact]
    public void Re_reading_an_indent_refreshes_what_the_erp_says_and_not_what_it_is()
    {
        var conversion = IndentPoConversion.Open(6841, IndentKind.Regular, "26-27/TE/NF1/000012", 1, Now);

        conversion.Refresh("26-27/TE/NF1/000012", siteId: 4, new DateOnly(2026, 8, 20));

        conversion.SiteId.Should().Be(4);
        conversion.IndentDate.Should().Be(new DateOnly(2026, 8, 20));
        conversion.IndentId.Should().Be(6841);
        conversion.IndentKind.Should().Be(IndentKind.Regular);
    }

    [Fact]
    public void A_run_records_how_far_it_got_even_when_it_failed()
    {
        var run = NewRun();

        run.RecordProgress(indentsExamined: 12, jobsCreated: 3, purchaseOrdersCreated: 1);
        run.Fail("The ERP stopped answering.", Now.AddSeconds(5));

        run.Status.Should().Be(RunStatus.Failed);
        run.IndentsExamined.Should().Be(12);
        run.PurchaseOrdersCreated.Should().Be(1);
        run.FailureReason.Should().Be("The ERP stopped answering.");
        run.CompletedAtUtc.Should().Be(Now.AddSeconds(5));
    }

    [Fact]
    public void A_finished_run_cannot_finish_again()
    {
        var run = NewRun();
        run.Complete(Now.AddSeconds(1));

        var again = () => run.Fail("Too late.", Now.AddSeconds(2));

        again.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_run_must_say_what_it_was_allowed_to_convert()
    {
        // Blank would make "found nothing" and "was not allowed to look" indistinguishable.
        var blank = () => AutomationRun.Start(
            "IndentToPurchaseOrder", TriggerSource.Api, AutomationMode.Full, "  ", Now);

        blank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_job_belongs_to_one_conversion_and_keeps_it()
    {
        var job = NewJob();
        var conversionId = Guid.NewGuid();

        job.LinkToConversion(conversionId, Guid.NewGuid());

        var relink = () => job.LinkToConversion(Guid.NewGuid(), null);

        // A job is the record of one attempt. Moving it to another indent would rewrite history.
        relink.Should().Throw<DomainException>();
        job.ConversionId.Should().Be(conversionId);
    }

    [Fact]
    public void A_job_enqueued_outside_a_run_is_still_linked_to_its_indent()
    {
        // A resume or an ERP push has no run, and must not be blocked from being tracked at all.
        var job = NewJob();

        job.LinkToConversion(Guid.NewGuid(), runId: null);

        job.RunId.Should().BeNull();
        job.ConversionId.Should().NotBeNull();
    }

    private static AutomationRun NewRun() => AutomationRun.Start(
        "IndentToPurchaseOrder",
        TriggerSource.Chatbot,
        AutomationMode.Full,
        "Regular, Capital, Service",
        Now);

    private static Job NewJob() => Job.Create(
        "IndentToPurchaseOrder",
        IndentDocumentTypes.Regular,
        "6841",
        AutomationMode.Full,
        Now);
}
