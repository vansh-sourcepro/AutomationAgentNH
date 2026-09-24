namespace NewHorizon.Automation.Application.Workflows.Definitions;

/// <summary>
/// Costed bill of materials.
/// </summary>
/// <remarks>
/// SCAFFOLD — design doc §18.4 is still open: the exact operation list for this stage has not been
/// confirmed against the ERP's API inventory. What is known is that the SJO → CBOM transition is
/// automated inside the ERP behind a flag, so the agent confirms it rather than performing it.
///
/// The single verifying operation below is therefore correct as far as it goes, and deliberately
/// minimal: no operation is invented here, because a wrong guess would create real ERP documents.
/// Add the confirmed operations to this stage — one line each — once the ERP team supplies them.
/// </remarks>
public static class CbomWorkflow
{
    public static WorkflowDefinition Create() =>
        WorkflowBuilder.For(WorkflowNames.Cbom)
            .Stage(WorkflowNames.Cbom, stage => stage
                // The ERP owns this transition entirely, so VerifyOnly: the agent must never
                // create a CBOM itself, only record the one the ERP produced.
                .VerifyOnly("CbomGeneration", ErpTransitions.SjoToCbom))

            // TODO(§18.4): append the confirmed CBOM operations here, e.g.
            //   .Execute("CostRollUp", ...)
            //   .Execute("StandardCostUpdate", ...)
            .Build();
}
