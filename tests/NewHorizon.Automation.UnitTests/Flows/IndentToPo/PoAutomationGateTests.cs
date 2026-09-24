using FluentAssertions;
using NewHorizon.Automation.Application.Flows.IndentToPo;
using NewHorizon.Automation.Domain.Flows.IndentToPo;

namespace NewHorizon.Automation.UnitTests.Flows.IndentToPo;

public sealed class PoAutomationGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task No_automation_database_means_no_gate()
    {
        var configs = new FakeConfigRepository(isEnabled: false);

        var reason = await new PoAutomationGate(configs).OffReasonAsync(
            [IndentKind.Regular, IndentKind.Capital], CancellationToken.None);

        reason.Should().BeNull();
    }

    [Fact]
    public async Task Every_targeted_type_switched_on_passes()
    {
        var configs = new FakeConfigRepository(isEnabled: true)
            .With(IndentKind.Regular, active: true)
            .With(IndentKind.Capital, active: true)
            .With(IndentKind.Service, active: true);

        var reason = await new PoAutomationGate(configs).OffReasonAsync(
            [IndentKind.Regular, IndentKind.Capital, IndentKind.Service], CancellationToken.None);

        reason.Should().BeNull();
    }

    [Fact]
    public async Task One_targeted_type_switched_off_is_refused_and_named()
    {
        var configs = new FakeConfigRepository(isEnabled: true)
            .With(IndentKind.Regular, active: true)
            .With(IndentKind.Capital, active: false);

        var reason = await new PoAutomationGate(configs).OffReasonAsync(
            [IndentKind.Regular, IndentKind.Capital], CancellationToken.None);

        reason.Should().NotBeNull();
        reason.Should().Contain("Capital");
    }

    [Fact]
    public async Task A_type_that_is_not_targeted_does_not_matter()
    {
        var configs = new FakeConfigRepository(isEnabled: true)
            .With(IndentKind.Regular, active: true)
            .With(IndentKind.Service, active: false);

        var reason = await new PoAutomationGate(configs).OffReasonAsync(
            [IndentKind.Regular], CancellationToken.None);

        reason.Should().BeNull();
    }

    [Fact]
    public async Task The_switch_is_re_read_on_every_call()
    {
        // The gate must never cache: a sweep that checks before each indent has to see a mid-run
        // toggle-off. FakeConfigRepository.GetAsync returns whatever the row currently holds.
        var configs = new FakeConfigRepository(isEnabled: true).With(IndentKind.Regular, active: true);
        var gate = new PoAutomationGate(configs);

        (await gate.OffReasonAsync([IndentKind.Regular], CancellationToken.None)).Should().BeNull();

        configs.With(IndentKind.Regular, active: false);

        (await gate.OffReasonAsync([IndentKind.Regular], CancellationToken.None)).Should().NotBeNull();
    }

    private sealed class FakeConfigRepository : IIndentPoAutomationConfigRepository
    {
        private readonly Dictionary<IndentKind, IndentPoAutomationConfig> _rows = [];

        public FakeConfigRepository(bool isEnabled) => IsEnabled = isEnabled;

        public bool IsEnabled { get; }

        public FakeConfigRepository With(IndentKind kind, bool active)
        {
            var config = IndentPoAutomationConfig.CreateDefault(kind, Now);
            config.Update(new IndentPoAutomationConfigUpdate { IsActive = active }, Now, "test");
            _rows[kind] = config;
            return this;
        }

        public Task<IndentPoAutomationConfig> GetAsync(IndentKind indentKind, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(indentKind, out var config)
                ? config
                : IndentPoAutomationConfig.CreateDefault(indentKind, Now));

        public Task<IReadOnlyList<IndentPoAutomationConfig>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndentPoAutomationConfig>>([.. _rows.Values]);

        public Task<IndentPoAutomationConfig> UpsertAsync(
            IndentKind indentKind, IndentPoAutomationConfigUpdate update, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(IndentPoAutomationConfig config, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<IndentPoAutomationConfig>> SetAllActiveAsync(
            bool active, string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
