using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// The agent's real unit of work: one repeating cycle that starts after OAF creation and loops
/// over every site. Covers the two things that are easy to get wrong â€” the site loop resuming at
/// the failed site, and the delivery-date ordering that <em>is</em> the sequence.
/// </summary>
public sealed class AutoShopCycleTests
{
    private static readonly DateTimeOffset Day = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static EngineHarness NewHarness(int siteCount = 3, int maxRetry = 3)
    {
        var harness = new EngineHarness(AutoShopCycleWorkflow.Create(), maxRetry);

        for (var i = 1; i <= siteCount; i++)
        {
            var siteId = $"SITE-{i:D2}";
            harness.Erp.Sites.Add(new ErpSite(siteId, $"Plant {i}"));
            harness.Erp.SjoBySite[siteId] = [SjoSequenceRow.Create($"SJO-{i}-A", Day.AddDays(i))];
            harness.Erp.AutoShopBySite[siteId] = [SjoSequenceRow.Create($"SJO-{i}-A", Day.AddDays(i))];
        }

        return harness;
    }

    [Fact]
    public async Task A_cycle_runs_oaf_to_sjo_then_every_site_in_order()
    {
        var harness = NewHarness();
        harness.Erp.OafAwaitingSjo.Add(new OafAwaitingSjo("OAF-1", "SITE-01"));

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);

