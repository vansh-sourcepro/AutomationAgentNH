using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;

namespace NewHorizon.Automation.IntegrationTests.Flows.PoToGrn;

/// <summary>
/// The two PO → GRN APIs end to end through the real host — routing, API key, body binding, the
/// toggle and invoice-number rules — with the settings row in memory and the ERP conversation
/// replaced, so no database or ERP is needed.
/// </summary>
[Collection(AgentHostCollection.Name)]
public sealed class PoToGrnApiTests : IClassFixture<PoToGrnApiFactory>
{
    private const string ConfigRoute = "/api/automation/grn-automation";
    private const string TriggerRoute = "/api/automation/po-to-grn";

    private readonly PoToGrnApiFactory _factory;

    public PoToGrnApiTests(PoToGrnApiFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    [Theory]
    [InlineData("GET", ConfigRoute)]
    [InlineData("PUT", ConfigRoute)]
    [InlineData("POST", TriggerRoute)]
    public async Task Both_APIs_require_the_inbound_API_key(string method, string route)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = JsonContent.Create(new { }),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_settings_can_be_read_and_changed_in_place()
    {
        using var client = _factory.Authenticated();

        var put = await client.PutAsJsonAsync(ConfigRoute, new
        {
            isActive = true,
            receiptMode = "partial",
            invoiceNumber = "INV-2026-09",
            runMode = "Both",
            scheduleTime = "18:30",
            updatedBy = "postman",
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await client.GetFromJsonAsync<JsonObject>(ConfigRoute);
        saved!["isActive"]!.GetValue<bool>().Should().BeTrue();
        saved["receiptMode"]!.GetValue<string>().Should().Be("Partial");
        saved["invoiceNumber"]!.GetValue<string>().Should().Be("INV-2026-09");
        saved["scheduleTime"]!.GetValue<string>().Should().StartWith("18:30");
        saved["updatedBy"]!.GetValue<string>().Should().Be("postman");
    }

    [Theory]
    [InlineData("{\"receiptMode\":\"Sometimes\"}")]
    [InlineData("{\"runMode\":\"Hourly\"}")]
    [InlineData("{\"scheduleTime\":\"half past six\"}")]
    [InlineData("{\"sites\":\"1,x\"}")]
    public async Task Bad_settings_are_a_400(string body)
    {
        using var client = _factory.Authenticated();

        var response = await client.PutAsync(ConfigRoute, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_trigger_is_refused_while_GRN_automation_is_off()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate { IsActive = false, InvoiceNumber = "INV" });
        using var client = _factory.Authenticated();

        var response = await client.PostAsJsonAsync(TriggerRoute, new { });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Service.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_real_run_is_refused_without_an_invoice_number_but_a_dry_run_is_not()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate { IsActive = true, InvoiceNumber = " " });
        using var client = _factory.Authenticated();

        var real = await client.PostAsJsonAsync(TriggerRoute, new { });
        var dry = await client.PostAsJsonAsync(TriggerRoute, new { dryRun = true });

        real.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await real.Content.ReadAsStringAsync()).Should().Contain("invoice number");
        dry.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_field_is_a_400_not_a_silent_receive_everything()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate { IsActive = true, InvoiceNumber = "INV" });
        using var client = _factory.Authenticated();

        var response = await client.PostAsJsonAsync(TriggerRoute, new { poNumber = "000012" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Service.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_run_uses_the_saved_settings_and_answers_with_each_POs_result()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate
        {
            IsActive = true,
            InvoiceNumber = "INV-77",
            ReceiptMode = GrnReceiptMode.Partial,
            Sites = "4",
        });

        using var client = _factory.Authenticated();

        var response = await client.PostAsJsonAsync(TriggerRoute + "?trigger=manual", new { poTypes = new[] { "capital" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = _factory.Service.Requests.Should().ContainSingle().Subject;
        sent.InvoiceNumber.Should().Be("INV-77");
        sent.ReceiptMode.Should().Be(GrnReceiptMode.Partial);
        sent.Sites.Should().Equal(4);
        sent.PoTypes.Should().Equal(PoGrnType.Capital);

        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body!["trigger"]!.GetValue<string>().Should().Be("Manual");
        body["grnsCreated"]!.GetValue<int>().Should().Be(1);
        body["results"]![0]!["grnNumber"]!.GetValue<string>().Should().Be("26-27/GR/NF1/000101");
    }

    [Fact]
    public async Task A_per_run_receipt_mode_overrides_the_saved_one()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate { IsActive = true, InvoiceNumber = "INV", ReceiptMode = GrnReceiptMode.Partial });
        using var client = _factory.Authenticated();

        await client.PostAsJsonAsync(TriggerRoute, new { receiptMode = "complete" });

        _factory.Service.Requests.Should().ContainSingle().Which.ReceiptMode.Should().Be(GrnReceiptMode.Complete);
    }

    [Theory]
    [InlineData("?trigger=hourly", "{}")]
    [InlineData("", "{\"poTypes\":[\"service\"]}")]
    public async Task An_unknown_trigger_or_PO_type_is_a_400(string query, string body)
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate { IsActive = true, InvoiceNumber = "INV" });
        using var client = _factory.Authenticated();

        var response = await client.PostAsync(TriggerRoute + query, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class PoToGrnApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-inbound-key";

    public InMemoryPoGrnConfigs Configs { get; } = new();

    public RecordingPoToGrnService Service { get; } = new();

    public void Reset()
    {
        Configs.Reset();
        Service.Requests.Clear();
    }

    public HttpClient Authenticated()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Automation-Api-Key", ApiKey);

        return client;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // No database: these tests are about the HTTP contract, and the repositories are replaced.
        var configuration = new Dictionary<string, string?>(TestConfiguration.Valid)
        {
            ["AutomationAgent:Database:ConnectionString"] = string.Empty,
        };

        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(configuration));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPoGrnAutomationConfigRepository>();
            services.RemoveAll<IPoToGrnService>();
            services.RemoveAll<IPoGrnHistory>();

            services.AddSingleton<IPoGrnAutomationConfigRepository>(Configs);
            services.AddSingleton<IPoToGrnService>(Service);
            services.AddScoped<IPoGrnHistory, NullPoGrnHistory>();
        });
    }
}

