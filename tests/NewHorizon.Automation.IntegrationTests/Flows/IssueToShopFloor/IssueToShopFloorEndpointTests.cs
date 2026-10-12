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

    [Fact]
    public async Task Invalid_mode_returns_400_with_allowed_options()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "invalidMode" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("timerBased").And.Contain("numberBased").And.Contain("both").And.Contain("none");
    }

    [Fact]
    public async Task TimerBased_mode_without_time_returns_400()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "timerBased" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("time");
    }

    [Fact]
    public async Task TimerBased_mode_with_invalid_time_format_returns_400()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "timerBased", time = "25:99" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Expected format:");
    }

    [Fact]
    public async Task TimerBased_mode_with_valid_time_schedules_execution_without_document_number()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "timerBased", time = "14:30" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        result.Should().NotBeNull();
        result!["mode"]!.GetValue<string>().Should().Be("timerBased");
        result["status"]!.GetValue<string>().Should().Be("Scheduled");
        result["configuredTime"]!.GetValue<string>().Should().Be("14:30:00");
    }

    [Fact]
    public async Task Both_mode_without_time_returns_400()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "both", documentNumber = "26-27/SJ/NF1/000123" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("time");
    }

    [Fact]
    public async Task Both_mode_without_document_number_returns_400()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "both", time = "14:30" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("documentNumber");
    }

    [Fact]
    public async Task Both_mode_with_valid_time_and_document_number_schedules_execution()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new
        {
            issueType = "sjo",
            mode = "both",
            time = "15:45:00",
            documentNumber = "26-27/SJ/NF1/000123"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        result.Should().NotBeNull();
        result!["mode"]!.GetValue<string>().Should().Be("both");
        result["status"]!.GetValue<string>().Should().Be("Scheduled");
        result["configuredTime"]!.GetValue<string>().Should().Be("15:45:00");
        result["documentNumber"]!.GetValue<string>().Should().Be("26-27/SJ/NF1/000123");
    }

    [Fact]
    public async Task None_mode_disables_automation()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "none" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        result.Should().NotBeNull();
        result!["mode"]!.GetValue<string>().Should().Be("none");
        result["status"]!.GetValue<string>().Should().Be("Disabled");
    }

    [Fact]
    public async Task NumberBased_mode_without_documentNumber_processes_bulk_eligible_documents()
    {
        using var client = Authenticated();

        var response = await client.PostAsJsonAsync(Path, new { issueType = "sjo", mode = "numberBased", dryRun = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
        result.Should().NotBeNull();
        result!["mode"]!.GetValue<string>().Should().Be("numberBased");
        result["dryRun"]!.GetValue<bool>().Should().BeTrue();
    }

    private HttpClient Authenticated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyFilter.HeaderName, "test-inbound-key");
        return client;
    }
}
