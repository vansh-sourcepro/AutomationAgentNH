using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NewHorizon.Automation.Infrastructure.Persistence;

namespace NewHorizon.Automation.IntegrationTests.Persistence;

/// <summary>
/// A real SQL Server database for tests that must exercise SQL Server behaviour the in-memory
/// provider cannot reproduce: filtered unique indexes, UPDLOCK/READPAST claiming, rowversion.
/// Tests using it are skipped when no LocalDB is present, so the suite still runs on a machine
/// (or a build agent) without SQL Server.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True;Connect Timeout=5;";

    private readonly string _databaseName = $"NewHorizon_Automation_Test_{Guid.NewGuid():N}";

    public bool IsAvailable { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public string? SkipReason => IsAvailable ? null : "SQL Server LocalDB is not available on this machine.";

    public async Task InitializeAsync()
    {
        ConnectionString =
            $"Server=(localdb)\\MSSQLLocalDB;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;";

        try
        {
            await using var connection = new SqlConnection(MasterConnectionString);
            await connection.OpenAsync();
        }
        catch (SqlException)
        {
            IsAvailable = false;
            return;
        }
        catch (PlatformNotSupportedException)
        {
            IsAvailable = false;
            return;
        }

        await using var context = CreateContext();
        await context.Database.MigrateAsync();

        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
    }

    /// <summary>
    /// Empties the job tables. Claiming is deliberately global — a worker takes whatever is
    /// pending — so any test that asserts on which jobs were claimed must start from a clean
    /// table rather than compete with rows left by its neighbours.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM AutomationJob;");
    }

    public AutomationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AutomationDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

        return new AutomationDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
