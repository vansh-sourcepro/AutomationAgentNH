using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Application.Workflows.Definitions;
using NewHorizon.Automation.Domain.Configuration;
using NewHorizon.Automation.Domain.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Jobs;
using NewHorizon.Automation.ErpClient.Flows.IndentToPo;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.IntegrationTests.Flows.IndentToPo;

[CollectionDefinition(Name)]
public sealed class ProcessJobApiCollection : ICollectionFixture<ProcessJobApiFixture>
{
    public const string Name = "process-job-api";
}

/// <summary>
/// Hosts the agent against a database of its own, with the ERP conversion scripted.
/// </summary>
/// <remarks>
/// One host and one database for the whole class: the assertions are about what a call recorded,
/// so they must not compete with rows another suite left behind. Every test claims a fresh indent
/// id, which is what keeps them independent of each other's order.
/// </remarks>
public sealed class ProcessJobApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ApiKey = "test-inbound-key";
    public const string HeaderName = "X-Automation-Api-Key";

    private const string MasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=5;";

    private readonly string _databaseName = $"NewHorizon_ProcessApi_{Guid.NewGuid():N}";

    public static string SkipReason => "SQL Server LocalDB is not available on this machine.";

    public ScriptedConversion Conversion { get; } = new();

    public bool IsAvailable { get; private set; }

    private string ConnectionString =>
        $"Server=(localdb)\\MSSQLLocalDB;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;";

    public async Task InitializeAsync()
    {
        try
        {
            await using var probe = new SqlConnection(MasterConnectionString);
            await probe.OpenAsync();
        }
        catch (Exception exception) when (exception is SqlException or PlatformNotSupportedException)
        {
            IsAvailable = false;
            return;
        }

        await using var context = CreateDbContext();
        await context.Database.MigrateAsync();

        // The migration seeds the three IndentPoAutomationConfig rows switched off. These suites
        // are about what a conversion recorded, not about the master toggle, so turn it on for the
        // class — PoAutomationToggleGateTests flips it off explicitly and restores it.
        await context.Database.ExecuteSqlRawAsync("UPDATE IndentPoAutomationConfig SET IsActive = 1");

        IsAvailable = true;
    }

    /// <summary>Flips the PO Automation master toggle for the whole class's database.</summary>
    public async Task SetPoAutomationActiveAsync(bool active)
    {
        await using var context = CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE IndentPoAutomationConfig SET IsActive = {0}", active ? 1 : 0);
    }

    /// <summary>The most recently created indent-conversion job, for a retry test.</summary>
    public async Task<Guid> LatestConversionJobIdAsync()
    {
        await using var context = CreateDbContext();

        return await context.Jobs
            .Where(job => job.ConversionId != null)
            .OrderByDescending(job => job.CreatedAtUtc)
            .Select(job => job.Id)
            .FirstAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (IsAvailable)
        {
            await using var context = CreateDbContext();
            await context.Database.EnsureDeletedAsync();
        }

        await DisposeAsync();
    }

    public HttpClient Anonymous() => CreateClient();

    public HttpClient Authenticated()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(HeaderName, ApiKey);

        return client;
    }

    public async Task SetModeAsync(string mode)
    {
        using var scope = Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IAutomationConfigRepository>();

        await configs.UpsertAsync(
            WorkflowNames.IndentToPurchaseOrder,
            new AutomationConfigUpdate { Mode = Enum.Parse<AutomationMode>(mode) },
            "tests",
            CancellationToken.None);
    }

    /// <summary>
    /// The company the grid should report — read from the host's own options rather than restated,
    /// so the assertion cannot drift from what the endpoint actually uses.
    /// </summary>
    public int ConfiguredCompanyId =>
        Services.GetRequiredService<IOptions<AutomationAgentOptions>>().Value.ErpApi.CompanyId;

    public async Task<int> CountRunsAsync()
    {
        await using var context = CreateDbContext();

        return await context.Runs.CountAsync();
    }

    public async Task<int> CountExecutionsAsync(long indentId)
    {
        await using var context = CreateDbContext();

        return await context.Jobs
            .CountAsync(job => context.IndentPoConversions
                .Any(conversion => conversion.Id == job.ConversionId && conversion.IndentId == indentId));
    }

    public async Task<int> CountConversionsAsync(long indentId)
    {
        await using var context = CreateDbContext();

        return await context.IndentPoConversions.CountAsync(conversion => conversion.IndentId == indentId);
    }

    public async Task<string?> LatestRunStatusAsync()
    {
        await using var context = CreateDbContext();

        return await context.Runs
            .OrderByDescending(run => run.StartedAtUtc)
            .Select(run => run.Status.ToString())
            .FirstOrDefaultAsync();
    }

    /// <summary>A conversion case with no execution, for the read endpoints to find.</summary>
    public async Task<long> AddConversionAsync(IndentKind kind, long? indentId = null)
    {
        var id = indentId ?? ScriptedConversion.NewIndentId();

        await using var context = CreateDbContext();

        context.IndentPoConversions.Add(IndentPoConversion.Open(
            id,
            kind,
            $"26-27/TE/NF1/{id % 1_000_000:000000}",
            siteId: 1,
            DateTimeOffset.UtcNow));

        await context.SaveChangesAsync();

        return id;
    }

    /// <summary>
    /// A job that is not an indent conversion, so the read endpoints can refuse it and the grid
    /// can leave it out.
    /// </summary>
    /// <remarks>
    /// A document job rather than a cycle. Two live cycles are refused by
    /// <c>UX_AutomationJob_LiveCycle</c> — correctly — so a helper that made one could only ever be
    /// called once per database, and the second caller would fail on a constraint that has nothing
    /// to do with what it was testing. A unique document id keeps every call independent.
    /// </remarks>
    public async Task<Guid> AddUnrelatedJobAsync()
    {
        await using var context = CreateDbContext();

        var job = Job.Create(
            WorkflowNames.Sjo,
            "SalesOrder",
            $"SO-{Guid.NewGuid():N}",
            AutomationMode.Full,
            DateTimeOffset.UtcNow);

        job.PlanSteps([new PlannedOperation("SJO", "DeAllocation")]);

        context.Jobs.Add(job);
        await context.SaveChangesAsync();

        return job.Id;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var configuration = new Dictionary<string, string?>(TestConfiguration.Valid)
        {
            ["AutomationAgent:Database:ConnectionString"] = ConnectionString,
        };

        builder.ConfigureHostConfiguration(host => host.AddInMemoryCollection(configuration));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // A usable database makes Program start the real timer, dispatcher and orphan sweep.
            // A live cycle enqueuing itself mid-test would compete for the same job table and make
            // every count here depend on how long the test took.
            services.RemoveAll<IHostedService>();

            services.RemoveAll<IIndentToPoService>();

            // One instance per scope, each holding its own request's tracker — the same lifetime
            // the real IndentToPoService has. A single shared instance with a mutable Tracker
            // field would have two concurrent requests writing each other's runs, and the race
            // under test would be the double's rather than the agent's.
            services.AddScoped<IIndentToPoService>(provider =>
                new ScriptedConversionScope(Conversion, provider.GetRequiredService<IIndentPoTracker>()));
        });
    }

    private AutomationDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AutomationDbContext>()
            .UseSqlServer(ConnectionString)
            .Options);
}

