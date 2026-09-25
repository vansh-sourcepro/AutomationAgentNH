using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;
using NewHorizon.Automation.Worker.Services;

namespace NewHorizon.Automation.IntegrationTests.Flows.PoToGrn;

/// <summary>
/// The PO → GRN APIs end to end through the real host — routing, ERP-token and API-key auth, form
/// rights, body binding, the toggle, saved filters and invoice-number rules — with the settings row
/// in memory and the ERP conversation and rights lookup replaced, so no database or ERP is needed.
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

    [Fact]
    public async Task The_trigger_requires_the_inbound_API_key()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(TriggerRoute, new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "")]
    [InlineData("PUT", "/enabled")]
    [InlineData("PUT", "/po-types")]
    [InlineData("PUT", "/po-numbers")]
    public async Task The_settings_API_needs_the_API_key_or_an_ERP_login(string method, string path)
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod(method), ConfigRoute + path)
        {
            Content = JsonContent.Create(new { }),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_API_key_alone_can_switch_it_on_set_types_and_a_PO_with_no_ERP_login()
    {
        using var client = _factory.Authenticated();

        (await client.PutAsJsonAsync(ConfigRoute + "/enabled", new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(ConfigRoute + "/po-types", new { poTypes = new[] { "Regular" } })).StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await (await client.PutAsJsonAsync(ConfigRoute + "/po-numbers", new { poNumbers = "26-27/TE/NF1/000190" }))
            .Content.ReadFromJsonAsync<JsonObject>();

        saved!["isActive"]!.GetValue<bool>().Should().BeTrue();
        saved["poTypes"]!.AsArray().Select(type => type!.GetValue<string>()).Should().Equal("Regular");
        saved["poNumbers"]!.GetValue<string>().Should().Be("26-27/TE/NF1/000190");
        saved["updatedBy"]!.GetValue<string>().Should().Be("api-key");
        _factory.Rights.FormsAsked.Should().BeEmpty();
    }

    [Fact]
    public async Task Viewing_needs_the_I_right_and_changing_needs_the_E_right_on_form_011171()
    {
        using var viewer = _factory.ErpUser(rights: "I");

        (await viewer.GetAsync(ConfigRoute)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PutAsJsonAsync(ConfigRoute + "/enabled", new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _factory.Rights.FormsAsked.Should().OnlyContain(form => form == "011171");
    }

    [Fact]
    public async Task The_settings_can_be_read_and_changed_in_place_and_are_stamped_with_the_ERP_user()
    {
        using var client = _factory.ErpUser();

        var put = await client.PutAsJsonAsync(ConfigRoute, new
        {
            receiptMode = "partial",
            invoiceNumber = "INV-2026-09",
            runMode = "Both",
            scheduleTime = "18:30",
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var saved = await client.GetFromJsonAsync<JsonObject>(ConfigRoute);
        saved!["receiptMode"]!.GetValue<string>().Should().Be("Partial");
        saved["invoiceNumber"]!.GetValue<string>().Should().Be("INV-2026-09");
        saved["scheduleTime"]!.GetValue<string>().Should().StartWith("18:30");
        saved["updatedBy"]!.GetValue<string>().Should().Be(PoToGrnApiFactory.UserFullName);
    }

    [Fact]
    public async Task The_master_switch_has_its_own_route()
    {
        using var client = _factory.ErpUser();

        var on = await client.PutAsJsonAsync(ConfigRoute + "/enabled", new { enabled = true });
        var off = await client.PutAsJsonAsync(ConfigRoute + "/enabled", new { enabled = false });

        (await on.Content.ReadFromJsonAsync<JsonObject>())!["isActive"]!.GetValue<bool>().Should().BeTrue();
        (await off.Content.ReadFromJsonAsync<JsonObject>())!["isActive"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task PO_types_are_saved_and_an_empty_list_means_both()
    {
        using var client = _factory.ErpUser();

        var capital = await client.PutAsJsonAsync(ConfigRoute + "/po-types", new { poTypes = new[] { "capital" } });
        (await capital.Content.ReadFromJsonAsync<JsonObject>())!["poTypes"]!.AsArray()
            .Select(type => type!.GetValue<string>()).Should().Equal("Capital");

        var both = await client.PutAsJsonAsync(ConfigRoute + "/po-types", new { poTypes = Array.Empty<string>() });
        (await both.Content.ReadFromJsonAsync<JsonObject>())!["poTypes"]!.AsArray().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Service")]
    [InlineData("Blanket")]
    [InlineData("1")]
    public async Task Only_Regular_and_Capital_are_PO_types(string type)
    {
        using var client = _factory.ErpUser();

        var response = await client.PutAsJsonAsync(ConfigRoute + "/po-types", new { poTypes = new[] { type } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task One_PO_can_be_saved_and_cleared()
    {
        using var client = _factory.ErpUser();

        var one = await client.PutAsJsonAsync(ConfigRoute + "/po-numbers", new { poNumbers = " 26-27/TE/NF1/000190 " });
        (await one.Content.ReadFromJsonAsync<JsonObject>())!["poNumbers"]!.GetValue<string>().Should().Be("26-27/TE/NF1/000190");

        var cleared = await client.PutAsJsonAsync(ConfigRoute + "/po-numbers", new { poNumbers = "" });
        (await cleared.Content.ReadFromJsonAsync<JsonObject>())!["poNumbers"].Should().BeNull();
    }

    [Theory]
    [InlineData("{\"receiptMode\":\"Sometimes\"}")]
    [InlineData("{\"runMode\":\"Hourly\"}")]
    [InlineData("{\"scheduleTime\":\"half past six\"}")]
    [InlineData("{\"sites\":\"1,x\"}")]
    public async Task Bad_settings_are_a_400(string body)
    {
        using var client = _factory.ErpUser();

        var response = await client.PutAsync(ConfigRoute, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_empty_run_uses_the_saved_PO_types_and_PO_numbers()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate
        {
            IsActive = true,
            InvoiceNumber = "INV",
            PoTypes = ["Regular"],
            PoNumbers = "26-27/TE/NF1/000190",
        });

        using var client = _factory.Authenticated();

        (await client.PostAsJsonAsync(TriggerRoute + "?trigger=timer", new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = _factory.Service.Requests.Should().ContainSingle().Subject;
        sent.PoTypes.Should().Equal(PoGrnType.Regular);
        sent.PoNumbers.Should().Equal("26-27/TE/NF1/000190");
    }

    [Fact]
    public async Task A_run_that_names_its_own_types_or_POs_overrides_the_saved_ones()
    {
        _factory.Configs.Set(new PoGrnAutomationConfigUpdate
        {
            IsActive = true,
            InvoiceNumber = "INV",
            PoTypes = ["Regular"],
            PoNumbers = "26-27/TE/NF1/000190",
        });

        using var client = _factory.Authenticated();

        await client.PostAsJsonAsync(TriggerRoute, new { poTypes = new[] { "capital" }, poIds = new[] { 55L } });

        var sent = _factory.Service.Requests.Should().ContainSingle().Subject;
        sent.PoTypes.Should().Equal(PoGrnType.Capital);
        sent.PoIds.Should().Equal(55L);
        sent.PoNumbers.Should().BeNull();
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

    public const string UserFullName = "Store Keeper";

    // Short like the ERP's own key (the agent verifies HS256 itself, so the length is fine).
    private const string SigningKey = "test-erp-secret";

    public InMemoryPoGrnConfigs Configs { get; } = new();

    public RecordingPoToGrnService Service { get; } = new();

    public FakeErpUserRights Rights { get; } = new();

    public void Reset()
    {
        Configs.Reset();
        Service.Requests.Clear();
        Rights.Reset();
    }

    /// <summary>A machine caller: the inbound API key, no user.</summary>
    public HttpClient Authenticated()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Automation-Api-Key", ApiKey);

        return client;
    }

    /// <summary>A browser caller: a signed ERP token, holding <paramref name="rights"/> on every form.</summary>
    public HttpClient ErpUser(string rights = "EI")
    {
        Rights.Letters = rights;

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());

        return client;
    }

    /// <summary>An HS256 token shaped like the ERP's: sourcepro issuer/audience, user claims.</summary>
    private static string Token()
    {
        static string Encode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Encode(Encoding.ASCII.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "sourcepro.issuer",
            ["aud"] = "sourcepro.audience",
            ["nbf"] = now - 60,
            ["exp"] = now + 3600,
            ["id"] = "42",
            ["userFullName"] = UserFullName,
        }));

        var signature = HMACSHA256.HashData(Encoding.ASCII.GetBytes(SigningKey), Encoding.ASCII.GetBytes(header + "." + payload));

        return header + "." + payload + "." + Encode(signature);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // No database: these tests are about the HTTP contract, and the repositories are replaced.
        var configuration = new Dictionary<string, string?>(TestConfiguration.Valid)
        {
            ["AutomationAgent:Database:ConnectionString"] = string.Empty,
            ["AutomationAgent:InboundJwt:Issuer"] = "sourcepro.issuer",
            ["AutomationAgent:InboundJwt:Audience"] = "sourcepro.audience",
            ["AutomationAgent:InboundJwt:SigningKey"] = SigningKey,
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
            services.RemoveAll<IErpUserRightsService>();

            services.AddSingleton<IErpUserRightsService>(Rights);
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

/// <summary>Stands in for the ERP's rights lookup: every form answers <see cref="Letters"/>.</summary>
public sealed class FakeErpUserRights : IErpUserRightsService
{
    public string Letters { get; set; } = "EI";

    public ConcurrentQueue<string> FormsAsked { get; } = new();

    public void Reset()
    {
        Letters = "EI";
        FormsAsked.Clear();
    }

    public Task<string> RightsForAsync(HttpContext httpContext, string formId, CancellationToken cancellationToken)
    {
        FormsAsked.Enqueue(formId);
        return Task.FromResult(Letters);
    }
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
