using System.ComponentModel.DataAnnotations;

namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// How the agent exposes its management/read API to the ERP. The API is loopback-only by
/// default and always requires the inbound API key, so no arbitrary process can enqueue or read.
/// </summary>
public sealed class AgentHostOptions
{
    [Range(1, 65535)]
    public int ManagementApiPort { get; init; } = 5080;

    public bool BindToLoopbackOnly { get; init; } = true;

    [Required(AllowEmptyStrings = false)]
    public string InboundApiKey { get; init; } = string.Empty;
}
