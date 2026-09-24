using NewHorizon.Automation.Domain.Workflows;

namespace NewHorizon.Automation.Application.Erp;

/// <summary>
/// Bridges the workflow-facing context to the ERP request envelope, so an operation body never
/// assembles one by hand and cannot forget the correlation id.
/// </summary>
public static class OperationContextExtensions
{
    public static ErpOperationRequest ToErpRequest(this OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ErpOperationRequest(
            context.DocumentType,
            context.DocumentId,
            context.CorrelationId,
            context.JobId);
    }
}
