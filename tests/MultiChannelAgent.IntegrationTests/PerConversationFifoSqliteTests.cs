namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The same end-to-end per-ChannelConversation FIFO scenario
/// <see cref="StockConversationScenarioTests"/> runs against real SQL Server, run here against an
/// in-memory SQLite database instead: a real relational engine, no Docker, so the ordering invariant
/// is exercised on every developer machine and not only where containers are available.
/// </summary>
public sealed class PerConversationFifoSqliteTests : IAsyncLifetime
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
    public async Task A_stuck_turn_holds_only_its_own_conversation_and_the_conversation_resumes_in_order() =>
        await PerConversationFifoScenario.RunAsync(_factory);
}
