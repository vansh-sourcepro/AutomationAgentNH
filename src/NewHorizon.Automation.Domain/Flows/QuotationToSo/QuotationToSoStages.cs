namespace NewHorizon.Automation.Domain.Flows.QuotationToSo;

/// <summary>
/// The stages a Quotation → Sales Order conversion moves through.
/// </summary>
public static class QuotationToSoStages
{
    public const string Discovery = "Discovery";
    public const string QuotationDetail = "QuotationDetail";
    public const string Validation = "Validation";
    public const string CreateSalesOrder = "CreateSalesOrder";

    public static IReadOnlyList<string> InOrder { get; } =
    [
        Discovery,
        QuotationDetail,
        Validation,
        CreateSalesOrder,
    ];
}

/// <summary>
/// The task names recorded on a step for Quotation → Sales Order conversion.
/// </summary>
public static class QuotationToSoTasks
{
    public const string ListOpenQuotations = "ListOpenQuotations";
    public const string GetQuotationDetail = "GetQuotationDetail";
    public const string VerifyExistingSalesOrder = "VerifyExistingSalesOrder";
    public const string CreateSalesOrderEntry = "CreateSalesOrderEntry";

    public static string For(string stage) => stage switch
    {
        QuotationToSoStages.Discovery => ListOpenQuotations,
        QuotationToSoStages.QuotationDetail => GetQuotationDetail,
        QuotationToSoStages.Validation => VerifyExistingSalesOrder,
        QuotationToSoStages.CreateSalesOrder => CreateSalesOrderEntry,
        _ => stage,
    };
}

