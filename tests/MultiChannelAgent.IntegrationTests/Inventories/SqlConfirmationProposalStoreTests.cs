using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Proves against real SQL Server, under production migrations, the invariants the pending proposal
/// store exists for: exactly one pending proposal per Participant and ChannelConversation - enforced
/// by the database, not by agreement between two code paths - atomic replacement, a guarded settle
/// only one caller can win, a token that is nowhere in the row, and bounded sweeps.
/// </summary>
public sealed class SqlConfirmationProposalStoreTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private const string Conversation = "web:profile-1";

    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private SqlConfirmationProposalStore NewStore(MultiChannelAgentDbContext db) => new(db);

    private async Task SeedInventoryAsync()
    {
        using var db = NewContext();
        var creatorId = Guid.NewGuid();
        db.Participants.Add(new ParticipantEntity
        {
            Id = creatorId,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = $"Warehouse {_inventory.Value:N}",
            NormalizedName = $"warehouse {_inventory.Value:N}",
            CreatedByParticipantId = creatorId,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    private ConfirmationProposal Proposal(
        string token, string? conversation = null, ParticipantId? participantId = null, StockEntryId[]? touched = null)
    {
        var sourceId = touched?.ElementAtOrDefault(0) ?? new StockEntryId(Guid.NewGuid());
        var destinationId = touched?.ElementAtOrDefault(1) ?? new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId ?? _participant,
            conversation ?? Conversation,
            _inventory,
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Rename,
                    Effect = StockChangeEffectKind.RenameMerged,
                    Source = new ProposedEntryState(
                        sourceId, "Steel Bolts", "steel bolts", _unit, "each", null, null, "Blue box",
                        Quantity.Create(4m), Quantity.Zero, Retired: true),
                    Destination = new ProposedEntryState(
                        destinationId, "Brass Rivets", "brass rivets", _unit, "each", null, null, null,
                        Quantity.Create(6m), Quantity.Create(10m), Retired: false),
                    TransferredQuantity = Quantity.Create(4m),
                    NewName = "Brass Rivets",
                    NewNormalizedName = "brass rivets",
                },
            ],
            [new ExpectedEntryVersion(sourceId, Guid.NewGuid()), new ExpectedEntryVersion(destinationId, Guid.NewGuid())],
            [],
            Now);
    }

    [SkippableFact]
    public async Task A_stored_proposal_round_trips_every_exact_effect_and_expected_version()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var token = ConfirmationToken.Issue();
        var proposal = Proposal(token);

        using (var writer = NewContext())
        {
            await NewStore(writer).StoreAsync(proposal, Now, CancellationToken.None);
        }

        using var reader = NewContext();
        var stored = await NewStore(reader).FindPendingAsync(_participant, Conversation, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(proposal.Id, stored!.Id);
        Assert.Equal(proposal.ProposedInTurnId, stored.ProposedInTurnId);
        Assert.Equal(Now.AddMinutes(ConfirmationProposal.LifetimeMinutes), stored.ExpiresAt);

        var change = Assert.Single(stored.Changes);
        var expected = proposal.Changes[0];
        Assert.Equal(expected.Order, change.Order);
        Assert.Equal(StockMutationKind.Rename, change.Kind);
        Assert.Equal(StockChangeEffectKind.RenameMerged, change.Effect);
        Assert.Equal("Brass Rivets", change.NewName);
        Assert.Equal("brass rivets", change.NewNormalizedName);
        Assert.Equal("4", change.TransferredQuantity.ToInvariantText());

        Assert.Equal(expected.Source.StockEntryId, change.Source.StockEntryId);
        Assert.Equal("Steel Bolts", change.Source.Name);
        Assert.Equal("steel bolts", change.Source.NormalizedName);
        Assert.Equal(_unit, change.Source.UnitId);
        Assert.Equal("each", change.Source.UnitCanonicalName);
        Assert.Null(change.Source.LocationId);
        Assert.Equal("Blue box", change.Source.Note);
        Assert.Equal("4", change.Source.PreviousQuantity.ToInvariantText());
        Assert.Equal("0", change.Source.ResultingQuantity.ToInvariantText());
        Assert.True(change.Source.Retired);

        Assert.Equal(expected.Destination!.StockEntryId, change.Destination!.StockEntryId);
        Assert.Equal("Brass Rivets", change.Destination.Name);
        Assert.Equal("6", change.Destination.PreviousQuantity.ToInvariantText());
        Assert.Equal("10", change.Destination.ResultingQuantity.ToInvariantText());
        Assert.False(change.Destination.Retired);

        Assert.Equal(
            proposal.ExpectedVersions.OrderBy(v => v.StockEntryId.ToString(), StringComparer.Ordinal),
            stored.ExpectedVersions.OrderBy(v => v.StockEntryId.ToString(), StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task A_second_pending_proposal_for_one_conversation_cannot_exist_at_all()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        using (var writer = NewContext())
        {
            await NewStore(writer).StoreAsync(Proposal(ConfirmationToken.Issue()), Now, CancellationToken.None);
        }

        // Deliberately bypasses the store: the invariant must be the database's, not the code's.
        using var smuggler = NewContext();
        smuggler.ConfirmationProposals.Add(new ConfirmationProposalEntity
        {
            ProposalId = Guid.NewGuid(),
            TokenHash = ConfirmationToken.HashOf(ConfirmationToken.Issue()).Value,
            ParticipantId = _participant.Value,
            ChannelConversationId = Conversation,
            InventoryId = _inventory.Value,
            ProposedInTurnId = Guid.NewGuid(),
            Status = nameof(ProposalStatus.Pending),
            ChangesJson = "{}",
            ExpectedVersionsJson = "[]",
            ExpectedAbsencesJson = "[]",
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(10),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => smuggler.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Storing_a_replacement_supersedes_the_previous_one_and_leaves_exactly_one_pending()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var first = Proposal(ConfirmationToken.Issue());
        var second = Proposal(ConfirmationToken.Issue());

        using (var writer = NewContext())
        {
            var store = NewStore(writer);
            Assert.False((await store.StoreAsync(first, Now, CancellationToken.None)).SupersededExisting);
        }

        using (var writer = NewContext())
        {
            Assert.True((await NewStore(writer).StoreAsync(second, Now, CancellationToken.None)).SupersededExisting);
        }

        using var reader = NewContext();
        var store2 = NewStore(reader);
        Assert.Equal(ProposalStatus.Superseded, await store2.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Equal(second.Id, (await store2.FindPendingAsync(_participant, Conversation, CancellationToken.None))!.Id);
        Assert.Equal(
            1,
            await reader.ConfirmationProposals
                .AsNoTracking()
                .CountAsync(p => p.ParticipantId == _participant.Value && p.Status == nameof(ProposalStatus.Pending)));
    }

    [SkippableFact]
    public async Task Two_conversations_may_each_hold_their_own_pending_proposal()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var mine = Proposal(ConfirmationToken.Issue());
        var other = Proposal(ConfirmationToken.Issue(), "web:profile-2");

        using (var writer = NewContext())
        {
            await NewStore(writer).StoreAsync(mine, Now, CancellationToken.None);
        }

        using (var writer = NewContext())
        {
            Assert.False((await NewStore(writer).StoreAsync(other, Now, CancellationToken.None)).SupersededExisting);
        }

        using var reader = NewContext();
        var store = NewStore(reader);
        Assert.Equal(mine.Id, (await store.FindPendingAsync(_participant, Conversation, CancellationToken.None))!.Id);
        Assert.Equal(other.Id, (await store.FindPendingAsync(_participant, "web:profile-2", CancellationToken.None))!.Id);
    }

    [SkippableFact]
    public async Task Only_the_first_of_two_concurrent_settles_wins()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var proposal = Proposal(ConfirmationToken.Issue());
        using (var writer = NewContext())
        {
            await NewStore(writer).StoreAsync(proposal, Now, CancellationToken.None);
        }

        using var first = NewContext();
        using var second = NewContext();

        var settles = await Task.WhenAll(
            NewStore(first).SettleAsync(proposal.Id, ProposalStatus.Rejected, Now, CancellationToken.None),
            NewStore(second).SettleAsync(proposal.Id, ProposalStatus.Confirmed, Now, CancellationToken.None));

        Assert.Single(settles, settled => settled);

        using var reader = NewContext();
        Assert.NotEqual(ProposalStatus.Pending, await NewStore(reader).FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_token_hash_is_unique_and_the_token_itself_is_nowhere_in_the_row()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var token = ConfirmationToken.Issue();
        var proposal = Proposal(token);

        using (var writer = NewContext())
        {
            await NewStore(writer).StoreAsync(proposal, Now, CancellationToken.None);
        }

        using var reader = NewContext();
        var row = await reader.ConfirmationProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value);

        Assert.Equal(ConfirmationToken.HashTextLength, row.TokenHash.Length);
        foreach (var column in new[]
                 {
                     row.TokenHash, row.ChangesJson, row.ExpectedVersionsJson, row.ExpectedAbsencesJson,
                     row.ChannelConversationId, row.Status,
                 })
        {
            Assert.DoesNotContain(token, column, StringComparison.Ordinal);
        }

        // The unique token-hash index means one token can never back two proposals.
        using var smuggler = NewContext();
        smuggler.ConfirmationProposals.Add(new ConfirmationProposalEntity
        {
            ProposalId = Guid.NewGuid(),
            TokenHash = row.TokenHash,
            ParticipantId = Guid.NewGuid(),
            ChannelConversationId = "web:profile-9",
            InventoryId = _inventory.Value,
            ProposedInTurnId = Guid.NewGuid(),
            Status = nameof(ProposalStatus.Pending),
            ChangesJson = "{}",
            ExpectedVersionsJson = "[]",
            ExpectedAbsencesJson = "[]",
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(10),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => smuggler.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Expiring_settles_only_pending_proposals_past_their_lifetime()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var proposal = Proposal(ConfirmationToken.Issue());
        using (var writer = NewContext())
        {
            await NewStore(writer).StoreAsync(proposal, Now, CancellationToken.None);
        }

        using (var sweeper = NewContext())
        {
            Assert.Equal(0, await NewStore(sweeper).ExpirePendingBeforeAsync(Now.AddMinutes(9), 100, CancellationToken.None));
        }

        using (var sweeper = NewContext())
        {
            Assert.Equal(1, await NewStore(sweeper).ExpirePendingBeforeAsync(Now.AddMinutes(10), 100, CancellationToken.None));
        }

        using var reader = NewContext();
        Assert.Equal(ProposalStatus.Expired, await NewStore(reader).FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Deleting_settled_proposals_leaves_pending_ones_alone()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal store.");
        await SeedInventoryAsync();

        var settled = Proposal(ConfirmationToken.Issue());
        var pending = Proposal(ConfirmationToken.Issue(), "web:profile-2");

        using (var writer = NewContext())
        {
            var store = NewStore(writer);
            await store.StoreAsync(settled, Now, CancellationToken.None);
            await store.SettleAsync(settled.Id, ProposalStatus.Rejected, Now, CancellationToken.None);
            await store.StoreAsync(pending, Now, CancellationToken.None);
        }

        using (var sweeper = NewContext())
        {
            // The sweep hands the store "now minus the retention window"; nothing has aged out yet.
            Assert.Equal(0, await NewStore(sweeper).DeleteSettledBeforeAsync(Now.AddHours(-1), 100, CancellationToken.None));
        }

        using (var sweeper = NewContext())
        {
            Assert.Equal(1, await NewStore(sweeper).DeleteSettledBeforeAsync(Now.AddHours(1), 100, CancellationToken.None));
        }

        using var reader = NewContext();
        var store2 = NewStore(reader);
        Assert.Null(await store2.FindStatusAsync(settled.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await store2.FindStatusAsync(pending.Id, CancellationToken.None));
    }
}
