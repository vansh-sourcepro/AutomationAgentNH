namespace NewHorizon.Automation.ErpClient.Flows.SoAllocationToDC;

// ─────────────────────────────────────────────────────────────────
//  Validation failure
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// A business-rule or mandatory-field check failed before the DC was created. Not retryable:
/// the same data produces the same failure.
/// </summary>
public sealed class DcValidationException : Exception
{
    public IReadOnlyList<DcValidationError> Errors { get; }

    public DcValidationException(string message, IReadOnlyList<DcValidationError> errors)
        : base(message)
    {
        Errors = errors ?? [];
    }

    public DcValidationException(string field, string message)
        : this(message, [new DcValidationError(field, message)])
    { }
}

/// <summary>One validation failure, identified by field name and human-readable message.</summary>
public sealed record DcValidationError(string Field, string Message);

// ─────────────────────────────────────────────────────────────────
//  What the ERP created
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// The successful outcome: DC number, DC auto-id, and the counts that confirm
/// every allocated item and quantity was transferred exactly.
/// </summary>
public sealed record DcCreationResult(
    string DcNumber,
    long DcId,
    int OafId,
    string? OafNo,
    string? IntOrdNo,
    int AllocatedItemCount,
    int DcItemCount,
    decimal TotalAllocatedQty,
    decimal TotalDcQty);

/// <summary>
/// Status summary of an OAF candidate for Delivery Challan conversion.
/// </summary>
public sealed record PendingOafSummary(
    string OafNo,
    string IntOrdNo,
    int OafId,
    string CustomerCode,
    string ShipmentCode,
    string CustomerOrderNumber,
    bool IsReadyForDc,
    string StatusMessage,
    IReadOnlyList<PendingOafItemSummary> Items);

/// <summary>
/// Item-level status on a pending OAF.
/// </summary>
public sealed record PendingOafItemSummary(
    string SalesItemCode,
    string ItemName,
    decimal OafQty,
    decimal AllocatedQty,
    decimal AlreadyDcQty,
    decimal PendingDcQty,
    int AllocationId,
    string WarehouseCode,
    bool HasPendingShipmentRecord);

/// <summary>
/// Request parameters for the main batch API creating Delivery Challans from all eligible allocations.
/// All fields are optional: if empty, the automation scans all allocations across all eligible customers.
/// </summary>
public sealed record BatchDcCreationRequest(
    string? CustomerCode = null,
    int? SiteId = null,
    string? SiteCode = null,
    string? OafYear = null,
    IReadOnlyList<int>? AllocationIds = null,
    IReadOnlyList<int>? OafIds = null,
    bool DryRun = false);

/// <summary>
/// Master summary response returned by the main automation API.
/// </summary>
public sealed record BatchDcCreationResult(
    bool Success,
    string Message,
    BatchDcCreationData Data);

public sealed record BatchDcCreationData(
    int TotalAllocations,
    int ProcessedAllocations,
    int SuccessfulAllocations,
    int PartialAllocations,
    int FailedAllocations,
    int TotalItems,
    int SuccessfulItems,
    int FailedItems,
    int CreatedDCCount,
    IReadOnlyList<AllocationDcItemResult> Results);

public sealed record AllocationDcItemResult(
    int AllocationId,
    int OafId,
    string? OafNo,
    string? IntOrdNo,
    string? CustomerCode,
    string? ShipmentCode,
    string Status, // "SUCCESS", "PARTIAL_SUCCESS", "FAILED"
    IReadOnlyList<DcItemResult> Items);

public sealed record DcItemResult(
    string ItemCode,
    string Status, // "SUCCESS", "FAILED"
    bool DcCreated,
    long? DcId,
    string? DcNumber,
    decimal? AllocatedQuantity,
    decimal? DcQuantity,
    string? Message,
    string? FailedField = null,
    string? FailedApi = null);

// ─────────────────────────────────────────────────────────────────
//  ERP response shapes  (internal – only the service reads them)
// ─────────────────────────────────────────────────────────────────

/// <summary>Transporter item from <c>getCodeTypeList/TR/GEN</c>.</summary>
internal sealed record ErpTransporter(
    string Code,
    string Description,
    string CodeFullName);

/// <summary>Decimal configuration from <c>GetDegitAfterDecimal</c>.</summary>
internal sealed record ErpDecimalConfig(
    int DigitsAfterDecimal);

