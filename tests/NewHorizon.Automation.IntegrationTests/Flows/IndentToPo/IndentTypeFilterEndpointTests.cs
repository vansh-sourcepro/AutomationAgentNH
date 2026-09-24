using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;

namespace NewHorizon.Automation.IntegrationTests.Flows.IndentToPo;

/// <summary>
/// The <c>indentTypes</c> allow-list, at the HTTP boundary.
/// </summary>
/// <remarks>
/// These tests own the half the service-level ones cannot see: how the parameter is spelled on the
/// wire, what an unknown value does, and that whatever the caller wrote reaches the conversion as
/// the set they meant. The conversion itself is replaced by a recorder, so what is asserted is the
/// selection that was handed to it — the one thing that decides which indents can become orders.
/// </remarks>
[Collection(AgentHostCollection.Name)]
public sealed class IndentTypeFilterEndpointTests : IClassFixture<IndentTypeFilterEndpointTests.Factory>
{
    private const string ApiKey = "test-inbound-key";

    private readonly Factory _factory;

    public IndentTypeFilterEndpointTests(Factory factory) => _factory = factory;

    public static TheoryData<string[], string[]> EveryCombination() => new()
    {
        { ["regular"], ["Regular"] },
        { ["capital"], ["Capital"] },
        { ["service"], ["Service"] },
        { ["regular", "capital"], ["Regular", "Capital"] },
        { ["regular", "service"], ["Regular", "Service"] },
        { ["capital", "service"], ["Capital", "Service"] },
        { ["regular", "capital", "service"], ["Regular", "Capital", "Service"] },
    };

    // ---- The trigger -------------------------------------------------------

    [Fact]
    public async Task Startup_converts_nothing()
    {
        // The defect this feature exists to fix: the agent used to convert every authorised indent
        // of every type within two minutes of the process starting. There is now a sanctioned
        // background trigger — PoAutomationSchedulerService — but it is inert until a person turns a
        // type on: every IndentPoAutomationConfig row ships Disabled and inactive. The host below is
        // booted from the real appsettings.json with that default seed, so if the scheduler (or any
        // other timer or startup task) still reached the conversion, the recorder would have caught it.
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        // Prove the host is actually up and serving before concluding that it converted nothing.
        var health = await client.GetAsync("/api/automation/health");
        health.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);

        // Longer than any interval the deleted timer ever used, so "not yet" cannot pass for "never".
        await Task.Delay(TimeSpan.FromSeconds(3));