        // Sequencing for every site first, then AutoShop for every site â€” the order described.
        job.Steps.Select(step => step.Stage).Should().Equal(
            "OafToSjo",
            "Discovery",
            "SjoSequence", "SjoSequence", "SjoSequence",
            "AutoShop", "AutoShop", "AutoShop");
    }

    [Fact]
    public async Task Site_discovery_expands_the_plan_to_one_step_per_site()
    {
        var harness = NewHarness(siteCount: 4);

        var job = await harness.RunAsync(harness.NewJob());

        // Two static steps plus one per site per stage â€” each individually checkpointed.
        job.Steps.Should().HaveCount(2 + (4 * 2));
        job.Steps.Where(step => step.Stage == "SjoSequence").Select(step => step.Target)
            .Should().Equal("SITE-01", "SITE-02", "SITE-03", "SITE-04");
    }

    [Fact]
    public async Task A_failure_at_one_site_resumes_at_that_site_and_does_not_resubmit_earlier_ones()
    {
        // The reason each site is its own step.
        var harness = NewHarness(siteCount: 5);
        harness.Erp.FailSite("SITE-03", new ErpTransientException("ERP timeout", "504"));

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Pending);

        var failedStep = job.Steps.First(step => !step.IsTerminal);
        failedStep.Target.Should().Be("SITE-03");

        // Sites 1 and 2 submitted once and are done; nothing beyond site 3 ran.
        harness.Erp.Submissions.Select(s => s.SiteId).Should().Equal("SITE-01", "SITE-02");

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.RunAsync(job);

        job.Status.Should().Be(JobStatus.Completed);

        // Sites 1 and 2 were not resubmitted on resume â€” the whole point of per-site checkpoints.
        var sequenceSubmissions = harness.Erp.Submissions
            .Where(s => s.Endpoint == "sjo-sequence")
            .Select(s => s.SiteId)
            .ToList();

        sequenceSubmissions.Should().Equal("SITE-01", "SITE-02", "SITE-03", "SITE-04", "SITE-05");
        sequenceSubmissions.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_site_with_no_data_is_skipped_rather_than_failed()
    {
        var harness = NewHarness();
        harness.Erp.SjoBySite["SITE-02"] = [];

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);

        var skipped = job.Steps.Single(step => step.Stage == "SjoSequence" && step.Target == "SITE-02");
        skipped.Status.Should().Be(StepStatus.Skipped);
        skipped.Remarks.Should().Contain("no SJO to sequence");

        harness.Erp.Submissions.Where(s => s.Endpoint == "sjo-sequence")
            .Select(s => s.SiteId).Should().Equal("SITE-01", "SITE-03");
    }

    [Fact]
    public async Task Rows_are_submitted_in_delivery_date_order()
    {
        var harness = NewHarness(siteCount: 1);

        // Deliberately out of order, and one row with no delivery date at all.
        harness.Erp.SjoBySite["SITE-01"] =
        [
            SjoSequenceRow.Create("SJO-LATE", Day.AddDays(10)),
            SjoSequenceRow.Create("SJO-NONE", null),
            SjoSequenceRow.Create("SJO-EARLY", Day.AddDays(1)),
            SjoSequenceRow.Create("SJO-MID", Day.AddDays(5)),
        ];

        await harness.RunAsync(harness.NewJob());

        var submitted = harness.Erp.Submissions
            .Single(s => s.Endpoint == "sjo-sequence")
            .Rows.Select(row => row.SjoNumber);

        // Ascending by delivery date; an undated row sorts last so it cannot jump the queue.
        submitted.Should().Equal("SJO-EARLY", "SJO-MID", "SJO-LATE", "SJO-NONE");
    }

    [Fact]
    public async Task A_quiet_cycle_with_no_pending_oaf_still_completes()
    {
        // Nothing to create is the normal quiet case, not a failure.
        var harness = NewHarness();

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        job.Steps[0].Status.Should().Be(StepStatus.Skipped);
        job.Steps[0].Remarks.Should().Contain("No OAF is awaiting an SJO");
    }

    [Fact]
    public async Task A_cycle_with_no_sites_completes_without_looping()
    {
        var harness = new EngineHarness(AutoShopCycleWorkflow.Create());

        var job = await harness.RunAsync(harness.NewJob());

        job.Status.Should().Be(JobStatus.Completed);
        job.Steps.Should().HaveCount(2);
        job.Steps[1].Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task Creating_sjo_from_oaf_adopts_what_a_previous_attempt_already_made()
    {
        var harness = NewHarness();
        harness.Erp.OafAwaitingSjo.Add(new OafAwaitingSjo("OAF-1", "SITE-01"));
        harness.Erp.OafAwaitingSjo.Add(new OafAwaitingSjo("OAF-2", "SITE-01"));

        await harness.RunAsync(harness.NewJob());
        var afterFirstCycle = harness.Erp.CreateCountFor("sjo");

        // A second cycle sees the same OAFs still listed â€” the ERP has not refreshed them yet.
        var secondHarness = NewHarness();
        secondHarness.Erp.OafAwaitingSjo.Add(new OafAwaitingSjo("OAF-1", "SITE-01"));
        await secondHarness.RunAsync(secondHarness.NewJob("CYCLE-2"));

        afterFirstCycle.Should().Be(2);

        // Query-before-create means a repeated OAF does not produce a second SJO.
        await secondHarness.RunAsync(secondHarness.NewJob("CYCLE-3"));
        secondHarness.Erp.CreateCountFor("sjo").Should().Be(1);
    }

    [Fact]
    public async Task The_timeline_names_each_site_step()
    {
        var harness = NewHarness(siteCount: 2);

        var job = await harness.RunAsync(harness.NewJob());

        // What the ERP UI renders: "SequenceSite / SITE-01" rather than three identical rows.
        job.Steps.Where(step => step.Target is not null)
            .Select(step => step.DisplayName)
            .Should().Contain("SequenceSite / SITE-01")
            .And.Contain("AutoShopSite / SITE-02");
    }

    [Fact]
    public async Task Every_field_the_erp_sent_comes_back_untouched_apart_from_the_flag()
    {
        // The point of the workflow: the agent orders the rows and sets one flag. Anything else it
        // received must reach the ERP again exactly as sent, including properties the agent has
        // never heard of and nested structures it does not model.
        var harness = NewHarness(siteCount: 1);

        harness.Erp.SjoBySite["SITE-01"] =
        [
            SjoSequenceRow.FromJson(new JsonObject
            {
                ["sjoNumber"] = "SJO-1",
                ["deliveryDate"] = "2026-08-02T00:00:00.0000000+00:00",
                ["customerName"] = "Acme Engineering",
                ["revision"] = 4,
                ["lines"] = new JsonArray(new JsonObject { ["itemCode"] = "X-1", ["qty"] = 7 }),
            }),
        ];

        await harness.RunAsync(harness.NewJob());

        var submitted = harness.Erp.Submissions
            .Single(s => s.Endpoint == "sjo-sequence")
            .Rows.Single()
            .Payload;

        submitted["customerName"]!.GetValue<string>().Should().Be("Acme Engineering");
        submitted["revision"]!.GetValue<int>().Should().Be(4);
        submitted["lines"]![0]!["itemCode"]!.GetValue<string>().Should().Be("X-1");
        submitted["lines"]![0]!["qty"]!.GetValue<int>().Should().Be(7);

        // The one deliberate mutation.
        submitted["isSelected"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Every_submitted_row_carries_the_flag()
    {
        var harness = NewHarness(siteCount: 1);

        harness.Erp.SjoBySite["SITE-01"] =
        [
            SjoSequenceRow.Create("SJO-LATE", Day.AddDays(10)),
            SjoSequenceRow.Create("SJO-EARLY", Day.AddDays(1)),
        ];

        await harness.RunAsync(harness.NewJob());

        harness.Erp.Submissions
            .Single(s => s.Endpoint == "sjo-sequence")
            .Rows.Should().OnlyContain(row => row.Payload["isSelected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Autoshop_runs_for_every_site_after_sequencing()
    {
        var harness = NewHarness(siteCount: 3);

        await harness.RunAsync(harness.NewJob());

        var order = harness.Erp.Submissions.Select(s => $"{s.Endpoint}:{s.SiteId}");

        order.Should().Equal(
            "sjo-sequence:SITE-01", "sjo-sequence:SITE-02", "sjo-sequence:SITE-03",
            "autoshop:SITE-01", "autoshop:SITE-02", "autoshop:SITE-03");
    }
}
