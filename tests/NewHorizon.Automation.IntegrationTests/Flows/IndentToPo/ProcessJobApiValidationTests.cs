using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.IntegrationTests.Flows.IndentToPo;

/// <summary>
/// The process-tracking API under everything a caller can actually do to it: bad input, bad ids,
/// duplicates, races, retries and an ERP that refuses.
/// </summary>
/// <remarks>
/// The conversion is stood in for, and the double calls the tracker where the real service calls
/// it. What is under test is the API and the recording — whether an indent is convertible is the
/// ERP's answer, and here it is scripted so every branch can be reached on demand.
/// </remarks>
[Collection(ProcessJobApiCollection.Name)]
public sealed class ProcessJobApiValidationTests
{
    private readonly ProcessJobApiFixture _fixture;

    public ProcessJobApiValidationTests(ProcessJobApiFixture fixture)
    {
        _fixture = fixture;
        _fixture.Conversion.Reset();
    }

    // ---- authorisation ------------------------------------------------------

    [SkippableTheory]
    [InlineData("GET", "/api/process-jobs")]
    [InlineData("GET", "/api/process-jobs/summary")]
    [InlineData("GET", "/api/process-jobs/daily-summary")]
    [InlineData("GET", "/api/process-jobs/8a1d4a02-0000-0000-0000-000000000000")]
    [InlineData("GET", "/api/process-jobs/indent/1")]
    [InlineData("GET", "/api/process-jobs/runs")]
    [InlineData("GET", "/api/process-jobs/runs/8a1d4a02-0000-0000-0000-000000000000")]
    [InlineData("POST", "/api/process-jobs")]
    [InlineData("POST", "/api/process-jobs/8a1d4a02-0000-0000-0000-000000000000/retry")]
    public async Task Every_route_refuses_a_call_with_no_api_key(string method, string route)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Anonymous();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task A_wrong_api_key_is_refused_like_a_missing_one()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Anonymous();
        client.DefaultRequestHeaders.Add(ProcessJobApiFixture.HeaderName, "not-the-key");

        var response = await client.GetAsync("/api/process-jobs/runs");

        // Same answer either way: which of the two it was is not the caller's business.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- POST /api/process-jobs: happy paths --------------------------------

    [SkippableFact]
    public async Task A_conversion_is_recorded_end_to_end()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();

        var run = await StartAsync(new
        {
            indentTypes = new[] { "regular" },
            trigger = "chatbot",
            triggeredBy = "assistant",
            triggerReference = "chat-4417",
        });

        run.Run.Status.Should().Be("Completed");
        run.Run.Trigger.Should().Be("Chatbot");
        run.Run.IndentTypes.Should().Be("Regular");
        run.Run.IndentsExamined.Should().Be(1);
        run.Run.PurchaseOrdersCreated.Should().Be(1);

