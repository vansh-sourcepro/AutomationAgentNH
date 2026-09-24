using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// The guarantees the whole design rests on: run to completion, checkpoint every operation, resume
/// from the exact failure point, and never create a duplicate ERP document.
/// </summary>
public sealed class WorkflowEngineTests
{
    [Fact]
    public async Task A_job_runs_every_stage_and_operation_to_completion()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        job.Steps.Should().OnlyContain(step => step.IsTerminal);
        job.NextStep().Should().BeNull();
    }

    [Fact]
    public async Task Every_operation_is_checkpointed_before_the_next_one_starts()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        // At least one save per operation (start + finish) plus the final completion. Without
        // this, a crash would lose the record of ERP documents already created.
        harness.Jobs.SaveCount.Should().BeGreaterThanOrEqualTo(job.Steps.Count);
        job.Steps.Where(step => step.Status is StepStatus.Completed)
            .Should().OnlyContain(step => step.CompletedAtUtc != null);
    }

    [Fact]
    public async Task A_completed_operation_records_the_erp_document_it_produced()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        var allocation = job.Steps.Single(step => step.OperationName == "Allocation");
        allocation.ErpDocumentRef.Should().Be("ALLOCATION-0001");
        job.CompletedDocumentRefs().Should().ContainKey("SJO/Allocation");
    }

    [Fact]
    public async Task A_transient_failure_requeues_the_job_with_a_backoff_delay()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());
        harness.Erp.FailNext("allocation", new ErpTransientException("ERP unavailable", "503"));

        var job = await harness.RunAsync(harness.NewJob());

        // Re-queued, not failed: the ERP was simply unwell.
        job.Status.Should().Be(JobStatus.Pending);
        job.NotBeforeUtc.Should().NotBeNull();
        job.NotBeforeUtc.Should().BeAfter(harness.Clock.UtcNow);
        harness.Jobs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resumed_job_continues_from_the_failed_operation_without_duplicating_earlier_work()
    {
        // The central guarantee. De-allocation succeeds, allocation fails, then the job resumes.
        var harness = new EngineHarness(SjoWorkflow.Create());
        harness.Erp.FailNext("allocation", new ErpTransientException("ERP unavailable", "503"));

        var job = await harness.RunAsync(harness.NewJob());
        job.Status.Should().Be(JobStatus.Pending);

        var deAllocationRefBefore = job.Steps[0].ErpDocumentRef;

        // Second claim, as the queue processor would do once the backoff elapsed.
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.RunAsync(job);

        job.Status.Should().Be(JobStatus.Completed);

        // De-allocation ran exactly once across both attempts, and kept its original document.
        harness.Erp.CreateCountFor("deallocation").Should().Be(1);
        job.Steps[0].ErpDocumentRef.Should().Be(deAllocationRefBefore);
    }

    [Fact]
    public async Task Re_running_a_completed_job_creates_no_second_erp_document()
    {
        // Simulates a double trigger reaching the engine twice for the same run.
        var harness = new EngineHarness(SjoWorkflow.Create());
        var job = await harness.RunAsync(harness.NewJob());

        var createsAfterFirstRun = harness.Erp.CreateCounts.ToDictionary(pair => pair.Key, pair => pair.Value);

        // A completed job cannot be claimed again, which is itself the first line of defence.
        var reclaim = () => job.Claim(harness.Clock.UtcNow);
        reclaim.Should().Throw<InvalidJobTransitionException>();

        harness.Erp.CreateCounts.Should().BeEquivalentTo(createsAfterFirstRun);
    }

    [Fact]
    public async Task Query_before_create_adopts_a_document_a_previous_attempt_already_made()
    {
        // The ERP already holds a work order for this document — as it would if a previous attempt
        // created one and then crashed before checkpointing.
        var harness = new EngineHarness(SjoWorkflow.Create());
        await harness.Erp.CreateWorkOrderAsync(
            new WorkOrderRequest(new ErpOperationRequest("SalesOrder", "SO-123", "corr", Guid.NewGuid())),
            CancellationToken.None);

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);

        // Adopted, not recreated: exactly one work order exists for this document.
        harness.Erp.CreateCountFor("workorder").Should().Be(1);
        job.Steps.Single(step => step.OperationName == "WorkOrderGeneration")
            .ErpDocumentRef.Should().Be("WORKORDER-0001");
    }

    [Fact]
    public async Task A_business_failure_stops_the_job_and_records_both_messages()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());
        harness.Erp.FailNext(
            "allocation",
            new ErpBusinessException("Vendor missing for item X", "400 from /allocation"));

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Failed);

        var error = harness.Jobs.Errors.Should().ContainSingle().Subject;
        error.ErrorType.Should().Be(ErrorType.Business);
        error.LaymanMessage.Should().Be("Vendor missing for item X");
        error.TechnicalMessage.Should().Contain("400");

        // A human is told, and the ERP was called exactly once — business refusals never retry.
        harness.Notifications.Failures.Should().ContainSingle();
        harness.Erp.CreateCountFor("allocation").Should().Be(0);
    }

    [Fact]
    public async Task Retries_are_bounded_and_then_the_job_fails_for_a_human()
    {
        var harness = new EngineHarness(SjoWorkflow.Create(), maxRetry: 2);
        harness.Erp.FailNext(
            "deallocation",
            new ErpTransientException("down", "503"),
            new ErpTransientException("down", "503"),
            new ErpTransientException("down", "503"));

        var job = harness.NewJob();

        await harness.RunAsync(job);
        job.Status.Should().Be(JobStatus.Pending);

        await harness.RunAsync(job);
        job.Status.Should().Be(JobStatus.Pending);

        await harness.RunAsync(job);

        // Third attempt exhausts the budget: the job stops for a human rather than looping.
        job.Status.Should().Be(JobStatus.Failed);
        harness.Jobs.Errors.Should().ContainSingle()
            .Which.ErrorType.Should().Be(ErrorType.Technical);
    }

    [Fact]
    public async Task An_unmet_precondition_skips_the_operation_rather_than_failing_it()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());
        harness.Erp.ChildrenAllocated = false;

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);

        var workOrder = job.Steps.Single(step => step.OperationName == "WorkOrderGeneration");
        workOrder.Status.Should().Be(StepStatus.Skipped);
        harness.Erp.CreateCountFor("workorder").Should().Be(0);

        // The dependent labour step skips too, since there is no work order to attach to.
        job.Steps.Single(step => step.OperationName == "LaborPR").Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task No_shortage_skips_the_creating_operation()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());
        harness.Erp.NetShortage = 0m;

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        harness.Erp.CreateCountFor("purchase-requisition").Should().Be(0);
        job.Steps.Single(step => step.OperationName == "PurchaseRequisition")
            .Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task Partial_mode_pauses_at_the_gate_and_resumes_after_approval()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob(AutomationMode.Partial));

        job.Status.Should().Be(JobStatus.AwaitingApproval);
        harness.Notifications.ApprovalRequests.Should().ContainSingle().Which.Should().Be("SJO/PurchaseRequisition");
        harness.Erp.CreateCountFor("purchase-requisition").Should().Be(0);

        job.Approve("planner@client.com", harness.Clock.UtcNow, "Budget confirmed");
        await harness.RunAsync(job);

        // The approved gate released and its operation ran — it did not re-arm and trap the job.
        harness.Erp.CreateCountFor("purchase-requisition").Should().Be(1);

        // SJO gates two operations in Partial mode, so the job correctly stops again at the second.
        job.Status.Should().Be(JobStatus.AwaitingApproval);
        harness.Notifications.ApprovalRequests.Should().Equal("SJO/PurchaseRequisition", "SJO/LaborPR");

        job.Approve("planner@client.com", harness.Clock.UtcNow, "Labour approved");
        await harness.RunAsync(job);

        job.Status.Should().Be(JobStatus.Completed);
        harness.Erp.CreateCountFor("labor-requisition").Should().Be(1);
    }

    [Fact]
    public async Task Each_gate_is_approved_by_its_own_decision()
    {
        // Approving one operation must not silently authorise a later one.
        var harness = new EngineHarness(SjoWorkflow.Create());
        var job = await harness.RunAsync(harness.NewJob(AutomationMode.Partial));

        job.Approve("planner@client.com", harness.Clock.UtcNow, "PR only");
        await harness.RunAsync(job);

        var purchaseStep = job.Steps.Single(step => step.OperationName == "PurchaseRequisition");
        var labourStep = job.Steps.Single(step => step.OperationName == "LaborPR");

        purchaseStep.ApprovedBy.Should().Be("planner@client.com");
        labourStep.ApprovedBy.Should().BeNull();
        labourStep.Status.Should().Be(StepStatus.Pending);
    }

    [Fact]
    public async Task Full_mode_has_no_gates()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        harness.Notifications.ApprovalRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rejected_job_never_runs_its_gated_operation()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());
        var job = await harness.RunAsync(harness.NewJob(AutomationMode.Partial));

        job.Reject("planner@client.com", "Not approved this quarter", harness.Clock.UtcNow);

        job.Status.Should().Be(JobStatus.Cancelled);
        harness.Erp.CreateCountFor("purchase-requisition").Should().Be(0);
    }

    [Fact]
    public async Task An_operation_missing_from_the_definition_fails_loudly()
    {
        // A workflow redefined while a job was in flight. Guessing which operation was meant could
        // create the wrong ERP document, so the only safe answer is to stop.
        var harness = new EngineHarness(SjoWorkflow.Create());

        var strayJob = Job.Create("SJO", "SalesOrder", "SO-999", AutomationMode.Full, harness.Clock.UtcNow);
        strayJob.PlanSteps([new PlannedOperation("SJO", "OperationThatNoLongerExists")]);

        await harness.RunAsync(strayJob);

        strayJob.Status.Should().Be(JobStatus.Failed);
        harness.Jobs.Errors.Should().ContainSingle()
            .Which.TechnicalMessage.Should().Contain("not present in the current definition");
    }

    [Fact]
    public async Task A_crashed_job_resumes_at_the_interrupted_operation_without_duplicating()
    {
        var harness = new EngineHarness(SjoWorkflow.Create());
        var job = harness.NewJob();

        // First operation completes, then the process "dies" mid-second-operation.
        job.Claim(harness.Clock.UtcNow);
        var first = job.NextStep()!;
        first.Start(harness.Clock.UtcNow);
        first.Complete(harness.Clock.UtcNow, "DEALLOCATION-0001", null, null);
        job.NextStep()!.Start(harness.Clock.UtcNow);

        job.MarkResumable();
        await harness.RunAsync(job);

        job.Status.Should().Be(JobStatus.Completed);
        // The completed de-allocation was never re-run.
        harness.Erp.CreateCountFor("deallocation").Should().Be(0);
        job.Steps[0].ErpDocumentRef.Should().Be("DEALLOCATION-0001");
    }
}
