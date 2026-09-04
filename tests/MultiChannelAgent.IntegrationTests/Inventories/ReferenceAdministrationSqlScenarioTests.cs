namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The same administration protocol against a real SQL Server instance with production migrations
/// applied - the repository's highest required correctness seam. Its SQLite twin proves the same
/// behavior without Docker; this one additionally proves the production schema, its filtered unique
/// indexes, and its isolation behavior.
/// </summary>
public sealed class ReferenceAdministrationSqlScenarioTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Unit_and_Location_administration_works_end_to_end()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration scenario.");

        await ReferenceAdministrationScenario.RunAsync(Factory!);
    }
}
