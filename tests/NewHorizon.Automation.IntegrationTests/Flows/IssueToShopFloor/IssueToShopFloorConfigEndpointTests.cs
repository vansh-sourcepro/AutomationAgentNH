using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NewHorizon.Automation.Worker.Endpoints;
using NewHorizon.Automation.Worker.Flows.IssueToShopFloor.Contracts;

namespace NewHorizon.Automation.IntegrationTests.Flows.IssueToShopFloor;

[Collection(AgentHostCollection.Name)]
public sealed class IssueToShopFloorConfigEndpointTests : IClassFixture<AgentApplicationFactory>
{
    private const string BasePath = "/api/automation/issue-to-shop-floor/config";
    private const string LegacyBasePath = "/api/automation/sjo-to-issue/config";

    private readonly AgentApplicationFactory _factory;

    public IssueToShopFloorConfigEndpointTests(AgentApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData(BasePath)]
    [InlineData($"{BasePath}/sjo")]
    [InlineData(LegacyBasePath)]
    public async Task Endpoints_require_authentication(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_returns_all_three_issue_sources()
    {
        using var client = Authenticated();

        var response = await client.GetAsync(BasePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<List<IssueToShopFloorConfigResponse>>();
        list.Should().NotBeNull();
        list!.Should().HaveCount(3);
        list!.Select(c => c.IssueSource).Should().BeEquivalentTo(["Sjo", "WorkOrder", "SalesOaf"]);
    }

    [Fact]
    public async Task Get_single_source_returns_200()
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/sjo");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var config = await response.Content.ReadFromJsonAsync<IssueToShopFloorConfigResponse>();
        config.Should().NotBeNull();
        config!.IssueSource.Should().Be("Sjo");
    }

    [Fact]
    public async Task Get_unknown_source_returns_400()
    {
        using var client = Authenticated();

        var response = await client.GetAsync($"{BasePath}/unknownSource");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetEnabled_flips_master_toggle_on_all_rows()
    {
        using var client = Authenticated();

        var toggleOn = await client.PutAsJsonAsync(
            $"{BasePath}/enabled",
            new SetIssueToShopFloorEnabledRequest(Enabled: true));

        toggleOn.StatusCode.Should().Be(HttpStatusCode.OK);
        var rowsOn = await toggleOn.Content.ReadFromJsonAsync<List<IssueToShopFloorConfigResponse>>();
        rowsOn.Should().NotBeNull();
        rowsOn!.Should().OnlyContain(r => r.IsActive);

        var toggleOff = await client.PutAsJsonAsync(
            $"{BasePath}/enabled",
            new SetIssueToShopFloorEnabledRequest(Enabled: false));

        toggleOff.StatusCode.Should().Be(HttpStatusCode.OK);
        var rowsOff = await toggleOff.Content.ReadFromJsonAsync<List<IssueToShopFloorConfigResponse>>();
        rowsOff.Should().NotBeNull();
        rowsOff!.Should().OnlyContain(r => !r.IsActive);
    }

    [Fact]
    public async Task Update_source_config_persists_settings()
    {
        using var client = Authenticated();

        var updateReq = new UpdateIssueToShopFloorConfigRequest(
            RunMode: "Timer",
            ScheduleTime: "14:30",
            DocumentNumbers: "26-27/SJ/NF1/000123",
            Sites: "1,2",
            MaxDocumentsPerRun: 25,
            DryRun: false);

        var updateResponse = await client.PutAsJsonAsync($"{BasePath}/sjo", updateReq);

        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<IssueToShopFloorConfigResponse>();
        updated.Should().NotBeNull();
        updated!.RunMode.Should().Be("Timer");
        updated.ScheduleTime.Should().Be(new TimeOnly(14, 30));
        updated.Sites.Should().Be("1,2");
        updated.MaxDocumentsPerRun.Should().Be(25);
    }

    [Fact]
    public async Task Legacy_base_path_answers_identically()
    {
        using var client = Authenticated();

        var response = await client.GetAsync(LegacyBasePath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<List<IssueToShopFloorConfigResponse>>();
        list.Should().NotBeNull();
        list!.Should().HaveCount(3);
    }

    private HttpClient Authenticated()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyFilter.HeaderName, "test-inbound-key");
        return client;
    }
}
