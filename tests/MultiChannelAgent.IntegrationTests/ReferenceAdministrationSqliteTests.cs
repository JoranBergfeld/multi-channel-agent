namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The Docker-free twin of the SQL Server-backed administration scenario. It boots the real Host
/// against an in-memory SQLite database and runs the identical externally observable protocol, so a
/// regression is caught locally in seconds rather than only in CI.
/// </summary>
public sealed class ReferenceAdministrationSqliteTests
{
    [Fact]
    public async Task Unit_and_Location_administration_works_end_to_end()
    {
        await using var factory = new SqliteWebApplicationFactory();

        await ReferenceAdministrationScenario.RunAsync(factory);
    }
}
