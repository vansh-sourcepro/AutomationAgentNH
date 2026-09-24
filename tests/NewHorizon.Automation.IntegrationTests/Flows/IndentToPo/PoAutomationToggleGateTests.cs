using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace NewHorizon.Automation.IntegrationTests.Flows.IndentToPo;

/// <summary>
/// The PO Automation master toggle (persisted on <c>IndentPoAutomationConfig.IsActive</c>) refuses
/// every conversion path when it is off — not just the screen's Run button, but the external
/// conversion trigger, the vendor form, <c>POST /api/process-jobs</c> and a retry.
/// </summary>
/// <remarks>
/// Shares the class database with the other process-job suites, so it always restores the toggle to
/// on when it finishes (<see cref="DisposeAsync"/>).
/// </remarks>
[Collection(ProcessJobApiCollection.Name)]
public sealed class PoAutomationToggleGateTests : IAsyncLifetime
{
    private readonly ProcessJobApiFixture _fixture;

    public PoAutomationToggleGateTests(ProcessJobApiFixture fixture)
    {
        _fixture = fixture;
        _fixture.Conversion.Reset();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() =>
        _fixture.IsAvailable ? _fixture.SetPoAutomationActiveAsync(true) : Task.CompletedTask;

    [SkippableFact]
    public async Task Process_jobs_is_refused_while_po_automation_is_off_and_accepted_once_on()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        using var client = _fixture.Authenticated();

        await _fixture.SetPoAutomationActiveAsync(false);
        var refused = await client.PostAsJsonAsync("/api/process-jobs", new { indentTypes = new[] { "regular" } });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("PO Automation is turned off");

        await _fixture.SetPoAutomationActiveAsync(true);
        _fixture.Conversion.UseNewIndent();
        var accepted = await client.PostAsJsonAsync("/api/process-jobs", new { indentTypes = new[] { "regular" } });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [SkippableTheory]
    [InlineData("/api/automation/indent-to-po")]
    [InlineData("/api/automation/indent-to-po/convert")]
    public async Task The_conversion_trigger_is_refused_while_po_automation_is_off(string route)
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        _fixture.Conversion.UseNewIndent();
        using var client = _fixture.Authenticated();

        await _fixture.SetPoAutomationActiveAsync(false);

        var response = await client.PostAsJsonAsync(route, new { indentTypes = new[] { "regular" } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PO Automation is turned off");
    }

    [SkippableFact]
    public async Task The_vendor_form_is_refused_while_po_automation_is_off()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        using var client = _fixture.Authenticated();

        await _fixture.SetPoAutomationActiveAsync(false);

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/vendor",
            new { vendorCode = "V0012", indentTypes = new[] { "regular" } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("PO Automation is turned off");
    }

    [SkippableFact]
    public async Task A_retry_is_refused_while_po_automation_is_off()
    {
        Skip.If(!_fixture.IsAvailable, ProcessJobApiFixture.SkipReason);

        var indentId = _fixture.Conversion.UseNewIndent();
        using var client = _fixture.Authenticated();

        // A completed conversion to retry — made while the toggle is on.
        await _fixture.SetPoAutomationActiveAsync(true);
        _fixture.Conversion.Succeed();
        var started = await client.PostAsJsonAsync("/api/process-jobs", new { indentId, indentTypes = new[] { "regular" } });
        started.StatusCode.Should().Be(HttpStatusCode.OK);

        var jobId = await _fixture.LatestConversionJobIdAsync();

        await _fixture.SetPoAutomationActiveAsync(false);
        var retry = await client.PostAsync($"/api/process-jobs/{jobId}/retry", content: null);

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await retry.Content.ReadAsStringAsync()).Should().Contain("PO Automation is turned off");
    }
}
