namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The same durable response-part scenario <see cref="StockConversationScenarioTests"/> runs against
/// real SQL Server, run here against an in-memory SQLite database: a real relational engine, no
/// Docker, so it is exercised on every developer machine too.
/// </summary>
public sealed class StockReadDeliverySqliteTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_answered_read_records_exactly_one_channel_neutral_response_part() =>
        await StockReadDeliveryScenario.RunAsync(_factory);
}