/// <summary>Sales policy detail from <c>salespolicy/getDetail/{cmpCd}</c>.</summary>
internal sealed record ErpSalesPolicy(
    string CompanyCode,
    bool IsDcBasePack,
    bool AutoAllocItmOnOafCreate);

/// <summary>Customer row from <c>getCustomerForAllocationDeAllocation</c>.</summary>
internal sealed record ErpAllocationCustomer(
    string CustCode,
    string CustName,
    string? City,
    string? State,
    string? Country);

/// <summary>One row from <c>getShipmentForDC/{customerCode}/L</c>.</summary>
internal sealed record ErpShipment(
    string ShipmentCode,
    string ShipmentName,
    string? Add1,
    string? Add2,
    string? Add3,
    string? CityCode,
    string? CityDesc,
    string? StateCode,
    string? StateDesc,
    string? CountryCode,
    string? CountryDesc,
    string? PinCode,
    string? GstNo);

/// <summary>
/// One row from <c>getOAFItemDetailsForDC</c>. Every field is preserved verbatim
/// because the create payload echoes most of them back.
/// </summary>
internal sealed record ErpOafItemDetail(
    string SalesItemCode,
    string SalesItemName,
    string IntItemCode,
    string IntItemName,
    string WarehouseCode,
    decimal OafQty,
    decimal AlreadyDcQty,
    decimal DcQty,
    int OafId,
    int WareHouseId,
    string Suom,
    int Precision,
    bool ManufacturingBatchReq,
    int OafDetailLineId,
    decimal MaxDcQtyIuom,
    int AllocationId,
    int SoItemLineNo,
    int OafDetailLineNo,
    bool SizeReq,
    bool InwardReq,
    bool HeatReq,
    bool SrNoReq,
    string? DimFlagVal,
    decimal DensityValue,
    string? DimensionFormula,
    string? DimensionSize,
    int NoOfPiece,
    decimal TotalWeight,
    string? ServiceSalesItem,
    string? XcdRemark,
    string? TagNo);

/// <summary>Conversion factor from <c>getConversionFactorpost</c>.</summary>
internal sealed record ErpConversionFactor(decimal ConFactor);

/// <summary>One allocation row from <c>GetItemDetailsForAllocation</c>.</summary>
internal sealed record ErpAllocationRow(
    int OafId,
    string ItemCode,
    string SalesItemCode,
    decimal AllocatedQty,
    int OafDetailLineId,
    int OafDetailLineNo,
    int SoItemLineNo,
    int AllocationId,
    int WareHouseId,
    string WarehouseCode);

/// <summary>
/// OAF detail row from <c>getOAFDetailListForDC</c>, used to confirm OAF/customer/shipment
/// context and to extract the OAF's SO number and group needed for the DC header.
/// </summary>
internal sealed record ErpOafDetail(
    string OafYear,
    string OafGroup,
    string LocationCode,
    string OafNumber,
    int OafAutoId,
    string IntOrderYear,
    string IntOrderGroup,
    string IntOrderLocation,
    string IntOrderNumber,
    string CustomerOrderNumber);

/// <summary>Customer address from <c>getCustomerAddress/{customerCode}</c>.</summary>
internal sealed record ErpCustomerAddress(
    string CustomerCode,
    string CustomerName,
    string? Add1,
    string? Add2,
    string? Add3,
    string? CityPin,
    string? StateCoun,
    string? State,
    string? Country,
    string? FullAddress);

/// <summary>Document-control default for the DC document type.</summary>
internal sealed record ErpDocumentControlDefault(
    string FinancialYear,
    string GroupCode,
    string LocationCode,
    string LocationName,
    int LocationId,
    string SiteRequired,
    string AutoNumberRequired,
    string AuthorisationRequired);

/// <summary>One row from <c>getShipmentInfo/{oafId}/{shipCode}</c>.</summary>
internal sealed record ErpShipmentInfo(
    string SalesItemCode,
    string ShipCode,
    string RateStructureCode,
    string BillToCode,
    long RecordId,
    int OafDetailRefLineId,
    int OafQtyOfLineId,
    int OafRefId);

/// <summary>One row from <c>getItemCombinations4DCForItm</c>.</summary>
internal sealed record ErpItemCombination(
    int RowId,
    long LineNo,
    decimal Quantity,
    decimal OnHandQty,
    decimal Alloc,
    long StockId,
    string ItemCode,
    int DocId,
    int WarehouseId);

