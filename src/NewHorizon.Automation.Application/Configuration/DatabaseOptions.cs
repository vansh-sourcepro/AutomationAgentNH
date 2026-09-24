namespace NewHorizon.Automation.Application.Configuration;

/// <summary>
/// Connection to the automation database only. The agent never connects to the ERP database;
/// all ERP effects go through <c>IErpClient</c> over HTTP.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// Optional. Left blank, the agent starts without a database: the ERP client, the login and the
    /// request-driven endpoints all work, and only the job queue, job history and the runtime config
    /// table are unavailable. Set it to run the timer-driven cycle.
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;
}
