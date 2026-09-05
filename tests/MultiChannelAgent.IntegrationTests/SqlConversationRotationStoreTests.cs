using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage of what "New conversation" actually does to durable state: it starts a
/// fresh Foundry conversation generation and settles whatever confirmation was waiting, in one
/// transaction, and it touches neither Membership nor the Active Inventory selection. The last part
/// is the one an implementation could silently get wrong, so it is asserted directly rather than
/// inferred.
/// </summary>
public sealed class SqlConversationRotationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ChannelConversationId Conversation = new("web:profile-1");

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();

    public SqlConversationRotationStoreTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Rotating_starts_a_new_generation_with_a_different_foundry_conversation()
    {
        using var db = CreateContext();
        var before = await new SqlFoundryConversationBindingStore(db)
            .GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        var result = await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(before.Generation + 1, result.Binding.Generation);
        Assert.NotEqual(before.FoundryConversationId, result.Binding.FoundryConversationId);
        Assert.False(result.ClearedPendingConfirmation);

        using var verifyDb = CreateContext();
        var row = await verifyDb.FoundryConversationBindings.AsNoTracking().SingleAsync();
        Assert.Equal(result.Binding.Generation, row.Generation);
        Assert.Equal(result.Binding.FoundryConversationId.Value, row.FoundryConversationId);
    }

    [Fact]
    public async Task Rotating_for_a_conversation_that_has_never_been_used_still_starts_a_fresh_generation()
    {
        using var db = CreateContext();

        var result = await Store(db).RotateAsync(Participant, Conversation, Now, CancellationToken.None);

        Assert.Equal(2, result.Binding.Generation);

        using var verifyDb = CreateContext();
        Assert.Single(await verifyDb.FoundryConversationBindings.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Rotating_settles_the_one_pending_confirmation_as_a_conversation_reset()
    {
        using var db = CreateContext();
        var proposalStore = new SqlConfirmationProposalStore(db);
        var proposal = StockProposal();
        await proposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        var result = await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.True(result.ClearedPendingConfirmation);
        Assert.Equal(ProposalStatus.ConversationReset, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await proposalStore.FindPendingAsync(Participant, Conversation.Value, CancellationToken.None));
    }

    [Fact]
    public async Task Rotating_never_touches_membership_or_the_active_inventory_selection()
    {
        using var db = CreateContext();
        var selections = new SqlActiveInventorySelectionStore(db);
        await selections.UpsertAsync(
            new ActiveInventorySelection(Participant, Conversation.Value, new InventoryId(_inventoryId), Now),
            CancellationToken.None);

        await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        using var verifyDb = CreateContext();
        var selection = await new SqlActiveInventorySelectionStore(verifyDb)
            .FindAsync(Participant, Conversation.Value, CancellationToken.None);

        Assert.NotNull(selection);
        Assert.Equal(new InventoryId(_inventoryId), selection.InventoryId);
        Assert.Equal(1, await verifyDb.Memberships.AsNoTracking().CountAsync(m => m.ParticipantId == Participant.Value));
    }

    [Fact]
    public async Task Rotating_leaves_another_conversations_pending_confirmation_exactly_where_it_was()
    {
        using var db = CreateContext();
        var proposalStore = new SqlConfirmationProposalStore(db);
        var otherConversation = new ChannelConversationId("web:profile-2");
        var proposal = StockProposal(otherConversation.Value);
        await proposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        await Store(db).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(ProposalStatus.Pending, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Two_rotations_of_the_same_conversation_advance_two_distinct_generations()
    {
        using var seedDb = CreateContext();
        await new SqlFoundryConversationBindingStore(seedDb)
            .GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

        using var firstDb = CreateContext();
        using var secondDb = CreateContext();

        // Two independent contexts rotating the same conversation. SQLite serializes writers, so this
        // proves the GUARD rather than a race: the second rotation must observe the first's generation
        // and advance past it instead of overwriting it. The genuinely concurrent case is Task 14's
        // SQL Server scenario, where two live HTTP resets are in flight at once.
        var results = await Task.WhenAll(
            Store(firstDb).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None),
            Store(secondDb).RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None));

        // Each rotation is a real reset, so two of them advance two generations - never the same one
        // twice, and never a lost update where both write generation 2.
        Assert.Equal([2, 3], results.Select(r => r.Binding.Generation).Order());
        Assert.Equal(2, results.Select(r => r.Binding.FoundryConversationId).Distinct().Count());

        using var verifyDb = CreateContext();
        Assert.Equal(3, (await verifyDb.FoundryConversationBindings.AsNoTracking().SingleAsync()).Generation);
    }

    private static SqlConversationRotationStore Store(MultiChannelAgentDbContext db) =>
        new(db, new SqlFoundryConversationBindingStore(db));

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    private ConfirmationProposal StockProposal(string? conversationId = null)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            conversationId ?? Conversation.Value,
            new InventoryId(_inventoryId),
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", new UnitId(_unitId), "each",
                        LocationId: null, LocationName: null, Note: null,
                        Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = Participant.Value,
            DisplayName = "Resetting Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = Participant.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = _inventoryId,
            ParticipantId = Participant.Value,
            Role = MembershipRole.Owner,
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
