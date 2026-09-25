namespace NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

/// <summary>
/// Which SJO a caller meant: the whole document number <c>26-27/SJ/NF1/000123</c>, or the bare
/// running number <c>000123</c> (or <c>123</c>).
/// </summary>
/// <remarks>
/// Exact, case- and whitespace-insensitive, never a substring — the ERP's list search is a
/// <c>LIKE '%x%'</c>, so its answer is only a candidate list. A bare number that fits more than one
/// SJO (the same running number in another year, group or site) is ambiguous and refused: issuing
/// material against an SJO nobody named is exactly the mistake to avoid. Same rule as the Indent → PO
/// flow's indent numbers.
/// </remarks>
internal sealed class DocumentNumberSelection
{
    private const int RunningNumberWidth = 6;

    private DocumentNumberSelection(string input, string? year, string? group, string? siteCode, string number)
    {
        Input = input;
        Year = year;
        Group = group;
        SiteCode = siteCode;
        Number = number;
    }

    public string Input { get; }

    public string? Year { get; }

    public string? Group { get; }

    public string? SiteCode { get; }

    /// <summary>The running number, zero-padded to the ERP's six digits.</summary>
    public string Number { get; }

    public bool IsFullNumber => Year is not null;

    /// <summary>Parses the caller's text, or explains why it cannot be an SJO number.</summary>
    public static bool TryParse(string? input, out DocumentNumberSelection? selection, out string? error)
    {
        selection = null;
        error = null;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "documentNumber is required, e.g. \"26-27/SJ/NF1/000123\".";
            return false;
        }

        var parts = text.Split('/', StringSplitOptions.TrimEntries);

        if (parts.Length == 1)
        {
            if (!IsRunningNumber(parts[0]))
            {
                error = $"'{text}' is not a document number: expected \"year/group/site/number\" or the bare running number.";
                return false;
            }

            selection = new DocumentNumberSelection(text, null, null, null, Pad(parts[0]));
            return true;
        }

        if (parts.Length != 4 || parts.Take(3).Any(part => part.Length == 0) || !IsRunningNumber(parts[3]))
        {
            error = $"'{text}' is not a document number: expected \"year/group/site/number\", e.g. \"26-27/SJ/NF1/000123\".";
            return false;
        }

        selection = new DocumentNumberSelection(text, parts[0], parts[1], parts[2], Pad(parts[3]));
        return true;
    }

    /// <summary>The SJOs this selection names, out of the ERP's candidate list.</summary>
    public IReadOnlyList<SjoHeader> Match(IEnumerable<SjoHeader> candidates) =>
        candidates
            .Where(sjo => Same(Pad(sjo.Number), Number)
                && (!IsFullNumber
                    || (Same(sjo.Year, Year) && Same(sjo.Group, Group) && Same(sjo.SiteCode, SiteCode))))
            .DistinctBy(sjo => sjo.Id)
            .ToList();

    private static bool IsRunningNumber(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static string Pad(string number) => number.Trim().PadLeft(RunningNumberWidth, '0');

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
