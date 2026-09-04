namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The conversational stock mutation scenario against SQLite: the same externally observable behavior
/// as <see cref="StockConversationScenarioTests"/> proves against SQL Server, with no Docker needed.
/// </summary>
public sealed class StockMutationSqliteTests : IAsyncLifetime
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
    public async Task Adding_removing_and_setting_stock_through_a_web_conversation_behaves_exactly_as_specified() =>
        await StockMutationScenario.RunAsync(_factory!);
}
