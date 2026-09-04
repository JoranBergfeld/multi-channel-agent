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
/// Proves against real SQL Server the one transaction the confirmation protocol rests on: proposal
/// consumption, every state change, every audit fact, and the ledger commit together - or nothing
/// does. A conflict must leave Stock byte-for-byte unchanged and the proposal still Pending, and two
/// concurrent confirmations of one proposal must apply it exactly once.
/// </summary>
public sealed class SqlStockChangeSetStoreTests : SqlIntegrationTestBase
{
    private const string SkipReason = "Docker is not available in this environment; skipping the SQL-backed change-set store.";
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private const string Conversation = "web:profile-1";

    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly InventoryId _otherInventory = new(Guid.NewGuid());
    private readonly ParticipantId _actor = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _shelfA = new(Guid.NewGuid());

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private async Task SeedAsync()
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

        foreach (var inventoryId in new[] { _inventory, _otherInventory })
        {
            db.Inventories.Add(new InventoryEntity
            {
                Id = inventoryId.Value,
                Name = $"Warehouse {inventoryId.Value:N}",
                NormalizedName = $"warehouse {inventoryId.Value:N}",
                CreatedByParticipantId = creatorId,
                ClientRequestId = Guid.NewGuid().ToString(),
                CreatedAt = Now,
            });
        }

        db.Units.Add(new UnitEntity
        {
            Id = _unit.Value,
            InventoryId = _inventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            CreatedAt = Now,
        });
        db.Locations.Add(new LocationEntity
        {
            Id = _shelfA.Value,
            InventoryId = _inventory.Value,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(StockEntryId Id, Guid Stamp)> SeedStockAsync(string name, decimal quantity, LocationId? locationId = null)
    {
        using var db = NewContext();
        var entry = new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            UnitId = _unit.Value,
            LocationId = locationId?.Value,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = Now,
        };
        db.StockEntries.Add(entry);
        await db.SaveChangesAsync();

        return (new StockEntryId(entry.Id), entry.ConcurrencyStamp);
    }

    private ProposedEntryState State(
        StockEntryId? id,
        string name,
        decimal previous,
        decimal resulting,
        bool retired = false,
        LocationId? locationId = null) => new(
        id,
        name,
        NameNormalization.Normalize(name),
        _unit,
        "each",
        locationId,
        locationId is null ? null : "Shelf A",
        Note: null,
        Quantity.Create(previous),
        Quantity.Create(resulting),
        retired);

    private StockChangeSetCommand Command(
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        ProposalId? proposalId = null,
        StockOperationId? operationId = null,
        TurnId? turnId = null,
        IReadOnlyList<ExpectedEquivalentStockAbsence>? absences = null) => new()
        {
            OperationId = operationId ?? new StockOperationId(Guid.NewGuid()),
            InventoryId = _inventory,
            ActorId = _actor,
            ConfirmedByTurnId = turnId ?? TurnId.NewId(),
            ConsumesProposalId = proposalId,
            Changes = changes,
            ExpectedVersions = versions,
            ExpectedAbsences = absences ?? [],
            Now = Now,
        };

    /// <summary>Stores a pending proposal carrying exactly these changes, so a command can consume it.</summary>
    private async Task<ProposalId> StoreProposalAsync(
        IReadOnlyList<ProposedChange> changes, IReadOnlyList<ExpectedEntryVersion> versions)
    {
        var proposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _actor,
            Conversation,
            _inventory,
            TurnId.NewId(),
            changes,
            versions,
            [],
            Now);

        using var db = NewContext();
        await new SqlConfirmationProposalStore(db).StoreAsync(proposal, Now, CancellationToken.None);

        return proposal.Id;
    }

    private async Task<(ProposedChange Change, ExpectedEntryVersion[] Versions, StockEntryId Source, StockEntryId Destination)>
        SeedMergeAsync()
    {
        var source = await SeedStockAsync("Steel Bolts", 10m);
        var destination = await SeedStockAsync("Steel Bolts", 4m, _shelfA);

        var change = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Effect = StockChangeEffectKind.Merged,
            Source = State(source.Id, "Steel Bolts", 10m, 0m, retired: true),
            Destination = State(destination.Id, "Steel Bolts", 4m, 14m, locationId: _shelfA),
            TransferredQuantity = Quantity.Create(10m),
        };

