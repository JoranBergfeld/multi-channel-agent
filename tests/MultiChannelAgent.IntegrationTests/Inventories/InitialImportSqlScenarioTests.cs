namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The whole Initial Import protocol against a real SQL Server instance under production migrations -
/// the highest required correctness seam in this repository. Its SQLite twin proves the same
/// externally observable behavior without Docker; this one additionally proves the production schema,
/// the filtered unique index that admits one pending import per Participant and Inventory, the
/// serializable supersede, and the atomic write that creates every Stock Entry or none.
/// </summary>
public sealed class InitialImportSqlScenarioTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Initial_import_works_end_to_end()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import scenario.");

        await InitialImportScenario.RunAsync(Factory!);
    }
}
