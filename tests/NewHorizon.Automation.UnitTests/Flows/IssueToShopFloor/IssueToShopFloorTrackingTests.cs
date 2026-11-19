using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.UnitTests.Workflows;
using Xunit;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class IssueToShopFloorTrackingTests
{
    private readonly DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Conversion_OpenAndRecordAttempts_TracksCorrectly()
    {
        var conversion = IssueToShopFloorConversion.Open("Sjo", "26-27/SJ/NF1/000100", 12345, 1, "NF1", _now);

        conversion.DocumentNumber.Should().Be("26-27/SJ/NF1/000100");
        conversion.IssueSource.Should().Be("Sjo");
        conversion.TotalAttempts.Should().Be(1);
        conversion.IsTerminal.Should().BeFalse();
        conversion.TerminalIssueNumber.Should().BeNull();

        // 1st recorded attempt: failed
        conversion.RecordAttempt(_now.AddMinutes(5), succeeded: false, issueNumber: null);
        conversion.TotalAttempts.Should().Be(2);
        conversion.IsTerminal.Should().BeFalse();
        conversion.TerminalIssueNumber.Should().BeNull();

        // 2nd recorded attempt: succeeded
        conversion.RecordAttempt(_now.AddMinutes(10), succeeded: true, issueNumber: "26-27/IS/NF1/000001");
        conversion.TotalAttempts.Should().Be(3);
        conversion.IsTerminal.Should().BeTrue();
        conversion.TerminalIssueNumber.Should().Be("26-27/IS/NF1/000001");
    }

    [Fact]
    public void Outcome_Create_HoldsOutcomeData()
    {
        var jobId = Guid.NewGuid();
        var outcome = IssueToShopFloorOutcome.Create(
            jobId,
            "Sjo",
            "26-27/SJ/NF1/000100",
            "Created",
            "26-27/IS/NF1/000001",
            lineCount: 3,
            totalQuantity: 25.5m,
            refusalReason: null,
            checksJson: "[]",
            shortagesJson: null,
            linesJson: "[{\"item\":\"A\"}]",
            _now);

        outcome.JobId.Should().Be(jobId);
        outcome.IssueSource.Should().Be("Sjo");
        outcome.DocumentNumber.Should().Be("26-27/SJ/NF1/000100");
        outcome.Outcome.Should().Be("Created");
        outcome.IssueNumber.Should().Be("26-27/IS/NF1/000001");
        outcome.LineCount.Should().Be(3);
        outcome.TotalQuantity.Should().Be(25.5m);
        outcome.RefusalReason.Should().BeNull();
        outcome.LinesJson.Should().Be("[{\"item\":\"A\"}]");
    }

    [Fact]
    public void Stages_InOrder_ContainsExpectedPipelineStages()
    {
        IssueToShopFloorStages.InOrder.Should().ContainInOrder(
            IssueToShopFloorStages.Discovery,
            IssueToShopFloorStages.DocumentControl,
            IssueToShopFloorStages.WorkAllocation,
            IssueToShopFloorStages.ErpEligibility,
            IssueToShopFloorStages.PendingItems,
            IssueToShopFloorStages.StockAllocation,
            IssueToShopFloorStages.CreateIssue);
    }

    [Fact]
    public async Task TrackerService_LifecycleSuccess_RecordsStagesAndOutcome()
    {
        var tracking = new FakeTrackingRepo();
        var jobs = new InMemoryJobRepository();
        var configs = new StubConfigRepository(WorkflowNames.IssueToShopFloor, 3);
        var clock = new TestClock(_now);

        var tracker = new IssueToShopFloorTrackerService(
            tracking,
            jobs,
            configs,
            clock,
            NullLogger<IssueToShopFloorTrackerService>.Instance);

        await tracker.StartRunAsync(new StartIssueRunRequest(
            TriggerSource.Api,
            "Sjo",
            "Planner1",
            "26-27/SJ/NF1/000100"), CancellationToken.None);

        await tracker.StartExecutionAsync(
            "Sjo",
            "26-27/SJ/NF1/000100",
            documentId: 1001,
            siteId: 1,
            siteCode: "NF1",
            CancellationToken.None);

        await tracker.EnterStageAsync(IssueToShopFloorStages.WorkAllocation, IssueToShopFloorTasks.CheckWorkAllocation, CancellationToken.None);
        await tracker.EnterStageAsync(IssueToShopFloorStages.CreateIssue, IssueToShopFloorTasks.CreateIssueSlip, CancellationToken.None);

        await tracker.CompleteExecutionAsync(
            "Created",
            "26-27/IS/NF1/000099",
            lineCount: 2,
            totalQuantity: 10m,
            refusalReason: null,
            checksJson: "[]",
            shortagesJson: null,
            linesJson: "[{\"line\":1}]",
            CancellationToken.None);

        await tracker.CompleteRunAsync(documentsExamined: 1, issuesCreated: 1, CancellationToken.None);

        tracking.AddedOutcomes.Should().HaveCount(1);
        tracking.AddedOutcomes[0].IssueNumber.Should().Be("26-27/IS/NF1/000099");
        tracking.AddedOutcomes[0].Outcome.Should().Be("Created");

        var createdJob = await jobs.GetAsync(tracking.AddedOutcomes[0].JobId, CancellationToken.None);
        createdJob.Should().NotBeNull();
        createdJob!.ConversionId.Should().Be(tracking.AddedConversions[0].Id);
    }

    [Fact]
    public async Task TrackerService_MultipleAttempts_LinksAllToSameConversion()
    {
        var tracking = new FakeTrackingRepo();
        var jobs = new InMemoryJobRepository();
        var configs = new StubConfigRepository(WorkflowNames.IssueToShopFloor, 3);
        var clock = new TestClock(_now);

        var tracker = new IssueToShopFloorTrackerService(
            tracking,
            jobs,
            configs,
            clock,
            NullLogger<IssueToShopFloorTrackerService>.Instance);

        // Attempt 1: Refused
        await tracker.StartExecutionAsync("Sjo", "26-27/SJ/NF1/000100", 12345, 1, "NF1", CancellationToken.None);
        var firstJobId = tracker.ExecutionId!.Value;
        await tracker.CompleteExecutionAsync("Refused", null, 0, 0, "Shortage", null, null, null, CancellationToken.None);

        // Attempt 2: Created
        await tracker.StartExecutionAsync("Sjo", "26-27/SJ/NF1/000100", 12345, 1, "NF1", CancellationToken.None);
        var secondJobId = tracker.ExecutionId!.Value;
        await tracker.CompleteExecutionAsync("Created", "26-27/IS/NF1/000001", 1, 10m, null, null, null, null, CancellationToken.None);

        firstJobId.Should().NotBe(secondJobId);
        tracking.AddedConversions.Should().HaveCount(1);

        var firstJob = await jobs.GetAsync(firstJobId, CancellationToken.None);
        var secondJob = await jobs.GetAsync(secondJobId, CancellationToken.None);

        firstJob!.ConversionId.Should().Be(tracking.AddedConversions[0].Id);
        secondJob!.ConversionId.Should().Be(tracking.AddedConversions[0].Id);
    }

    [Fact]
    public async Task TrackerService_LifecycleRefused_RecordsRefusalOutcome()
    {
        var tracking = new FakeTrackingRepo();
        var jobs = new InMemoryJobRepository();
        var configs = new StubConfigRepository(WorkflowNames.IssueToShopFloor, 3);
        var clock = new TestClock(_now);

        var tracker = new IssueToShopFloorTrackerService(
            tracking,
            jobs,
            configs,
            clock,
            NullLogger<IssueToShopFloorTrackerService>.Instance);

        await tracker.StartRunAsync(new StartIssueRunRequest(
            TriggerSource.Api,
            "Sjo"), CancellationToken.None);

        await tracker.StartExecutionAsync(
            "Sjo",
            "26-27/SJ/NF1/000101",
            documentId: 1002,
            siteId: 1,
            siteCode: "NF1",
            CancellationToken.None);

        await tracker.EnterStageAsync(IssueToShopFloorStages.StockAllocation, IssueToShopFloorTasks.AllocateStock, CancellationToken.None);

        await tracker.CompleteExecutionAsync(
            "Refused",
            issueNumber: null,
            lineCount: 0,
            totalQuantity: 0,
            refusalReason: "Item X has insufficient balance stock.",
            checksJson: "[{\"check\":\"StockAllocation\",\"passed\":false}]",
            shortagesJson: "[{\"item\":\"X\",\"shortage\":5}]",
            linesJson: null,
            CancellationToken.None);

        await tracker.CompleteRunAsync(documentsExamined: 1, issuesCreated: 0, CancellationToken.None);

        tracking.AddedOutcomes.Should().HaveCount(1);
        tracking.AddedOutcomes[0].Outcome.Should().Be("Refused");
        tracking.AddedOutcomes[0].RefusalReason.Should().Contain("insufficient balance");
    }

    [Fact]
    public async Task TrackerService_GetSummary_DelegatesToRepository()
    {
        var tracking = new FakeTrackingRepo
        {
            SummaryToReturn = new IssueToShopFloorHistorySummary(10, 8, 2, 0, 80.0, 1500.0)
        };
        var jobs = new InMemoryJobRepository();
        var configs = new StubConfigRepository(WorkflowNames.IssueToShopFloor, 3);
        var clock = new TestClock(_now);

        var service = new IssueToShopFloorTrackerService(
            tracking,
            jobs,
            configs,
            clock,
            NullLogger<IssueToShopFloorTrackerService>.Instance);

        var summary = await service.GetSummaryAsync(new IssueToShopFloorHistoryQuery(), CancellationToken.None);

        summary.TotalAttempts.Should().Be(10);
        summary.IssuesCreated.Should().Be(8);
        summary.SuccessRatePercentage.Should().Be(80.0);
    }

    [Fact]
    public async Task TrackerService_GetDashboardSummary_DelegatesToRepository()
    {
        var expectedSummary = new IssueToShopFloorDashboardSummary(
            TotalJobs: 5,
            CountsByStatus: new Dictionary<string, int> { ["Completed"] = 4, ["Skipped"] = 1 },
            CountsBySource: new Dictionary<string, int> { ["Sjo"] = 5 },
            SuccessCount: 4,
            SuccessRate: 0.8,
            AverageDurationMs: 1250.0,
            TotalTriggerAttempts: 6,
            TriggerAttemptsWithoutEligibleDocument: 1,
            BusinessRefusalCount: 1,
            TechnicalFailureCount: 0,
            TotalQuantityIssued: 50m,
            TotalLinesIssued: 10);

        var tracking = new FakeTrackingRepo { DashboardSummaryToReturn = expectedSummary };
        var jobs = new InMemoryJobRepository();
        var configs = new StubConfigRepository(WorkflowNames.IssueToShopFloor, 3);
        var clock = new TestClock(_now);

        var service = new IssueToShopFloorTrackerService(
            tracking,
            jobs,
            configs,
            clock,
            NullLogger<IssueToShopFloorTrackerService>.Instance);

        var summary = await service.GetDashboardSummaryAsync(new IssueToShopFloorHistoryQuery(), CancellationToken.None);

        summary.TotalJobs.Should().Be(5);
        summary.SuccessCount.Should().Be(4);
        summary.BusinessRefusalCount.Should().Be(1);
        summary.TotalQuantityIssued.Should().Be(50m);
    }

    [Fact]
    public async Task TrackerService_GetDailyStats_ZeroFillsGapsAcrossWindow()
    {
        var today = DateOnly.FromDateTime(_now.UtcDateTime);
        var twoDaysAgo = today.AddDays(-2);

        var tracking = new FakeTrackingRepo
        {
            DailyStatsToReturn = [new DailyIssueConversionStat(twoDaysAgo, 2, 2, 0, 0, 10m)]
        };
        var jobs = new InMemoryJobRepository();
        var configs = new StubConfigRepository(WorkflowNames.IssueToShopFloor, 3);
        var clock = new TestClock(_now);

        var service = new IssueToShopFloorTrackerService(
            tracking,
            jobs,
            configs,
            clock,
            NullLogger<IssueToShopFloorTrackerService>.Instance);

        var stats = await service.GetDailyStatsAsync(3, CancellationToken.None);

        stats.Should().HaveCount(3);
        stats[0].Date.Should().Be(twoDaysAgo);
        stats[0].IssuesCreated.Should().Be(2);

        stats[1].Date.Should().Be(today.AddDays(-1));
        stats[1].IssuesCreated.Should().Be(0);

        stats[2].Date.Should().Be(today);
        stats[2].IssuesCreated.Should().Be(0);
    }

    private sealed class FakeTrackingRepo : IIssueToShopFloorTrackingRepository
    {
        public List<AutomationRun> AddedRuns { get; } = [];
        public List<IssueToShopFloorConversion> AddedConversions { get; } = [];
        public List<IssueToShopFloorOutcome> AddedOutcomes { get; } = [];
        public IssueToShopFloorHistorySummary SummaryToReturn { get; set; } = new(0, 0, 0, 0, 0, 0);
        public IssueToShopFloorDashboardSummary DashboardSummaryToReturn { get; set; } = new(
            0,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            0,
            null,
            null,
            0,
            0,
            0,
            0,
            0m,
            0);
        public IReadOnlyList<DailyIssueConversionStat> DailyStatsToReturn { get; set; } = [];
        public IssueToShopFloorHistoryRow? LastCompletedJobToReturn { get; set; }
        public IReadOnlyList<IssueToShopFloorHistoryRow> RecentJobsToReturn { get; set; } = [];

        public Task AddRunAsync(AutomationRun run, CancellationToken cancellationToken)
        {
            AddedRuns.Add(run);
            return Task.CompletedTask;
        }

        public Task<AutomationRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.FromResult(AddedRuns.Find(r => r.Id == runId));

        public Task UpdateRunAsync(AutomationRun run, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IssueToShopFloorConversion?> FindConversionAsync(string documentNumber, CancellationToken cancellationToken) =>
            Task.FromResult(AddedConversions.Find(c => c.DocumentNumber == documentNumber));

        public Task<IssueToShopFloorConversion> AddConversionAsync(IssueToShopFloorConversion conversion, CancellationToken cancellationToken)
        {
            AddedConversions.Add(conversion);
            return Task.FromResult(conversion);
        }

        public Task UpdateConversionAsync(IssueToShopFloorConversion conversion, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddOutcomeAsync(IssueToShopFloorOutcome outcome, CancellationToken cancellationToken)
        {
            AddedOutcomes.Add(outcome);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IssueToShopFloorHistoryRow>> ListHistoryRowsAsync(IssueToShopFloorHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IssueToShopFloorHistoryRow>>([]);

        public Task<int> CountHistoryRowsAsync(IssueToShopFloorHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<Job?> GetJobWithStepsAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult<Job?>(null);

        public Task<IssueToShopFloorOutcome?> GetOutcomeAsync(Guid jobId, CancellationToken cancellationToken) =>
            Task.FromResult<IssueToShopFloorOutcome?>(null);

        public Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetDocumentHistoryRowsAsync(string documentNumber, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IssueToShopFloorHistoryRow>>([]);

        public Task<IssueToShopFloorHistorySummary> GetSummaryAsync(IssueToShopFloorHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(SummaryToReturn);

        public Task<IssueToShopFloorDashboardSummary> GetDashboardSummaryAsync(IssueToShopFloorHistoryQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(DashboardSummaryToReturn);

        public Task<IReadOnlyList<DailyIssueConversionStat>> GetDailyStatsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) =>
            Task.FromResult(DailyStatsToReturn);

        public Task<IssueToShopFloorHistoryRow?> GetLastCompletedJobAsync(CancellationToken cancellationToken) =>
            Task.FromResult(LastCompletedJobToReturn);

        public Task<IReadOnlyList<IssueToShopFloorHistoryRow>> GetRecentJobsAsync(int count, string? issueSource, CancellationToken cancellationToken) =>
            Task.FromResult(RecentJobsToReturn);
    }
}
