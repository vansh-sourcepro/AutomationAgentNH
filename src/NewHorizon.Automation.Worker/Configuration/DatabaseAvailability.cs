using Microsoft.Data.SqlClient;

namespace NewHorizon.Automation.Worker.Configuration;

/// <summary>What one attempt to open the automation database found.</summary>
/// <param name="Configured">A connection string was supplied. False is the deliberate no-database mode.</param>
/// <param name="Usable">
/// Whether to start the three hosted services that poll the database. True for a successful open,
/// and also for a failure a later attempt may survive — a SQL Server still starting up must not
/// cost the agent its job queue for the lifetime of the process.
/// </param>
/// <param name="Transient">The open failed, but on something that may right itself.</param>
/// <param name="Reason">Server, database and what the failure was, ready to log.</param>
internal sealed record DatabaseProbeResult(
    bool Configured,
    bool Usable,
    bool Transient,
    string Reason);

/// <summary>
/// Opens the automation database once, at startup, to separate "not configured" from "configured
/// and refused" from "configured and briefly unreachable".
/// </summary>
/// <remarks>
/// Without this, a database the agent's login cannot open is indistinguishable at startup from a
/// healthy one, and only announces itself as a stack trace from every hosted service on every
/// tick — three retries deep, because EF's <c>SqlServerRetryingExecutionStrategy</c> counts SQL
/// error 4060 among the transient ones. It is not: no number of retries grants a login access to
/// a database it has no user in.
/// </remarks>
internal static class DatabaseAvailability
{
    /// <summary>Login and permission failures. A retry produces the identical refusal.</summary>
    private static readonly int[] PermanentErrors =
    [
        4060,  // Cannot open database "X" requested by the login.
        4064,  // Cannot open user default database.
        18456, // Login failed for user.
        916,   // The server principal is not able to access the database under the current security context.
        18470, // Login failed; the account is disabled.
    ];

    /// <summary>Kept short: this runs on the startup path, before the host is listening.</summary>
    private const int ProbeTimeoutSeconds = 5;

    public static DatabaseProbeResult Probe(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new DatabaseProbeResult(
                Configured: false,
                Usable: false,
                Transient: false,
                Reason: "AutomationAgent:Database:ConnectionString is not set.");
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = ProbeTimeoutSeconds,
            };
        }
        catch (ArgumentException ex)
        {
            return new DatabaseProbeResult(
                Configured: true,
                Usable: false,
                Transient: false,
                Reason: $"AutomationAgent:Database:ConnectionString is not a valid SQL Server "
                    + $"connection string: {ex.Message}");
        }

        var target = $"database '{builder.InitialCatalog}' on server '{builder.DataSource}'";

        try
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();

            return new DatabaseProbeResult(
                Configured: true,
                Usable: true,
                Transient: false,
                Reason: $"Opened {target}.");
        }
        catch (SqlException ex) when (Array.IndexOf(PermanentErrors, ex.Number) >= 0)
        {
            return new DatabaseProbeResult(
                Configured: true,
                Usable: false,
                Transient: false,
                Reason: $"SQL error {ex.Number} opening {target}: {ex.Message.Trim()} "
                    + "The agent's login needs a user in that database — as an administrator, "
                    + $"`CREATE USER [<login>] FOR LOGIN [<login>]` in [{builder.InitialCatalog}] "
                    + "and grant it db_datareader, db_datawriter and EXECUTE — or point "
                    + "AutomationAgent:Database:ConnectionString at credentials that already have "
                    + "it. Leave the setting blank to run without a database deliberately.");
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // Unreachable rather than refused: the server may simply be starting. The hosted
            // services keep their own retry, so the database stays registered.
            return new DatabaseProbeResult(
                Configured: true,
                Usable: true,
                Transient: true,
                Reason: $"Could not reach {target} at startup: {ex.Message.Trim()}");
        }
    }
}
