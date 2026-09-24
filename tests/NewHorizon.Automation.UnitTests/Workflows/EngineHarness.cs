using Microsoft.Extensions.Logging.Abstractions;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Notifications;
using NewHorizon.Automation.Application.Workflows;
using NewHorizon.Automation.Domain.Configuration;
using NewHorizon.Automation.Domain.Errors;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Workflows;

/// <summary>
/// Assembles a real <see cref="WorkflowEngine"/> over in-memory adapters, so the tests exercise the
/// production engine rather than a simplified stand-in.
/// </summary>
public sealed class EngineHarness
{
    public EngineHarness(WorkflowDefinition definition, int maxRetry = 3)
    {
        Catalog = new WorkflowCatalog([definition]);
        Config = new StubConfigRepository(definition.WorkflowType, maxRetry);

        Engine = new WorkflowEngine(
            Catalog,
            Jobs,
            Config,
            Erp,
            Notifications,
            Clock,
            NullLogger<WorkflowEngine>.Instance);

        Definition = definition;
    }

    public WorkflowDefinition Definition { get; }

    public IWorkflowCatalog Catalog { get; }

    public StubConfigRepository Config { get; }

    public FakeErpClient Erp { get; } = new();

    public InMemoryJobRepository Jobs { get; } = new();

    public RecordingNotificationService Notifications { get; } = new();

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 7, 27, 9, 0, 0, TimeSpan.Zero));

    public IWorkflowEngine Engine { get; }

    /// <summary>Creates a cycle job whose identity is the cycle, not a document.</summary>
    public Job NewJob(string cycleId) => NewJob(AutomationMode.Full, cycleId);

    /// <summary>Creates a job planned from the definition, exactly as the enqueue path will.</summary>
    public Job NewJob(AutomationMode mode = AutomationMode.Full, string documentId = "SO-123")
    {
        var job = Job.Create(Definition.WorkflowType, "SalesOrder", documentId, mode, Clock.UtcNow);
        job.PlanSteps(Definition.Plan());

        return job;
    }

    /// <summary>Claims and runs a job, as the queue processor will.</summary>
    public async Task<Job> RunAsync(Job job)
    {
        job.Claim(Clock.UtcNow);
        await Engine.RunAsync(job, CancellationToken.None);

        return job;
    }
}

public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }

    public TimeOnly LocalTimeOfDay => TimeOnly.FromTimeSpan(UtcNow.TimeOfDay);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class StubConfigRepository : IAutomationConfigRepository
{
    private readonly AutomationConfig _config;

    public StubConfigRepository(string module, int maxRetry)
    {
        _config = AutomationConfig.CreateDefault(module, DateTimeOffset.UnixEpoch);
        _config.Update(new AutomationConfigUpdate { RetryCount = maxRetry }, DateTimeOffset.UnixEpoch, "test");
    }

    public Task<AutomationConfig> GetOrDefaultAsync(string module, CancellationToken cancellationToken) =>
        Task.FromResult(_config);

    public Task<IReadOnlyList<AutomationConfig>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AutomationConfig>>([_config]);

    public Task<AutomationConfig> UpsertAsync(
        string module,
        AutomationConfigUpdate update,
        string? updatedBy,
        CancellationToken cancellationToken) =>
        Task.FromResult(_config);
}

public sealed class RecordingNotificationService : INotificationService
{
    public List<AutomationError> Failures { get; } = [];

    public List<string> ApprovalRequests { get; } = [];

    public Task NotifyJobFailedAsync(Job job, AutomationError error, CancellationToken cancellationToken)
    {
        Failures.Add(error);
        return Task.CompletedTask;
    }

    public Task NotifyApprovalRequiredAsync(
        Job job,
        string stage,
        string operationName,
        CancellationToken cancellationToken)
    {
        ApprovalRequests.Add($"{stage}/{operationName}");
        return Task.CompletedTask;
    }
}
