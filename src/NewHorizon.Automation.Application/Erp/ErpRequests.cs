namespace NewHorizon.Automation.Application.Erp;

/// <summary>Common envelope for every ERP execution call: who, which document, which run.</summary>
public sealed record ErpOperationRequest(
    string DocumentType,
    string DocumentId,
    string CorrelationId,
    Guid JobId);

/// <summary>Releases stock reserved against the document, manufacturing items before bought-out.</summary>
public sealed record DeAllocationRequest(ErpOperationRequest Context);

/// <summary>Allocates from free on-hand per the MRP policy, cascading to children for AS/MK parents.</summary>
public sealed record AllocationRequest(ErpOperationRequest Context);

/// <summary>Creates a work order for eligible AS/MK items on the net requirement.</summary>
public sealed record WorkOrderRequest(ErpOperationRequest Context);

/// <summary>Creates a purchase requisition for the net shortage, referencing the source document.</summary>
public sealed record PurchaseRequisitionRequest(ErpOperationRequest Context, string? SourceDocumentRef);

/// <summary>Creates a labour requisition against an outside operation of an existing work order.</summary>
public sealed record LaborRequisitionRequest(
    ErpOperationRequest Context,
    string? WorkOrderRef,
    string? SourceDocumentRef);

/// <summary>Attaches the OAF link to the trading document.</summary>
public sealed record OafLinkRequest(ErpOperationRequest Context);

/// <summary>Computes MIL shortage (MIL − free on-hand) net of pipeline documents, plant-wise.</summary>
public sealed record MilShortageRequest(ErpOperationRequest Context);
