using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// What the supersession read answers against a real database, and - just as much of its contract -
/// what it refuses to do. It is a second, deliberately narrower way of reading the same row as
/// <see cref="SqlFoundryConversationBindingStore.GetOrCreateAsync"/>, and the two must not drift into
/// each other: this one never writes, and answers null rather than inventing a first generation.
/// </summary>
public sealed class SqlFoundryConversationBindingSupersessionReadTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ChannelConversationId Conversation = new("web:profile-1");

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlFoundryConversationBindingSupersessionReadTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();

        // The binding row is a Participant's, so the Participant has to exist for it to reference.
        db.Participants.Add(new ParticipantEntity
        {
            Id = Participant.Value,
            DisplayName = "Resetting Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private MultiChannelAgentDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    [Fact]
    public async Task It_reads_back_the_generation_the_conversation_currently_holds()
    {
        using var db = CreateContext();
        var store = new SqlFoundryConversationBindingStore(db);
        var created = await store.GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        var read = await store.ReadCurrentForSupersessionAsync(Participant, Conversation, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(created.Generation, read!.Generation);
        Assert.Equal(created.FoundryConversationId, read.FoundryConversationId);
        Assert.Equal(Participant, read.ParticipantId);
        Assert.Equal(Conversation, read.ChannelConversationId);
    }

    [Fact]
    public async Task It_reads_back_the_new_generation_once_a_reset_has_committed()
    {
        using var db = CreateContext();
        var store = new SqlFoundryConversationBindingStore(db);
        await store.GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        var rotated = await new SqlConversationRotationStore(db, store)
            .RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        var read = await store.ReadCurrentForSupersessionAsync(Participant, Conversation, CancellationToken.None);

        Assert.Equal(rotated.Binding.Generation, read!.Generation);
        Assert.Equal(rotated.Binding.FoundryConversationId, read.FoundryConversationId);
    }

    // A read that creates what it was asked about is not a read. Nothing has ever rotated a
    // conversation with no binding at all, so "there is none" is the honest answer - and writing a row
    // to say so would have this seam quietly establishing conversations.
    [Fact]
    public async Task It_never_creates_a_binding_for_a_conversation_that_has_none()
    {
        using var db = CreateContext();

        var read = await new SqlFoundryConversationBindingStore(db)
            .ReadCurrentForSupersessionAsync(Participant, Conversation, CancellationToken.None);

        Assert.Null(read);

        using var verifyDb = CreateContext();
        Assert.Empty(await verifyDb.FoundryConversationBindings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task It_reads_only_the_pair_it_was_asked_about()
    {
        using var db = CreateContext();
        var store = new SqlFoundryConversationBindingStore(db);
        await store.GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);
        var other = new ChannelConversationId("web:profile-2");
        await store.GetOrCreateAsync(Participant, other, Now, CancellationToken.None);
        await new SqlConversationRotationStore(db, store)
            .RotateAsync(Participant, other, Now.AddMinutes(1), CancellationToken.None);

        var read = await store.ReadCurrentForSupersessionAsync(Participant, Conversation, CancellationToken.None);

        // The other conversation is on generation 2; this one never moved.
        Assert.Equal(1, read!.Generation);
    }

    // The read leaves no transaction of its own behind for the next unrelated write in the same
    // scope to inherit - the DbContext is shared by a whole batch of Turns.
    [Fact]
    public async Task It_leaves_no_transaction_open_behind_it()
    {
        using var db = CreateContext();
        var store = new SqlFoundryConversationBindingStore(db);
        await store.GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        await store.ReadCurrentForSupersessionAsync(Participant, Conversation, CancellationToken.None);

        Assert.Null(db.Database.CurrentTransaction);
    }

    // A caller that is already in a transaction holds its locks to its own commit, which is stronger
    // than anything this read would open for itself - so it must join rather than fail.
    [Fact]
    public async Task It_reads_inside_a_transaction_the_caller_already_holds()
    {
        using var db = CreateContext();
        var store = new SqlFoundryConversationBindingStore(db);
        var created = await store.GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var read = await store.ReadCurrentForSupersessionAsync(Participant, Conversation, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        Assert.Equal(created.Generation, read!.Generation);
    }
}
