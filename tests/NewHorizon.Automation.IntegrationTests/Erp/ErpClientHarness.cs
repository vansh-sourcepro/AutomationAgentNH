using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHorizon.Automation.Application.Abstractions;
using NewHorizon.Automation.Application.Erp;
using NewHorizon.Automation.ErpClient;
using NewHorizon.Automation.ErpClient.Authentication;

namespace NewHorizon.Automation.IntegrationTests.Erp;

/// <summary>
/// Builds a real <see cref="IErpClient"/> — full resilience pipeline and auth handler — pointed
/// at the stub ERP. Nothing is mocked, so what the tests prove is what production runs.
/// </summary>
public sealed class ErpClientHarness : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    public ErpClientHarness(string baseUrl, MutableClock clock, int maxRetry = 3, int timeoutSeconds = 30)
    {
        Clock = clock;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutomationAgent:Database:ConnectionString"] = "unused-by-the-erp-client",
                ["AutomationAgent:ErpApi:BaseUrl"] = baseUrl,
                ["AutomationAgent:ErpApi:LoginPath"] = "/api/v1/auth/login",
                ["AutomationAgent:ErpApi:UserName"] = "automation",
                ["AutomationAgent:ErpApi:Password"] = "stub-password",
                ["AutomationAgent:ErpApi:LoginConnectionString"] = "Server=.;Database=ERP_Stub;uid=sa;pwd=;",
                ["AutomationAgent:ErpApi:TokenTtlHours"] = "24",
                ["AutomationAgent:ErpApi:TimeoutSeconds"] = timeoutSeconds.ToString(),
                ["AutomationAgent:Host:InboundApiKey"] = "unused",
                ["AutomationAgent:Defaults:MaxRetry"] = maxRetry.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<Application.Configuration.AutomationAgentOptions>()
            .Bind(configuration.GetSection(Application.Configuration.AutomationAgentOptions.SectionName));
        services.AddSingleton<IClock>(clock);
        services.AddErpClient();

        _serviceProvider = services.BuildServiceProvider();
    }

    public MutableClock Clock { get; }

    public IErpClient Client => _serviceProvider.GetRequiredService<IErpClient>();

    public IErpTokenProvider TokenProvider => _serviceProvider.GetRequiredService<IErpTokenProvider>();

    /// <summary>
    /// The same named, fully-resilient <see cref="HttpClient"/> <see cref="Client"/> is built on —
    /// for tests that need to send a raw request (e.g. to set a request-level resilience option that
    /// none of <see cref="IErpClient"/>'s typed methods expose).
    /// </summary>
    public HttpClient CreateErpHttpClient() =>
        _serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(DependencyInjection.ErpHttpClientName);

    public void Dispose() => _serviceProvider.Dispose();
}

/// <summary>A clock the tests can move, so token expiry is tested without waiting for it.</summary>
public sealed class MutableClock : IClock
{
    public MutableClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    public DateTimeOffset UtcNow { get; set; }

    public TimeOnly LocalTimeOfDay => TimeOnly.FromTimeSpan(UtcNow.TimeOfDay);

    public void Advance(TimeSpan by) => UtcNow += by;
}
