using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Domain;

public sealed class JobStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_job_starts_pending_with_a_plan()
    {
        var job = CreateJob();

        job.Status.Should().Be(JobStatus.Pending);
        job.Steps.Should().HaveCount(3);
        job.Steps.Select(step => step.Sequence).Should().BeInAscendingOrder();
        job.NextStep()!.OperationName.Should().Be("DeAllocation");
    }

    [Fact]
    public void Only_a_pending_job_can_be_claimed()
    {
        var job = CreateJob();
        job.Claim(Now);

        job.Status.Should().Be(JobStatus.Running);
        job.StartedAtUtc.Should().Be(Now);

        var second = () => job.Claim(Now);
        second.Should().Throw<InvalidJobTransitionException>();
    }

    [Fact]
    public void A_running_job_fails_and_a_retry_requeues_it_at_higher_priority()
    {
        var job = CreateJob();
        job.Claim(Now);
        RunStep(job, Now);
        job.NextStep()!.Start(Now);
        job.NextStep()!.Fail(Now, requestPayload: null, responsePayload: null);
        job.Fail(Now);

        job.Status.Should().Be(JobStatus.Failed);
        var priorityBeforeRetry = job.Priority;

        job.RequeueForRetry();

        // Re-queued rather than resumed in place: one claiming path, and the priority bump is what
        // gets it picked up ahead of newly enqueued work.
        job.Status.Should().Be(JobStatus.Pending);
        job.Priority.Should().BeGreaterThan(priorityBeforeRetry);
        job.RetryCount.Should().Be(1);
        job.CompletedAtUtc.Should().BeNull();

        // The failed operation is pending again; the completed one before it is untouched.
        job.Steps[0].Status.Should().Be(StepStatus.Completed);
        job.Steps[1].Status.Should().Be(StepStatus.Pending);
    }

    [Fact]
    public void A_job_that_never_failed_cannot_be_retried()
    {
        var job = CreateJob();
        job.Claim(Now);

        var retry = () => job.RequeueForRetry();

        retry.Should().Throw<InvalidJobTransitionException>();
    }

    [Fact]
    public void A_running_job_skips_on_a_business_rule_refusal_and_is_terminal()
    {
        var job = CreateJob();
        job.Claim(Now);

        job.Skip(Now);

        job.Status.Should().Be(JobStatus.Skipped);
        job.CompletedAtUtc.Should().Be(Now);
        job.IsTerminal.Should().BeTrue();

        // Unlike a technical Failed job, a business-rule refusal is never retried in place: the
        // underlying data hasn't changed, so retrying would just reproduce the same refusal.
        var retry = () => job.RequeueForRetry();
        retry.Should().Throw<InvalidJobTransitionException>();
    }

    [Fact]
    public void Only_a_running_job_can_be_skipped()
    {
        var job = CreateJob();

        var skip = () => job.Skip(Now);

        skip.Should().Throw<InvalidJobTransitionException>();
    }

    [Fact]
    public void Approval_releases_the_gate_and_records_the_approver_on_job_and_step()
    {
        var job = CreateJob(AutomationMode.Partial);
        job.Claim(Now);
        RunStep(job, Now);

        var gatedStep = job.NextStep()!;
        job.PauseForApproval(gatedStep.Stage, gatedStep.OperationName);
        job.Status.Should().Be(JobStatus.AwaitingApproval);

        var approvedAt = Now.AddMinutes(30);
        job.Approve("planner@client.com", approvedAt, "Checked against budget");

        job.Status.Should().Be(JobStatus.Pending);
        job.ApprovedBy.Should().Be("planner@client.com");
        job.ApprovedAtUtc.Should().Be(approvedAt);

        // Audit must survive on the specific operation that was gated, not only on the job.
        gatedStep.ApprovedBy.Should().Be("planner@client.com");
        gatedStep.ApprovedAtUtc.Should().Be(approvedAt);
        gatedStep.Remarks.Should().Be("Checked against budget");
    }

    [Fact]
    public void Rejection_cancels_the_job_with_the_rejector_and_reason()
    {
        var job = CreateJob(AutomationMode.Partial);
        job.Claim(Now);
        var gatedStep = job.NextStep()!;
        job.PauseForApproval(gatedStep.Stage, gatedStep.OperationName);

        job.Reject("planner@client.com", "Vendor not approved", Now);

        job.Status.Should().Be(JobStatus.Cancelled);
        job.CancelledBy.Should().Be("planner@client.com");
        job.CancellationReason.Should().Be("Vendor not approved");
        job.CompletedAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Approve_and_reject_only_apply_at_an_approval_gate()
    {
        // Guards the semantic split: approval is a business decision, not failure recovery.
        var job = CreateJob(AutomationMode.Partial);
        job.Claim(Now);

        var approve = () => job.Approve("someone", Now);
        var reject = () => job.Reject("someone", "no", Now);

        approve.Should().Throw<InvalidJobTransitionException>();
        reject.Should().Throw<InvalidJobTransitionException>();
    }

    [Fact]
    public void A_full_mode_job_has_no_approval_gates()
    {
        var job = CreateJob();
        job.Claim(Now);

        var pause = () => job.PauseForApproval("SJO", "PurchaseRequisition");

        pause.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_job_cannot_complete_while_an_operation_is_outstanding()
    {
        var job = CreateJob();
        job.Claim(Now);
        RunStep(job, Now);

        var complete = () => job.Complete(Now);

        complete.Should().Throw<DomainException>()
            .WithMessage("*has not finished*");
    }

    [Fact]
    public void A_job_completes_when_every_operation_is_terminal()
    {
        var job = CreateJob();
        job.Claim(Now);
        RunStep(job, Now, "WO-1");
        RunStep(job, Now, "PR-1");
        job.NextStep()!.Skip(Now, "No labour shortage");

        job.Complete(Now);

        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAtUtc.Should().Be(Now);
        job.NextStep().Should().BeNull();
    }

    [Fact]
    public void A_crashed_job_becomes_resumable_and_restarts_at_the_interrupted_operation()
    {
        var job = CreateJob();
        job.Claim(Now);
        RunStep(job, Now, "WO-1");
        job.NextStep()!.Start(Now);

        job.MarkResumable();

        job.Status.Should().Be(JobStatus.Pending);
        // Resume point: the operation that was mid-flight, not the completed one before it.
        job.NextStep()!.OperationName.Should().Be("Allocation");
        job.Steps[0].Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public void A_finished_job_cannot_be_cancelled()
    {
        var job = CreateJob();
        job.Claim(Now);
        job.Cancel("admin", "Order withdrawn", Now);

        var again = () => job.Cancel("admin", "Order withdrawn", Now);

        again.Should().Throw<InvalidJobTransitionException>();
    }

    [Fact]
    public void Steps_are_planned_once()
    {
        var job = CreateJob();

        var replan = () => job.PlanSteps([new PlannedOperation("SJO", "DeAllocation")]);

        replan.Should().Throw<DomainException>().WithMessage("*already has a plan*");
    }

    [Fact]
    public void Completed_document_refs_are_exposed_to_later_operations()
    {
        var job = CreateJob();
        job.Claim(Now);
        RunStep(job, Now, "WO-4711");

        job.CompletedDocumentRefs().Should().ContainKey("SJO/DeAllocation")
            .WhoseValue.Should().Be("WO-4711");
    }

    private static Job CreateJob(AutomationMode mode = AutomationMode.Full)
    {
        var job = Job.Create("SJO", "SalesOrder", "SO-123", mode, Now);
        job.PlanSteps(
        [
            new PlannedOperation("SJO", "DeAllocation"),
            new PlannedOperation("SJO", "Allocation"),
            new PlannedOperation("SJO", "WorkOrderGeneration"),
        ]);

        return job;
    }

    private static void RunStep(Job job, DateTimeOffset now, string? erpDocumentRef = null)
    {
        var step = job.NextStep()!;
        step.Start(now);
        step.Complete(now, erpDocumentRef, requestPayload: "{}", responsePayload: "{}");
    }
}
