namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The Docker-free twin of the SQL Server-backed Initial Import scenario. It boots the real Host
/// against an in-memory SQLite database and runs the identical externally observable protocol, so
/// every acceptance criterion of #34 is proven on every machine in seconds rather than only in CI.
/// </summary>
public sealed class InitialImportSqliteTests
{
    [Fact]
    public async Task Initial_import_works_end_to_end()
    {
        await using var factory = new SqliteWebApplicationFactory();

        await InitialImportScenario.RunAsync(factory);
    }
}
