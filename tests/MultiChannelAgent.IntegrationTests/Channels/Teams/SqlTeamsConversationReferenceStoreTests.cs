using System.Data.Common;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Infrastructure.Channels.Teams;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Migrations;

namespace MultiChannelAgent.IntegrationTests.Channels.Teams;

public sealed class SqlTeamsConversationReferenceStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlTeamsConversationReferenceStoreTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Saving_a_reference_makes_it_findable_by_channel_conversation_id()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 6, 19, 30, 0, TimeSpan.Zero));

        using (var db = CreateContext())
        {
            await new SqlTeamsConversationReferenceStore(db, timeProvider)
                .SaveAsync("teams-conversation-1", "{\"conversation\":1}", CancellationToken.None);
        }

        using var verifyDb = CreateContext();
        var found = await new SqlTeamsConversationReferenceStore(verifyDb, timeProvider)
            .FindAsync("teams-conversation-1", CancellationToken.None);
        Assert.Equal("{\"conversation\":1}", found);
    }

    [Fact]
    public async Task Saving_again_for_the_same_conversation_updates_the_reference_in_place_and_refreshes_updated_at()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 9, 6, 19, 30, 0, TimeSpan.Zero));

        using (var db = CreateContext())
        {
            await new SqlTeamsConversationReferenceStore(db, timeProvider)
                .SaveAsync("teams-conversation-2", "first", CancellationToken.None);
        }

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        using (var db = CreateContext())
        {
            await new SqlTeamsConversationReferenceStore(db, timeProvider)
                .SaveAsync("teams-conversation-2", "second", CancellationToken.None);
        }

        using var verifyDb = CreateContext();
        var row = await verifyDb.Set<MultiChannelAgent.Infrastructure.Persistence.Entities.TeamsConversationReferenceEntity>()
            .AsNoTracking()
            .SingleAsync(x => x.ChannelConversationId == "teams-conversation-2");
        Assert.Equal("second", row.SerializedReference);
        Assert.Equal(timeProvider.GetUtcNow(), row.UpdatedAt);
    }

    [Fact]
    public async Task Two_concurrent_saves_for_the_same_conversation_converge_on_one_row_without_throwing()
    {
        var now = new DateTimeOffset(2026, 9, 6, 19, 30, 0, TimeSpan.Zero);
        var gate = new SelectGateInterceptor();
        using var dbA = CreateContext(gate);
        using var dbB = CreateContext(gate);
        var storeA = new SqlTeamsConversationReferenceStore(dbA, new FakeTimeProvider(now));
        var storeB = new SqlTeamsConversationReferenceStore(dbB, new FakeTimeProvider(now.AddMinutes(1)));

        var saveA = storeA.SaveAsync("teams-conversation-race", "first", CancellationToken.None);
        var saveB = storeB.SaveAsync("teams-conversation-race", "second", CancellationToken.None);

        var error = await Record.ExceptionAsync(() => Task.WhenAll(saveA, saveB));

        Assert.Null(error);

        using var verifyDb = CreateContext();
        var rows = await verifyDb.Set<MultiChannelAgent.Infrastructure.Persistence.Entities.TeamsConversationReferenceEntity>()
            .AsNoTracking()
            .Where(x => x.ChannelConversationId == "teams-conversation-race")
            .ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].SerializedReference is "first" or "second");
    }

    [Fact]
    public async Task AddTeamsConversationReferences_migration_generates_sql_that_executes_cleanly_on_sqlite()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(connection).Options);

        var generator = db.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(
            new ExposedAddTeamsConversationReferences().BuildUpOperations("Microsoft.EntityFrameworkCore.Sqlite"),
            db.Model);

        foreach (var command in commands)
        {
            await using var sqlite = connection.CreateCommand();
            sqlite.CommandText = command.CommandText;
            await sqlite.ExecuteNonQueryAsync();
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'TeamsConversationReferences';";
        Assert.Equal("TeamsConversationReferences", (string?)await verify.ExecuteScalarAsync());
    }

    private MultiChannelAgentDbContext CreateContext() =>
        CreateContext(interceptor: null);

    private MultiChannelAgentDbContext CreateContext(DbCommandInterceptor? interceptor) =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(interceptor ?? NoOpInterceptor.Instance)
            .Options);

    private sealed class ExposedAddTeamsConversationReferences : AddTeamsConversationReferences
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations(string activeProvider)
        {
            ActiveProvider = activeProvider;
            var builder = new MigrationBuilder(activeProvider);
            Up(builder);
            return builder.Operations;
        }
    }

    private sealed class SelectGateInterceptor : DbCommandInterceptor
    {
        private readonly Barrier _barrier = new(2);
        private int _remainingSelectsToGate = 2;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TeamsConversationReferences", StringComparison.Ordinal)
                && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Decrement(ref _remainingSelectsToGate) >= 0)
            {
                await Task.Run(() => _barrier.SignalAndWait(cancellationToken), cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class NoOpInterceptor : DbCommandInterceptor
    {
        public static readonly NoOpInterceptor Instance = new();
    }
}
