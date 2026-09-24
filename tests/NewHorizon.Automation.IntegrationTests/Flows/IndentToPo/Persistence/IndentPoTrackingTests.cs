using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Jobs;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.Infrastructure.Flows.IndentToPo;
using NewHorizon.Automation.Infrastructure.Persistence;
using NewHorizon.Automation.IntegrationTests.Persistence;

namespace NewHorizon.Automation.IntegrationTests.Flows.IndentToPo.Persistence;

/// <summary>
/// The tracking guarantees that live in SQL Server rather than in C#: the two filtered unique
/// indexes on the idempotency key, the outcome check constraint, and the computed durations.
/// </summary>
/// <remarks>
/// These are the rules the application deliberately does not enforce on its own. A service can be
/// bypassed by the next entry point somebody adds; an index cannot.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class IndentPoTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public IndentPoTrackingTests(SqlServerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_second_live_attempt_at_one_indent_is_refused()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var conversion = await OpenConversionAsync();

        var first = await EnqueueExecutionAsync(conversion);
        var second = await EnqueueExecutionAsync(conversion);

        first.WasCreated.Should().BeTrue();

        // Two live executions would race for the same indent lines in the ERP, so the second
        // adopts the first rather than starting beside it.
        second.WasCreated.Should().BeFalse();
        second.Job.Id.Should().Be(first.Job.Id);
    }

    [SkippableFact]
    public async Task A_finished_attempt_lets_the_same_indent_be_converted_again()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var conversion = await OpenConversionAsync();
        var first = await EnqueueExecutionAsync(conversion);

        await CompleteAsync(first.Job.Id);

        var second = await EnqueueExecutionAsync(conversion);

        // Unlike a one-shot document, an indent whose lines are still open is meant to be
        // attempted again — which is the whole reason the conversion index excludes Completed.
        second.WasCreated.Should().BeTrue();
        second.Job.Id.Should().NotBe(first.Job.Id);

        await using var context = _fixture.CreateContext();
        context.Jobs.Count(job => job.ConversionId == conversion.Id).Should().Be(2);
    }

    [SkippableFact]
    public async Task A_document_job_still_cannot_be_enqueued_twice()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        // The conversion rule is added beside the per-document one, not in place of it. Both are
        // unique on the same column, and EF will silently keep only one unless each is named — so
        // this asserts the older guarantee survived the newer one.
        var documentId = $"SO-{Guid.NewGuid():N}";

        var first = await EnqueueDocumentJobAsync(documentId);
        var second = await EnqueueDocumentJobAsync(documentId);

        first.WasCreated.Should().BeTrue();
        second.WasCreated.Should().BeFalse();
        second.Job.Id.Should().Be(first.Job.Id);
    }

    [SkippableFact]
    public async Task A_completed_document_job_still_blocks_a_second_one()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var documentId = $"SO-{Guid.NewGuid():N}";
        var first = await EnqueueDocumentJobAsync(documentId);

        await CompleteAsync(first.Job.Id);

        var second = await EnqueueDocumentJobAsync(documentId);

        // A document is converted once. Only the indent rule relaxes that, and only for indents.
        second.WasCreated.Should().BeFalse();
        second.Job.Id.Should().Be(first.Job.Id);
    }

    [SkippableFact]
    public async Task An_outcome_that_ordered_nothing_must_name_its_reason()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var conversion = await OpenConversionAsync();
        var execution = await EnqueueExecutionAsync(conversion);

        await using var context = _fixture.CreateContext();

        // Written as raw SQL on purpose: the domain factory already refuses this, and the point of
        // the check constraint is that it holds for anything that reaches the table another way.
        var insert = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO IndentPoOutcome
                (Id, JobId, StepId, Sequence, Outcome, VendorCode, CurrencyCode,
                 RateStructureCode, PoId, PoNumber, LineCount, Reason, CreatedAtUtc)
            VALUES
                (NEWID(), @jobId, NULL, 1, 'Skipped', NULL, NULL, NULL, NULL, NULL, 0, NULL, SYSDATETIMEOFFSET());
            """,
            new SqlParameter("@jobId", execution.Job.Id));

        await insert.Should().ThrowAsync<SqlException>()
            .Where(ex => ex.Message.Contains("CK_IndentPoOutcome_Result", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task One_purchase_order_is_recorded_once()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var conversion = await OpenConversionAsync();
        var execution = await EnqueueExecutionAsync(conversion);
        var poId = Random.Shared.NextInt64(1, long.MaxValue);

        await AddOutcomeAsync(IndentPoOutcome.Created(
            execution.Job.Id, 1, "V0012", "RS", "P0002", poId, "26-27/TE/NF1/000002", 4, Now));

        var duplicate = async () => await AddOutcomeAsync(IndentPoOutcome.Created(
            execution.Job.Id, 2, "V0012", "RS", "P0002", poId, "26-27/TE/NF1/000002", 4, Now));

        // A bug that recorded the same order twice would otherwise double every count on every
        // report built from this table.
        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }

    [SkippableFact]
    public async Task Duration_is_null_while_running_and_set_once_finished()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var conversion = await OpenConversionAsync();
        var execution = await EnqueueExecutionAsync(conversion);

        await using (var running = _fixture.CreateContext())
        {
            var job = await running.Jobs.AsNoTracking()
                .FirstAsync(candidate => candidate.Id == execution.Job.Id);

            // Derived from the timestamps, so there is nothing to be wrong yet.
            job.DurationMs.Should().BeNull();
        }

        await CompleteAsync(execution.Job.Id, Now.AddSeconds(3));

        await using var finished = _fixture.CreateContext();
        var completed = await finished.Jobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == execution.Job.Id);

        completed.DurationMs.Should().Be(3_000);
    }

    [SkippableFact]
    public async Task A_run_that_converted_nothing_still_says_what_it_was_allowed_to_convert()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var run = AutomationRun.Start(
            WorkflowNames.IndentToPurchaseOrder,
            TriggerSource.Chatbot,
            AutomationMode.Full,
            "Regular",
            Now,
            triggeredBy: "assistant",
            triggerReference: "chat-4417");

        run.RecordProgress(indentsExamined: 40, jobsCreated: 0, purchaseOrdersCreated: 0);
        run.Complete(Now.AddSeconds(12));

        await using (var context = _fixture.CreateContext())
        {
            var repository = new IndentPoTrackingRepository(context);
            await repository.AddRunAsync(run, CancellationToken.None);
            await repository.UpdateRunAsync(run, CancellationToken.None);
        }

        await using var reader = _fixture.CreateContext();
        var stored = await reader.Runs.AsNoTracking().FirstAsync(candidate => candidate.Id == run.Id);

        // The difference between "found no Capital indents" and "was never allowed to look for
        // Capital indents" is this column, and it survives a run that created no jobs at all.
        stored.RequestedIndentTypes.Should().Be("Regular");
        stored.IndentsExamined.Should().Be(40);
        stored.PurchaseOrdersCreated.Should().Be(0);
        stored.TriggerSource.Should().Be(TriggerSource.Chatbot);
        stored.TriggerReference.Should().Be("chat-4417");
        stored.DurationMs.Should().Be(12_000);
    }

    [SkippableFact]
    public async Task An_indent_is_one_case_however_many_times_it_is_attempted()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        var conversion = await OpenConversionAsync();

        await using var context = _fixture.CreateContext();
        var repository = new IndentPoTrackingRepository(context);

        var again = IndentPoConversion.Open(
            conversion.IndentId,
            conversion.IndentKind,
            conversion.IndentNumber,
            conversion.SiteId,
            Now);

        var adopted = await repository.AddConversionAsync(again, CancellationToken.None);

        // The natural key arbitrates and the caller gets the row that already existed. Throwing
        // would make two triggers finding the same indent at the same moment a server error.
        adopted.Id.Should().Be(conversion.Id);
        adopted.Id.Should().NotBe(again.Id);

        context.IndentPoConversions
            .Count(candidate => candidate.IndentId == conversion.IndentId)
            .Should().Be(1);
    }

    [SkippableFact]
    public async Task A_material_and_a_service_indent_may_share_an_id()
    {
        Skip.If(!_fixture.IsAvailable, _fixture.SkipReason);

        // XINDID and XINDAUTOID are keys into different ERP tables, so the same number is a
        // plausible value for both. The natural key includes the kind for exactly this reason.
        var indentId = NewIndentId();

        await OpenConversionAsync(indentId, IndentKind.Regular);
        await OpenConversionAsync(indentId, IndentKind.Service);

        await using var context = _fixture.CreateContext();
        var repository = new IndentPoTrackingRepository(context);

        var both = await repository.FindConversionsAsync(indentId, CancellationToken.None);

        both.Should().HaveCount(2);
    }

    private static long NewIndentId() => Random.Shared.NextInt64(1, int.MaxValue);

    private async Task<IndentPoConversion> OpenConversionAsync(
        long? indentId = null,
        IndentKind kind = IndentKind.Regular)
    {
        var conversion = IndentPoConversion.Open(
            indentId ?? NewIndentId(),
            kind,
            $"26-27/TE/NF1/{Random.Shared.Next(100000, 999999)}",
            siteId: 1,
            Now,
            new DateOnly(2026, 8, 20));

        await using var context = _fixture.CreateContext();
        await new IndentPoTrackingRepository(context).AddConversionAsync(conversion, CancellationToken.None);

        return conversion;
    }

    private async Task<JobEnqueueResult> EnqueueExecutionAsync(IndentPoConversion conversion)
    {
        await using var context = _fixture.CreateContext();
        var repository = CreateRepository(context);

        var job = Job.Create(
            WorkflowNames.IndentToPurchaseOrder,
            conversion.DocumentType,
            conversion.IndentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AutomationMode.Full,
            Now);

        job.LinkToConversion(conversion.Id, runId: null);
        job.PlanSteps(IndentPoStages.InOrder.Select(stage => new PlannedOperation(stage, stage)));

        // Inserted already Running, as the tracker does: a conversion is executed inline and must
        // never be offered to the dispatcher.
        job.Claim(Now);

        return await repository.EnqueueAsync(job, CancellationToken.None);
    }

    private async Task<JobEnqueueResult> EnqueueDocumentJobAsync(string documentId)
    {
        await using var context = _fixture.CreateContext();
        var repository = CreateRepository(context);

        var job = Job.Create("SJO", "SalesOrder", documentId, AutomationMode.Full, Now);
        job.PlanSteps([new PlannedOperation("SJO", "DeAllocation")]);

        return await repository.EnqueueAsync(job, CancellationToken.None);
    }

    private async Task AddOutcomeAsync(IndentPoOutcome outcome)
    {
        await using var context = _fixture.CreateContext();
        await new IndentPoTrackingRepository(context).AddOutcomesAsync([outcome], CancellationToken.None);
    }

    private async Task CompleteAsync(Guid jobId, DateTimeOffset? completedAtUtc = null)
    {
        await using var context = _fixture.CreateContext();
        var repository = CreateRepository(context);

        var job = await repository.GetAsync(jobId, CancellationToken.None);

        // A conversion job is inserted Running; a document job waits for the dispatcher to claim
        // it. Either way it has to be Running before it can complete.
        if (job!.Status is JobStatus.Pending)
        {
            job.Claim(Now);
        }

        foreach (var step in job.Steps)
        {
            step.Skip(Now, "Not needed by this test.");
        }

        job.Complete(completedAtUtc ?? Now);
        await repository.SaveAsync(job, CancellationToken.None);
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
