using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Application.Flows.QuotationToSo;
using NewHorizon.Automation.ErpClient.Flows.QuotationToSo;
using Xunit;

namespace NewHorizon.Automation.UnitTests.Flows.QuotationToSo;

public sealed class QuotationToSoAuthorizationRequirementTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
        public DateOnly LocalDate => DateOnly.FromDateTime(now.LocalDateTime);
        public TimeOnly LocalTimeOfDay => TimeOnly.FromDateTime(now.LocalDateTime);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://erp.test") };
    }

    private static QuotationToSoService CreateService(HttpMessageHandler handler)
    {
        var factory = new SingleHandlerFactory(handler);
        var options = Options.Create(new AutomationAgentOptions());
        var clock = new StubClock(Now);
        var tracker = new NullQuotationToSoTracker();
        var logger = NullLogger<QuotationToSoService>.Instance;

        return new QuotationToSoService(factory, options, clock, tracker, logger);
    }

    [Fact]
    public async Task When_flag1_is_off_in_getDocumentDetail_returns_false()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("getDocumentDetail/1/OB/OB"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "details": [
                            {
                                "type": "OB",
                                "subType": "OB",
                                "isAuthRequired": false
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsSalesOrderAuthorizationRequiredAsync(1, "NF1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task When_flag1_is_on_in_getDocumentDetail_returns_true()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("getDocumentDetail/1/OB/OB"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "details": [
                            {
                                "type": "OB",
                                "subType": "OB",
                                "isAuthRequired": true
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsSalesOrderAuthorizationRequiredAsync(1, "NF1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task When_flag1_is_off_in_getDefaultDocumentDetail_fallback_returns_false()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("getDocumentDetail/1/OB/OB"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (req.RequestUri!.AbsolutePath.Contains("getDefaultDocumentDetail") && req.RequestUri.AbsolutePath.Contains("/OB/OB/1"))
            {
                var body = """
                {
                    "success": true,
                    "data": [
                        {
                            "groupCode": "SW",
                            "isAutorisationRequired": false
                        }
                    ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsSalesOrderAuthorizationRequiredAsync(1, "NF1");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task When_flag2_is_off_in_sales_policy_returns_false()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("salespolicy/getDetail"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "salesPolicyResultModel": [
                            {
                                "autogenerateoafonsalesorder": false
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsAutoGenerateOafOnSalesOrderAuthorizationEnabledAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task When_flag2_is_on_in_sales_policy_returns_true()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("salespolicy/getDetail"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "salesPolicyResultModel": [
                            {
                                "autogenerateoafonsalesorder": true
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsAutoGenerateOafOnSalesOrderAuthorizationEnabledAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task When_sales_order_is_authorized_via_action_detail_returns_true()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("salesorderentry/getActionDetail/100"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "actiondetails": [
                            {
                                "actionType": "Created By",
                                "userName": "Admin",
                                "actionDate": "2026-11-17T00:00:00"
                            },
                            {
                                "actionType": "Authorized By",
                                "userName": "Manager",
                                "actionDate": "2026-11-17T01:00:00"
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsSalesOrderAuthorizedAsync(100);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task When_sales_order_is_not_authorized_via_action_detail_returns_false()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("salesorderentry/getActionDetail/100"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "actiondetails": [
                            {
                                "actionType": "Created By",
                                "userName": "Admin",
                                "actionDate": "2026-11-17T00:00:00"
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.IsSalesOrderAuthorizedAsync(100);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task When_creating_oaf_and_flag2_is_off_skips_oaf_creation()
    {
        var handler = new StubHandler(req =>
        {
            // OAF list check returns empty
            if (req.RequestUri!.AbsolutePath.Contains("orderackentry/list"))
            {
                var body = """{ "success": true, "data": [] }""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            // Flag 2 is OFF
            if (req.RequestUri.AbsolutePath.Contains("salespolicy/getDetail"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "salesPolicyResultModel": [
                            {
                                "autogenerateoafonsalesorder": false
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.CreateOafForSalesOrderAsync("26-27/SW/NF1/000001", 100, 1, "NF1", "C001", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Flag 2");
    }

    [Fact]
    public async Task When_creating_oaf_and_sales_order_is_not_authorized_skips_oaf_creation()
    {
        var handler = new StubHandler(req =>
        {
            // OAF list check returns empty
            if (req.RequestUri!.AbsolutePath.Contains("orderackentry/list"))
            {
                var body = """{ "success": true, "data": [] }""";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            // Flag 2 is ON
            if (req.RequestUri.AbsolutePath.Contains("salespolicy/getDetail"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "salesPolicyResultModel": [
                            {
                                "autogenerateoafonsalesorder": true
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            // Order is NOT authorized
            if (req.RequestUri.AbsolutePath.Contains("salesorderentry/getActionDetail/100"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "actiondetails": [
                            {
                                "actionType": "Created By",
                                "userName": "Admin",
                                "actionDate": "2026-11-17T00:00:00"
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.CreateOafForSalesOrderAsync("26-27/SW/NF1/000001", 100, 1, "NF1", "C001", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not authorized");
    }

    [Fact]
    public async Task When_flag2_is_on_resolves_oaf_group_for_site_from_sales_policy()
    {
        var handler = new StubHandler(req =>
        {
            // Flag 2 is ON
            if (req.RequestUri!.AbsolutePath.Contains("salespolicy/getDetail"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "salesPolicyResultModel": [
                            {
                                "autogenerateoafonsalesorder": true
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            // GetOAFGroupData returns mappings for NF1 -> OA and NF2 -> OF
            if (req.RequestUri.AbsolutePath.Contains("salespolicy/GetOAFGroupData"))
            {
                var body = """
                {
                    "success": true,
                    "data": [
                        {
                            "companyId": 1,
                            "siteId": 1,
                            "siteCode": "NF1",
                            "siteName": "Fabcon Machines P...",
                            "groupCode": "OA",
                            "groupDescription": "Auto GeneRate OAF"
                        },
                        {
                            "companyId": 1,
                            "siteId": 2,
                            "siteCode": "NF2",
                            "siteName": "Fabcon site 2",
                            "groupCode": "OF",
                            "groupDescription": "OAF"
                        }
                    ]
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);

        var oafGroupNf1 = await service.GetSalesPolicyOafGroupForSiteAsync(1, "NF1");
        var oafGroupNf2 = await service.GetSalesPolicyOafGroupForSiteAsync(2, "NF2");

        oafGroupNf1.Should().Be("OA");
        oafGroupNf2.Should().Be("OF");
    }

    [Fact]
    public async Task When_flag2_is_off_returns_null_oaf_group()
    {
        var handler = new StubHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("salespolicy/getDetail"))
            {
                var body = """
                {
                    "success": true,
                    "data": {
                        "salesPolicyResultModel": [
                            {
                                "autogenerateoafonsalesorder": false
                            }
                        ]
                    }
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var service = CreateService(handler);
        var result = await service.GetSalesPolicyOafGroupForSiteAsync(1, "NF1");

        result.Should().BeNull();
    }
}
