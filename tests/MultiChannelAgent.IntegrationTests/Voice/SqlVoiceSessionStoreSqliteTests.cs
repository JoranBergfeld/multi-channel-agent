using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// Runs the <see cref="VoiceSessionStoreContractTests"/> against <see cref="SqlVoiceSessionStore"/>
/// backed by a real SQLite relational engine. This proves that the SQL store's mapping, queries, and
/// unique-index handling satisfy the same contract as the in-memory test double, with a real database
/// enforcing constraints — without requiring Docker.
/// </summary>
public sealed class SqlVoiceSessionStoreSqliteTests : VoiceSessionStoreContractTests, IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlVoiceSessionStoreSqliteTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    protected override IVoiceSessionStore CreateStore() => new SqlVoiceSessionStore(CreateContext());

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }
}