        var execution = run.Executions.Should().ContainSingle().Subject;
        execution.Status.Should().Be("Completed");
        execution.Mode.Should().Be("Full");
        execution.Indent.IndentId.Should().Be(indentId);
        execution.AttemptNo.Should().Be(1);
        execution.DurationMs.Should().NotBeNull();
        execution.Outcomes.Should().HaveCount(2);
    }

    [SkippableTheory]
    [InlineData("api", "Api")]
    [InlineData("USERPROMPT", "UserPrompt")]
    [InlineData("Chatbot", "Chatbot")]
    [InlineData("timer", "Timer")]
    [InlineData("erppush", "ErpPush")]
    [InlineData("reconcile", "Reconcile")]
    [InlineData("manual", "Manual")]
    public async Task Every_trigger_is_accepted_whatever_its_casing(string sent, string recorded)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        var run = await StartAsync(new { trigger = sent });

        run.Run.Trigger.Should().Be(recorded);
    }

    [SkippableFact]
    public async Task An_absent_trigger_is_recorded_as_the_api_that_was_called()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        var run = await StartAsync(new { indentTypes = new[] { "regular" } });

        // Something called the API, so that is what is recorded. Guessing "Timer" or "Chatbot"
        // from an unlabelled call would put a lie in the history.
        run.Run.Trigger.Should().Be("Api");
    }

    [SkippableFact]
    public async Task A_whitespace_trigger_is_treated_as_absent_rather_than_rejected()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        var run = await StartAsync(new { trigger = "   " });

        run.Run.Trigger.Should().Be("Api");
    }

    [SkippableTheory]
    [InlineData("{}")]
    [InlineData("""{ "indentTypes": [] }""")]
    [InlineData("""{ "indentTypes": null }""")]
    public async Task No_selection_means_every_type_and_says_so(string body)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        var response = await PostRawAsync("/api/process-jobs", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await Read<RunDetail>(response);

        // An absent filter is not a filter, and the run records what it was therefore allowed to
        // convert rather than repeating the blank it was sent.
        run.Run.IndentTypes.Should().Be("Regular, Capital, Service");
    }

    [SkippableFact]
    public async Task An_empty_request_body_is_accepted()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        using var client = _fixture.Authenticated();
        var response = await client.PostAsync("/api/process-jobs", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Unexpected_fields_are_ignored_rather_than_refused()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        var response = await PostRawAsync(
            "/api/process-jobs",
            """{ "trigger": "manual", "somethingElse": 42, "nested": { "a": 1 } }""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableFact]
    public async Task Partial_mode_is_captured_from_configuration_at_the_start_of_the_run()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        await _fixture.SetModeAsync("Partial");

        try
        {
            _fixture.Conversion.UseNewIndent();

            var run = await StartAsync(new { trigger = "manual" });

            run.Run.Mode.Should().Be("Partial");
            run.Executions.Should().ContainSingle().Which.Mode.Should().Be("Partial");
        }
        finally
        {
            await _fixture.SetModeAsync("Full");
        }
    }

    // ---- the conversion trigger, now recorded under a run -------------------

    /// <remarks>
    /// <c>/api/automation/indent-to-po</c> and its <c>/convert</c> alias wrote conversions, jobs,
    /// steps and outcomes but never a run, so <c>trigger</c>, <c>triggeredBy</c> and <c>runId</c>
    /// were null on every row they produced — 193 of them on the live installation — and
    /// <c>?trigger=</c> could only ever answer an empty page.
    /// </remarks>
    [SkippableTheory]
    [InlineData("/api/automation/indent-to-po")]
    [InlineData("/api/automation/indent-to-po/convert")]
    public async Task A_conversion_through_the_trigger_records_its_run(string route)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();

        await ConvertAsync(route, new { indentTypes = new[] { "regular" } });

        var row = await FindRowAsync(indentId, "pageSize=200");

        row.Trigger.Should().Be("Api");
        row.RunId.Should().NotBeNull();
    }

    [SkippableFact]
    public async Task A_recorded_conversion_names_no_user_because_this_endpoint_has_none()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();

        await ConvertAsync("/api/automation/indent-to-po", new { indentTypes = new[] { "regular" } });

        var row = await FindRowAsync(indentId, "pageSize=200");

        // Deliberate, and asserted so it cannot be quietly filled in later. This endpoint
        // authenticates with a shared API key: there is no authentication middleware, no
        // HttpContext.User and no claim to read. The ERP service account the agent signs in as is
        // the agent, not whoever called, so recording it would put a name on every row that
        // answers a question nobody asked. Callers that know who they are say so — /api/process-jobs
        // takes triggeredBy in the body, and cancel requires cancelledBy.
        row.TriggeredBy.Should().BeNull();
    }

    [SkippableFact]
    public async Task A_recorded_conversion_is_findable_by_its_trigger()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();

        await ConvertAsync("/api/automation/indent-to-po", new { indentTypes = new[] { "regular" } });

        // The filter was correct all along and unusable, because nothing ever carried a trigger.
        var page = await GridAsync("trigger=Api&pageSize=200");

        page.Items.Should().Contain(row => row.IndentId == indentId);
    }

    [SkippableFact]
    public async Task A_conversion_opens_exactly_one_run()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await _fixture.CountRunsAsync();

        _fixture.Conversion.UseNewIndent();
        await ConvertAsync("/api/automation/indent-to-po", new { indentTypes = new[] { "regular" } });

        // One invocation is one run — the existing lifecycle reused, not a second one added.
        (await _fixture.CountRunsAsync()).Should().Be(before + 1);
    }

    [SkippableFact]
    public async Task A_dry_run_through_the_trigger_opens_no_run()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await _fixture.CountRunsAsync();

        _fixture.Conversion.UseNewIndent();

        await ConvertAsync(
            "/api/automation/indent-to-po",
            new { indentTypes = new[] { "regular" }, dryRun = true });

        // A dry run creates nothing, so it has nothing to have a history of. Only the run is
        // asserted here: the scripted conversion records an execution whatever it is asked, where
        // the real pipeline skips tracking for a dry run outright.
        (await _fixture.CountRunsAsync()).Should().Be(before);
    }

    [SkippableTheory]
    [InlineData("Partial")]
    [InlineData("Full")]
    public async Task The_mode_on_a_recorded_conversion_is_the_configured_one(string mode)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        await _fixture.SetModeAsync(mode);

        try
        {
            var indentId = _fixture.Conversion.UseNewIndent();

            await ConvertAsync("/api/automation/indent-to-po", new { indentTypes = new[] { "regular" } });

            var row = await FindRowAsync(indentId, "pageSize=200");

            // The Partial case is the one that matters: a hardcoded or defaulted "Full" can pass
            // the Full case for ever and can never pass this one.
            row.Mode.Should().Be(mode);
        }
        finally
        {
            await _fixture.SetModeAsync("Full");
        }
    }

    [SkippableFact]
    public async Task The_mode_is_read_from_configuration_even_when_no_run_is_open()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        await _fixture.SetModeAsync("Partial");

        try
        {
            var indentId = _fixture.Conversion.UseNewIndent();

            // Straight at the tracker, with StartRunAsync deliberately never called — the path
            // that used to read `_run?.Mode ?? AutomationMode.Full` and so stamped Full on every
            // job the conversion endpoints ever recorded. Every endpoint opens a run now, which
            // makes this unreachable through HTTP; it is asserted here so the assumption cannot
            // come back as a trap for the next entry point.
            using (var scope = _fixture.Services.CreateScope())
            {
                var tracker = scope.ServiceProvider.GetRequiredService<IIndentPoTracker>();

                await tracker.StartExecutionAsync(
                    new TrackedIndent(indentId, IndentKind.Regular, $"26-27/IN/SP1/{indentId}", 1),
                    CancellationToken.None);

                await tracker.CompleteExecutionAsync([], CancellationToken.None);
            }

            var row = await FindRowAsync(indentId, "pageSize=200");

            row.Mode.Should().Be("Partial");
            row.RunId.Should().BeNull();
        }
        finally
        {
            await _fixture.SetModeAsync("Full");
        }
    }

    // ---- POST /api/process-jobs: invalid input ------------------------------

    [SkippableTheory]
    [InlineData("banana", "banana")]
    [InlineData("Regular,banana", "banana")]
    // An ordinal that silently means Regular is a worse answer than a refusal that names it.
    [InlineData("0", "0")]
    public async Task An_unknown_indent_type_is_refused_and_named(string indentType, string named)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await _fixture.CountRunsAsync();

        var response = await PostAsync(new { indentTypes = new[] { indentType } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(named);

        // The refusal names the accepted values, and discloses nothing about the server.
        body.Should().Contain("Regular, Capital, Service").And.NotContain("StackTrace");

        // Refused before the run row is opened, so an invalid request leaves no history behind.
        (await _fixture.CountRunsAsync()).Should().Be(before);
    }

    [SkippableFact]
    public async Task An_unknown_trigger_is_refused_and_named()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await _fixture.CountRunsAsync();

        var response = await PostAsync(new { trigger = "cron" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("cron");

        // Refused before the run row is opened, so a bad trigger leaves no history behind.
        (await _fixture.CountRunsAsync()).Should().Be(before);
    }

    [SkippableTheory]
    [InlineData("{ not json")]
    [InlineData("""{ "trigger": }""")]
    [InlineData("[]")]
    public async Task Malformed_json_is_a_bad_request_not_a_crash(string body)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var response = await PostRawAsync("/api/process-jobs", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableTheory]
    [InlineData("""{ "indentId": "not-a-number" }""")]
    [InlineData("""{ "maxIndents": "lots" }""")]
    [InlineData("""{ "indentTypes": "regular" }""")]
    [InlineData("""{ "sites": [ "one" ] }""")]
    public async Task A_field_of_the_wrong_type_is_a_bad_request(string body)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var response = await PostRawAsync("/api/process-jobs", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public async Task An_impossible_indent_id_is_answered_not_recorded(long indentId)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        // The ERP is the authority on whether an indent exists; a nonsense id simply finds nothing.
        _fixture.Conversion.FindNothing();

        var response = await PostAsync(new { indentId, indentTypes = new[] { "regular" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await Read<RunDetail>(response);
        run.Executions.Should().BeEmpty();
        run.Run.PurchaseOrdersCreated.Should().Be(0);
    }

    // ---- GET /api/process-jobs: the conversion grid -------------------------

    [SkippableFact]
    public async Task The_grid_carries_every_column_the_dashboard_asks_for()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        var started = await StartAsync(new
        {
            indentTypes = new[] { "regular" },
            trigger = "chatbot",
            triggeredBy = "assistant",
        });

        var row = await FindRowAsync(indentId, "pageSize=200");

        // JOB, DOCUMENT, WORKFLOW, TRIGGER, MODE, STAGE, STATUS, DURATION — the eight asked for,
        // in one row, from one call. Nothing else returns all of them across conversions.
        row.JobId.Should().Be(started.Executions[0].JobId);
        row.Document.Should().Be("26-27/IN/NF1/000012");
        row.Workflow.Should().Be("IndentToPurchaseOrder");
        row.Trigger.Should().Be("Chatbot");
        row.TriggeredBy.Should().Be("assistant");
        row.Mode.Should().Be("Full");
        row.Stage.Should().Be("CreatePurchaseOrder");
        row.Status.Should().Be("Completed");
        row.DurationMs.Should().NotBeNull();

        // Drill-down keys, so a grid line can open the run or the indent history.
        row.RunId.Should().Be(started.Run.RunId);
        row.IndentId.Should().Be(indentId);
        row.IndentType.Should().Be("Regular");
        row.SiteId.Should().Be(1);

        // COMPANY — configuration, not a column, and the same on every row of this installation.
        row.Company.Should().Be(_fixture.ConfiguredCompanyId);
    }

    [SkippableFact]
    public async Task The_grid_comes_back_newest_first()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var older = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var newer = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var page = await GridAsync("pageSize=200");

        var olderAt = page.Items.ToList().FindIndex(row => row.IndentId == older);
        var newerAt = page.Items.ToList().FindIndex(row => row.IndentId == newer);

        olderAt.Should().BeGreaterThan(-1);
        newerAt.Should().BeGreaterThan(-1);

        // ORDER BY j.CreatedAtUtc DESC, applied by SQL Server before paging — not a sort over
        // whatever the first page happened to contain.
        newerAt.Should().BeLessThan(olderAt);
    }

    [SkippableFact]
    public async Task No_filters_returns_every_conversion()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var page = await GridAsync("pageSize=200");

        page.Items.Should().Contain(row => row.IndentId == indentId);
        page.TotalCount.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task Search_matches_part_of_the_indent_number()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        // The double's indent number is 26-27/IN/NF1/000012 — a fragment must find it, because
        // nobody types a whole document number into a search box.
        var row = await FindRowAsync(indentId, "search=NF1/0000&pageSize=200");

        row.Document.Should().Contain("NF1/0000");
    }

    [SkippableFact]
    public async Task Search_matches_the_purchase_order_number_the_conversion_produced()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        var run = await StartAsync(new { indentTypes = new[] { "regular" } });

        var poNumber = run.Executions[0].Outcomes
            .Single(outcome => outcome.Outcome == "Created")
            .PoNumber!;

        // "Which conversion produced PO …" is the question support asks most, and the number is
        // on the outcome rather than the grid row — so the search reaches through to it.
        var row = await FindRowAsync(indentId, $"search={Uri.EscapeDataString(poNumber)}&pageSize=200");

        row.IndentId.Should().Be(indentId);
    }

    [SkippableFact]
    public async Task Search_matches_who_triggered_it()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" }, triggeredBy = "priya.desai" });

        var row = await FindRowAsync(indentId, "search=priya&pageSize=200");

        row.TriggeredBy.Should().Be("priya.desai");
    }

    [SkippableFact]
    public async Task Search_that_matches_nothing_returns_an_empty_page_not_an_error()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var page = await GridAsync("search=nothing-matches-this-zzz&pageSize=200");

        // No rows is a legitimate answer to a search. Only an invalid *filter value* is a refusal.
        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    [SkippableFact]
    public async Task The_workflow_filter_narrows_and_excludes()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var matched = await FindRowAsync(indentId, "workflow=IndentToPurchaseOrder&pageSize=200");
        matched.Workflow.Should().Be("IndentToPurchaseOrder");

        // A real workflow that this grid never contains excludes everything.
        var other = await GridAsync("workflow=AutoShopCycle&pageSize=200");
        other.Items.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task An_unknown_workflow_is_refused_and_named()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        // "IndentToPO" is the plausible guess. Refusing it and naming the real value is more use
        // than an empty grid the caller reads as "no conversions have run".
        var response = await client.GetAsync("/api/process-jobs?workflow=IndentToPO");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("IndentToPO").And.Contain("IndentToPurchaseOrder");
    }

    [SkippableFact]
    public async Task The_company_filter_matches_the_configured_company_and_excludes_any_other()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        // Asking for this installation's company is no filter at all.
        var mine = await FindRowAsync(indentId, $"company={_fixture.ConfiguredCompanyId}&pageSize=200");
        mine.Company.Should().Be(_fixture.ConfiguredCompanyId);

        // Asking for anyone else's is a question whose honest answer is empty — there is one
        // company on this installation and it is not that one.
        var theirs = await GridAsync("company=ABC&pageSize=200");
        theirs.Items.Should().BeEmpty();
        theirs.TotalCount.Should().Be(0);
    }

    [SkippableFact]
    public async Task All_four_filters_together_narrow_to_the_same_row()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" }, triggeredBy = "priya.desai" });

        var row = await FindRowAsync(
            indentId,
            $"search=NF1&workflow=IndentToPurchaseOrder&status=Completed"
                + $"&company={_fixture.ConfiguredCompanyId}&pageSize=200");

        row.Status.Should().Be("Completed");
        row.Workflow.Should().Be("IndentToPurchaseOrder");

        // One mismatching filter in the set is enough to exclude it.
        var excluded = await GridAsync(
            $"search=NF1&workflow=IndentToPurchaseOrder&status=Failed"
                + $"&company={_fixture.ConfiguredCompanyId}&pageSize=200");

        excluded.Items.Should().NotContain(candidate => candidate.IndentId == indentId);
    }

    [SkippableFact]
    public async Task A_job_that_is_not_a_conversion_never_appears_in_the_grid()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var cycleJobId = await _fixture.AddUnrelatedJobAsync();

        var page = await GridAsync("pageSize=200");

        // The inner join to IndentPoConversion is what makes this a conversion grid rather than a
        // job list. A cycle has no indent and must not be rendered as though it had one.
        page.Items.Should().NotContain(row => row.JobId == cycleJobId);
    }

    [SkippableFact]
    public async Task The_grid_narrows_by_status_trigger_stage_and_indent_type()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" }, trigger = "reconcile" });

        foreach (var filter in new[]
                 {
                     "status=Completed",
                     "trigger=Reconcile",
                     "stage=CreatePurchaseOrder",
                     "indentType=Regular",
                 })
        {
            var match = await FindRowAsync(indentId, $"{filter}&pageSize=200");

            match.Should().NotBeNull($"the conversion should survive ?{filter}");
        }

        // And a filter it does not match excludes it, rather than being ignored.
        var excluded = await GridAsync("status=Failed&pageSize=200");

        excluded.Items.Should().NotContain(row => row.IndentId == indentId);
    }

    [SkippableTheory]
    [InlineData("status=Sideways", "Sideways")]
    [InlineData("trigger=cron", "cron")]
    [InlineData("stage=Whenever", "Whenever")]
    [InlineData("indentType=banana", "banana")]
    public async Task An_unknown_grid_filter_is_refused_and_named(string query, string offender)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync($"/api/process-jobs?{query}");

        // A typo must be a refusal, not an empty grid — an empty grid reads as "nothing happened".
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(offender);
    }

    [SkippableTheory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, -5, 1, 1)]
    [InlineData(1, 5000, 1, 200)]
    public async Task Grid_paging_is_clamped_rather_than_trusted(
        int page,
        int pageSize,
        int expectedPage,
        int expectedPageSize)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var result = await GridAsync($"page={page}&pageSize={pageSize}");

        result.Page.Should().Be(expectedPage);
        result.PageSize.Should().Be(expectedPageSize);
    }

    // ---- GET /api/process-jobs/summary --------------------------------------

    [SkippableFact]
    public async Task A_created_purchase_order_counts_as_a_success()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        var marker = $"summary-{Guid.NewGuid():N}";
        var run = await StartAsync(new { indentTypes = new[] { "regular" }, triggeredBy = marker });

        var summary = await SummaryAsync($"search={marker}");

        summary.TotalJobs.Should().Be(1);
        summary.CountsByStatus.Should().ContainKey("Completed").WhoseValue.Should().Be(1);
        summary.SuccessCount.Should().Be(1);
        summary.SuccessRate.Should().Be(1.0);
        summary.AverageDurationMs.Should().NotBeNull();

        // The grid line behind the count carries the PO number a dashboard would show, not just the
        // fact that something succeeded.
        var createdPoNumber = run.Executions.Single().Outcomes
            .Single(outcome => outcome.Outcome == "Created").PoNumber;

        var row = await FindRowAsync(indentId, "pageSize=200");
        row.PoNumber.Should().Be(createdPoNumber);
        row.FailureReason.Should().BeNull();
    }

    [SkippableFact]
    public async Task An_indent_that_orders_nothing_is_completed_but_not_counted_as_a_success()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.OrderNothing("Every item line on this indent is already closed.");
        var marker = $"summary-{Guid.NewGuid():N}";
        await StartAsync(new { indentTypes = new[] { "regular" }, triggeredBy = marker });

        var summary = await SummaryAsync($"search={marker}");

        summary.TotalJobs.Should().Be(1);
        summary.CountsByStatus.Should().ContainKey("Completed").WhoseValue.Should().Be(1);

        // The distinction the status count alone cannot make: the job did its work and finished
        // Completed, but nothing was actually ordered, so it is not a success.
        summary.SuccessCount.Should().Be(0);
        summary.SuccessRate.Should().Be(0.0);

        // The grid line says why, rather than just "not a success" — the same reason the outcome
        // recorded, so a viewer never has to open the execution detail to find out.
        var row = await FindRowAsync(indentId, "pageSize=200");
        row.PoNumber.Should().BeNull();
        row.FailureReason.Should().Be("Every item line on this indent is already closed.");
    }

    [SkippableFact]
    public async Task A_failed_execution_is_counted_by_status_but_not_as_a_success()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.FailWith("Document Control has no 26-27 row for site 1.");
        var marker = $"summary-{Guid.NewGuid():N}";

        using (var client = _fixture.Authenticated())
        {
            await client.PostAsJsonAsync(
                "/api/process-jobs", new { indentTypes = new[] { "regular" }, triggeredBy = marker });
        }

        var summary = await SummaryAsync($"search={marker}");

        summary.TotalJobs.Should().Be(1);
        summary.CountsByStatus.Should().ContainKey("Failed").WhoseValue.Should().Be(1);
        summary.SuccessCount.Should().Be(0);

        // The technical failure's layman message, the same text an operator reading the error list
        // already sees — the grid should not need a second call to explain a failed row.
        var row = await FindRowAsync(indentId, "pageSize=200");
        row.PoNumber.Should().BeNull();
        row.FailureReason.Should().Be("Document Control has no 26-27 row for site 1.");
    }

    [SkippableFact]
    public async Task A_trigger_that_finds_no_eligible_indent_is_an_attempt_but_not_a_conversion_process()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await SummaryAsync(string.Empty);

        _fixture.Conversion.FindNothing();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var after = await SummaryAsync(string.Empty);

        // The trigger really happened and opened a run, so it must be visible as an attempt —
        after.TotalTriggerAttempts.Should().Be(before.TotalTriggerAttempts + 1);
        after.TriggerAttemptsWithoutEligibleIndent.Should().Be(before.TriggerAttemptsWithoutEligibleIndent + 1);

        // — but nothing eligible and authorised was ever found, so no conversion process started,
        // and the dashboard's "Total Processes" must not move.
        after.TotalJobs.Should().Be(before.TotalJobs);
    }

    [SkippableFact]
    public async Task A_conversion_process_that_starts_counts_as_both_an_attempt_and_a_process()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await SummaryAsync(string.Empty);

        _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var after = await SummaryAsync(string.Empty);

        after.TotalTriggerAttempts.Should().Be(before.TotalTriggerAttempts + 1);
        after.TriggerAttemptsWithoutEligibleIndent.Should().Be(before.TriggerAttemptsWithoutEligibleIndent);
        after.TotalJobs.Should().Be(before.TotalJobs + 1);
    }

    [SkippableFact]
    public async Task Summary_takes_the_same_filters_the_grid_does()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        var marker = $"summary-{Guid.NewGuid():N}";
        await StartAsync(new
        {
            indentTypes = new[] { "regular" },
            trigger = "reconcile",
            triggeredBy = marker,
        });

        var matched = await SummaryAsync($"search={marker}&trigger=Reconcile");
        matched.TotalJobs.Should().Be(1);

        // The same query narrowed to a trigger this job was not started under, the same way the
        // grid excludes it — one mismatching filter is enough.
        var excluded = await SummaryAsync($"search={marker}&trigger=Timer");
        excluded.TotalJobs.Should().Be(0);
    }

    [SkippableFact]
    public async Task An_unknown_summary_filter_is_refused_and_named()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync("/api/process-jobs/summary?status=Sideways");

        // The same refusal ?status= gets on the grid — a typo must not read as "nothing matched".
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Sideways");
    }

    // ---- GET /api/process-jobs/daily-summary --------------------------------

    [SkippableFact]
    public async Task A_created_purchase_order_is_counted_on_todays_daily_stat()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await TodaysDailyStatAsync();

        _fixture.Conversion.UseNewIndent();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var after = await TodaysDailyStatAsync();

        after.PurchaseOrdersCreated.Should().Be(before.PurchaseOrdersCreated + 1);
        after.IndentsConverted.Should().Be(before.IndentsConverted + 1);
        after.IndentsFailed.Should().Be(before.IndentsFailed);
    }

    [SkippableFact]
    public async Task A_failed_execution_is_counted_on_todays_daily_stat_but_not_as_converted()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await TodaysDailyStatAsync();

        _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.FailWith("Document Control has no 26-27 row for site 1.");

        using (var client = _fixture.Authenticated())
        {
            await client.PostAsJsonAsync("/api/process-jobs", new { indentTypes = new[] { "regular" } });
        }

        var after = await TodaysDailyStatAsync();

        after.IndentsFailed.Should().Be(before.IndentsFailed + 1);
        after.IndentsConverted.Should().Be(before.IndentsConverted);
        after.PurchaseOrdersCreated.Should().Be(before.PurchaseOrdersCreated);
    }

    [SkippableFact]
    public async Task An_indent_that_orders_nothing_is_not_counted_on_either_daily_series()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await TodaysDailyStatAsync();

        _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.OrderNothing("Every item line on this indent is already closed.");
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var after = await TodaysDailyStatAsync();

        // It completed — it just did not convert anything and did not fail, so neither chart series
        // may count it. This is the same "unauthorised or merely attempted" exclusion the dashboard
        // requires: only an actual PO creation counts as Converted, and only a real error as Failed.
        after.IndentsConverted.Should().Be(before.IndentsConverted);
        after.IndentsFailed.Should().Be(before.IndentsFailed);
        after.PurchaseOrdersCreated.Should().Be(before.PurchaseOrdersCreated);
    }

    [SkippableFact]
    public async Task A_trigger_that_finds_no_eligible_indent_does_not_appear_on_the_daily_chart()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var before = await TodaysDailyStatAsync();

        _fixture.Conversion.FindNothing();
        await StartAsync(new { indentTypes = new[] { "regular" } });

        var after = await TodaysDailyStatAsync();

        after.Should().Be(before);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(30)]
    public async Task Daily_stats_cover_every_day_in_the_window_with_no_gaps(int days)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var stats = await DailyStatsAsync(days);

        stats.Should().HaveCount(days);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expectedDates = Enumerable.Range(0, days)
            .Select(offset => today.AddDays(-offset).ToString("yyyy-MM-dd"))
            .OrderBy(date => date)
            .ToList();

        stats.Select(stat => stat.Date).Should().Equal(expectedDates);
    }

    [SkippableFact]
    public async Task An_out_of_range_days_value_is_clamped_rather_than_refused()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        // A dashboard chart, not a report export — an absurd request narrows to the ceiling instead
        // of erroring, since there is nothing unsafe about asking for more days than allowed.
        var stats = await DailyStatsAsync(10_000);

        stats.Should().HaveCount(90);
    }

    // ---- GET /api/process-jobs/{jobId} --------------------------------------

    [SkippableFact]
    public async Task An_unknown_execution_is_not_found()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync($"/api/process-jobs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task A_malformed_job_id_does_not_reach_the_handler()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync("/api/process-jobs/not-a-guid");

        // The route constraint answers first, so a malformed id can never become a query.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task A_job_that_is_not_a_conversion_is_not_served_here()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var cycleJobId = await _fixture.AddUnrelatedJobAsync();

        using var client = _fixture.Authenticated();
        var response = await client.GetAsync($"/api/process-jobs/{cycleJobId}");

        // This endpoint answers in the indent's shape. A cycle has no indent, and pretending
        // otherwise would mean inventing one.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task An_execution_carries_its_stage_timeline_and_its_outcomes()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        var run = await StartAsync(new { indentTypes = new[] { "regular" } });
        var jobId = run.Executions[0].JobId;

        using var client = _fixture.Authenticated();
        var detail = await client.GetFromJsonAsync<ExecutionDetail>($"/api/process-jobs/{jobId}");

        detail.Should().NotBeNull();
        detail!.Stages.Should().HaveCount(5);
        detail.Stages.Select(stage => stage.Stage).Should().Equal(
            "Discovery", "VendorResolution", "DocumentControl", "PendingLines", "CreatePurchaseOrder");

        // Every stage is terminal once the execution completes: the ones it walked are Completed,
        // the ones it never needed are Skipped. None may be left Pending.
        detail.Stages.Should().OnlyContain(stage => stage.Status == "Completed" || stage.Status == "Skipped");
        detail.Errors.Should().BeEmpty();
        detail.Execution.Outcomes.Should().HaveCount(2);
    }

    // ---- GET /api/process-jobs/indent/{indentId} ----------------------------

    [SkippableFact]
    public async Task An_indent_nobody_has_converted_is_not_found()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync("/api/process-jobs/indent/987654321");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableTheory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("not-a-number")]
    [InlineData("99999999999999999999")]
    public async Task An_impossible_indent_id_is_never_a_server_error(string indentId)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync($"/api/process-jobs/indent/{indentId}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.BadRequest);
    }

    [SkippableFact]
    public async Task An_id_that_is_both_a_material_and_a_service_indent_asks_which()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = await _fixture.AddConversionAsync(IndentKind.Regular);
        await _fixture.AddConversionAsync(IndentKind.Service, indentId);

        using var client = _fixture.Authenticated();

        var ambiguous = await client.GetAsync($"/api/process-jobs/indent/{indentId}");

        // XINDID and XINDAUTOID are keys into different ERP tables. Guessing would be a coin toss.
        ambiguous.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ambiguous.Content.ReadAsStringAsync()).Should().Contain("indentType=");

        var resolved = await client.GetAsync($"/api/process-jobs/indent/{indentId}?indentType=service");

        resolved.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await Read<IndentHistory>(resolved);
        history.Ambiguous.Should().BeFalse();
        history.Conversions.Should().ContainSingle().Which.IndentType.Should().Be("Service");
    }

    [SkippableFact]
    public async Task An_unknown_indent_type_filter_is_refused_and_named()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync("/api/process-jobs/indent/1?indentType=banana");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("banana");
    }

    // ---- GET /api/process-jobs/runs -----------------------------------------

    [SkippableTheory]
    [InlineData("trigger=cron")]
    [InlineData("status=Sideways")]
    public async Task An_unknown_filter_value_is_refused_and_named(string query)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync($"/api/process-jobs/runs?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [SkippableTheory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, -5, 1, 1)]
    [InlineData(1, 5000, 1, 200)]
    public async Task Paging_is_clamped_rather_than_trusted(
        int page,
        int pageSize,
        int expectedPage,
        int expectedPageSize)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var result = await client.GetFromJsonAsync<PagedRuns>(
            $"/api/process-jobs/runs?page={page}&pageSize={pageSize}");

        // An unbounded page size would let one call pull the whole run history into memory.
        result.Should().NotBeNull();
        result!.Page.Should().Be(expectedPage);
        result.PageSize.Should().Be(expectedPageSize);
    }

    [SkippableFact]
    public async Task Runs_can_be_found_by_the_trigger_that_started_them()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        var started = await StartAsync(new { trigger = "erppush" });

        using var client = _fixture.Authenticated();
        var page = await client.GetFromJsonAsync<PagedRuns>("/api/process-jobs/runs?trigger=ErpPush&pageSize=200");

        page!.Items.Should().Contain(run => run.RunId == started.Run.RunId);
        page.Items.Should().OnlyContain(run => run.Trigger == "ErpPush");
    }

    [SkippableFact]
    public async Task An_unknown_run_is_not_found()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.GetAsync($"/api/process-jobs/runs/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task A_run_that_converted_nothing_still_says_what_it_was_allowed_to_look_for()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.FindNothing();

        var run = await StartAsync(new { indentTypes = new[] { "capital" }, trigger = "reconcile" });

        run.Executions.Should().BeEmpty();

        using var client = _fixture.Authenticated();
        var stored = await client.GetFromJsonAsync<RunDetail>($"/api/process-jobs/runs/{run.Run.RunId}");

        // The whole point of the run row: "found no Capital indents" is not the same statement as
        // "was never allowed to look for Capital indents", and only this column tells them apart.
        stored!.Run.IndentTypes.Should().Be("Capital");
        stored.Run.Status.Should().Be("Completed");
        stored.Run.PurchaseOrdersCreated.Should().Be(0);
    }

    // ---- the ERP refusing ---------------------------------------------------

    [SkippableFact]
    public async Task An_indent_that_orders_nothing_is_completed_with_the_reason_beside_it()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.OrderNothing("Every item line on this indent is already closed.");

        var run = await StartAsync(new { indentTypes = new[] { "regular" } });

        var execution = run.Executions.Should().ContainSingle().Subject;

        // Completed, not Failed: the execution did its work and the answer was "nothing to order".
        execution.Status.Should().Be("Completed");
        execution.Outcomes.Should().ContainSingle()
            .Which.Reason.Should().Contain("already closed");
        run.Run.PurchaseOrdersCreated.Should().Be(0);
    }

    [SkippableFact]
    public async Task A_partially_converted_indent_records_the_order_and_the_refusal()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();

        var run = await StartAsync(new { indentTypes = new[] { "regular" } });
        var outcomes = run.Executions.Should().ContainSingle().Subject.Outcomes;

        outcomes.Should().ContainSingle(outcome => outcome.Outcome == "Created");
        outcomes.Should().ContainSingle(outcome => outcome.Outcome == "Skipped");
    }

    // ---- duplicates and concurrency -----------------------------------------

    [SkippableFact]
    public async Task Two_conversions_of_one_indent_at_the_same_moment_produce_one_execution()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.HoldUntilReleased();

        using var first = _fixture.Authenticated();
        using var second = _fixture.Authenticated();

        var left = first.PostAsJsonAsync("/api/process-jobs", new { indentTypes = new[] { "regular" } });
        var right = second.PostAsJsonAsync("/api/process-jobs", new { indentTypes = new[] { "regular" } });

        _fixture.Conversion.Release();

        var responses = await Task.WhenAll(left, right);

        foreach (var response in responses)
        {
            // The body is in the failure message on purpose: "one of two returned 500" is not
            // enough to act on, and a race is exactly the case you cannot re-run by hand.
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        }

        // Both runs are recorded — two people did ask — but the indent has one execution, because
        // two would race for the same indent lines in the ERP.
        (await _fixture.CountExecutionsAsync(indentId)).Should().Be(1);
        (await _fixture.CountConversionsAsync(indentId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task Two_trackers_opening_the_same_indent_at_once_neither_throws()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indent = new TrackedIndent(
            ScriptedConversion.NewIndentId(),
            IndentKind.Regular,
            "26-27/TE/NF1/000099",
            SiteId: 1);

        // Straight at the service, in two scopes, with no HTTP in the way: a race here surfaces as
        // the exception it really is instead of a 500 the problem handler has already swallowed.
        //
        // The host is built first: WebApplicationFactory builds it lazily on first touch, and two
        // threads racing that is a different race from the one under test.
        var services = _fixture.Services;
        var barrier = new Barrier(2);

        async Task ConvertAsync()
        {
            using var scope = services.CreateScope();
            var tracker = scope.ServiceProvider.GetRequiredService<IIndentPoTracker>();

            await tracker.StartRunAsync(
                new StartRunRequest(TriggerSource.Api, "Regular"),
                CancellationToken.None);

            barrier.SignalAndWait(TimeSpan.FromSeconds(10));

            await tracker.StartExecutionAsync(indent, CancellationToken.None);
            await tracker.CompleteExecutionAsync(
                [TrackedOutcome.Note("Nothing outstanding.")],
                CancellationToken.None);

            await tracker.CompleteRunAsync(1, CancellationToken.None);
        }

        var both = async () => await Task.WhenAll(Task.Run(ConvertAsync), Task.Run(ConvertAsync));

        await both.Should().NotThrowAsync();

        (await _fixture.CountConversionsAsync(indent.IndentId)).Should().Be(1);
        (await _fixture.CountExecutionsAsync(indent.IndentId)).Should().Be(1);
    }

    [SkippableFact]
    public async Task The_same_indent_converted_twice_in_a_row_is_two_attempts_of_one_case()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();

        await StartAsync(new { indentTypes = new[] { "regular" } });
        await StartAsync(new { indentTypes = new[] { "regular" } });

        using var client = _fixture.Authenticated();
        var history = await client.GetFromJsonAsync<IndentHistory>($"/api/process-jobs/indent/{indentId}");

        history!.Executions.Should().HaveCount(2);
        history.Executions.Select(execution => execution.AttemptNo).Should().Equal(1, 2);

        // One case, two attempts — the indent's number and site are stored once, not twice.
        history.Conversions.Should().ContainSingle();
    }

    // ---- retry --------------------------------------------------------------

    [SkippableFact]
    public async Task An_erp_refusal_is_the_callers_problem_not_a_server_error()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.FailWith("Document Control has no 26-27 row for site 1.");

        var response = await PostAsync(new { indentTypes = new[] { "regular" } });

        // A person has to change something before this can work, so it is a bad request — the same
        // answer the untracked endpoint gives for the same refusal.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Document Control");

        (await _fixture.LatestRunStatusAsync()).Should().Be("Failed");
    }

    [SkippableFact]
    public async Task An_unreachable_erp_is_answered_as_try_again()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.FailTransiently("The ERP did not answer in time.");

        var response = await PostAsync(new { indentTypes = new[] { "regular" } });

        // Transient means "ask again", and the status code has to say so or a caller will treat a
        // blip as a permanent refusal and stop trying.
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [SkippableFact]
    public async Task A_failed_execution_can_be_retried_into_a_successful_one()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.FailWith("Document Control has no 26-27 row for site 1.");

        var refused = await PostAsync(new { indentId, indentTypes = new[] { "regular" } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var client = _fixture.Authenticated();

        var afterFailure = await client.GetFromJsonAsync<IndentHistory>($"/api/process-jobs/indent/{indentId}");
        var firstAttempt = afterFailure!.Executions.Should().ContainSingle().Subject;

        // The attempt is kept, not rolled back: the failure is the thing worth remembering.
        firstAttempt.Status.Should().Be("Failed");

        var detail = await client.GetFromJsonAsync<ExecutionDetail>($"/api/process-jobs/{firstAttempt.JobId}");
        detail!.Errors.Should().ContainSingle()
            .Which.LaymanMessage.Should().Contain("Document Control");
        detail.Execution.DurationMs.Should().NotBeNull();

        // The master data is fixed, and the same indent is asked for again.
        _fixture.Conversion.Succeed();

        var retried = await client.PostAsync($"/api/process-jobs/{firstAttempt.JobId}/retry", content: null);
        retried.StatusCode.Should().Be(HttpStatusCode.OK);

        var retryRun = await Read<RunDetail>(retried);
        retryRun.Run.Trigger.Should().Be("Manual");
        retryRun.Run.TriggerReference.Should().Contain(firstAttempt.JobId.ToString());

        var history = await client.GetFromJsonAsync<IndentHistory>($"/api/process-jobs/indent/{indentId}");

        history!.Executions.Should().HaveCount(2);
        history.Executions[0].Status.Should().Be("Failed");
        history.Executions[1].Status.Should().Be("Completed");
        history.Executions.Select(execution => execution.AttemptNo).Should().Equal(1, 2);

        // Two attempts, one case: the indent's identity was not stored twice.
        history.Conversions.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Retrying_an_execution_that_does_not_exist_is_not_found()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        var response = await client.PostAsync($"/api/process-jobs/{Guid.NewGuid()}/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [SkippableFact]
    public async Task A_failed_run_is_recorded_as_failed_rather_than_left_running()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.ThrowUnexpected();

        using var client = _fixture.Authenticated();
        var response = await client.PostAsJsonAsync("/api/process-jobs", new { indentTypes = new[] { "regular" } });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // The caller gets the error, and the run stops claiming to be in progress.
        var latest = await _fixture.LatestRunStatusAsync();
        latest.Should().Be("Failed");
    }

    [SkippableFact]
    public async Task A_conversion_job_cannot_be_requeued_through_the_generic_job_api()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        _fixture.Conversion.FailWith("The ERP refused it.");

        await PostAsync(new { indentTypes = new[] { "regular" } });

        using var client = _fixture.Authenticated();
        var history = await client.GetFromJsonAsync<IndentHistory>($"/api/process-jobs/indent/{indentId}");
        var jobId = history!.Executions.Should().ContainSingle().Subject.JobId;

        var response = await client.PostAsync($"/api/automation/jobs/{jobId}/retry", content: null);

        // Re-queueing would hand it to a dispatcher whose engine has no definition for this
        // workflow. The refusal names the endpoint that does work.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("/api/process-jobs/");
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<RunDetail> StartAsync(object body)
    {
        var response = await PostAsync(body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return await Read<RunDetail>(response);
    }

    private async Task<HttpResponseMessage> PostAsync(object body)
    {
        using var client = _fixture.Authenticated();

        return await client.PostAsJsonAsync("/api/process-jobs", body);
    }

    /// <summary>
    /// The conversion trigger — the endpoint that converts and answers with the purchase orders,
    /// as opposed to <see cref="StartAsync"/>, which answers with the run.
    /// </summary>
    private async Task ConvertAsync(string route, object body)
    {
        using var client = _fixture.Authenticated();

        var response = await client.PostAsJsonAsync(route, body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<PagedJobs> GridAsync(string query)
    {
        using var client = _fixture.Authenticated();

        var page = await client.GetFromJsonAsync<PagedJobs>($"/api/process-jobs?{query}");

        page.Should().NotBeNull();

        return page!;
    }

    /// <summary>The grid row for one indent, asserted to be there exactly once.</summary>
    private async Task<GridRow> FindRowAsync(long indentId, string query)
    {
        var page = await GridAsync(query);

        return page.Items.Should().ContainSingle(row => row.IndentId == indentId).Subject;
    }

    private async Task<SummaryResponse> SummaryAsync(string query)
    {
        using var client = _fixture.Authenticated();

        var summary = await client.GetFromJsonAsync<SummaryResponse>($"/api/process-jobs/summary?{query}");

        summary.Should().NotBeNull();

        return summary!;
    }

    private async Task<IReadOnlyList<DailyStatResponse>> DailyStatsAsync(int days)
    {
        using var client = _fixture.Authenticated();

        var stats = await client.GetFromJsonAsync<List<DailyStatResponse>>(
            $"/api/process-jobs/daily-summary?days={days}");

        stats.Should().NotBeNull();

        return stats!;
    }

    /// <summary>
    /// Today's row from the daily chart. Always present: a 1-day window is zero-filled even when
    /// nothing happened today, so this never falls through to "no such row."
    /// </summary>
    private async Task<DailyStatResponse> TodaysDailyStatAsync()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var stats = await DailyStatsAsync(1);

        return stats.Single(stat => stat.Date == today);
    }

    private async Task<HttpResponseMessage> PostRawAsync(string route, string body)
    {
        using var client = _fixture.Authenticated();

        return await client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>();

        value.Should().NotBeNull();

        return value!;
    }

    private sealed record RunSummary(
        Guid RunId,
        string Trigger,
        string? TriggeredBy,
        string? TriggerReference,
        string Mode,
        string IndentTypes,
        string Status,
        int IndentsExamined,
        int PurchaseOrdersCreated,
        long? DurationMs);

    private sealed record IndentSummary(long IndentId, string IndentType, string IndentNumber, int SiteId);

    private sealed record OutcomeRow(string Outcome, string? PoNumber, string? Reason, string? VendorCode);

    private sealed record Execution(
        Guid JobId,
        IndentSummary Indent,
        string Mode,
        string Status,
        int AttemptNo,
        long? DurationMs,
        IReadOnlyList<OutcomeRow> Outcomes);

    private sealed record StageRow(string Stage, string Task, string Status);

    private sealed record ErrorRow(string ErrorType, string LaymanMessage, string TechnicalMessage);

    private sealed record RunDetail(RunSummary Run, IReadOnlyList<Execution> Executions);

    private sealed record ExecutionDetail(
        Execution Execution,
        IReadOnlyList<StageRow> Stages,
        IReadOnlyList<ErrorRow> Errors);

    private sealed record IndentHistory(
        long IndentId,
        bool Ambiguous,
        IReadOnlyList<IndentSummary> Conversions,
        IReadOnlyList<Execution> Executions);

    private sealed record PagedRuns(IReadOnlyList<RunSummary> Items, int TotalCount, int Page, int PageSize);

    /// <summary>One line of the conversion grid, as the dashboard would read it.</summary>
    private sealed record GridRow(
        Guid JobId,
        Guid? RunId,
        long IndentId,
        string IndentType,
        string Document,
        int SiteId,
        int Company,
        string Workflow,
        string? Trigger,
        string? TriggeredBy,
        string Mode,
        string? Stage,
        string Status,
        long? DurationMs,
        string? PoNumber,
        string? FailureReason);

    private sealed record PagedJobs(IReadOnlyList<GridRow> Items, int TotalCount, int Page, int PageSize);

    private sealed record SummaryResponse(
        int TotalJobs,
        IReadOnlyDictionary<string, int> CountsByStatus,
        int SuccessCount,
        double? SuccessRate,
        double? AverageDurationMs,
        int TotalTriggerAttempts,
        int TriggerAttemptsWithoutEligibleIndent);

    private sealed record DailyStatResponse(
        string Date,
        int PurchaseOrdersCreated,
        int IndentsConverted,
        int IndentsFailed);
}
