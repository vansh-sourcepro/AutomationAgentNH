using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace NewHorizon.Automation.IntegrationTests;

/// <summary>
/// Hosts the agent in-memory. Bootstrap configuration is supplied explicitly rather than read
/// from a file, so a test never depends on the deployed appsettings.json and the options
/// validation path is exercised exactly as it is in production.
/// </summary>
public sealed class AgentApplicationFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection(TestConfiguration.Valid));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
