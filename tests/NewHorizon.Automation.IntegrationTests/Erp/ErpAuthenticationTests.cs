using System.Text.Json;
using FluentAssertions;
using NewHorizon.Automation.Application.Erp;

namespace NewHorizon.Automation.IntegrationTests.Erp;

/// <summary>
/// Covers the three concerns the ERP-login design exists to solve: expiry, rejection, and
/// concurrency across parallel workers.
/// </summary>
public sealed class ErpAuthenticationTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Every_call_carries_a_bearer_token_without_the_operation_asking_for_one()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        var request = erp.Requests.Single(r => r.Path.EndsWith("allocation", StringComparison.Ordinal));
        request.BearerToken.Should().Be("stub-token-1");
        // The correlation id travels with the call so one identifier traces the run across systems.
        request.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task The_token_is_cached_across_calls()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        for (var i = 0; i < 5; i++)
        {
            await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        }

        // Five ERP calls, one sign-in: the agent must not authenticate per request.
        erp.TokensIssued.Should().Be(1);
    }

    [Fact]
    public async Task The_token_is_refreshed_before_it_expires_rather_than_after()
    {
        await using var erp = await StubErpServer.StartAsync();
        erp.TokenExpiresInSeconds = 600;

        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.TokensIssued.Should().Be(1);

        // Still comfortably valid — no refresh.
        clock.Advance(TimeSpan.FromMinutes(5));
        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.TokensIssued.Should().Be(1);

        // Inside the two-minute safety margin: refresh happens now, before the token can expire
        // mid-request and surface as a spurious 401 in the middle of an operation.
        clock.Advance(TimeSpan.FromMinutes(3, 30));
        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.TokensIssued.Should().Be(2);
    }

    [Fact]
    public async Task A_rejected_token_triggers_one_reauthentication_and_a_replay()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        // Prime the cache, then revoke that token as a restarted ERP would.
        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.RevokedTokens.Add("stub-token-1");

        var result = await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        // The operation succeeded despite the rejection: the caller never saw the 401.
        result.ErpDocumentRef.Should().Be("DOC-allocation");
        erp.TokensIssued.Should().Be(2);

        var tokensUsed = erp.Requests
            .Where(r => r.Path.EndsWith("allocation", StringComparison.Ordinal))
            .Select(r => r.BearerToken)
            .ToList();

        // Rejected once with the stale token, replayed once with the fresh one.
        tokensUsed.Should().Equal("stub-token-1", "stub-token-1", "stub-token-2");
    }

    [Fact]
    public async Task A_replayed_request_keeps_its_body_and_headers()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        await harness.Client.CreateWorkOrderAsync(new WorkOrderRequest(Context()), CancellationToken.None);
        erp.RevokedTokens.Add("stub-token-1");

        await harness.Client.CreateWorkOrderAsync(new WorkOrderRequest(Context()), CancellationToken.None);

        // The clone must carry the idempotency key: a replay that dropped it could look like a
        // brand-new create to an ERP that honours the header.
        var replay = erp.Requests.Last(r => r.Path.EndsWith("workorder", StringComparison.Ordinal));
        replay.BearerToken.Should().Be("stub-token-2");
        replay.IdempotencyKey.Should().Be("SalesOrder:SO-1:workorder");
        replay.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task A_persistent_rejection_is_not_retried_forever()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        // Every token is rejected: the service account genuinely lacks access.
        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.RevokedTokens.Add("stub-token-1");
        erp.RevokedTokens.Add("stub-token-2");

        var call = async () =>
            await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        // Surfaced as a business failure, so it reaches a human instead of looping: no amount of
        // retrying grants a missing AUTOMATION_AGENT role.
        (await call.Should().ThrowAsync<ErpBusinessException>())
            .Which.LaymanMessage.Should().Contain("not permitted");
    }

    [Fact]
    public async Task Bad_credentials_fail_loudly_rather_than_silently_retrying()
    {
        await using var erp = await StubErpServer.StartAsync();
        erp.TokenEndpointStatusOverride = StatusCodes.Unauthorized;

        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        var call = async () =>
            await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        (await call.Should().ThrowAsync<ErpAuthenticationException>())
            .Which.LaymanMessage.Should().Contain("ERP credentials");
    }

    [Fact]
    public async Task Parallel_workers_cause_one_authentication_not_one_each()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        // Eight workers starting at once, all needing a token that does not yet exist.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None)));

        // The acquisition lock must collapse the stampede into a single sign-in.
        erp.TokensIssued.Should().Be(1);
    }

    [Fact]
    public async Task The_login_body_is_the_ERPs_own_contract_read_from_configuration()
    {
        await using var erp = await StubErpServer.StartAsync();
        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        // The ERP's property names, not ours: it reads userName/connStr/isCEFlag verbatim, so a
        // renamed or dropped field is a sign-in that fails only against the real ERP.
        var body = JsonDocument.Parse(erp.LoginBodies.Single()).RootElement;

        body.GetProperty("userName").GetString().Should().Be("automation");
        body.GetProperty("password").GetString().Should().Be("stub-password");
        body.GetProperty("connStr").GetString().Should().Be("Server=.;Database=ERP_Stub;uid=sa;pwd=;");
        body.GetProperty("isCEFlag").GetBoolean().Should().BeFalse();
        body.TryGetProperty("appID", out _).Should().BeTrue();
        body.TryGetProperty("userId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_token_is_cached_for_the_lifetime_the_ERP_states_not_a_lifetime_we_assume()
    {
        await using var erp = await StubErpServer.StartAsync();
        // The real ERP issues 24-hour tokens and states the expiry; a day of cycles must run on one.
        erp.TokenExpiresInSeconds = (int)TimeSpan.FromHours(24).TotalSeconds;

        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(23));
        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.TokensIssued.Should().Be(1);

        // Past the stated validTo: signed in again, without anyone having to ask.
        clock.Advance(TimeSpan.FromHours(2));
        await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);
        erp.TokensIssued.Should().Be(2);
    }

    [Fact]
    public async Task A_refusal_is_read_from_the_body_not_the_status_code()
    {
        await using var erp = await StubErpServer.StartAsync();
        // The ERP refuses a bad password with 400 + success:false, not with 401.
        erp.TokenEndpointStatusOverride = StatusCodes.BadRequest;

        var clock = new MutableClock(Start);
        erp.NowUtc = () => clock.UtcNow;
        using var harness = new ErpClientHarness(erp.BaseUrl, clock);

        var call = async () =>
            await harness.Client.AllocateAsync(new AllocationRequest(Context()), CancellationToken.None);

        var thrown = await call.Should().ThrowAsync<ErpAuthenticationException>();
        thrown.Which.LaymanMessage.Should().Contain("ERP credentials");
        // The ERP's own message key belongs in the technical detail, where an admin can act on it.
        thrown.Which.TechnicalMessage.Should().Contain("InvalidUsernamePasswordKey");
    }

    private static ErpOperationRequest Context() =>
        new("SalesOrder", "SO-1", "corr-1", Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static class StatusCodes
    {
        public const int Unauthorized = 401;
        public const int BadRequest = 400;
    }
}
