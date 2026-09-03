using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free regression coverage for the durable-binding race behind
/// <see cref="SqlFoundryConversationBindingStore"/>: two independent
/// <see cref="MultiChannelAgentDbContext"/> instances - mirroring two concurrent Turns for the same
/// (Participant, ChannelConversation) both being processed - race to create the first-generation
/// Foundry conversation binding. A real relational engine's primary key guarantees exactly one insert
/// wins; <see cref="SqlFoundryConversationBindingStore.GetOrCreateAsync"/> must resolve the loser into
/// the winner's binding instead of letting a bare <see cref="DbUpdateException"/> escape.
/// </summary>
public sealed class SqlFoundryConversationBindingStoreConcurrencyTests : IDisposable
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ChannelConversationId SomeConversation = new("conversation-race-1");
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlFoundryConversationBindingStoreConcurrencyTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Participants.Add(new ParticipantEntity
        {
            Id = SomeParticipant.Value,
            DisplayName = "Some Participant",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Two_concurrent_get_or_create_calls_for_the_same_pair_converge_on_one_binding()
    {
        var now = DateTimeOffset.UtcNow;
        using var dbA = CreateContext();
        using var dbB = CreateContext();
        var storeA = new SqlFoundryConversationBindingStore(dbA);
        var storeB = new SqlFoundryConversationBindingStore(dbB);

        var taskA = storeA.GetOrCreateAsync(SomeParticipant, SomeConversation, now, CancellationToken.None);
        var taskB = storeB.GetOrCreateAsync(SomeParticipant, SomeConversation, now, CancellationToken.None);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(results[0].FoundryConversationId, results[1].FoundryConversationId);

        using var verifyDb = CreateContext();
        var rows = await verifyDb.FoundryConversationBindings.AsNoTracking()
            .Where(e => e.ParticipantId == SomeParticipant.Value && e.ChannelConversationId == SomeConversation.Value)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task A_second_call_for_the_same_pair_reuses_the_existing_binding_without_creating_another()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new SqlFoundryConversationBindingStore(db);

        var first = await store.GetOrCreateAsync(SomeParticipant, SomeConversation, now, CancellationToken.None);
        var second = await store.GetOrCreateAsync(SomeParticipant, SomeConversation, now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.FoundryConversationId, second.FoundryConversationId);
        Assert.Equal(1, second.Generation);
    }
}
