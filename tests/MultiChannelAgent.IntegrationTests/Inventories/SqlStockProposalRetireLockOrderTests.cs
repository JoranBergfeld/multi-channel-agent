using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The lock order the two confirming stores share, proven under an orchestrated interleaving rather
/// than hoped for.
///
/// Both stores touch the same two kinds of row: a pending proposal, and the Unit or Location a change
/// depends on. <see cref="SqlReferenceAdministrationStore"/> takes the reference and only then, inside
/// the same transaction, settles every pending proposal that referenced it. If
/// <see cref="SqlStockChangeSetStore"/> consumed its own proposal before taking the reference, the two
/// would hold exactly what the other was waiting for - the stock confirmation holding its proposal and
/// wanting the Unit, the Retire holding the Unit and wanting that proposal - and cycle.
///
/// That cycle is avoidable and has nothing to do with the acknowledged Serializable Stock-range one,
/// so it is not something to tolerate: the shared order is reference, then proposal, then Stock rows.
/// The interleaving below drives both stores to exactly the point where the old order cycled.
/// </summary>
public sealed class SqlStockProposalRetireLockOrderTests : SqlIntegrationTestBase
{
    /// <summary>Long enough that the other side has certainly reached its own gate, short enough to keep the suite quick.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private MultiChannelAgentDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    /// <summary>
    /// A context on the same database carrying one gate. Interceptors are part of a context's options,
    /// so this is built here rather than resolved from the application's own container.
    /// </summary>
    private MultiChannelAgentDbContext GatedContext(GateInterceptor gate)
    {
        using var scope = Factory!.Services.CreateScope();
        var connectionString = Db(scope).Database.GetConnectionString();

        return new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(gate)
                .Options);
    }

    [SkippableFact]
    public async Task Confirming_a_Stock_proposal_while_its_Unit_is_retired_serializes_instead_of_deadlocking()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed lock order proof.");

        var (inventoryId, participantId) = await SeedAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box");
        var unitStamp = await UnitStampAsync(boxId);
        var proposal = await SeedPendingStockProposalAsync(inventoryId, participantId, boxId);

        // Each side opens its gate once it has taken the lock the other one needs next, then waits for
        // the other to do the same. Under the old order both gates opened - the stock confirmation
        // holding its proposal, the Retire holding the Unit - and the two then cycled into SQL error
        // 1205. Under the shared order the stock confirmation takes the Unit first, so the Retire never
        // reaches its gate, this side simply waits out the timeout, and one of them wins outright.
        using var stockHoldsItsProposal = new SemaphoreSlim(0, 1);
        using var retireHoldsTheUnit = new SemaphoreSlim(0, 1);

        async Task<StockChangeSetStoreOutcome> ConfirmStockAsync()
        {
            using var db = GatedContext(new GateInterceptor(
                "[ConfirmationProposals]", stockHoldsItsProposal, retireHoldsTheUnit, GateTimeout));

            var result = await new SqlStockChangeSetStore(db).ApplyAsync(
                ChangeSetCommand(inventoryId, participantId, boxId, proposal), CancellationToken.None);

            return result.Outcome;
        }

        async Task<ReferenceAdministrationStoreOutcome> RetireUnitAsync()
        {
            using var db = GatedContext(new GateInterceptor(
                "[Units]", retireHoldsTheUnit, stockHoldsItsProposal, GateTimeout));

            var result = await new SqlReferenceAdministrationStore(db, new SqlConfirmationProposalStore(db)).ApplyAsync(
                RetireCommand(inventoryId, participantId, boxId, unitStamp), CancellationToken.None);

            return result.Outcome;
        }

        var stock = ConfirmStockAsync();
        var retire = RetireUnitAsync();

        // Neither side may fault. A deadlock here would be the avoidable cycle, not the acknowledged
        // Serializable Stock-range one, so it is a failure rather than something to retry.
        var stockOutcome = await stock;
        var retireOutcome = await retire;

        using var verifyScope = Factory!.Services.CreateScope();
        var verifyDb = Db(verifyScope);

        var unit = await verifyDb.Units.AsNoTracking().SingleAsync(u => u.Id == boxId);
        var stockCount = await verifyDb.StockEntries.AsNoTracking().CountAsync(e => e.UnitId == boxId);
        var proposalStatus = (await verifyDb.ConfirmationProposals.AsNoTracking()
            .SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status;

        // Exactly one of them won, and the loser answered with a typed outcome rather than a fault.
        if (stockOutcome == StockChangeSetStoreOutcome.Applied)
        {
            Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, retireOutcome);
            Assert.Null(unit.RetiredAt);
            Assert.Equal(1, stockCount);
            Assert.Equal(nameof(ProposalStatus.Confirmed), proposalStatus);
            Assert.Empty(await verifyDb.ReferenceOperations.AsNoTracking().ToListAsync());
        }
        else
        {
            Assert.Equal(StockChangeSetStoreOutcome.Conflict, stockOutcome);
            Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, retireOutcome);
            Assert.NotNull(unit.RetiredAt);
            Assert.Equal(0, stockCount);

            // Settled by the Retire's own invalidation, and never executed.
            Assert.Equal(nameof(ProposalStatus.Conflicted), proposalStatus);
            Assert.Empty(await verifyDb.StockChangeSetOperations.AsNoTracking().ToListAsync());
        }

        // Whichever way it went, the one state that must be unreachable is unreachable.
        Assert.True(unit.RetiredAt is null || stockCount == 0);
    }

    /// <summary>
    /// Opens <paramref name="reached"/> once the command it is watching for has run - which is once
    /// this transaction holds that row's lock - then waits for the other side to reach its own gate.
    /// The wait is bounded, so an order that never lets the other side arrive simply proceeds.
    /// </summary>
    private sealed class GateInterceptor(
        string table, SemaphoreSlim reached, SemaphoreSlim other, TimeSpan timeout) : DbCommandInterceptor
    {
        private bool _opened;

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_opened && command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                _opened = true;
                reached.Release();
                await other.WaitAsync(timeout, cancellationToken);
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private static StockChangeSetCommand ChangeSetCommand(
        Guid inventoryId, Guid participantId, Guid unitId, ConfirmationProposal proposal) => new()
        {
            OperationId = proposal.ExecutionOperationId,
            InventoryId = new InventoryId(inventoryId),
            ActorId = new ParticipantId(participantId),
            ConfirmedByTurnId = TurnId.NewId(),
            ConsumesProposalId = proposal.Id,
            Changes = proposal.Changes,
            ExpectedVersions = proposal.ExpectedVersions,
            ExpectedAbsences = proposal.ExpectedAbsences,
            Now = Now,
        };

    private static ReferenceChangeSetCommand RetireCommand(
        Guid inventoryId, Guid participantId, Guid unitId, Guid unitStamp) => new()
        {
            OperationId = new ReferenceOperationId(Guid.NewGuid()),
            InventoryId = new InventoryId(inventoryId),
            ActorId = new ParticipantId(participantId),
            ConfirmedByTurnId = TurnId.NewId(),
            ConsumesProposalId = null,
            Changes =
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RetireUnit,
                    Target = new ProposedReferenceState(
                        ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", Reserved: false),
                },
            ],
            ExpectedVersions = [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, unitStamp)],
            ExpectedTermAbsences = [],
            Now = Now,
        };

    private async Task<ConfirmationProposal> SeedPendingStockProposalAsync(
        Guid inventoryId, Guid participantId, Guid unitId)
    {
        var proposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web:profile-1",
            new InventoryId(inventoryId),
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Add,
                    Effect = StockChangeEffectKind.Created,
                    Source = new ProposedEntryState(
                        null, "Steel Bolts", "steel bolts", new UnitId(unitId), "Cardboard Box",
                        LocationId: null, LocationName: null, Note: null,
                        Quantity.Zero, Quantity.Create(4m), Retired: false),
                },
            ],
            [],
            [new ExpectedEquivalentStockAbsence("steel bolts", new UnitId(unitId), null)],
            Now);

        using var scope = Factory!.Services.CreateScope();
        await new SqlConfirmationProposalStore(Db(scope)).StoreAsync(proposal, Now, CancellationToken.None);

        return proposal;
    }

    private async Task<Guid> UnitStampAsync(Guid unitId)
    {
        using var scope = Factory!.Services.CreateScope();

        return (await Db(scope).Units.AsNoTracking().SingleAsync(u => u.Id == unitId)).ConcurrencyStamp;
    }

    private async Task<Guid> SeedUnitAsync(Guid inventoryId, string canonicalName)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);
        var unitId = Guid.NewGuid();

        db.Units.Add(new UnitEntity
        {
            Id = unitId,
            InventoryId = inventoryId,
            CanonicalName = canonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(canonicalName),
            IsReserved = false,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            Term = canonicalName,
            NormalizedTerm = NameNormalization.Normalize(canonicalName),
            IsCanonical = true,
            IsReserved = false,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        return unitId;
    }

    private async Task<(Guid InventoryId, Guid ParticipantId)> SeedAsync()
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);
        var inventoryId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = $"Warehouse {inventoryId:N}",
            NormalizedName = $"warehouse {inventoryId:N}",
            CreatedByParticipantId = participantId,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = Now,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = inventoryId,
            ParticipantId = participantId,
            Role = MembershipRole.Owner,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        return (inventoryId, participantId);
    }
}
