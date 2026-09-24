using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Flows.PoToGrn;
using NewHorizon.Automation.Domain.Flows.PoToGrn;
using NewHorizon.Automation.ErpClient.Authentication;
using NewHorizon.Automation.ErpClient.Flows.PoToGrn;

namespace NewHorizon.Automation.UnitTests.Flows.PoToGrn;

/// <summary>
/// An in-memory ERP for PO → GRN: routes by path fragment, records every call. This flow's own,
/// because flows — and their tests — never reach into each other's folders.
/// </summary>
internal sealed class GrnFakeErp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<string, JsonObject?, JsonNode?>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, JsonObject? Body)> Requests { get; } = [];

    /// <summary>What <c>inventory/grn/create</c> answers: a message, or a failure status.</summary>
    public Func<JsonObject, (int Status, bool Success, string Message)> CreateResponse { get; set; } =
        _ => (200, true, "GRNCreated#26-27/GR/NF1/000101#5001");

    public GrnFakeErp Route(string pathFragment, JsonNode? data)
    {
        _routes[pathFragment] = (_, _) => data?.DeepClone();
        return this;
    }

    public GrnFakeErp Route(string pathFragment, Func<string, JsonNode?> respond)
    {
        _routes[pathFragment] = (path, _) => respond(path);
        return this;
    }

    public IEnumerable<JsonObject> CreateBodies =>
        Requests.Where(request => request.Path.Contains("grn/create", StringComparison.OrdinalIgnoreCase))
            .Select(request => request.Body)
            .OfType<JsonObject>();

    public int CallCount(string pathFragment) =>
        Requests.Count(request => request.Path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        JsonObject? body = null;

        if (request.Content is not null)
        {
            var raw = await request.Content.ReadAsStringAsync(cancellationToken);
            body = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw) as JsonObject;
        }

        Requests.Add((path, body));

        if (path.Contains("grn/create", StringComparison.OrdinalIgnoreCase))
        {
            var (status, success, message) = CreateResponse(body!);

            return Json(status, new JsonObject { ["data"] = null, ["success"] = success, ["message"] = message });
        }

        var route = _routes
            .Where(entry => path.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Key.Length)
            .FirstOrDefault();

        if (route.Key is null)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"GrnFakeErp has no route for '{path}'.", Encoding.UTF8, "text/plain"),
            };
        }

        return Json(200, new JsonObject { ["data"] = route.Value(path, body), ["success"] = true });
    }

    private static HttpResponseMessage Json(int status, JsonNode envelope) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(envelope.ToJsonString(new JsonSerializerOptions()), Encoding.UTF8, "application/json"),
        };
}

/// <summary>ERP answers shaped like the live ones (field names from WebAPICore's models).</summary>
internal static class GrnFixtures
{
    public static readonly DateOnly Today = new(2026, 9, 24);

    public static GrnFakeErp StandardErp(params JsonObject[] poListRows) =>
        new GrnFakeErp()
            .Route("getCommomSystemConfig", new JsonObject
            {
                ["financePeriodSetting"] = new JsonArray(
                    new JsonObject { ["siteId"] = 1, ["periodSDt"] = "2026-04-01T00:00:00", ["periodEDt"] = "2027-03-31T00:00:00" }),
            })
            .Route("inventoryPolicy/getDetail", new JsonObject { ["islinelevelwhgrn"] = false })
            .Route("getpotogrnredirectdata", new JsonArray(
                new JsonObject { ["povndcd"] = "V0001", ["powhid"] = 3, ["powhcd"] = "RM", ["povndname"] = "Acme Steel", ["vndcurcd"] = "INR", ["potype"] = "R" }))
            .Route("getDefaultDocumentDetail", new JsonArray(
                new JsonObject { ["groupCode"] = "GR", ["locationCode"] = "NF1", ["isLocationRequired"] = true, ["isAutoNumberGenerated"] = true, ["isDefault"] = "Y" }))
            .Route("getAllRateStructureDetails", new JsonArray(
                new JsonObject { ["msprtcd"] = "CGST", ["rtamt"] = 9m },
                new JsonObject { ["msprtcd"] = "SGST", ["rtamt"] = 9m }))
            .WithPoList(poListRows);