        recorder.SweptTypes.Should().BeEmpty("starting the agent must not convert anything");
        recorder.DiscoveredTypes.Should().BeEmpty("starting the agent must not even look for indents");
        recorder.VendorTypes.Should().BeEmpty();
        recorder.NamedIndentTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_gated_scheduler_may_reach_the_conversion()
    {
        // Startup_converts_nothing observes the behaviour; this observes the wiring. The one hosted
        // service allowed to reach indent → PO is PoAutomationSchedulerService, and only through a
        // config row a person set to Timer/Both and active. Anything else that auto-converts — an
        // ungated sweeper like the deleted IndentToPoSchedulerService / AutoConvert — fails here.
        using var client = Client();

        var hostedServiceNames = _factory.Services.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .ToList();

        hostedServiceNames.Should().NotContain(name =>
            name.Contains("IndentToPo", StringComparison.Ordinal)
            || name.Contains("AutoConvert", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task The_documented_trigger_converts_only_the_selected_types(
        string[] requested,
        string[] expected)
    {
        // POST /api/automation/indent-to-po, exactly as the caller writes it.
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po",
            new { indentTypes = requested, dryRun = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.SweptTypes.Should().Equal(expected.Select(Enum.Parse<IndentType>));

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.IndentTypes.Should().Equal(expected);
        payload.DryRun.Should().BeFalse();
        payload.Examined.Should().Be(expected.Length);
        payload.PurchaseOrdersCreated.Should().Be(expected.Length);
        payload.Indents.Select(indent => indent.IndentType).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task The_trigger_and_its_older_convert_path_behave_identically()
    {
        using var client = Client();
        var body = new { indentTypes = new[] { "regular", "service" }, dryRun = true };

        _factory.Recorder.Reset();
        var viaTrigger = await client.PostAsJsonAsync("/api/automation/indent-to-po", body);
        var fromTrigger = await viaTrigger.Content.ReadFromJsonAsync<ConvertResponse>();

        _factory.Recorder.Reset();
        var viaConvert = await client.PostAsJsonAsync("/api/automation/indent-to-po/convert", body);
        var fromConvert = await viaConvert.Content.ReadFromJsonAsync<ConvertResponse>();

        fromTrigger!.IndentTypes.Should().Equal(fromConvert!.IndentTypes);
        fromTrigger.Examined.Should().Be(fromConvert.Examined);
        fromTrigger.DryRun.Should().Be(fromConvert.DryRun);
    }

    // ---- indentNumbers -----------------------------------------------------

    [Fact]
    public async Task The_existing_request_without_indent_numbers_is_unchanged()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        // Byte for byte the body existing clients already send.
        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = new[] { "Regular" }, dryRun = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.Examined.Should().Be(1);
        payload.PurchaseOrdersCreated.Should().Be(1);

        // Nothing was named, so nothing is echoed and nothing can be unmatched.
        payload.IndentNumbers.Should().BeEmpty();
        payload.NotFound.Should().BeEmpty();
        recorder.SweptNumbers.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_indent_numbers_array_behaves_as_though_it_were_absent()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = new[] { "Regular" }, indentNumbers = Array.Empty<string>(), dryRun = false });

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.Examined.Should().Be(1);
        payload.IndentNumbers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_named_indent_number_reaches_the_conversion_and_is_echoed_back()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new
            {
                indentTypes = new[] { "Regular" },
                indentNumbers = new[] { "26-27/IN/NF1/000001" },
                dryRun = false,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.Examined.Should().Be(1);
        payload.IndentNumbers.Should().Equal("26-27/IN/NF1/000001");
        payload.NotFound.Should().BeEmpty();

        // The hand-off is the point: what the caller wrote must reach the conversion as the set
        // they meant, the same guarantee these tests make for indentTypes.
        recorder.SweptNumbers.Should().Equal("26-27/IN/NF1/000001");
    }

    [Fact]
    public async Task A_number_that_matches_nothing_is_reported_and_converts_nothing()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new
            {
                indentTypes = new[] { "Regular" },
                indentNumbers = new[] { "NOPE-999" },
                dryRun = false,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        // Not an error: an unknown number is an answer, and the answer is named.
        payload!.Examined.Should().Be(0);
        payload.PurchaseOrdersCreated.Should().Be(0);
        payload.NotFound.Should().Equal("NOPE-999");
    }

    [Fact]
    public async Task Mixed_valid_and_invalid_numbers_convert_the_valid_one_and_report_the_rest()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new
            {
                indentTypes = new[] { "Regular" },
                indentNumbers = new[] { "26-27/IN/NF1/000001", "NOPE-999" },
                dryRun = false,
            });

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        // One bad number must not cost the good one its purchase order.
        payload!.PurchaseOrdersCreated.Should().Be(1);
        payload.Indents.Should().ContainSingle();
        payload.NotFound.Should().Equal("NOPE-999");
    }

    [Fact]
    public async Task A_dry_run_with_a_named_number_plans_it_and_creates_nothing()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new
            {
                indentTypes = new[] { "Regular" },
                indentNumbers = new[] { "26-27/IN/NF1/000001" },
                dryRun = true,
            });

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.DryRun.Should().BeTrue();
        payload.PurchaseOrdersCreated.Should().Be(0);
        payload.PurchaseOrdersPlanned.Should().Be(1);
        payload.IndentNumbers.Should().Equal("26-27/IN/NF1/000001");
    }

    [Fact]
    public async Task A_misspelled_indent_numbers_property_is_refused_rather_than_ignored()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        // The defect this guards: "indentnumber" is not a parameter, so it used to be discarded
        // and the request became "convert every Regular indent" with dryRun false. A caller who
        // named one indent and got twenty orders has been silently overruled by a typo.
        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = new[] { "Regular" }, indentnumber = new[] { "000123" }, dryRun = false });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // And nothing was converted on the way to finding out.
        recorder.SweptTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_indent_id_and_indent_numbers_together_are_refused()
    {
        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new
            {
                indentId = 871,
                indentNumbers = new[] { "26-27/IN/NF1/000001" },
                dryRun = false,
            });

        // Two contradictory ways of saying which indents to convert. Answering one and ignoring
        // the other would convert something the caller did not ask for.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();

        problem!.Title.Should().Contain("indentId").And.Contain("indentNumbers");
    }

    [Fact]
    public async Task A_dry_run_reports_and_creates_nothing()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po",
            new { indentTypes = new[] { "regular" }, dryRun = true });

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.DryRun.Should().BeTrue();
        payload.PurchaseOrdersCreated.Should().Be(0);
        payload.PurchaseOrdersPlanned.Should().BeGreaterThan(0);
        // A dry run creates nothing by design, so reporting every indent as "skipped" would be
        // noise rather than news.
        payload.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task An_indent_that_produced_nothing_is_reported_with_its_reason()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();
        recorder.RefuseWith("Item 0500000DQ has no item/vendor purchase record in the ERP.");

        using var client = Client();

        try
        {
            var response = await client.PostAsJsonAsync(
                "/api/automation/indent-to-po",
                new { indentTypes = new[] { "capital" }, dryRun = false });

            var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

            payload!.PurchaseOrdersCreated.Should().Be(0);
            payload.Examined.Should().Be(1);

            var skipped = payload.Skipped.Should().ContainSingle().Subject;

            skipped.IndentType.Should().Be("Capital");
            skipped.Reasons.Should().ContainSingle()
                .Which.Should().Contain("no item/vendor purchase record");
        }
        finally
        {
            recorder.RefuseWith(null);
        }
    }

    [Fact]
    public async Task An_old_vendor_driven_call_to_the_trigger_is_refused_not_reinterpreted()
    {
        // This path used to be the vendor-driven form. Silently answering a vendor code with a
        // sweep of every authorised indent would be discovered only by the purchase orders it
        // left behind.
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po",
            new { indentType = "Regular", vendorCode = "A032" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        recorder.SweptTypes.Should().BeEmpty();
        recorder.VendorTypes.Should().BeEmpty();

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        problem!.Title.Should().Contain("does not take a vendor");
    }
    // ---- POST /convert -----------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task A_sweep_converts_only_the_selected_types(string[] requested, string[] expected)
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = requested });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // What the conversion was allowed to touch...
        recorder.SweptTypes.Should().Equal(expected.Select(Enum.Parse<IndentType>));

        // ...and what it actually produced, which must be the same set and nothing else.
        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();

        payload!.IndentTypes.Should().Equal(expected);
        payload.Indents.Select(indent => indent.IndentType).Distinct().Should().BeSubsetOf(expected);
        payload.Indents.Should().OnlyContain(indent => indent.Converted);
        payload.PurchaseOrdersCreated.Should().Be(expected.Length);
    }

    [Fact]
    public async Task An_unselected_type_is_never_offered_to_the_conversion()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = new[] { "service" } });

        recorder.SweptTypes.Should().Equal(IndentType.Service);
        recorder.SweptTypes.Should().NotContain(IndentType.Regular).And.NotContain(IndentType.Capital);
    }

    [Fact]
    public async Task No_selection_still_means_every_type()
    {
        // The existing convention, shared by every entry point. Asserted so a
        // future tightening of the empty case cannot happen by accident.
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync("/api/automation/indent-to-po/convert", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.SweptTypes.Should().Equal(IndentType.Regular, IndentType.Capital, IndentType.Service);
    }

    [Fact]
    public async Task An_explicitly_empty_selection_is_read_the_same_way_as_an_absent_one()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = Array.Empty<string>() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.SweptTypes.Should().Equal(IndentType.Regular, IndentType.Capital, IndentType.Service);
    }

    [Fact]
    public async Task A_type_named_twice_is_swept_once()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = new[] { "service", "SERVICE", "Service" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.SweptTypes.Should().Equal(IndentType.Service);

        var payload = await response.Content.ReadFromJsonAsync<ConvertResponse>();
        payload!.PurchaseOrdersCreated.Should().Be(1, "one selection is one sweep of that type");
    }

    [Fact]
    public async Task The_singular_form_still_works_and_reads_as_one_set_with_the_plural()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentType = "capital", indentTypes = new[] { "regular" } });

        recorder.SweptTypes.Should().Equal(IndentType.Regular, IndentType.Capital);
    }

    [Theory]
    [InlineData("labour")]
    [InlineData("Regular2")]
    [InlineData("0")]
    [InlineData("")]
    public async Task An_unknown_indent_type_is_refused(string unknown)
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentTypes = new[] { "regular", unknown } });

        // Empty strings are dropped as whitespace by the parser, so that one selection is simply
        // "regular"; everything else is a value the caller meant and got wrong.
        if (string.IsNullOrWhiteSpace(unknown))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            recorder.SweptTypes.Should().Equal(IndentType.Regular);
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        recorder.SweptTypes.Should().BeEmpty("nothing may be converted on a request that was refused");

        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        problem!.Title.Should().Contain(unknown).And.Contain("Regular, Capital, Service");
    }

    [Fact]
    public async Task A_named_indent_is_converted_under_the_same_allow_list()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/convert",
            new { indentId = 55, indentTypes = new[] { "service" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.NamedIndentTypes.Should().Equal(IndentType.Service);
    }

    // ---- GET /eligible -----------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task The_eligible_list_reads_the_same_selection(string[] requested, string[] expected)
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var query = string.Join("&", requested.Select(type => $"indentTypes={type}"));
        var response = await client.GetAsync($"/api/automation/indent-to-po/eligible?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.DiscoveredTypes.Should().Equal(expected.Select(Enum.Parse<IndentType>));
    }

    [Fact]
    public async Task The_eligible_list_also_accepts_one_comma_separated_value()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.GetAsync(
            "/api/automation/indent-to-po/eligible?indentTypes=regular,service");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.DiscoveredTypes.Should().Equal(IndentType.Regular, IndentType.Service);
    }

    // ---- POST / (the vendor-driven form) -----------------------------------

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public async Task The_vendor_form_orders_only_the_selected_material_types(
        string[] requested,
        string[] expected)
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/vendor",
            new { indentTypes = requested, vendorCode = "A032" });

        var materialTypes = expected.Where(type => type != "Service").ToList();

        if (materialTypes.Count == 0)
        {
            // Service only: this endpoint runs the material sequence, and the ERP has no
            // vendor-first pending-lines question for service indents.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            recorder.VendorTypes.Should().BeEmpty();
            return;
        }

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // One run of the material sequence per selected material type, and none for Service.
        recorder.VendorTypes.Should().Equal(materialTypes.Select(Enum.Parse<IndentType>));

        var payload = await response.Content.ReadFromJsonAsync<VendorResponse>();

        payload!.IndentTypes.Should().Equal(expected);
        payload.PurchaseOrders.Should().HaveCount(materialTypes.Count);

        if (expected.Contains("Service"))
        {
            payload.Notes.Should().ContainSingle().Which.Should().Contain("Service indents cannot be ordered by vendor");
        }
    }

    [Fact]
    public async Task The_vendor_form_still_accepts_the_singular_type_it_shipped_with()
    {
        var recorder = _factory.Recorder;
        recorder.Reset();

        using var client = Client();

        var response = await client.PostAsJsonAsync(
            "/api/automation/indent-to-po/vendor",
            new { indentType = "Regular", vendorCode = "A032" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recorder.VendorTypes.Should().Equal(IndentType.Regular);
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Automation-Api-Key", ApiKey);

        return client;
    }

    private sealed record ConvertResponse(
        int Examined,
        int PurchaseOrdersCreated,
        int PurchaseOrdersPlanned,
        bool DryRun,
        IReadOnlyList<string> IndentTypes,
        IReadOnlyList<string> IndentNumbers,
        IReadOnlyList<ConvertedIndent> Indents,
        IReadOnlyList<SkippedIndent> Skipped,
        IReadOnlyList<string> NotFound);

    private sealed record ConvertedIndent(
        long IndentId,
        string IndentNumber,
        string IndentType,
        int SiteId,
        bool Converted);

    private sealed record SkippedIndent(
        long IndentId,
        string IndentNumber,
        string IndentType,
        int SiteId,
        IReadOnlyList<string> Reasons);

    private sealed record VendorResponse(
        IReadOnlyList<string> IndentTypes,
        IReadOnlyList<CreatedPo> PurchaseOrders,
        IReadOnlyList<string> Notes);

    private sealed record CreatedPo(string PoNumber, long PoId, string VendorCode, int ItemCount);

    private sealed record ProblemPayload(string Title);

    /// <summary>Hosts the agent with the conversion replaced by <see cref="ConversionRecorder"/>.</summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public ConversionRecorder Recorder { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration =>
                configuration.AddInMemoryCollection(TestConfiguration.Valid));

            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            // Nothing is suppressed here. The host's content root is the Worker project, so its
            // real appsettings.json is loaded — and the point of Startup_converts_nothing below is
            // that loading it converts nothing, because there is no longer a timer to suppress.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IIndentToPoService>();
                services.AddSingleton<IIndentToPoService>(Recorder);

                // These tests exercise the indentTypes / indentNumbers filter. Force the "no
                // automation database" repository so the conversion's config stamping is a no-op
                // regardless of whether the test machine has LocalDB with the config rows.
                services.RemoveAll<NewHorizon.Automation.Application.Flows.IndentToPo.IIndentPoAutomationConfigRepository>();
                services.AddScoped<NewHorizon.Automation.Application.Flows.IndentToPo.IIndentPoAutomationConfigRepository,
                    NewHorizon.Automation.Application.Flows.IndentToPo.NullIndentPoAutomationConfigRepository>();
            });
        }
    }

    /// <summary>
    /// Stands in for the real conversion and remembers the allow-list it was handed.
    /// </summary>
    /// <remarks>
    /// It answers as though exactly one convertible indent of every type existed, so "how many
    /// orders came back" is a direct count of how many types the request was allowed to convert.
    /// The real filtering is proved against a fake ERP in the unit suite; what is proved here is
    /// that the request the caller wrote arrives as the selection they meant.
    /// </remarks>
    public sealed class ConversionRecorder : IIndentToPoService
    {
        private readonly List<IndentType> _swept = [];
        private readonly List<IndentType> _discovered = [];
        private readonly List<IndentType> _vendor = [];
        private readonly List<IndentType> _named = [];
        private readonly List<string> _sweptNumbers = [];

        public IReadOnlyList<IndentType> SweptTypes => _swept;

        public IReadOnlyList<IndentType> DiscoveredTypes => _discovered;

        public IReadOnlyList<IndentType> VendorTypes => _vendor;

        public IReadOnlyList<IndentType> NamedIndentTypes => _named;

        /// <summary>The indent numbers the endpoint passed down, so the hand-off can be asserted.</summary>
        public IReadOnlyList<string> SweptNumbers => _sweptNumbers;

        private string? _refusal;

        public void Reset()
        {
            _swept.Clear();
            _discovered.Clear();
            _vendor.Clear();
            _named.Clear();
            _sweptNumbers.Clear();
        }

        /// <summary>
        /// Makes every indent come back converted-to-nothing with this reason, the way the real
        /// conversion reports a vendor group the ERP refused. Null restores the happy path.
        /// </summary>
        public void RefuseWith(string? reason) => _refusal = reason;

        public Task<IndentPoResult> CreateAsync(IndentPoRequest request, CancellationToken cancellationToken)
        {
            if (request.IndentType == IndentType.Service)
            {
                // The real service does the same, and the endpoint is not supposed to reach it.
                throw new ErpBusinessException("Service indents are converted per indent.", "test");
            }

            _vendor.Add(request.IndentType);

            return Task.FromResult(new IndentPoResult(
                $"26-27/XX/NF1/{_vendor.Count:000000}", 100 + _vendor.Count, request.VendorCode ?? "A032", 1));
        }

        public Task<IReadOnlyList<EligibleIndent>> FindEligibleAsync(
            IndentDiscoveryRequest request,
            CancellationToken cancellationToken)
        {
            var selection = IndentTypeSelection.Normalise(request.IndentTypes);
            _discovered.AddRange(selection);

            return Task.FromResult<IReadOnlyList<EligibleIndent>>(
                selection.Select(Eligible).ToList());
        }

        public Task<IndentConversionResult> CreateFromIndentAsync(
            IndentReference indent,
            CancellationToken cancellationToken) =>
            Task.FromResult(Converted(indent));

        public Task<IndentConversionResult> ConvertIndentAsync(
            long indentId,
            string? indentNumber,
            IReadOnlyList<int>? sites,
            bool dryRun,
            CancellationToken cancellationToken) =>
            ConvertIndentAsync(indentId, indentNumber, sites, null, dryRun, cancellationToken);

        public Task<IndentConversionResult> ConvertIndentAsync(
            long indentId,
            string? indentNumber,
            IReadOnlyList<int>? sites,
            IReadOnlyList<IndentType>? indentTypes,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            var selection = IndentTypeSelection.Normalise(indentTypes);
            _named.AddRange(selection);

            return Task.FromResult(Converted(Reference(selection[0], indentId)));
        }

        public Task<IndentSweepResult> ConvertEligibleAsync(
            IndentSweepRequest request,
            CancellationToken cancellationToken)
        {
            var selection = IndentTypeSelection.Normalise(request.IndentTypes);
            _swept.AddRange(selection);

            if (request.IndentNumbers is { Count: > 0 })
            {
                _sweptNumbers.AddRange(request.IndentNumbers);
            }

            // A dry run creates nothing and counts what it would have created, exactly as the real
            // conversion does — otherwise a test could not tell the two apart here.
            var numbers = IndentNumberSelection.Normalise(request.IndentNumbers);

            var results = selection
                .Select(type => Reference(type))

                // Narrowed the way the real discovery narrows, so an unmatched number genuinely
                // produces no result here and the endpoint has something to report.
                .Where(indent => IndentNumberSelection.Allows(numbers, indent))
                .Select(indent => request.DryRun ? Planned(indent) : Converted(indent))
                .ToList();

            return Task.FromResult(new IndentSweepResult(results.Count, request.DryRun, results));
        }

        private static EligibleIndent Eligible(IndentType type) =>
            new(Reference(type), "Open", new DateOnly(2026, 8, 1), "07 - BABU SINGH");

        private static IndentReference Reference(IndentType type, long? indentId = null) =>
            new(indentId ?? 100 + (long)type, "26-27", "IN", "000001", 1, "NF1", type);

        private IndentConversionResult Converted(IndentReference indent) =>
            _refusal is null
                ? new(indent, [new IndentPoResult("26-27/XX/NF1/000001", 1, "A032", 1)], [])
                : new(indent, [], [_refusal]);

        /// <summary>One order worked out and not placed, which is what a dry run reports.</summary>
        private static IndentConversionResult Planned(IndentReference indent) =>
            new(indent, [], [$"Would raise a purchase order for {indent.DisplayNumber}."], PlannedPurchaseOrders: 1);
    }
}
