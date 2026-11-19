using System.Globalization;

namespace NewHorizon.Automation.ErpClient;

/// <summary>
/// The ERP's financial-year string, <c>"yy-yy"</c>, for a date. April to March, the year the ERP's
/// own documents are stamped with — a document dated 2026-08-06 is "26-27".
/// </summary>
internal static class FinancialYears
{
    public static string For(DateOnly date)
    {
        var startYear = date.Month >= 4 ? date.Year : date.Year - 1;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{startYear % 100:00}-{(startYear + 1) % 100:00}");
    }
}
