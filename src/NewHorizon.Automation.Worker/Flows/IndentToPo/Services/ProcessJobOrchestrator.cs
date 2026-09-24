using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Contracts;
using NewHorizon.Automation.Worker.Flows.IndentToPo.Endpoints;

namespace NewHorizon.Automation.Worker.Flows.IndentToPo.Services;

/// <summary>
/// What a tracked start could not do, and why, in the caller's words. The status code travels with
/// the reason so the endpoint does not have to infer it from the wording.
/// </summary>
public sealed record ProcessJobRefusal(string Title, string Detail, int StatusCode);

/// <summary>
/// Runs a conversion and records it: opens the run, hands the work to the ERP client, closes the
/// run with what it found.
/// </summary>
/// <remarks>
/// It lives in the host because it is the only place both halves are visible. Tracking is an
/// Application concern and knows nothing about purchase orders; the ERP client makes purchase
/// orders and knows nothing about runs. Composing them is the composition root's job, and putting
/// it here keeps the endpoints free of it.
/// </remarks>
public interface IProcessJobOrchestrator
{
    /// <summary>Converts what the request allows, under a new run.</summary>
    Task<(ProcessRunDetailResponse? Result, ProcessJobRefusal? Refusal)> StartAsync(
        StartProcessJobRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attempts one indent again, as a new execution under a new run. Distinct from re-queueing a
    /// failed job: a conversion that completed can still have lines left, and this is the only way
    /// to ask for them.
    /// </summary>
    Task<(ProcessRunDetailResponse? Result, ProcessJobRefusal? Refusal)> RetryAsync(
        Guid jobId,
        string? triggeredBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens one run, runs the conversion inside it, and closes the run either way — handing back
    /// whatever the conversion produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run lifecycle, in one place, for every entry point that converts. It exists because
    /// there are now two shapes of caller — this class's own <see cref="StartAsync"/>, which
    /// answers with the run, and <c>PurchaseOrderEndpoints.ConvertAsync</c>, which answers with the
    /// purchase orders — and a second copy of "open, count, close, map the refusal" is exactly the
    /// duplication that has already produced two defects here.
    /// </para>
    /// <para>
    /// <b>It does not check <see cref="IIndentPoTracker.IsEnabled"/>.</b> Callers that promise the
    /// caller a run id check it themselves and refuse with 503. The conversion endpoints promise
    /// purchase orders and have always worked on an installation with no automation database, so
    /// with tracking off this simply runs the conversion and records nothing — every tracker call
    /// is a no-op on <c>NullProcessJobService</c>.
    /// </para>
    /// <para>
    /// <paramref name="convert"/> reports the indents it examined, which is what the run row
    /// records. A non-ERP exception is recorded against the run and then <b>rethrown</b>, so a
    /// caller's own handler still sees it.
    /// </para>
    /// </remarks>
    /// <param name="requireRun">
    /// True for a caller whose answer <i>is</i> the run: if the run cannot be opened it is refused
    /// before anything is converted, rather than creating real purchase orders it then has nothing
    /// to report them against. False for a caller that answers with the purchase orders — it
    /// converts either way and simply goes unrecorded.
    /// </param>
    Task<(T? Result, ProcessJobRefusal? Refusal)> RunTrackedAsync<T>(
        StartRunRequest request,
        Func<Task<(T? Value, int Examined)>> convert,
        CancellationToken cancellationToken,
        bool requireRun = false)
        where T : class;
}

/// <inheritdoc />
public sealed class ProcessJobOrchestrator : IProcessJobOrchestrator
{
    private readonly IIndentPoTracker _tracker;
    private readonly IProcessJobService _processJobs;
    private readonly IIndentToPoService _indentToPo;
    private readonly IPoAutomationGate _poAutomationGate;
    private readonly ILogger<ProcessJobOrchestrator> _logger;

    public ProcessJobOrchestrator(
        IIndentPoTracker tracker,
        IProcessJobService processJobs,
        IIndentToPoService indentToPo,
        IPoAutomationGate poAutomationGate,
        ILogger<ProcessJobOrchestrator> logger)
    {
        _tracker = tracker;
        _processJobs = processJobs;
        _indentToPo = indentToPo;
        _poAutomationGate = poAutomationGate;
        _logger = logger;
    }

    public async Task<(ProcessRunDetailResponse?, ProcessJobRefusal?)> StartAsync(
        StartProcessJobRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_tracker.IsEnabled)
        {
            return (null, TrackingOff);
        }

        var filter = IndentTypeFilter.Parse(request.IndentTypes, request.IndentType);
        if (!filter.IsValid)
        {
            return (null, new ProcessJobRefusal("Unknown indent type.", filter.Error!, StatusCodes.Status400BadRequest));
        }

        if (!TryParseTrigger(request.Trigger, out var trigger, out var triggerError))
        {
            return (null, new ProcessJobRefusal("Unknown trigger.", triggerError!, StatusCodes.Status400BadRequest));
        }

        // Normalised before it is recorded, so the run says which types it was actually willing to
        // convert rather than repeating a blank the caller sent.
        var selection = IndentTypeSelection.Normalise(filter.Selection);

        // The PO Automation master toggle gates this path too: off ⇒ no authorised indent converts,
        // whichever endpoint asked. A no-op when there is no automation database.
        var poAutomationOff = await _poAutomationGate.OffReasonAsync(
            selection.Select(type => Enum.Parse<IndentKind>(type.ToString())),
            cancellationToken);

        if (poAutomationOff is not null)
        {
            return (null, new ProcessJobRefusal(
                "PO Automation is turned off.", poAutomationOff, StatusCodes.Status409Conflict));
        }

        // Recorded as null when the caller did not set a ceiling, and left unset on the sweep
        // request below so IndentSweepRequest's own default applies. Repeating that number here
        // would be a second copy of it, free to drift from the one that actually takes effect.
        var (_, refusal) = await RunTrackedAsync<ProcessRunDetailResponse>(
            new StartRunRequest(
                trigger,
                string.Join(", ", selection),
                request.TriggeredBy,
                request.TriggerReference,
                request.Sites is { Count: > 0 } ? string.Join(", ", request.Sites) : null,
                request.MaxIndents),
            async () => (null, await ConvertAsync(request, filter.Selection, cancellationToken)),
            cancellationToken,

            // This endpoint answers with the run, so a run it could not open is a refusal — made
            // before anything is converted rather than after real purchase orders exist.
            requireRun: true);

        // The answer is the run itself, which can only be read once the run has closed — so it is
        // fetched here rather than inside the delegate, where it would still say Running.
        return refusal is not null ? (null, refusal) : await RunDetailAsync(cancellationToken);
    }

    public async Task<(ProcessRunDetailResponse?, ProcessJobRefusal?)> RetryAsync(
        Guid jobId,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        if (!_tracker.IsEnabled)
        {
            return (null, TrackingOff);
        }

        var previous = await _processJobs.GetExecutionAsync(jobId, cancellationToken);
        if (previous is null)
        {
            return (null, new ProcessJobRefusal(
                "No such execution.",
                $"There is no tracked indent conversion with job id {jobId}.",
                StatusCodes.Status404NotFound));
        }

        var indent = previous.Execution.Conversion;
        var kind = indent.IndentKind;

        // A retry converts too, so the master toggle gates it the same as a fresh start.
        var poAutomationOff = await _poAutomationGate.OffReasonAsync([kind], cancellationToken);
        if (poAutomationOff is not null)
        {
            return (null, new ProcessJobRefusal(
                "PO Automation is turned off.", poAutomationOff, StatusCodes.Status409Conflict));
        }

        var (_, refusal) = await RunTrackedAsync<ProcessRunDetailResponse>(
            new StartRunRequest(
                TriggerSource.Manual,
                kind.ToString(),
                triggeredBy,
                $"retry of job {jobId}"),
            async () =>
            {
                var one = await _indentToPo.ConvertIndentAsync(
                    indent.IndentId,
                    indent.IndentNumber,
                    sites: [indent.SiteId],

                    // The family is known from the case, so the id cannot be mistaken for the
                    // other table's — which is the whole reason the kind is part of its identity.
                    indentTypes: [Enum.Parse<IndentType>(kind.ToString())],
                    dryRun: false,
                    cancellationToken);

                _ = one;
                return (null, 1);
            },
            cancellationToken,

            // As with StartAsync: the answer is the run, so an unopenable run is refused up front.
            requireRun: true);

        return refusal is not null ? (null, refusal) : await RunDetailAsync(cancellationToken);
    }

    private static ProcessJobRefusal TrackingOff => new(
        "Process tracking is off on this installation.",
        "No automation database is configured, so a conversion cannot be recorded. "
        + "POST /api/automation/indent-to-po still converts; it just leaves no history. "
        + "Set AutomationAgent:Database:ConnectionString to turn tracking on.",
        StatusCodes.Status503ServiceUnavailable);

    /// <inheritdoc />
    public async Task<(T? Result, ProcessJobRefusal? Refusal)> RunTrackedAsync<T>(
        StartRunRequest request,
        Func<Task<(T? Value, int Examined)>> convert,
        CancellationToken cancellationToken,
        bool requireRun = false)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(convert);

        // Opened here rather than by the caller, so "open it" and "close it either way" cannot be
        // separated — a run left Running because something threw is worse than useless: it reads
        // as still going.
        await TryTrackAsync(() => _tracker.StartRunAsync(request, cancellationToken), "open the run");

        var runId = _tracker.RunId;

        if (requireRun && runId is null)
        {
            return (null, TrackingOff);
        }

        var examined = 0;
        T? value;

        try
        {
            (value, examined) = await convert();
            await TryTrackAsync(() => _tracker.CompleteRunAsync(examined, cancellationToken), "close the run");
        }
        catch (ErpException erp)
        {
            // The ERP refusing is an answer, not a server fault, and the exception already carries
            // the verdict: transient means "ask again", business means "a person has to change
            // something first". Same mapping the untracked endpoint uses, so one caller cannot get
            // a 400 from one path and a 500 from the other for the same refusal.
            await TryTrackAsync(
                () => _tracker.FailRunAsync(erp.LaymanMessage, examined, cancellationToken),
                "record the run as failed");

            _logger.LogWarning(
                "Run {RunId} stopped after examining {Examined} indent(s): {Reason}",
                runId,
                examined,
                erp.LaymanMessage);

            return (null, new ProcessJobRefusal(
                erp.LaymanMessage,
                erp.TechnicalMessage,
                erp.IsTransient
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status400BadRequest));
        }
        catch (Exception ex)
        {
            // Anything else really is a fault. Recorded, then rethrown: the caller gets it through
            // the problem handler, and the run row stops claiming to be in progress.
            await TryTrackAsync(
                () => _tracker.FailRunAsync(ex.Message, examined, cancellationToken),
                "record the run as failed");

            _logger.LogError(ex, "Run {RunId} failed after examining {Examined} indent(s)", runId, examined);
            throw;
        }

        return (value, null);
    }