public sealed class InMemoryPoGrnConfigs : IPoGrnAutomationConfigRepository
{
    private PoGrnAutomationConfig _config = PoGrnAutomationConfig.CreateDefault(DateTimeOffset.UtcNow);

    public bool IsEnabled => true;

    public void Reset() => _config = PoGrnAutomationConfig.CreateDefault(DateTimeOffset.UtcNow);

    public void Set(PoGrnAutomationConfigUpdate update) => _config.Update(update, DateTimeOffset.UtcNow, "tests");

    public Task<PoGrnAutomationConfig> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_config);

    public Task<PoGrnAutomationConfig> UpdateAsync(PoGrnAutomationConfigUpdate update, string? updatedBy, CancellationToken cancellationToken)
    {
        _config.Update(update, DateTimeOffset.UtcNow, updatedBy);
        return Task.FromResult(_config);
    }

    public Task SaveAsync(PoGrnAutomationConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class RecordingPoToGrnService : IPoToGrnService
{
    public List<PoGrnSweepRequest> Requests { get; } = [];

    public Task<PoGrnSweepResult> ReceiveEligibleAsync(PoGrnSweepRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        PoGrnResult[] results =
        [
            new(101, "26-27/PR/NF1/000012", "R", 1, "V0001", 3, PoGrnReceiptStatus.Created,
                request.DryRun, request.DryRun ? null : 5001, request.DryRun ? null : "26-27/GR/NF1/000101", 1, 0, []),
        ];

        return Task.FromResult(new PoGrnSweepResult(1, request.DryRun, results, [], null));
    }
}