        return (
            change,
            [new ExpectedEntryVersion(source.Id, source.Stamp), new ExpectedEntryVersion(destination.Id, destination.Stamp)],
            source.Id,
            destination.Id);
    }

    [SkippableFact]
    public async Task A_merge_retiring_Move_updates_the_destination_deletes_the_source_and_records_both_identities()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var (change, versions, source, destination) = await SeedMergeAsync();

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(Command([change], versions), CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Applied, result.Outcome);
        var effect = Assert.Single(result.Recorded!.Effects);
        Assert.Equal(destination, effect.SurvivingStockEntryId);
        Assert.Equal(source, effect.RetiredStockEntryId);

        using var reader = NewContext();
        Assert.Equal(14m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == destination.Value)).Quantity);
        Assert.False(await reader.StockEntries.AsNoTracking().AnyAsync(e => e.Id == source.Value));
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value && a.EventType == "StockMoved"));
    }

    [SkippableFact]
    public async Task A_Rename_collision_merges_into_the_colliding_entry_and_retires_the_source()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var source = await SeedStockAsync("Steel Bolts", 4m);
        var colliding = await SeedStockAsync("Brass Rivets", 6m);

        var change = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Rename,
            Effect = StockChangeEffectKind.RenameMerged,
            Source = State(source.Id, "Steel Bolts", 4m, 0m, retired: true),
            Destination = State(colliding.Id, "Brass Rivets", 6m, 10m),
            TransferredQuantity = Quantity.Create(4m),
            NewName = "Brass Rivets",
            NewNormalizedName = "brass rivets",
        };

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(
            Command(
                [change],
                [new ExpectedEntryVersion(source.Id, source.Stamp), new ExpectedEntryVersion(colliding.Id, colliding.Stamp)]),
            CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Applied, result.Outcome);

        using var reader = NewContext();
        Assert.Equal(10m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == colliding.Id.Value)).Quantity);
        Assert.False(await reader.StockEntries.AsNoTracking().AnyAsync(e => e.Id == source.Id.Value));
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockRenamed"));
    }

    [SkippableFact]
    public async Task A_Forget_removes_the_Stock_Entry_and_leaves_its_ledger_row_behind()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var empty = await SeedStockAsync("Steel Bolts", 0m);

        var change = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Forget,
            Effect = StockChangeEffectKind.Forgotten,
            Source = State(empty.Id, "Steel Bolts", 0m, 0m, retired: true),
        };
        var operationId = new StockOperationId(Guid.NewGuid());

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(
            Command([change], [new ExpectedEntryVersion(empty.Id, empty.Stamp)], operationId: operationId), CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Applied, result.Outcome);

        using var reader = NewContext();
        Assert.False(await reader.StockEntries.AsNoTracking().AnyAsync(e => e.Id == empty.Id.Value));

        // The record of what happened outlives the row it describes.
        var recorded = await new SqlStockChangeSetStore(reader).FindRecordedAsync(_inventory, operationId, CancellationToken.None);
        Assert.Equal(empty.Id, Assert.Single(recorded!.Effects).Source.StockEntryId);
    }

    [SkippableFact]
    public async Task Applying_a_change_set_writes_its_state_changes_audits_and_ledger_together()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var first = await SeedStockAsync("Steel Bolts", 10m);
        var second = await SeedStockAsync("Brass Rivets", 6m);

        var changes = new[]
        {
            new ProposedChange
            {
                Order = 1,
                Kind = StockMutationKind.Add,
                Effect = StockChangeEffectKind.QuantityIncreased,
                Source = State(first.Id, "Steel Bolts", 10m, 11m),
            },
            new ProposedChange
            {
                Order = 2,
                Kind = StockMutationKind.Remove,
                Effect = StockChangeEffectKind.QuantityDecreased,
                Source = State(second.Id, "Brass Rivets", 6m, 4m),
            },
        };
        var operationId = new StockOperationId(Guid.NewGuid());

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(
            Command(
                changes,
                [new ExpectedEntryVersion(first.Id, first.Stamp), new ExpectedEntryVersion(second.Id, second.Stamp)],
                operationId: operationId),
            CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Applied, result.Outcome);
        Assert.Equal([1, 2], result.Recorded!.Effects.Select(e => e.Order));

        using var reader = NewContext();
        Assert.Equal(11m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == first.Id.Value)).Quantity);
        Assert.Equal(4m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == second.Id.Value)).Quantity);
        Assert.Single(reader.StockChangeSetOperations.AsNoTracking().Where(o => o.OperationId == operationId.Value));
        Assert.Equal(2, await reader.StockChangeSetEffects.AsNoTracking().CountAsync(e => e.OperationId == operationId.Value));
        Assert.Equal(2, await reader.InventoryAudits.AsNoTracking().CountAsync(a => a.InventoryId == _inventory.Value));
    }

    [SkippableFact]
    public async Task A_change_set_whose_expected_version_moved_applies_nothing_at_all()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var (change, versions, source, destination) = await SeedMergeAsync();
        var proposalId = await StoreProposalAsync([change], versions);

        // A competing writer moves one of the rows between planning and confirming.
        using (var competitor = NewContext())
        {
            await competitor.StockEntries
                .Where(e => e.Id == destination.Value)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ConcurrencyStamp, Guid.NewGuid()), CancellationToken.None);
        }

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(
            Command([change], versions, proposalId), CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);

        using var reader = NewContext();
        Assert.Equal(10m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == source.Value)).Quantity);
        Assert.Equal(4m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == destination.Value)).Quantity);
        Assert.Empty(reader.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value));
        Assert.Empty(reader.StockChangeSetOperations.AsNoTracking().Where(o => o.InventoryId == _inventory.Value));

        // The rolled-back consumption must roll back too, or the Participant loses a proposal that
        // never executed.
        Assert.Equal(
            ProposalStatus.Pending,
            await new SqlConfirmationProposalStore(reader).FindStatusAsync(proposalId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_change_set_whose_expected_absence_was_filled_applies_nothing_at_all()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var source = await SeedStockAsync("Steel Bolts", 10m);

        var change = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Effect = StockChangeEffectKind.Split,
            Source = State(source.Id, "Steel Bolts", 10m, 7m),
            Destination = State(null, "Steel Bolts", 0m, 3m, locationId: _shelfA),
            TransferredQuantity = Quantity.Create(3m),
        };
        var absence = new ExpectedEquivalentStockAbsence("steel bolts", _unit, _shelfA);

        // A competing writer creates the very Equivalent Stock this split intended to create.
        await SeedStockAsync("Steel Bolts", 1m, _shelfA);

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(
            Command([change], [new ExpectedEntryVersion(source.Id, source.Stamp)], absences: [absence]), CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);

        using var reader = NewContext();
        Assert.Equal(10m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == source.Id.Value)).Quantity);
        Assert.Empty(reader.StockChangeSetOperations.AsNoTracking().Where(o => o.InventoryId == _inventory.Value));
    }

    [SkippableFact]
    public async Task Consuming_a_proposal_and_applying_it_happen_in_one_transaction()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var (change, versions, _, destination) = await SeedMergeAsync();
        var proposalId = await StoreProposalAsync([change], versions);
        var operationId = new StockOperationId(Guid.NewGuid());

        using var writer = NewContext();
        var result = await new SqlStockChangeSetStore(writer).ApplyAsync(
            Command([change], versions, proposalId, operationId), CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Applied, result.Outcome);

        using var reader = NewContext();
        Assert.Equal(
            ProposalStatus.Confirmed,
            await new SqlConfirmationProposalStore(reader).FindStatusAsync(proposalId, CancellationToken.None));
        Assert.Equal(14m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == destination.Value)).Quantity);
        Assert.Equal(
            proposalId.Value,
            (await reader.StockChangeSetOperations.AsNoTracking().SingleAsync(o => o.OperationId == operationId.Value)).ProposalId);

        // Consumed inside the mutation transaction, and still settled in the form the retention sweep
        // compares on - a confirmed proposal must not outlive every other terminal status.
        var consumed = await reader.ConfirmationProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposalId.Value);
        Assert.Equal(Now, consumed.SettledAt);
        Assert.Equal(Now.UtcTicks, consumed.SettledAtTicks);
    }

    [SkippableFact]
    public async Task Two_concurrent_confirmations_of_one_proposal_apply_it_exactly_once()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var (change, versions, source, destination) = await SeedMergeAsync();
        var proposalId = await StoreProposalAsync([change], versions);

        // Both confirmations derive the same execution identity from the same proposal, exactly as
        // StockConfirmationService does.
        var operationId = StockOperationId.DeriveForProposal(proposalId);

        using var first = NewContext();
        using var second = NewContext();

        var results = await Task.WhenAll(
            new SqlStockChangeSetStore(first).ApplyAsync(Command([change], versions, proposalId, operationId), CancellationToken.None),
            new SqlStockChangeSetStore(second).ApplyAsync(Command([change], versions, proposalId, operationId), CancellationToken.None));

        Assert.Single(results, r => r.Outcome == StockChangeSetStoreOutcome.Applied);
        Assert.All(
            results,
            r => Assert.Contains(
                r.Outcome,
                new[] { StockChangeSetStoreOutcome.Applied, StockChangeSetStoreOutcome.Conflict, StockChangeSetStoreOutcome.AlreadyApplied }));

        using var reader = NewContext();
        Assert.Equal(14m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == destination.Value)).Quantity);
        Assert.False(await reader.StockEntries.AsNoTracking().AnyAsync(e => e.Id == source.Value));
        Assert.Single(reader.StockChangeSetOperations.AsNoTracking().Where(o => o.InventoryId == _inventory.Value));
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value));
    }

    [SkippableFact]
    public async Task Applying_the_same_operation_identity_again_re_reports_instead_of_re_applying()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var (change, versions, _, destination) = await SeedMergeAsync();
        var command = Command([change], versions);

        using (var writer = NewContext())
        {
            Assert.Equal(
                StockChangeSetStoreOutcome.Applied,
                (await new SqlStockChangeSetStore(writer).ApplyAsync(command, CancellationToken.None)).Outcome);
        }

        using var retry = NewContext();
        var again = await new SqlStockChangeSetStore(retry).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.AlreadyApplied, again.Outcome);
        Assert.Equal(destination, Assert.Single(again.Recorded!.Effects).SurvivingStockEntryId);

        using var reader = NewContext();
        Assert.Equal(14m, (await reader.StockEntries.AsNoTracking().SingleAsync(e => e.Id == destination.Value)).Quantity);
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value));
    }

    [SkippableFact]
    public async Task A_recorded_change_set_is_findable_by_the_Turn_that_confirmed_it_and_invisible_from_other_Inventories()
    {
        Skip.IfNot(DockerAvailable, SkipReason);
        await SeedAsync();
        var (change, versions, _, _) = await SeedMergeAsync();
        var turnId = TurnId.NewId();

        using (var writer = NewContext())
        {
            await new SqlStockChangeSetStore(writer).ApplyAsync(Command([change], versions, turnId: turnId), CancellationToken.None);
        }

        using var reader = NewContext();
        var store = new SqlStockChangeSetStore(reader);

        Assert.NotNull(await store.FindRecordedByTurnAsync(_inventory, turnId, CancellationToken.None));
        Assert.Null(await store.FindRecordedByTurnAsync(_otherInventory, turnId, CancellationToken.None));
        Assert.Null(await store.FindRecordedByTurnAsync(_inventory, TurnId.NewId(), CancellationToken.None));
    }
}
