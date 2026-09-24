using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.Worker.Services;

namespace NewHorizon.Automation.UnitTests.Worker;

/// <summary>
/// The agent's answer to "may this ERP user do this?" — asked of the ERP with the user's own token,
/// cached briefly, and denied (never allowed) when the ERP cannot be reached.
/// </summary>
public sealed class ErpUserRightsServiceTests
{
    private readonly StubHandler _handler = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task It_returns_the_letters_the_erp_grants_for_the_form()
    {
        _handler.RespondWith(HttpStatusCode.OK, """{ "data": { "011171": "EI", "011172": "I" }, "success": true }""");

        var service = Build();

        (await service.RightsForAsync(Context("Bearer abc", uid: "u1"), "011171", default)).Should().Be("EI");
        (await service.RightsForAsync(Context("Bearer abc", uid: "u1"), "011172", default)).Should().Be("I");
    }

    [Fact]
    public async Task A_form_the_role_does_not_grant_comes_back_empty()
    {
        _handler.RespondWith(HttpStatusCode.OK, """{ "data": { "011172": "I" }, "success": true }""");

        var service = Build();

        (await service.RightsForAsync(Context("Bearer abc", uid: "u1"), "011171", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task It_forwards_the_callers_own_bearer_token_not_the_agents()
    {
        _handler.RespondWith(HttpStatusCode.OK, """{ "data": {}, "success": true }""");

        await Build().RightsForAsync(Context("Bearer caller-token", uid: "u1"), "011171", default);

        _handler.LastRequest!.Headers.Authorization!.ToString().Should().Be("Bearer caller-token");
        _handler.LastRequest.Headers.GetValues("CompanyId").Should().ContainSingle().Which.Should().Be("1");
        _handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/api/v1/auth/form-rights");
    }

    [Fact]
    public async Task A_request_with_no_bearer_never_calls_the_erp_and_is_denied()
    {
        var service = Build();

        (await service.RightsForAsync(Context(authorization: null, uid: "u1"), "011171", default)).Should().BeEmpty();

        _handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_non_success_answer_fails_closed(HttpStatusCode status)
    {
        _handler.RespondWith(status, "nope");

        (await Build().RightsForAsync(Context("Bearer abc", uid: "u1"), "011171", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreachable_erp_fails_closed()
    {
        _handler.Throw(new HttpRequestException("connection refused"));

        (await Build().RightsForAsync(Context("Bearer abc", uid: "u1"), "011171", default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_good_answer_is_cached_so_the_screens_polling_does_not_hammer_the_erp()
    {
        _handler.RespondWith(HttpStatusCode.OK, """{ "data": { "011171": "EI" }, "success": true }""");

        var service = Build();
        var context = Context("Bearer abc", uid: "u1");

        await service.RightsForAsync(context, "011171", default);
        await service.RightsForAsync(context, "011171", default);
        await service.RightsForAsync(context, "011172", default);

        _handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Different_users_are_cached_apart()
    {
        _handler.RespondWith(HttpStatusCode.OK, """{ "data": { "011171": "EI" }, "success": true }""");

        var service = Build();

        await service.RightsForAsync(Context("Bearer abc", uid: "u1"), "011171", default);
        await service.RightsForAsync(Context("Bearer abc", uid: "u2"), "011171", default);

        _handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task A_fail_closed_result_is_not_cached_and_is_retried_next_request()
    {
        _handler.RespondWith(HttpStatusCode.InternalServerError, "down");

        var service = Build();
        var context = Context("Bearer abc", uid: "u1");

        (await service.RightsForAsync(context, "011171", default)).Should().BeEmpty();

        _handler.RespondWith(HttpStatusCode.OK, """{ "data": { "011171": "EI" }, "success": true }""");

        (await service.RightsForAsync(context, "011171", default)).Should().Be("EI");
        _handler.CallCount.Should().Be(2);
    }

    private ErpUserRightsService Build() =>
        new(
            new SingleHandlerFactory(_handler),
            _cache,
            Options.Create(new ErpEndpointOptions()),
            new AutomationAgentOptions { ErpApi = new ErpApiOptions { CompanyId = 1 } },
            NullLogger<ErpUserRightsService>.Instance);

    private static HttpContext Context(string? authorization, string uid)
    {
        var context = new DefaultHttpContext();

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", uid)], "test"));

        return context;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        private Exception? _throw;

        public HttpRequestMessage? LastRequest { get; private set; }

        public int CallCount { get; private set; }

        public void RespondWith(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
            _throw = null;
        }

        public void Throw(Exception exception) => _throw = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            if (_throw is not null)
            {
                return Task.FromException<HttpResponseMessage>(_throw);
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://erp.test") };
    }
}
