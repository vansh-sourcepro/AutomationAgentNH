using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.IntegrationTests.Flows.IssueToShopFloor;

[Collection(AgentHostCollection.Name)]
public sealed class IssueToShopFloorDashboardEndpointTests : IClassFixture<AgentApplicationFactory>
{
    private const string BasePath = "/api/automation/issue-to-shop-floor/dashboard";
    private const string LegacyBasePath = "/api/automation/sjo-to-issue/dashboard";

    private readonly AgentApplicationFactory _factory;

    public IssueToShopFloorDashboardEndpointTests(AgentApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData(BasePath)]
    [InlineData($"{BasePath}/summary")]
    [InlineData($"{BasePath}/daily-summary")]
    [InlineData($"{BasePath}/recent")]
    [InlineData(LegacyBasePath)]
    public async Task Endpoints_require_authentication(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dashboard_composite_returns_200_with_expected_structure()
    {
        using var client = Authenticated();

        var response = await client.GetAsync(BasePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<IssueToShopFloorDashboardResponse>();
        result.Should().NotBeNull();
        result!.Summary.Should().NotBeNull();
        result.DailyStats.Should().NotBeNull();
        result.DailyStats.Should().HaveCount(30); // Default 30 days
        result.RecentJobs.Should().NotBeNull();
    }

    [Fact]
    public async Task Legacy_dashboard_route_answers_identically()
    {
        using var client = Authenticated();

        var response = await client.GetAsync(LegacyBasePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<IssueToShopFloorDashboardResponse>();
        result.Should().NotBeNull();
        result!.Summary.Should().NotBeNull();
        result.DailyStats.Should().HaveCount(30);
    }

    [Fact]
    public async Task Summary_endpoint_returns_kpi_totals()
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<IssueToShopFloorDashboardSummaryResponse>();
        summary.Should().NotBeNull();
        summary!.CountsByStatus.Should().NotBeNull();
        summary.CountsBySource.Should().NotBeNull();
        summary.TotalJobs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(30)]
    public async Task Daily_summary_respects_days_parameter_and_fills_all_dates(int days)
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/daily-summary?days={days}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stats = await response.Content.ReadFromJsonAsync<List<DailyIssueConversionStatResponse>>();
        stats.Should().NotBeNull();
        stats!.Should().HaveCount(days);

        // Verify sorted oldest to newest
        for (int i = 0; i < stats.Count - 1; i++)
        {
            (DateOnly.Parse(stats[i].Date) < DateOnly.Parse(stats[i + 1].Date)).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(400)]
    public async Task Daily_summary_rejects_invalid_days_window(int days)
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/daily-summary?days={days}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Recent_jobs_endpoint_returns_list_and_respects_count()
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/recent?count=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jobs = await response.Content.ReadFromJsonAsync<List<IssueToShopFloorHistoryRowResponse>>();
        jobs.Should().NotBeNull();
        jobs!.Count.Should().BeLessThanOrEqualTo(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Recent_jobs_rejects_out_of_range_count(int count)
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/recent?count={count}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Summary_rejects_invalid_status()
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/summary?status=NotARealStatus");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private HttpClient Authenticated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyFilter.HeaderName, "test-inbound-key");
        return client;
    }
}
