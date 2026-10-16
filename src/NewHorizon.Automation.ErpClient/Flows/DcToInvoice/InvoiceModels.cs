using System;
using System.Collections.Generic;
using System.Linq;

namespace NewHorizon.Automation.ErpClient.Flows.DcToInvoice;

public sealed record InvoiceValidationError(string Field, string Message);

/// <summary>
/// Domain exception when invoice creation fails business validation or ERP pre-requisite checks.
/// </summary>
public sealed class InvoiceValidationException : Exception
{
    public InvoiceValidationException(string field, string message)
        : base(message)
    {
        Field = field;
        Errors = new[] { new InvoiceValidationError(field, message) };
    }

    public InvoiceValidationException(string field, string message, IReadOnlyList<InvoiceValidationError> errors)
        : base(message)
    {
        Field = field;
        Errors = errors;
    }

    public InvoiceValidationException(string field, string message, Exception innerException)
        : base(message, innerException)
    {
        Field = field;
        Errors = new[] { new InvoiceValidationError(field, message) };
    }

    public string Field { get; }
    public IReadOnlyList<InvoiceValidationError> Errors { get; }
}

/// <summary>
/// Result of an individual DC-to-Invoice conversion.
/// </summary>
public sealed record InvoiceCreationResult(
    string InvoiceNumber,
    long InvoiceId,
    long DcId,
    string? DcNo,
    string? CustomerCode,
    int ItemCount,
    decimal TotalInvoiceQty,
    decimal TotalInvoiceAmount,
    string Status = "Created",
    bool Authorized = false,
    string? RateStructureCode = null,
    bool LineLevelRateStructureApplied = false);

/// <summary>
/// Summary of a Delivery Challan pending invoice creation.
/// </summary>
public sealed record PendingDcSummary(
    long DcId,
    string DcNo,
    DateTime DcDate,
    string DcYear,
    string DcGroup,
    string DcSite,
    string CustomerCode,
    string CustomerName,
    string ShipmentCode,
    string CurrencyCode,
    string? RateStructureCode,
    int CreditDays,
    decimal CreditLimit,
    bool IsReadyForInvoice,
    string StatusMessage,
    IReadOnlyList<PendingDcItemSummary> Items);

/// <summary>
/// Item-level summary for a pending Delivery Challan.
/// </summary>
public sealed record PendingDcItemSummary(
    string SalesItemCode,
    string SalesItemName,
    string IntItmCode,
    decimal DcQty,
    decimal InvoiceQty,
    decimal OrderRate,
    string Suom,
    string Iuom,
    decimal ConvFact,
    int OafId,
    int OafDetailLineNo,
    string? HsnCode,
    string? RateStructureCode);

/// <summary>
/// Request parameters for the master batch API creating Invoices from all eligible Delivery Challans.
/// </summary>
public sealed record BatchInvoiceCreationRequest(
    string? CustomerCode = null,
    int? SiteId = null,
    string? SiteCode = null,
    string? DcYear = null,
    IReadOnlyList<long>? DcIds = null,
    IReadOnlyList<string>? DcNumbers = null,
    bool DryRun = false);

/// <summary>
/// Master summary response returned by the main automation API.
/// </summary>
public sealed record BatchInvoiceCreationResult(
    bool Success,
    string Message,
    BatchInvoiceCreationData Data);

public sealed record BatchInvoiceCreationData(
    int TotalDeliveryChallans,
    int ProcessedDeliveryChallans,
    int SuccessfulDeliveryChallans,
    int FailedDeliveryChallans,
    int TotalItems,
    int SuccessfulItems,
    int FailedItems,
    int CreatedInvoiceCount,
    IReadOnlyList<DcInvoiceResult> Results);

public sealed record DcInvoiceResult(
    long DcId,
    string? DcNo,
    string? CustomerCode,
    string? ShipmentCode,
    string Status, // "SUCCESS", "FAILED", "WOULD_CREATE"
    string? InvoiceNumber,
    long? InvoiceId,
    IReadOnlyList<InvoiceItemResult> Items,
    decimal TotalAmount,
    decimal TotalQuantity,
    string? Message = null,
    string? FailedField = null,
    string? FailedApi = null,
    bool Authorized = false,
    string? RateStructureCode = null,
    bool LineLevelRateStructureApplied = false)
{
    public bool Success => Status == "SUCCESS";
    public int ItemCount => Items.Count;
}