    public static GrnFakeErp WithPoList(this GrnFakeErp erp, params JsonObject[] rows)
    {
        var regular = new JsonArray([.. rows.Where(row => row["poType"]!.GetValue<string>() == "R").Select(row => (JsonNode)row.DeepClone())]);
        var capital = new JsonArray([.. rows.Where(row => row["poType"]!.GetValue<string>() == "C").Select(row => (JsonNode)row.DeepClone())]);

        // The list is asked once per type; the type travels in the body, which the route cannot
        // see, so answer by call order: Regular first, then Capital.
        var calls = 0;

        return erp.Route("POEntry/List", _ => calls++ == 0 ? regular : capital);
    }

    public static JsonObject PoRow(long id, string number = "000012", string type = "R", bool isgrn = true) => new()
    {
        ["totalRows"] = 1,
        ["id"] = id,
        ["year"] = "26-27",
        ["grp"] = "PR",
        ["site"] = "NF1",
        ["siteid"] = 1,
        ["nmbr"] = number,
        ["date"] = "2026-09-20T00:00:00",
        ["vendor"] = "V0001 - Acme Steel",
        ["poType"] = type,
        ["rowstatus"] = "O",
        ["isgrn"] = isgrn,
        ["isAmendment"] = true,
    };

    public static JsonObject Line(long poId, string item, decimal pending = 10m, Action<JsonObject>? tweak = null)
    {
        var line = new JsonObject
        {
            ["xgrndpoid"] = poId,
            ["itmcode"] = item,
            ["itmname"] = item + " name",
            ["iuom"] = "NOS",
            ["puom"] = "NOS",
            ["warecode"] = "RM",
            ["warename"] = "Raw material",
            ["xgrndwhid"] = 3,
            ["basicprice"] = 100m,
            ["popuomqty"] = pending,
            ["popuomrcvd"] = 0m,
            ["pendinggrnpuom"] = pending,
            ["pendinggrniuom"] = pending,
            ["iuomconv"] = 1m,
            ["puomconv"] = 1m,
            ["convfact"] = "P",
            ["convlimit"] = 0m,
            ["disctype"] = "N",
            ["discvalue"] = 0m,
            ["pohdiscpa"] = "None",
            ["pohdiscval"] = 0m,
            ["pohpovalbfdisc"] = 1000m,
            ["xgrnddensity"] = 0m,
            ["xgrndformula"] = "",
            ["xgrndsize"] = "",
            ["xgrndpoamd"] = 0,
            ["xgrndpoline"] = 1,
            ["mimbchreqd"] = false,
            ["miminwdreq"] = false,
            ["mimheatreq"] = false,
            ["mimitmsrreqd"] = false,
            ["mimmfgreq"] = false,
            ["isShelfLife"] = false,
            ["blkpotype"] = "R",
            ["rcmtype"] = "IGST",
            ["rateStructureCode"] = "P0002",
            ["podate"] = "2026-09-20T00:00:00",
            ["poamddate"] = "",
            ["grnhsncode"] = "7208",
            ["pohsncode"] = "7208",
            ["xgrndbincd"] = "",
            ["xgrndqcflg"] = false,
        };

        tweak?.Invoke(line);
        return line;
    }

    public static JsonArray TaxRows(long poId, params string[] items) =>
        new([.. items.SelectMany(item => new[]
        {
            (JsonNode)TaxRow(poId, item, "CGST", 1),
            TaxRow(poId, item, "SGST", 2),
        })]);

    public static JsonObject PoLines(long poId, JsonArray lines, JsonArray? taxes = null) => new()
    {
        ["itemDetailsModule"] = lines,
        ["ratestruDetailsModule"] = taxes ?? TaxRows(poId, [.. lines.OfType<JsonObject>().Select(line => line["itmcode"]!.GetValue<string>())]),
    };

