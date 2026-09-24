using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.IntegrationTests.Persistence;

/// <summary>
/// Exercises the guarantees that live in SQL Server rather than in C#: the filtered unique index
/// and the UPDLOCK/READPAST claiming statement.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class JobRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public JobRepositoryTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Enqueuing_the_same_document_twice_returns_the_first_job()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var documentId = NewDocumentId();

        var first = await EnqueueAsync(documentId);
        var second = await EnqueueAsync(documentId);

        first.WasCreated.Should().BeTrue();

        // The reconciliation poll re-reporting a document the push already handled is the normal
        // case, not an error: it adopts the existing job instead of creating a second one.
        second.WasCreated.Should().BeFalse();
        second.Job.Id.Should().Be(first.Job.Id);

        await using var context = _fixture.CreateContext();
        context.Jobs.Count(job => job.DocumentId == documentId).Should().Be(1);
    }

    [SkippableFact]
    public async Task A_cancelled_job_does_not_block_a_fresh_run_for_the_same_document()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var documentId = NewDocumentId();
        var first = await EnqueueAsync(documentId);

        await using (var context = _fixture.CreateContext())
        {
            var repository = CreateRepository(context);
            var job = await repository.GetAsync(first.Job.Id, CancellationToken.None);
            job!.Cancel("admin", "Superseded", Now);
            await repository.SaveAsync(job, CancellationToken.None);
        }

        var second = await EnqueueAsync(documentId);

        // The index is filtered on Status <> 'Cancelled' precisely so a rejected or abandoned
        // document can legitimately be re-run later.
        second.WasCreated.Should().BeTrue();
        second.Job.Id.Should().NotBe(first.Job.Id);
    }

    [SkippableFact]
    public async Task Claiming_flips_jobs_to_running_highest_priority_first()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);
        await _fixture.ResetAsync();

        var normal = await EnqueueAsync(NewDocumentId(), priority: 0);
        var retried = await EnqueueAsync(NewDocumentId(), priority: 100);

        await using var context = _fixture.CreateContext();
        var repository = CreateRepository(context);

        var claimed = await repository.ClaimPendingJobsAsync(batchSize: 1, CancellationToken.None);

        // A manual retry raises priority so it is picked up ahead of newly enqueued work; the
        // claiming statement must honour that ordering.
        claimed.Should().ContainSingle().Which.Should().Be(retried.Job.Id);

        var reloaded = await repository.GetAsync(retried.Job.Id, CancellationToken.None);
        reloaded!.Status.Should().Be(JobStatus.Running);

        var untouched = await repository.GetAsync(normal.Job.Id, CancellationToken.None);
        untouched!.Status.Should().Be(JobStatus.Pending);
    }

    [SkippableFact]
    public async Task Two_workers_claiming_at_once_never_take_the_same_job()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);
        await _fixture.ResetAsync();

        const int jobCount = 12;
        var enqueued = new List<Guid>(jobCount);
        for (var i = 0; i < jobCount; i++)
        {
            enqueued.Add((await EnqueueAsync(NewDocumentId())).Job.Id);
        }

        // Four independent contexts stand in for four parallel workers.
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var context = _fixture.CreateContext();
            var repository = CreateRepository(context);
            return await repository.ClaimPendingJobsAsync(batchSize: 3, CancellationToken.None);
        }));

        var allClaimed = claims.SelectMany(claim => claim).ToList();

        // READPAST means a worker skips rows another worker holds; no job is ever claimed twice.
        allClaimed.Should().OnlyHaveUniqueItems();
        allClaimed.Should().BeSubsetOf(enqueued);
    }

    [SkippableFact]
    public async Task Steps_round_trip_with_their_erp_document_references()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var enqueued = await EnqueueAsync(NewDocumentId());

        await using (var context = _fixture.CreateContext())
        {
            var repository = CreateRepository(context);
            var job = await repository.GetAsync(enqueued.Job.Id, CancellationToken.None);
            job!.Claim(Now);

            var step = job.NextStep()!;
            step.Start(Now);
            step.Complete(Now, "WO-4711", """{"item":"X"}""", """{"wo":"WO-4711"}""");

            await repository.SaveAsync(job, CancellationToken.None);
        }

        await using var verifyContext = _fixture.CreateContext();
        var verifyRepository = CreateRepository(verifyContext);
        var reloaded = await verifyRepository.GetAsync(enqueued.Job.Id, CancellationToken.None);

        // Checkpoint durability: a restart must be able to see what the previous process created.
        reloaded!.Steps.Should().HaveCount(2);
        reloaded.Steps[0].Status.Should().Be(StepStatus.Completed);
        reloaded.Steps[0].ErpDocumentRef.Should().Be("WO-4711");
        reloaded.CompletedDocumentRefs().Should().ContainKey("SJO/DeAllocation");
        reloaded.NextStep()!.OperationName.Should().Be("Allocation");
    }

    [SkippableFact]
    public async Task A_second_cycle_cannot_start_while_one_is_still_live()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);
        await _fixture.ResetAsync();

        // Two agents against one database, both ticking. Without the live-cycle index they would
        // both create SJOs for the same OAFs.
        var first = await EnqueueCycleAsync();
        var second = await EnqueueCycleAsync();

        first.WasCreated.Should().BeTrue();
        second.WasCreated.Should().BeFalse();
        second.Job.Id.Should().Be(first.Job.Id);
    }

    [SkippableFact]
    public async Task A_finished_cycle_does_not_block_the_next_one()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);
        await _fixture.ResetAsync();

        var first = await EnqueueCycleAsync();

        await using (var context = _fixture.CreateContext())
        {
            var repository = CreateRepository(context);
            var job = await repository.GetAsync(first.Job.Id, CancellationToken.None);
            job!.Claim(Now);

            // A quiet cycle: nothing was awaiting an SJO, so its one operation is skipped rather
            // than executed. The job may only complete once no operation is outstanding.
            job.Steps[0].Skip(Now, "No OAF is awaiting an SJO.");
            job.Complete(Now);
            await repository.SaveAsync(job, CancellationToken.None);
        }

        // Unlike a document, a cycle is meant to run again — which is why the filter excludes
        // Completed as well as Cancelled.
        var second = await EnqueueCycleAsync();

        second.WasCreated.Should().BeTrue();
        second.Job.Id.Should().NotBe(first.Job.Id);
    }

    private async Task<Application.Jobs.JobEnqueueResult> EnqueueCycleAsync()
    {
        await using var context = _fixture.CreateContext();
        var repository = CreateRepository(context);

        // Each cycle's identity is the moment it began, so no two cycles ever share a key: the
        // live-cycle index is the only thing standing between them.
        var job = Job.Create(
            "AutoShopCycle",
            "Cycle",
            $"cycle-{Guid.NewGuid():N}",
            AutomationMode.Full,
            Now);

        job.PlanSteps([new PlannedOperation("OafToSjo", "CreateSjoFromPendingOaf")]);

        return await repository.EnqueueAsync(job, CancellationToken.None);
    }

    private static string NewDocumentId() => $"SO-{Guid.NewGuid():N}";

    private async Task<Application.Jobs.JobEnqueueResult> EnqueueAsync(string documentId, int priority = 0)
    {
        await using var context = _fixture.CreateContext();
        var repository = CreateRepository(context);

        var job = Job.Create("SJO", "SalesOrder", documentId, AutomationMode.Full, Now, priority);
        job.PlanSteps([new PlannedOperation("SJO", "DeAllocation"), new PlannedOperation("SJO", "Allocation")]);

        return await repository.EnqueueAsync(job, CancellationToken.None);
    }

    private static JobRepository CreateRepository(AutomationDbContext context) =>
        new(context, new FixedClock(Now), NullLogger<JobRepository>.Instance);

    private sealed class FixedClock : IClock
    {
        private readonly DateTimeOffset _utcNow;

        public FixedClock(DateTimeOffset utcNow) => _utcNow = utcNow;

        public DateTimeOffset UtcNow => _utcNow;

        public TimeOnly LocalTimeOfDay => TimeOnly.FromTimeSpan(_utcNow.TimeOfDay);
    }
}
