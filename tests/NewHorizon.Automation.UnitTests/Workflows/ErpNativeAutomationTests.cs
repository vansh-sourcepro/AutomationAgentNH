using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Workflows;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// Covers the transitions the ERP already automates behind its own flag. The agent must confirm
/// these rather than perform them, or it would duplicate what the ERP just did.
/// </summary>
public sealed class ErpNativeAutomationTests
{
    private static Task<OperationResult> AttachLink(
        OperationContext context,
        IErpClient erp,
        CancellationToken ct) =>
        ErpOperations.CreateIfAbsentAsync(
            context,
            erp,
            "oaf-link",
            (request, token) => erp.AttachOafLinkAsync(new OafLinkRequest(request), token),
            ct);

    private static WorkflowDefinition VerifyOnlyWorkflow() =>
        WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage.VerifyOnly("OafGeneration", ErpTransitions.SalesOrderToOaf))
            .Build();

    private static WorkflowDefinition VerifyThenExecuteWorkflow() =>
        WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage.VerifyThenExecute(
                "OafGeneration",
                ErpTransitions.SalesOrderToOaf,
                AttachLink))
            .Build();

    [Fact]
    public async Task A_verified_step_records_the_erp_document_without_creating_anything()
    {
        var harness = new EngineHarness(VerifyOnlyWorkflow());
        harness.Erp.ErpAutomationCompleted = true;

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        job.Steps[0].ErpDocumentRef.Should().Be("ERP-SO-to-OAF");

        // The whole point: the agent called nothing that creates.
        harness.Erp.CreateCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_step_still_running_in_the_erp_waits_rather_than_taking_over()
    {
        var harness = new EngineHarness(VerifyOnlyWorkflow());
        harness.Erp.ErpAutomationEnabled = true;
        harness.Erp.ErpAutomationCompleted = false;

        var job = await harness.RunAsync(harness.NewJob());

        // Re-queued to check again — acting now would race the ERP and produce two documents.
        job.Status.Should().Be(JobStatus.Pending);
        job.NotBeforeUtc.Should().Be(harness.Clock.UtcNow + RetryPolicy.ErpAutomationPollInterval);
        harness.Erp.CreateCounts.Should().BeEmpty();
        harness.Jobs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Waiting_on_the_erp_is_not_charged_against_the_retry_budget()
    {
        // A slow ERP transition must not exhaust retries meant for genuine failures.
        var harness = new EngineHarness(VerifyOnlyWorkflow(), maxRetry: 1);
        harness.Erp.ErpAutomationCompleted = false;

        var job = harness.NewJob();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await harness.RunAsync(job);
            job.Status.Should().Be(JobStatus.Pending);
        }

        harness.Jobs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Waiting_stops_and_asks_a_human_once_the_budget_is_exceeded()
    {
        var harness = new EngineHarness(VerifyOnlyWorkflow());
        harness.Erp.ErpAutomationCompleted = false;

        var job = harness.NewJob();
        await harness.RunAsync(job);

        // Past the default 15-minute budget: a job must not wait forever on a stuck ERP.
        harness.Clock.Advance(TimeSpan.FromMinutes(20));
        await harness.RunAsync(job);

        job.Status.Should().Be(JobStatus.Failed);
        harness.Jobs.Errors.Should().ContainSingle()
            .Which.LaymanMessage.Should().Contain("has not completed");
    }

    [Fact]
    public async Task Verify_only_with_the_erp_flag_off_asks_a_human_instead_of_acting()
    {
        var harness = new EngineHarness(VerifyOnlyWorkflow());
        harness.Erp.ErpAutomationEnabled = false;
        harness.Erp.ErpAutomationCompleted = false;

        var job = await harness.RunAsync(harness.NewJob());

        // The agent is not permitted to do this work, so the honest outcome is to report the
        // misconfiguration — never to create the document itself.
        job.Status.Should().Be(JobStatus.Failed);
        harness.Erp.CreateCounts.Should().BeEmpty();
        harness.Jobs.Errors.Should().ContainSingle()
            .Which.LaymanMessage.Should().Contain("switched off in the ERP");
    }

    [Fact]
    public async Task Verify_then_execute_adopts_the_erp_document_when_the_flag_is_on()
    {
        var harness = new EngineHarness(VerifyThenExecuteWorkflow());
        harness.Erp.ErpAutomationCompleted = true;

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        job.Steps[0].ErpDocumentRef.Should().Be("ERP-SO-to-OAF");
        harness.Erp.CreateCountFor("oaf-link").Should().Be(0);
    }

    [Fact]
    public async Task Verify_then_execute_does_the_work_itself_when_the_flag_is_off()
    {
        var harness = new EngineHarness(VerifyThenExecuteWorkflow());
        harness.Erp.ErpAutomationEnabled = false;
        harness.Erp.ErpAutomationCompleted = false;

        var job = await harness.RunAsync(harness.NewJob());

        // The agent takes over without needing to read the ERP flag itself, and without a redeploy.
        job.Status.Should().Be(JobStatus.Completed);
        harness.Erp.CreateCountFor("oaf-link").Should().Be(1);
        job.Steps[0].ErpDocumentRef.Should().Be("OAF-LINK-0001");
    }

    [Fact]
    public async Task The_step_records_that_the_erp_performed_the_work()
    {
        var harness = new EngineHarness(VerifyOnlyWorkflow());

        var job = await harness.RunAsync(harness.NewJob());

        // Stored on the row so the timeline stays truthful even if the workflow is redefined later.
        job.Steps[0].Kind.Should().Be(OperationKind.VerifyOnly);
        job.Steps[0].ResponsePayload.Should().Contain("\"performedBy\":\"ERP\"");
    }

    [Fact]
    public async Task The_autoshop_workflow_chains_the_stages_in_order()
    {
        var harness = new EngineHarness(AutoShopWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        job.Steps.Select(step => step.Stage).Distinct()
            .Should().Equal("OAF", "SJO", "CBOM", "AutoShop");

        // CBOM is the ERP's own, so nothing was created for it.
        job.Steps.Single(step => step.Stage == "CBOM").ErpDocumentRef.Should().Be("ERP-SJO-to-CBOM");
    }

    [Fact]
    public async Task The_unspecified_autoshop_operation_skips_rather_than_inventing_a_call()
    {
        var harness = new EngineHarness(AutoShopWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        var release = job.Steps.Single(step => step.OperationName == "AutoShopRelease");
        release.Status.Should().Be(StepStatus.Skipped);
        release.Remarks.Should().Contain("not yet configured");
    }
}
