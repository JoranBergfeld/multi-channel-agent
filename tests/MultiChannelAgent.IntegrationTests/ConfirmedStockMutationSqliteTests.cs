namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The confirmed stock mutation protocol against SQLite: the same externally observable behavior as
/// <see cref="StockConversationScenarioTests"/> proves against SQL Server, with no Docker needed.
/// </summary>
public sealed class ConfirmedStockMutationSqliteTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Moving_renaming_forgetting_and_confirming_stock_behaves_exactly_as_specified() =>
        await ConfirmedStockMutationScenario.RunAsync(_factory!);
}