    /// <summary>
    /// One tracking write, whose failure is logged and swallowed.
    /// </summary>
    /// <remarks>
    /// Tracking is best-effort, and this is where that promise is kept for the run itself. A
    /// conversion creates real purchase orders in the ERP; losing the history of one is bad, and
    /// refusing to make it because the history could not be written is worse. The startup log
    /// already tells an operator that <c>/api/automation/indent-to-po</c> is unaffected by an
    /// unopenable automation database — before this, opening a run would have made that untrue.
    /// <para>
    /// A caller that genuinely needs the run says so with <c>requireRun</c> and is refused instead.
    /// </para>
    /// </remarks>
    private async Task TryTrackAsync(Func<Task> write, string what)
    {
        try
        {
            await write();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Process tracking could not {What}. The conversion is unaffected and goes unrecorded",
                what);
        }
    }

    /// <summary>
    /// The run that just closed, as the tracked endpoints answer it. Read after the run is closed,
    /// so the caller sees its final status and totals rather than a snapshot mid-flight.
    /// </summary>
    private async Task<(ProcessRunDetailResponse?, ProcessJobRefusal?)> RunDetailAsync(
        CancellationToken cancellationToken)
    {
        if (_tracker.RunId is not { } runId)
        {
            return (null, TrackingOff);
        }

        var detail = await _processJobs.GetRunAsync(runId, cancellationToken);

        return detail is null ? (null, TrackingOff) : (ProcessJobMapper.ToResponse(detail), null);
    }

    private async Task<int> ConvertAsync(
        StartProcessJobRequest request,
        IReadOnlyList<IndentType>? selection,
        CancellationToken cancellationToken)
    {
        if (request.IndentId is { } indentId)
        {
            await _indentToPo.ConvertIndentAsync(
                indentId,
                indentNumber: null,
                sites: request.Sites,
                indentTypes: selection,
                dryRun: false,
                cancellationToken);

            return 1;
        }

        var sweepRequest = new IndentSweepRequest
        {
            Sites = request.Sites,
            IndentTypes = selection,
            DryRun = false,
        };

        // Only overridden when the caller asked for a ceiling; otherwise the sweep keeps its own.
        if (request.MaxIndents is { } ceiling)
        {
            sweepRequest = sweepRequest with { MaxIndents = ceiling };
        }

        var sweep = await _indentToPo.ConvertEligibleAsync(sweepRequest, cancellationToken);

        return sweep.Examined;
    }

    private static bool TryParseTrigger(string? value, out TriggerSource trigger, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            // Something called the API, so that is what is recorded. Guessing "Timer" or "Chatbot"
            // from an unlabelled call would put a lie in the history.
            trigger = TriggerSource.Api;
            return true;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out trigger))
        {
            return true;
        }

        error = $"'{value}' is not a trigger. Expected one of: {string.Join(", ", Enum.GetNames<TriggerSource>())}.";
        return false;
    }
}
