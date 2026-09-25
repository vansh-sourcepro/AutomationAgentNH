using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NewHorizon.Automation.Worker.Endpoints;

namespace NewHorizon.Automation.IntegrationTests.Flows.IssueToShopFloor;

[Collection(AgentHostCollection.Name)]
public sealed class IssueToShopFloorEndpointTests : IClassFixture<AgentApplicationFactory>
{
    private const string Path = "/api/automation/issue-to-shop-floor";
    private const string LegacyPath = "/api/automation/sjo-to-issue";

    private readonly AgentApplicationFactory _factory;

    public IssueToShopFloorEndpointTests(AgentApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task The_first_sjo_only_path_still_answers_with_the_same_handler()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(LegacyPath, new { sjoNumber = "26-27/SJ/NF1/000123" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["issueType"]!.GetValue<string>().Should().Be("Sjo");
    }

    [Fact]
    public async Task Requires_the_inbound_api_key()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(Path, new { sjoNumber = "26-27/SJ/NF1/000123", dryRun = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sjoNumber\":\"   \"}")]
    public async Task A_missing_sjo_number_is_a_400(string body)
    {
        using var client = Authenticated();

        var response = await client.PostAsync(Path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unconfigured_flow_refuses_with_the_setting_to_fix_and_creates_nothing()
    {
        // The test host blanks Issue To / Issue By (TestConfiguration), so the flow must refuse before
        // any ERP call — and say which setting is missing, rather than fail somewhere inside the ERP.
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { sjoNumber = "26-27/SJ/NF1/000123" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        result!["created"]!.GetValue<bool>().Should().BeFalse();
        result["reason"]!.GetValue<string>().Should().Contain("IssueTo");
    }

    [Fact]
    public async Task An_unknown_issue_type_is_a_400_naming_the_accepted_ones()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "requisition", documentNumber = "26-27/SJ/NF1/000123" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("workOrder").And.Contain("salesOaf");
    }

    [Theory]
    [InlineData("workOrder", "26-27/WO/NF1/000010")]
    [InlineData("WO", "26-27/WO/NF1/000010")]
    [InlineData("salesOaf", "26-27/OF/NF1/000042")]
    public async Task Work_order_and_oaf_requests_are_understood_and_refused_by_the_unconfigured_flow(string issueType, string number)
    {
        // Sites is not needed for these, so what the shipped appsettings.json is missing next is
        // Issue To / Issue By — the refusal must name them, and nothing is created.
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType, documentNumber = number });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        result!["created"]!.GetValue<bool>().Should().BeFalse();
        result["issueType"]!.GetValue<string>().Should().NotBe("Sjo");
        result["reason"]!.GetValue<string>().Should().Contain("IssueTo");
    }

    private HttpClient Authenticated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyFilter.HeaderName, "test-inbound-key");
        return client;
    }
}