/// <summary>
/// The script: what the ERP does next, shared by every request in the test.
/// </summary>
/// <remarks>
/// It holds no tracker of its own. Each request gets a <see cref="ScriptedConversionScope"/>
/// carrying its own scope's tracker, which is the lifetime the real
/// <see cref="IndentToPoService"/> has — a shared mutable tracker field would have two concurrent
/// requests recording into each other's runs, and the concurrency test would be measuring the
/// double rather than the agent.
/// </remarks>
public sealed class ScriptedConversion
{
    private long _indentId = 1;
    private long _poId;
    private Behaviour _behaviour = Behaviour.Succeed;
    private string _reason = "Item 97000ASC0162 has no MITMVND row, so no vendor can be asked to supply it.";
    private TaskCompletionSource? _gate;

    private enum Behaviour
    {
        Succeed,
        OrderNothing,
        FindNothing,
        FailBusiness,
        FailTransient,
        ThrowUnexpected,
    }

    public bool FindsNothing => _behaviour is Behaviour.FindNothing;

    public static long NewIndentId() => Random.Shared.NextInt64(1, int.MaxValue);

    public void Reset()
    {
        _behaviour = Behaviour.Succeed;
        _gate = null;
    }

    /// <summary>Points the script at an indent nothing else has touched.</summary>
    public long UseNewIndent()
    {
        _indentId = NewIndentId();
        return _indentId;
    }

    public void Succeed() => _behaviour = Behaviour.Succeed;

    /// <summary>The indent converts to nothing, with a reason — not a failure.</summary>
    public void OrderNothing(string reason)
    {
        _behaviour = Behaviour.OrderNothing;
        _reason = reason;
    }

    /// <summary>Discovery finds no eligible indent at all.</summary>
    public void FindNothing() => _behaviour = Behaviour.FindNothing;

    /// <summary>The ERP refuses, the way a missing Document Control row does.</summary>
    public void FailWith(string reason)
    {
        _behaviour = Behaviour.FailBusiness;
        _reason = reason;
    }

    /// <summary>The ERP could not be reached — worth asking again, unlike a refusal.</summary>
    public void FailTransiently(string reason)
    {
        _behaviour = Behaviour.FailTransient;
        _reason = reason;
    }

    public void ThrowUnexpected() => _behaviour = Behaviour.ThrowUnexpected;

