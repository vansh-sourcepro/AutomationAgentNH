using FluentAssertions;
using NewHorizon.Automation.Application.Configuration;
using NewHorizon.Automation.Worker.Configuration;

namespace NewHorizon.Automation.UnitTests.Configuration;

public sealed class AutomationAgentOptionsValidatorTests
{
    private readonly AutomationAgentOptionsValidator _validator = new();

    [Fact]
    public void Valid_options_pass()
    {
        var result = _validator.Validate(name: null, CreateValid());

        result.Failed.Should().BeFalse(because: result.FailureMessage);
    }

    [Fact]
    public void Missing_connection_string_is_allowed()
    {
        // The database is optional. Without one the agent still signs in to the ERP and serves the
        // request-driven endpoints; Program.cs switches off the cycle timer, the job API and the
        // config API, all of which need somewhere to keep state. Refusing to start would deny the
        // ERP-facing half of the agent over a dependency it does not use.
        var options = CreateValid() with { Database = new DatabaseOptions { ConnectionString = "" } };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeFalse(because: result.FailureMessage);
    }

    [Fact]
    public void Missing_inbound_api_key_fails()
    {
        // The agent API must never be left unauthenticated, loopback binding notwithstanding.
        var options = CreateValid() with
        {
            Host = new AgentHostOptions { ManagementApiPort = 5080, BindToLoopbackOnly = true, InboundApiKey = "" },
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("InboundApiKey");
    }

    [Fact]
    public void Nested_range_violations_are_reported()
    {
        // Guards the reason this validator exists: ValidateDataAnnotations does not recurse.
        var options = CreateValid() with { Defaults = new DefaultsOptions { ParallelWorkers = 0 } };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(nameof(DefaultsOptions.ParallelWorkers));
    }

    private static AutomationAgentOptions CreateValid() => new()
    {
        Database = new DatabaseOptions
        {
            ConnectionString = "Server=.;Database=NewHorizon_Automation;Trusted_Connection=True;",
        },
        ErpApi = new ErpApiOptions
        {
            BaseUrl = "http://localhost:4400",
            LoginPath = "/api/v1/auth/login",
            UserName = "automation",
            Password = "secret",
            LoginConnectionString = "Server=.;Database=ERP;uid=sa;pwd=;",
            TokenTtlHours = 24,
            TimeoutSeconds = 30,
        },
        Host = new AgentHostOptions
        {
            ManagementApiPort = 5080,
            BindToLoopbackOnly = true,
            InboundApiKey = "inbound-key",
        },
        Defaults = new DefaultsOptions
        {
            PollIntervalSeconds = 30,
            ReconciliationIntervalMinutes = 5,
            ParallelWorkers = 4,
            MaxRetry = 3,
        },
        Serilog = new SerilogOptions { MinimumLevel = "Information" },
    };
}
