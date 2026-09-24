using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace NewHorizon.Automation.IntegrationTests;

[Collection(AgentHostCollection.Name)]
public sealed class HealthEndpointTests : IClassFixture<AgentApplicationFactory>
{
    private readonly AgentApplicationFactory _factory;

    public HealthEndpointTests(AgentApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/automation/health")]
    public async Task Health_reports_the_service_and_its_dependencies(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        // 503 when the automation database is unreachable — which is the correct answer on a
        // machine with no SQL Server, and still a served response rather than a dead socket.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);

        var payload = await response.Content.ReadFromJsonAsync<HealthPayload>();

        payload.Should().NotBeNull();
        payload!.Checks.Should().ContainKey("service").WhoseValue.Should().Be("Healthy");
        payload.Checks.Should().ContainKey("database");
        payload.UptimeSeconds.Should().BeGreaterThanOrEqualTo(0);
        payload.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Health_is_reachable_without_the_inbound_api_key()
    {
        // Monitoring must not need a secret; every other endpoint (Phase 6) will require the key.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/automation/health");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    private sealed record HealthPayload(
        string Status,
        string Version,
        DateTimeOffset StartedAtUtc,
        double UptimeSeconds,
        Dictionary<string, string> Checks);
}
