using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Infrastructure.Authentication;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// Fast, Docker-free coverage for the durable repository behind the SQL-backed authentication ticket
/// store: save (insert), find, renew (save again by the same key overwrites in place, never
/// duplicating a row), and delete, all against a real relational engine - mirroring the pattern used
/// for <see cref="MultiChannelAgent.Infrastructure.Turns.SqlInboxStore"/>.
/// </summary>
public sealed class SqlAuthTicketRepositoryTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlAuthTicketRepositoryTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }

    [Fact]
    public async Task Saving_a_new_key_makes_it_findable_by_that_key()
    {
        using var db = CreateContext();
        var repository = new SqlAuthTicketRepository(db, TimeProvider.System);
        var payload = new byte[] { 1, 2, 3, 4 };

        await repository.SaveAsync("key-1", payload, DateTimeOffset.UtcNow.AddHours(8), CancellationToken.None);

        using var verifyDb = CreateContext();
        var found = await new SqlAuthTicketRepository(verifyDb, TimeProvider.System).FindAsync("key-1", CancellationToken.None);
        Assert.Equal(payload, found);
    }

    [Fact]
    public async Task Finding_an_unknown_key_returns_null()
    {
        using var db = CreateContext();
        var repository = new SqlAuthTicketRepository(db, TimeProvider.System);

        var found = await repository.FindAsync("does-not-exist", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task Saving_again_by_the_same_key_renews_the_payload_in_place_without_duplicating_the_row()
    {
        using (var db = CreateContext())
        {
            await new SqlAuthTicketRepository(db, TimeProvider.System)
                .SaveAsync("key-2", [1, 2, 3], DateTimeOffset.UtcNow.AddHours(8), CancellationToken.None);
        }

        using (var db = CreateContext())
        {
            await new SqlAuthTicketRepository(db, TimeProvider.System)
                .SaveAsync("key-2", [9, 9, 9], DateTimeOffset.UtcNow.AddHours(16), CancellationToken.None);
        }

        using var verifyDb = CreateContext();
        var rows = await verifyDb.AuthTickets.AsNoTracking().Where(t => t.Key == "key-2").ToListAsync();
        Assert.Single(rows);
        Assert.Equal(new byte[] { 9, 9, 9 }, rows[0].ProtectedTicket);
    }

    [Fact]
    public async Task Deleting_a_key_makes_it_unfindable()
    {
        using (var db = CreateContext())
        {
            await new SqlAuthTicketRepository(db, TimeProvider.System)
                .SaveAsync("key-3", [1], DateTimeOffset.UtcNow.AddHours(8), CancellationToken.None);
        }

        using (var db = CreateContext())
        {
            await new SqlAuthTicketRepository(db, TimeProvider.System).DeleteAsync("key-3", CancellationToken.None);
        }

        using var verifyDb = CreateContext();
        var found = await new SqlAuthTicketRepository(verifyDb, TimeProvider.System).FindAsync("key-3", CancellationToken.None);
        Assert.Null(found);
    }

    [Fact]
    public async Task Deleting_an_unknown_key_does_not_throw()
    {
        using var db = CreateContext();
        var repository = new SqlAuthTicketRepository(db, TimeProvider.System);

        await repository.DeleteAsync("never-existed", CancellationToken.None);
    }
}
