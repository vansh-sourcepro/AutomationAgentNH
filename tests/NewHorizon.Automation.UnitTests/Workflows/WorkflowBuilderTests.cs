using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Workflows;
using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// Covers the two properties that make workflows extensible: a workflow is identified by a unique
/// name, and its operations are a list that can be edited without touching the engine.
/// </summary>
public sealed class WorkflowBuilderTests
{
    private static Task<OperationResult> NoOp(OperationContext ctx, IErpClient erp, CancellationToken ct) =>
        Task.FromResult(OperationResult.Success());

    [Fact]
    public void A_workflow_is_identified_by_its_unique_type_name()
    {
        var workflow = WorkflowBuilder.For("SJO")
            .Stage("SJO", stage => stage.Execute("Allocation", NoOp))
            .Build();

        workflow.WorkflowType.Should().Be("SJO");
    }

    [Fact]
    public void Sequences_are_assigned_automatically_in_declaration_order()
    {
        // The point of the builder: inserting or deleting a step is one line, with no sequence
        // numbers to renumber by hand and get wrong.
        var workflow = WorkflowBuilder.For("SJO")
            .Stage("SJO", stage => stage
                .Execute("DeAllocation", NoOp)
                .Execute("Allocation", NoOp)
                .Execute("WorkOrderGeneration", NoOp))
            .Build();

        var plan = workflow.Plan().ToList();

        plan.Select(step => step.OperationName)
            .Should().Equal("DeAllocation", "Allocation", "WorkOrderGeneration");
    }

    [Fact]
    public void Adding_an_operation_changes_only_the_plan()
    {
        var before = WorkflowBuilder.For("OAF")
            .Stage("OAF", stage => stage
                .Execute("DeAllocation", NoOp)
                .Execute("Allocation", NoOp))
            .Build();

        var after = WorkflowBuilder.For("OAF")
            .Stage("OAF", stage => stage
                .Execute("DeAllocation", NoOp)
                .Execute("Allocation", NoOp)
                .Execute("OafLinkAttachment", NoOp)
                .Execute("PurchaseRequisition", NoOp))
            .Build();

        before.Plan().Should().HaveCount(2);
        after.Plan().Should().HaveCount(4);
        after.Find("OAF", "OafLinkAttachment").Should().NotBeNull();
    }

    [Fact]
    public void Stages_run_in_the_order_declared()
    {
        var workflow = WorkflowBuilder.For("FULL")
            .Stage("SJO", stage => stage.Execute("Allocation", NoOp))
            .Stage("OAF", stage => stage.Execute("OafLinkAttachment", NoOp))
            .Stage("CBOM", stage => stage.VerifyOnly("CbomGeneration", "SJO-to-CBOM"))
            .Build();

        workflow.Plan().Select(step => step.Stage).Should().Equal("SJO", "OAF", "CBOM");
    }

    [Fact]
    public void The_plan_carries_who_performs_each_operation()
    {
        var workflow = WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage
                .Execute("Allocation", NoOp)
                .VerifyOnly("OafGeneration", "SO-to-OAF")
                .VerifyThenExecute("PurchaseRequisition", "SO-to-PR", NoOp))
            .Build();

        workflow.Plan().Select(step => step.Kind).Should().Equal(
            OperationKind.Execute,
            OperationKind.VerifyOnly,
            OperationKind.VerifyThenExecute);
    }

    [Fact]
    public void A_verify_only_operation_never_gets_an_execute_body()
    {
        // The guarantee that the agent cannot duplicate what the ERP already did.
        var workflow = WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage.VerifyOnly("OafGeneration", "SO-to-OAF"))
            .Build();

        var operation = workflow.Find("SO", "OafGeneration")!;

        operation.CanExecute.Should().BeFalse();
        operation.VerifiesFirst.Should().BeTrue();
        operation.Execute.Should().BeNull();
    }

    [Fact]
    public void A_verify_then_execute_operation_can_do_both()
    {
        var workflow = WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage.VerifyThenExecute("OafGeneration", "SO-to-OAF", NoOp))
            .Build();

        var operation = workflow.Find("SO", "OafGeneration")!;

        operation.CanExecute.Should().BeTrue();
        operation.VerifiesFirst.Should().BeTrue();
    }

    [Fact]
    public void A_verifying_operation_must_name_the_erp_transition_it_confirms()
    {
        // Caught at registration, not halfway through a production job.
        var build = () => WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage.VerifyOnly("OafGeneration", erpTransitionKind: " "))
            .Build();

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Duplicate_operation_names_within_a_stage_are_rejected()
    {
        // Operation name is the resume key; duplicates make "first not-completed step" ambiguous.
        var build = () => WorkflowBuilder.For("SJO")
            .Stage("SJO", stage => stage
                .Execute("Allocation", NoOp)
                .Execute("Allocation", NoOp))
            .Build();

        build.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");
    }

    [Fact]
    public void Duplicate_stage_names_are_rejected()
    {
        var build = () => WorkflowBuilder.For("SJO")
            .Stage("SJO", stage => stage.Execute("Allocation", NoOp))
            .Stage("SJO", stage => stage.Execute("WorkOrderGeneration", NoOp))
            .Build();

        build.Should().Throw<InvalidOperationException>().WithMessage("*more than once*");
    }

    [Fact]
    public void An_empty_workflow_or_stage_is_rejected()
    {
        var emptyWorkflow = () => WorkflowBuilder.For("SJO").Build();
        var emptyStage = () => WorkflowBuilder.For("SJO").Stage("SJO", _ => { }).Build();

        emptyWorkflow.Should().Throw<InvalidOperationException>();
        emptyStage.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_same_operation_name_may_appear_in_different_stages()
    {
        // DeAllocation legitimately occurs in both SJO and OAF.
        var workflow = WorkflowBuilder.For("COMBINED")
            .Stage("SJO", stage => stage.Execute("DeAllocation", NoOp))
            .Stage("OAF", stage => stage.Execute("DeAllocation", NoOp))
            .Build();

        workflow.Plan().Should().HaveCount(2);
    }

    [Fact]
    public void A_verification_wait_budget_defaults_but_can_be_overridden()
    {
        var workflow = WorkflowBuilder.For("SO")
            .Stage("SO", stage => stage
                .VerifyOnly("Default", "SO-to-OAF")
                .VerifyOnly("Patient", "SJO-to-CBOM", waitBudget: TimeSpan.FromHours(2)))
            .Build();

        workflow.Find("SO", "Default")!.WaitBudget
            .Should().Be(OperationDefinition.DefaultVerificationWaitBudget);
        workflow.Find("SO", "Patient")!.WaitBudget.Should().Be(TimeSpan.FromHours(2));
    }
}
