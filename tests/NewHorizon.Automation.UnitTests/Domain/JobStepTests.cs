using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Domain;

public sealed class JobStepTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_completed_operation_can_never_run_again()
    {
        // The single invariant standing between a resumed job and a duplicate work order.
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "WorkOrderGeneration", 0);
        step.Start(Now);
        step.Complete(Now, "WO-1", "{}", "{}");

        var rerun = () => step.Start(Now);

        rerun.Should().Throw<DomainException>().WithMessage("*must never re-run*");
    }

    [Fact]
    public void A_skipped_operation_is_not_started_again()
    {
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "LaborPR", 0);
        step.Skip(Now, "PR for Labor not required");

        var rerun = () => step.Start(Now);

        rerun.Should().Throw<DomainException>();
        step.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void Completing_records_the_erp_document_and_both_payloads()
    {
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "PurchaseRequisition", 1);
        step.Start(Now);

        step.Complete(Now.AddSeconds(5), "PR-99", """{"item":"X"}""", """{"pr":"PR-99"}""");

        step.Status.Should().Be(StepStatus.Completed);
        step.ErpDocumentRef.Should().Be("PR-99");
        step.RequestPayload.Should().Be("""{"item":"X"}""");
        step.ResponsePayload.Should().Be("""{"pr":"PR-99"}""");
        step.CompletedAtUtc.Should().Be(Now.AddSeconds(5));
    }

    [Fact]
    public void An_operation_cannot_complete_without_having_started()
    {
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "Allocation", 0);

        var complete = () => step.Complete(Now, "A-1", null, null);

        complete.Should().Throw<DomainException>();
    }

    [Fact]
    public void Retry_returns_a_failed_operation_to_pending_and_counts_the_attempt()
    {
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "Allocation", 0);
        step.Start(Now);
        step.Fail(Now, null, null);

        step.PrepareForRetry();

        step.Status.Should().Be(StepStatus.Pending);
        step.RetryCount.Should().Be(1);
        step.CompletedAtUtc.Should().BeNull();
        // StartedAtUtc is kept: the first attempt is when the operation really began.
        step.StartedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void A_terminal_operation_is_never_retried()
    {
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "Allocation", 0);
        step.Start(Now);
        step.Complete(Now, "A-1", null, null);

        var retry = step.PrepareForRetry;

        retry.Should().Throw<DomainException>();
    }

    [Fact]
    public void Restarting_after_a_failure_keeps_the_original_start_time()
    {
        var step = JobStep.Create(Guid.NewGuid(), "SJO", "Allocation", 0);
        step.Start(Now);
        step.Fail(Now, null, null);
        step.PrepareForRetry();

        step.Start(Now.AddMinutes(5));

        step.StartedAtUtc.Should().Be(Now);
        step.Status.Should().Be(StepStatus.Running);
    }
}
