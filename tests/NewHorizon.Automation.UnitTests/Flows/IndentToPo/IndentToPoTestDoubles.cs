using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.UnitTests.Erp;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

internal static class ServiceUnderTest
{
    public static IndentToPoService Build(
        FakeErp erp,
        IndentPoOptions? options = null,
        ILogger<IndentToPoService>? logger = null,
        IPoAutomationGate? poAutomationGate = null,
        IIndentPoTracker? tracker = null) =>
        new(
            new SingleClientFactory(erp),
            new StubTokenProvider(),
            Options.Create(new ErpEndpointOptions()),
            Options.Create(options ?? new IndentPoOptions
            {
                CompanyId = 1,
                LocationId = 1,
                Sites = [1, 2],
                FinancialYear = "26-27",
                BuyerCode = "001",
                Currency = "RS",
                DomesticCurrency = "RS",
            }),
            new FixedClock(),

            // These tests are about the ERP conversation, not the history of it. The no-op tracker
            // is the same one a database-less installation runs with, so nothing here is a stub
            // that only exists for the tests — unless a test passes its own, to exercise what
            // happens when recording the outcome itself fails.
            tracker ?? new NullProcessJobService(NullLogger<NullProcessJobService>.Instance),

            // Default: automation is on for every type, forever — the master switch is a separate
            // concern with its own tests (PoAutomationGateTests, and the "stops mid-sweep" test
            // that passes its own gate here).
            poAutomationGate ?? FakePoAutomationGate.AlwaysOn,
            logger ?? NullLogger<IndentToPoService>.Instance);
}

/// <summary>
/// A tracker whose <see cref="CompleteExecutionAsync"/> always fails — simulates a local database
/// hiccup recording a conversion's outcome, after the ERP conversation itself already succeeded.
/// Everything else behaves like <see cref="NullProcessJobService"/>: a no-op that always succeeds.
/// </summary>
internal sealed class ThrowingCompleteExecutionTracker : IIndentPoTracker
{
    public bool IsEnabled => true;

    public Guid? RunId => null;

    public Guid? ExecutionId => null;

    public int FailExecutionCalls { get; private set; }

    public Task StartRunAsync(StartRunRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartExecutionAsync(TrackedIndent indent, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task EnterStageAsync(string stage, string task, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteExecutionAsync(IReadOnlyList<TrackedOutcome> outcomes, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated failure recording the outcome to the local database.");

    public Task FailExecutionAsync(
        string laymanMessage,
        string technicalMessage,
        bool transient,
        CancellationToken cancellationToken)
    {
        FailExecutionCalls++;
        return Task.CompletedTask;
    }

    public Task CompleteRunAsync(int indentsExamined, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailRunAsync(string reason, int indentsExamined, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A hand-controlled <see cref="IPoAutomationGate"/>: on until <see cref="TurnOff"/> is called
/// (e.g. from a FakeErp route, to simulate the user disabling automation mid-sweep), off after.
/// </summary>
internal sealed class FakePoAutomationGate : IPoAutomationGate
{
    private volatile bool _off;

    public static FakePoAutomationGate AlwaysOn => new();

    public int Checks { get; private set; }

    public void TurnOff() => _off = true;

    public Task<string?> OffReasonAsync(IEnumerable<IndentKind> kinds, CancellationToken cancellationToken)
    {
        Checks++;
        return Task.FromResult<string?>(_off ? "PO Automation was turned off during the run." : null);
    }
}