    private static JsonObject TaxRow(long poId, string item, string rateCode, int index) => new()
    {
        ["index"] = index,
        ["rateCode"] = rateCode,
        ["rateDesc"] = rateCode,
        ["ie"] = "E",
        ["pv"] = "P",
        ["applicableOn"] = "0",
        ["taxValue"] = 9m,
        ["postnonpost"] = true,
        ["rateAmount"] = 0m,
        ["currencyCode"] = "INR",
        ["xgrndpoid"] = poId,
        ["xdtdtmcd"] = item,
        ["itemCode"] = item,
        ["taxRateCode"] = "P0002",
        ["mprtaxtyp"] = "P",
    };
}

internal sealed class GrnSingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://erp.test") };
}

internal sealed class GrnStubTokenProvider : IErpTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");

    public Task<int> GetUserIdAsync(CancellationToken cancellationToken) => Task.FromResult(7);

    public void Invalidate()
    {
    }
}

internal sealed class GrnFixedClock : IClock
{
    public DateTimeOffset UtcNow { get; init; } = new(2026, 9, 24, 6, 0, 0, TimeSpan.Zero);

    public TimeOnly LocalTimeOfDay { get; init; } = new(12, 0);

    public DateOnly LocalDate { get; init; } = GrnFixtures.Today;
}

/// <summary>A settings row in memory; the toggle can be flipped mid-run.</summary>
internal sealed class InMemoryGrnConfigs : IPoGrnAutomationConfigRepository
{
    private readonly PoGrnAutomationConfig _config = PoGrnAutomationConfig.CreateDefault(DateTimeOffset.UtcNow);

    public InMemoryGrnConfigs(bool active = true) =>
        _config.Update(new PoGrnAutomationConfigUpdate { IsActive = active, InvoiceNumber = "INV-AUTO" }, DateTimeOffset.UtcNow, "test");

    public int Reads { get; private set; }

    public Action<int>? OnRead { get; set; }

    public bool IsEnabled => true;

    public Task<PoGrnAutomationConfig> GetAsync(CancellationToken cancellationToken)
    {
        OnRead?.Invoke(++Reads);
        return Task.FromResult(_config);
    }

    public void TurnOff() => _config.Update(new PoGrnAutomationConfigUpdate { IsActive = false }, DateTimeOffset.UtcNow, "test");

    public Task<PoGrnAutomationConfig> UpdateAsync(PoGrnAutomationConfigUpdate update, string? updatedBy, CancellationToken cancellationToken)
    {
        _config.Update(update, DateTimeOffset.UtcNow, updatedBy);
        return Task.FromResult(_config);
    }

    public Task SaveAsync(PoGrnAutomationConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RecordingGrnHistory : IPoGrnHistory
{
    public List<PoGrnOutcome> Outcomes { get; } = [];

    public bool IsEnabled => true;

    public Guid? RunId { get; } = Guid.NewGuid();

    public Task StartRunAsync(StartGrnRunRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RecordAsync(PoGrnOutcome outcome, CancellationToken cancellationToken)
    {
        Outcomes.Add(outcome);
        return Task.CompletedTask;
    }

    public Task CompleteRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FailRunAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class GrnServiceUnderTest
{
    public static PoToGrnService Build(
        GrnFakeErp erp,
        IPoGrnAutomationConfigRepository? configs = null,
        IPoGrnHistory? history = null,
        PoGrnOptions? options = null) =>
        new(
            new GrnSingleClientFactory(erp),
            new GrnStubTokenProvider(),
            Options.Create(new PoGrnEndpointOptions()),
            Options.Create(options ?? new PoGrnOptions { Sites = [1], DomesticCurrency = "INR" }),
            new GrnFixedClock(),
            configs ?? new InMemoryGrnConfigs(),
            history ?? new NullPoGrnHistory(),
            NullLogger<PoToGrnService>.Instance);

    public static PoGrnSweepRequest Request(GrnReceiptMode mode = GrnReceiptMode.Complete, bool dryRun = false) => new()
    {
        ReceiptMode = mode,
        InvoiceNumber = "INV-AUTO",
        DryRun = dryRun,
    };
}