    /// <summary>Holds every conversion until <see cref="Release"/>, so two can be made to overlap.</summary>
    public void HoldUntilReleased() =>
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate?.TrySetResult();

    public EligibleIndent Eligible() =>
        new(Reference(), "Open", new DateOnly(2026, 8, 20), "07 - BABU SINGH");

    public IndentReference Reference() =>
        new(_indentId, "26-27", "IN", "000012", 1, "NF1", IndentType.Regular);

    /// <summary>Runs the script against one request's tracker.</summary>
    public async Task<IndentConversionResult> ConvertAsync(
        IIndentPoTracker tracker,
        CancellationToken cancellationToken)
    {
        if (_behaviour is Behaviour.FindNothing)
        {
            return new IndentConversionResult(Reference(), [], []);
        }

        var indent = Reference();

        await tracker.StartExecutionAsync(
            new TrackedIndent(indent.IndentId, IndentKind.Regular, indent.DisplayNumber, indent.SiteId),
            cancellationToken);

        if (_gate is not null)
        {
            // Both callers are inside the conversion before either finishes, which is the only way
            // to prove the second is refused rather than merely arriving later.
            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        await tracker.EnterStageAsync(
            IndentPoStages.VendorResolution,
            IndentPoTasks.ResolveItemVendor,
            cancellationToken);

        if (_behaviour is Behaviour.FailBusiness or Behaviour.FailTransient)
        {
            var transient = _behaviour is Behaviour.FailTransient;

            // What the real wrapper does with an ERP failure, before rethrowing.
            await tracker.FailExecutionAsync(_reason, $"ERP: {_reason}", transient, cancellationToken);

            throw transient
                ? new ErpTransientException(_reason, $"ERP: {_reason}", "/erp/test")
                : new ErpBusinessException(_reason, $"ERP: {_reason}");
        }

        if (_behaviour is Behaviour.ThrowUnexpected)
        {
            throw new InvalidOperationException("Something nobody planned for.");
        }

        await tracker.EnterStageAsync(
            IndentPoStages.CreatePurchaseOrder,
            IndentPoTasks.CreatePoEntry,
            cancellationToken);

        if (_behaviour is Behaviour.OrderNothing)
        {
            await tracker.CompleteExecutionAsync([TrackedOutcome.Note(_reason)], cancellationToken);

            return new IndentConversionResult(indent, [], [_reason]);
        }

        // A fresh order every time: re-using one id would trip UX_IndentPoOutcome_PoId, which is
        // the index doing its job rather than a fixture to work around.
        var poId = Interlocked.Increment(ref _poId);
        var poNumber = $"26-27/TE/NF1/{poId:000000}";

        await tracker.CompleteExecutionAsync(
            [
                TrackedOutcome.Order("V0012", poId, poNumber, 4, "RS", "P0002"),
                TrackedOutcome.Note(_reason),
            ],
            cancellationToken);

        return new IndentConversionResult(
            indent,
            [new IndentPoResult(poNumber, poId, "V0012", 4, "RS", "P0002")],
            [_reason]);
    }
}

/// <summary>One request's view of the script, holding that request's own tracker.</summary>
public sealed class ScriptedConversionScope : IIndentToPoService
{
    private readonly ScriptedConversion _script;
    private readonly IIndentPoTracker _tracker;

    public ScriptedConversionScope(ScriptedConversion script, IIndentPoTracker tracker)
    {
        _script = script;
        _tracker = tracker;
    }

    public Task<IndentPoResult> CreateAsync(IndentPoRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The vendor-driven form is not part of process tracking.");

    public Task<IReadOnlyList<EligibleIndent>> FindEligibleAsync(
        IndentDiscoveryRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EligibleIndent>>(
            _script.FindsNothing ? [] : [_script.Eligible()]);

    public Task<IndentConversionResult> CreateFromIndentAsync(
        IndentReference indent,
        CancellationToken cancellationToken) => _script.ConvertAsync(_tracker, cancellationToken);

    public Task<IndentConversionResult> ConvertIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        bool dryRun,
        CancellationToken cancellationToken) => _script.ConvertAsync(_tracker, cancellationToken);

    public Task<IndentConversionResult> ConvertIndentAsync(
        long indentId,
        string? indentNumber,
        IReadOnlyList<int>? sites,
        IReadOnlyList<IndentType>? indentTypes,
        bool dryRun,
        CancellationToken cancellationToken) => _script.ConvertAsync(_tracker, cancellationToken);

    public async Task<IndentSweepResult> ConvertEligibleAsync(
        IndentSweepRequest request,
        CancellationToken cancellationToken)
    {
        if (_script.FindsNothing)
        {
            return new IndentSweepResult(0, request.DryRun, []);
        }

        var result = await _script.ConvertAsync(_tracker, cancellationToken);

        return new IndentSweepResult(1, request.DryRun, [result]);
    }
}