public sealed record InvoiceItemResult(
    string ItemCode,
    string ItemName,
    decimal DcQuantity,
    decimal InvoiceQuantity,
    decimal Rate,
    decimal Amount,
    string RateStructureCode,
    string Status, // "SUCCESS", "FAILED"
    bool InvoiceCreated,
    long? InvoiceId,
    string? InvoiceNumber,
    string? Message = null,
    string? FailedField = null,
    string? FailedApi = null);

// ─────────────────────────────────────────────────────────────────
//  ERP response shapes (internal – only the service reads them)
// ─────────────────────────────────────────────────────────────────

internal sealed record ErpSalesPolicyDetail(
    string CompanyCode,
    bool EnableLineLevelRateStructure); // MSCENABLELINELEVELRTSTR

internal sealed record ErpInvoiceDocControlDefault(
    string FinancialYear,
    string GroupCode,
    string LocationCode,
    string LocationName,
    int LocationId,
    string SiteRequired,
    string AutoNumberRequired,
    string AuthorisationRequired);

internal sealed record ErpSalInvDcRow(
    long DcId,
    string DcNo,
    DateTime DcDate,
    string DcYear,
    string DcGroup,
    string DcSite,
    string OafNo,
    string OafYear,
    string OafGroup,
    string OafSite,
    int OafId,
    long OrderId,
    string IntOdrNo,
    string IntOdrYear,
    string IntOdrGroup,
    string IntOdrSite,
    string CustPoNo,
    DateTime? CustPoDate,
    string ShipCode,
    string BillTo,
    string BillToName,
    string CurrencyCode,
    decimal ExchangeRate,
    string? DcRateStructureCode,
    string? DeliveryName,
    string? DeliveryCityCode,
    string? DeliveryCityName,
    int CreditDays,
    decimal CreditLimit,
    string CrLimitInvFlg,
    string CreditLimitFlag,
    int CreditLimitDays);

internal sealed record ErpDcShipmentLogistics(
    string? LrNo,
    string? VehicleNo,
    decimal GrossWeight,
    decimal NetWeight,
    string? TransporterCode,
    string? RoadPermitNo);

internal sealed record ErpInvoiceShipment(
    string ShipmentCode,
    string ShipmentName,
    string? ShipmentAddress1,
    string? ShipmentCityCode,
    string? ShipmentCityName,
    string? ShipmentStateCode,
    string? ShipmentStateName,
    string? ShipmentPinCode,
    string? ShipmentGstNo);

internal sealed record ErpRateStructureOption(
    string RateStructureCode,
    string RateStructureDescription,
    string RateStructureFullName,
    bool IsGSTApplicable);

internal sealed record ErpInvoiceItemDetail(
    int LineNo,
    string SalesItemCode,
    string SalesItemName,
    string IntItmCode,
    string ProductSpare,
    string? OafRef,
    string? IonRef,
    string? ItemDcNo,
    string Suom,
    string Iuom,
    decimal ConvFact,
    decimal DcQty,
    decimal InvoiceQty,
    decimal OrdRateDis,
    decimal OrderRate,
    decimal AssessableRate,
    decimal Amount,
    decimal AmountWithTax,
    int OafId,
    int OafDetailLineNo,
    int DcoafLineNo,
    string DcRndNo,
    long DcId,
    long OrdId,
    long SjoId,
    int SoItmLineNo,
    string? HsnCode,
    string? SoHsnCode,
    string? RateStructureCode,
    decimal NetWeight,
    decimal GrossWeight,
    string? CaseSize,
    string? ItemSize,
    decimal ItemWeight,
    string? DiscountType,
    decimal DiscountVal,
    decimal DiscountAmt);

internal sealed record ErpInvoiceRateStructureLine(
    string RateCode,
    string RateDesc,
    decimal TaxValue,
    string AppOnDisplay,
    string Ie,
    string Pv,
    string MprTaxTyp,
    decimal RateAmount,
    int PostNonPost,
    int UniqueNo,
    string ItemCode,
    int SeqNo,
    string? RateStructureCode = null);

internal sealed record ErpDispatchFromAddress(
    string? Address1,
    string? Address2,
    string? Address3,
    string? City,
    string? CityName,
    string? StateName,
    string? CountryName,
    string? PinCode,
    string? GstNo);
