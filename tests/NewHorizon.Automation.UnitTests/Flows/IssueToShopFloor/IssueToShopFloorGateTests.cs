using FluentAssertions;
using NewHorizon.Automation.Application.Flows.IssueToShopFloor;
using NewHorizon.Automation.Domain.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class IssueToShopFloorGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task No_automation_database_means_no_gate()
    {
        var configs = new FakeIssueConfigRepository(isEnabled: false);

        var reason = await new IssueToShopFloorGate(configs).OffReasonAsync(
            [IssueToShopFloorSource.Sjo, IssueToShopFloorSource.WorkOrder], CancellationToken.None);

        reason.Should().BeNull();
    }

    [Fact]
    public async Task Every_targeted_source_switched_on_passes()
    {
        var configs = new FakeIssueConfigRepository(isEnabled: true)
            .With(IssueToShopFloorSource.Sjo, active: true)
            .With(IssueToShopFloorSource.WorkOrder, active: true)
            .With(IssueToShopFloorSource.SalesOaf, active: true);

        var reason = await new IssueToShopFloorGate(configs).OffReasonAsync(
            [IssueToShopFloorSource.Sjo, IssueToShopFloorSource.WorkOrder, IssueToShopFloorSource.SalesOaf], CancellationToken.None);

        reason.Should().BeNull();
    }

    [Fact]
    public async Task One_targeted_source_switched_off_is_refused_and_named()
    {
        var configs = new FakeIssueConfigRepository(isEnabled: true)
            .With(IssueToShopFloorSource.Sjo, active: true)
            .With(IssueToShopFloorSource.WorkOrder, active: false);

        var reason = await new IssueToShopFloorGate(configs).OffReasonAsync(
            [IssueToShopFloorSource.Sjo, IssueToShopFloorSource.WorkOrder], CancellationToken.None);

        reason.Should().NotBeNull();
        reason.Should().Contain("WorkOrder");
    }

    [Fact]
    public async Task A_source_that_is_not_targeted_does_not_matter()
    {
        var configs = new FakeIssueConfigRepository(isEnabled: true)
            .With(IssueToShopFloorSource.Sjo, active: true)
            .With(IssueToShopFloorSource.SalesOaf, active: false);

        var reason = await new IssueToShopFloorGate(configs).OffReasonAsync(
            [IssueToShopFloorSource.Sjo], CancellationToken.None);

        reason.Should().BeNull();
    }

    [Fact]
    public async Task The_switch_is_re_read_on_every_call()
    {
        var configs = new FakeIssueConfigRepository(isEnabled: true).With(IssueToShopFloorSource.Sjo, active: true);
        var gate = new IssueToShopFloorGate(configs);

        (await gate.OffReasonAsync([IssueToShopFloorSource.Sjo], CancellationToken.None)).Should().BeNull();

        configs.With(IssueToShopFloorSource.Sjo, active: false);

        (await gate.OffReasonAsync([IssueToShopFloorSource.Sjo], CancellationToken.None)).Should().NotBeNull();
    }

    private sealed class FakeIssueConfigRepository : IIssueToShopFloorAutomationConfigRepository
    {
        private readonly Dictionary<IssueToShopFloorSource, IssueToShopFloorAutomationConfig> _rows = [];

        public FakeIssueConfigRepository(bool isEnabled) => IsEnabled = isEnabled;

        public bool IsEnabled { get; }

        public FakeIssueConfigRepository With(IssueToShopFloorSource source, bool active)
        {
            var config = IssueToShopFloorAutomationConfig.CreateDefault(source, Now);
            config.Update(new IssueToShopFloorAutomationConfigUpdate { IsActive = active }, Now, "test");
            _rows[source] = config;
            return this;
        }

        public Task<IssueToShopFloorAutomationConfig> GetAsync(IssueToShopFloorSource source, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(source, out var config)
                ? config
                : IssueToShopFloorAutomationConfig.CreateDefault(source, Now));

        public Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IssueToShopFloorAutomationConfig>>([.. _rows.Values]);

        public Task<IssueToShopFloorAutomationConfig> UpsertAsync(
            IssueToShopFloorSource source, IssueToShopFloorAutomationConfigUpdate update, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(IssueToShopFloorAutomationConfig config, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IssueToShopFloorAutomationConfig>> SetAllActiveAsync(
            bool active, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
