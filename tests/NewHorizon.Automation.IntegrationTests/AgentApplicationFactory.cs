using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHorizon.Automation.Infrastructure.Persistence;

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

        var host = base.CreateHost(builder);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetService<AutomationDbContext>();
            if (db is not null && db.Database.CanConnect())
            {
                db.Database.Migrate();
                db.Database.ExecuteSqlRaw(
                    "UPDATE IssueToShopFloorAutomationConfig SET IsActive = 1");
            }
        }

        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
    }
}
