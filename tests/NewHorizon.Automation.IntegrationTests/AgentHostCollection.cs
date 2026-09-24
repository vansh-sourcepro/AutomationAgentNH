namespace NewHorizon.Automation.IntegrationTests;

/// <summary>
/// Groups every test class that boots the agent in-memory, so only one does at a time.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/> starts the
/// host by invoking the real <c>Program</c> entry point and intercepting the host it builds. That
/// interception is process-wide and not re-entrant: two factories starting at once make one of
/// them fail with "the entry point exited without ever building an IHost". xUnit runs collections
/// in parallel and gives each class its own by default, so the classes that boot a host have to
/// share one explicitly.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class AgentHostCollection
{
    public const string Name = "agent-host";
}
