using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.Domain.Flows.PoToGrn;

/// <summary>
/// The PO → GRN automation settings: one row, seeded inert by migration and only ever changed in
/// place through <c>PUT /api/automation/grn-automation</c>.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>IndentPoAutomationConfig</c>: the two flows sit on the same
/// dashboard but share no settings, table or endpoint.
/// </remarks>
public sealed class PoGrnAutomationConfig
{
    /// <summary>Longest invoice number the ERP's <c>XGRNBILLNO</c> column is given here.</summary>
    public const int InvoiceNumberMaxLength = 50;

    public const int PoTypesMaxLength = 50;

    public const int PoNumbersMaxLength = 500;

    // Materialisation constructor for EF Core.
    private PoGrnAutomationConfig()
    {
    }

    private PoGrnAutomationConfig(Guid id, DateTimeOffset nowUtc)
    {
        Id = id;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>The master switch. Off ⇒ the scheduler skips and every run is refused.</summary>
    public bool IsActive { get; private set; }

    public PoGrnRunMode RunMode { get; private set; } = PoGrnRunMode.Api;

    public TimeOnly? ScheduleTime { get; private set; }

    public GrnReceiptMode ReceiptMode { get; private set; } = GrnReceiptMode.Complete;

    /// <summary>
    /// The vendor invoice number stamped on every GRN the agent creates. Typed once by a user and
    /// editable; a run that would create GRNs is refused while it is blank.
    /// </summary>
    public string? InvoiceNumber { get; private set; }

    /// <summary>Comma-separated site ids to sweep; blank falls back to <c>AutomationAgent:PoToGrn:Sites</c>.</summary>
    public string? Sites { get; private set; }

    /// <summary>
    /// Comma-separated PO types every run is limited to (<c>"Regular"</c>, <c>"Capital"</c>);
    /// blank means both.
    /// </summary>
    public string? PoTypes { get; private set; }

    /// <summary>
    /// Comma-separated PO numbers every run is limited to — whole (<c>26-27/TE/NF1/000190</c>) or bare
    /// running number; blank means every eligible PO.
    /// </summary>
    public string? PoNumbers { get; private set; }

    public bool DryRun { get; private set; }

    public int? MaxPosPerRun { get; private set; }

    public DateOnly? LastScheduledRunDate { get; private set; }

    public DateTimeOffset? LastTriggeredAtUtc { get; private set; }

    public string? LastRunStatus { get; private set; }

    public Guid? LastRunReference { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public string? UpdatedBy { get; private set; }

    public bool HasInvoiceNumber => !string.IsNullOrWhiteSpace(InvoiceNumber);

    public static PoGrnAutomationConfig CreateDefault(DateTimeOffset nowUtc) => new(Guid.NewGuid(), nowUtc);

    /// <summary>
    /// The scheduler's rule: switched on, the mode admits a timer, a slot is set and has passed
    /// today, and today's slot has not been used. One rule that also catches up a slot missed while
    /// the agent was down.
    /// </summary>
    public bool ShouldRunOnSchedule(TimeOnly localNow, DateOnly localToday) =>
        IsActive
        && RunMode.Allows(TriggerSource.Timer)
        && ScheduleTime is { } slot
        && localNow >= slot
        && (LastScheduledRunDate is null || LastScheduledRunDate < localToday);

    public void Update(PoGrnAutomationConfigUpdate update, DateTimeOffset nowUtc, string? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(update);

        var scheduleBefore = ScheduleTime;

        IsActive = update.IsActive ?? IsActive;
        RunMode = update.RunMode ?? RunMode;
        ReceiptMode = update.ReceiptMode ?? ReceiptMode;
        DryRun = update.DryRun ?? DryRun;

        if (update.ScheduleTime is not null)
        {
            ScheduleTime = update.ScheduleTime;
        }

        if (update.ClearScheduleTime)
        {
            ScheduleTime = null;
        }

        if (update.InvoiceNumber is not null)
        {
            var invoice = update.InvoiceNumber.Trim();

            if (invoice.Length > InvoiceNumberMaxLength)
            {
                throw new DomainException(
                    $"{nameof(InvoiceNumber)} must be at most {InvoiceNumberMaxLength} characters.");
            }

            InvoiceNumber = invoice.Length == 0 ? null : invoice;
        }

        if (update.Sites is not null)
        {
            Sites = NormaliseSites(update.Sites);
        }

        if (update.PoTypes is not null)
        {
            PoTypes = NormalisePoTypes(update.PoTypes);
        }

        if (update.PoNumbers is not null)
        {
            PoNumbers = NormalisePoNumbers(update.PoNumbers);
        }

        if (update.MaxPosPerRun is { } ceiling)
        {
            if (ceiling <= 0)
            {
                throw new DomainException($"{nameof(MaxPosPerRun)} must be greater than zero.");
            }

            MaxPosPerRun = ceiling;
        }

        if (update.ClearMaxPosPerRun)
        {
            MaxPosPerRun = null;
        }

        // "PO-based" has no schedule, whatever the caller sent.
        if (RunMode == PoGrnRunMode.Api)
        {
            ScheduleTime = null;
        }

        // A new or changed slot gets a fresh chance today, instead of silently waiting for tomorrow.
        if (ScheduleTime is not null && ScheduleTime != scheduleBefore)
        {
            LastScheduledRunDate = null;
        }

        UpdatedAtUtc = nowUtc;
        UpdatedBy = updatedBy;
    }

    /// <summary>Claims today's slot for the scheduler and stamps the run.</summary>
    public void MarkScheduledRun(DateOnly localToday, DateTimeOffset nowUtc, RunStatus status, Guid? runId)
    {
        LastScheduledRunDate = localToday;
        MarkRun(nowUtc, status, runId);
    }

    /// <summary>Stamps the Last Run columns without touching the scheduler's slot.</summary>
    public void MarkRun(DateTimeOffset nowUtc, RunStatus status, Guid? runId)
    {
        LastTriggeredAtUtc = nowUtc;
        LastRunStatus = status.ToString();
        LastRunReference = runId;
        UpdatedAtUtc = nowUtc;
    }

    public IReadOnlyList<int> SiteIds() =>
        string.IsNullOrWhiteSpace(Sites)
            ? []
            : [.. Sites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)];

    /// <summary>The saved PO types; empty means both.</summary>
    public IReadOnlyList<PoGrnType> PoTypeList() =>
        string.IsNullOrWhiteSpace(PoTypes)
            ? []
            : [.. SplitList(PoTypes).Select(value => Enum.Parse<PoGrnType>(value, ignoreCase: true))];

    /// <summary>The saved PO numbers; empty means every eligible PO.</summary>
    public IReadOnlyList<string> PoNumberList() =>
        string.IsNullOrWhiteSpace(PoNumbers) ? [] : SplitList(PoNumbers);

    private static List<string> SplitList(string csv) =>
        [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? NormalisePoTypes(IReadOnlyList<string> types)
    {
        var parsed = new List<PoGrnType>();

        foreach (var value in types.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!Enum.TryParse(value.Trim(), ignoreCase: true, out PoGrnType type)
                || !Enum.IsDefined(type)
                || int.TryParse(value.Trim(), out _))
            {
                throw new DomainException(
                    $"'{value}' is not a PO type. Expected {string.Join(" and/or ", Enum.GetNames<PoGrnType>())}.");
            }

            if (!parsed.Contains(type))
            {
                parsed.Add(type);
            }
        }

        // Every type named is the same as none: store "both" one way only.
        return parsed.Count == 0 || parsed.Count == Enum.GetValues<PoGrnType>().Length
            ? null
            : string.Join(", ", parsed);
    }

    private static string? NormalisePoNumbers(string numbers)
    {
        var parsed = new List<string>();

        foreach (var number in SplitList(numbers))
        {
            if (!parsed.Contains(number, StringComparer.OrdinalIgnoreCase))
            {
                parsed.Add(number);
            }
        }

        var normalised = parsed.Count == 0 ? null : string.Join(", ", parsed);

        if (normalised is { Length: > PoNumbersMaxLength })
        {
            throw new DomainException($"{nameof(PoNumbers)} must be at most {PoNumbersMaxLength} characters.");
        }

        return normalised;
    }

    private static string? NormaliseSites(string sites)
    {
        if (string.IsNullOrWhiteSpace(sites))
        {
            return null;
        }

        var parsed = new List<int>();

        foreach (var part in sites.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var siteId) || siteId <= 0)
            {
                throw new DomainException($"'{part}' is not a valid site id. Give a comma-separated list of positive numbers.");
            }

            if (!parsed.Contains(siteId))
            {
                parsed.Add(siteId);
            }
        }

        return parsed.Count == 0 ? null : string.Join(", ", parsed);
    }
}

/// <summary>A partial change: a null field is left as it is.</summary>
public sealed record PoGrnAutomationConfigUpdate
{
    public bool? IsActive { get; init; }

    public PoGrnRunMode? RunMode { get; init; }

    public TimeOnly? ScheduleTime { get; init; }

    public bool ClearScheduleTime { get; init; }

    public GrnReceiptMode? ReceiptMode { get; init; }

    /// <summary>Blank clears it.</summary>
    public string? InvoiceNumber { get; init; }

    /// <summary>Comma-separated site ids; blank clears the list.</summary>
    public string? Sites { get; init; }

    /// <summary><c>Regular</c> and/or <c>Capital</c>; an empty list clears the limit (both types).</summary>
    public IReadOnlyList<string>? PoTypes { get; init; }

    /// <summary>Comma-separated PO numbers; blank clears the limit (every eligible PO).</summary>
    public string? PoNumbers { get; init; }

    public bool? DryRun { get; init; }

    public int? MaxPosPerRun { get; init; }

    public bool ClearMaxPosPerRun { get; init; }
}
