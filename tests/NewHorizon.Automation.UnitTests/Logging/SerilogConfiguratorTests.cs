using FluentAssertions;
using NewHorizon.Automation.Worker.Logging;
using Serilog.Events;

namespace NewHorizon.Automation.UnitTests.Logging;

public sealed class SerilogConfiguratorTests
{
    [Theory]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("WARNING", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    public void Configured_levels_are_parsed_case_insensitively(string configured, LogEventLevel expected) =>
        SerilogConfigurator.ParseLevel(configured).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chatty")]
    public void Unrecognised_levels_fall_back_to_Information(string? configured) =>
        // A typo in the level must never leave the service silent.
        SerilogConfigurator.ParseLevel(configured).Should().Be(LogEventLevel.Information);
}
