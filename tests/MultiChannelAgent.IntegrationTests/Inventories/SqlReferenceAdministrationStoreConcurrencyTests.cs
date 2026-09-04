using System.Data;
using Microsoft.Data.SqlClient;
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
/// Two writers racing for the same reference. The Serializable retire path and the guarded
/// version updates must let exactly one of them win, and the loser must change nothing at all.
/// </summary>
public sealed class SqlReferenceAdministrationStoreConcurrencyTests : SqlIntegrationTestBase
{
    /// <summary>SQL Server's "Transaction was deadlocked ... and has been chosen as the deadlock victim".</summary>
    private const int DeadlockVictimErrorNumber = 1205;

    private MultiChannelAgentDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private async Task<(Guid InventoryId, Guid EachUnitId)> SeedInventoryAsync()
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var inventoryId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Catalog Owner",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Catalog Warehouse",
            NormalizedName = "catalog warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = inventoryId,
            ParticipantId = participantId,
            Role = MembershipRole.Owner,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        var each = Unit.CreateReservedEach(new InventoryId(inventoryId), DateTimeOffset.UnixEpoch);
        db.Units.Add(new UnitEntity
        {
            Id = each.Id.Value,
            InventoryId = inventoryId,
            CanonicalName = each.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(each.CanonicalName),
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        foreach (var term in each.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = each.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = true,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await db.SaveChangesAsync();

        return (inventoryId, each.Id.Value);
    }

    private async Task<Guid> SeedUnitAsync(Guid inventoryId, string canonicalName, string[] aliases, bool retired = false)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var unit = Unit.Create(new InventoryId(inventoryId), canonicalName, aliases, DateTimeOffset.UnixEpoch);
        var retiredAt = retired ? (DateTimeOffset?)DateTimeOffset.UnixEpoch.AddDays(1) : null;

        db.Units.Add(new UnitEntity
        {
            Id = unit.Id.Value,
            InventoryId = inventoryId,
            CanonicalName = unit.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(unit.CanonicalName),
            IsReserved = false,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            RetiredAt = retiredAt,
        });

        foreach (var term in unit.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = unit.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = false,
                CreatedAt = DateTimeOffset.UnixEpoch,
                RetiredAt = retiredAt,
            });
        }

        await db.SaveChangesAsync();

        return unit.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(Guid inventoryId, string name, bool retired = false)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var location = Location.Create(new InventoryId(inventoryId), name, DateTimeOffset.UnixEpoch);

        db.Locations.Add(new LocationEntity
        {
            Id = location.Id.Value,
            InventoryId = inventoryId,
            Name = location.Name,
            NormalizedName = location.NormalizedName,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            RetiredAt = retired ? DateTimeOffset.UnixEpoch.AddDays(1) : null,
        });

        await db.SaveChangesAsync();

        return location.Id.Value;
    }

    private async Task SeedStockAsync(Guid inventoryId, Guid unitId, Guid? locationId, string name)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = 1m,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await db.SaveChangesAsync();
    }

    private SqlReferenceAdministrationStore Store(IServiceScope scope) =>
        new(Db(scope), new SqlConfirmationProposalStore(Db(scope)));

    private static ReferenceChangeSetCommand Command(
        Guid inventoryId,
        Guid participantId,
        Guid turnId,
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences,
        Guid? proposalId = null,
        ReferenceOperationId? operationId = null) => new()
        {
            // A retry has to carry the identity its first attempt did, or the ledger could not tell a
            // second attempt from a second operation.
            OperationId = operationId ?? ReferenceOperationId.Derive(new TurnId(turnId), "reference_tool", 0),
            InventoryId = new InventoryId(inventoryId),
            ActorId = new ParticipantId(participantId),
            ConfirmedByTurnId = new TurnId(turnId),
            ConsumesProposalId = proposalId is { } id ? new ProposalId(id) : null,
            Changes = changes,
            ExpectedVersions = versions,
            ExpectedTermAbsences = absences,
            Now = DateTimeOffset.UnixEpoch,
        };

    private async Task<Guid> ParticipantIdAsync(Guid inventoryId)
    {
        using var scope = Factory!.Services.CreateScope();

        return await Db(scope).Memberships
            .AsNoTracking()
            .Where(m => m.InventoryId == inventoryId)
            .Select(m => m.ParticipantId)
            .FirstAsync();
    }

    private async Task<(Guid Stamp, DateTimeOffset? RetiredAt)> UnitStateAsync(Guid unitId)
    {
        using var scope = Factory!.Services.CreateScope();
        var row = await Db(scope).Units.AsNoTracking().FirstAsync(u => u.Id == unitId);

        return (row.ConcurrencyStamp, row.RetiredAt);
    }

    private async Task<int> CountAuditsAsync(Guid inventoryId, string eventType)
    {
        using var scope = Factory!.Services.CreateScope();

        return await Db(scope).InventoryAudits
            .AsNoTracking()
            .CountAsync(a => a.InventoryId == inventoryId && a.EventType == eventType);
    }

    [SkippableFact]
    public async Task Only_one_of_two_concurrent_Retires_of_one_Unit_can_win()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stamp, _) = await UnitStateAsync(boxId);

        async Task<ReferenceAdministrationStoreOutcome> RetireAsync()
        {
            using var scope = Factory!.Services.CreateScope();
            var result = await Store(scope).ApplyAsync(
                Command(
                    inventoryId,
                    participantId,
                    Guid.NewGuid(),
                    [
                        new ProposedReferenceChange
                        {
                            Order = 1,
                            Kind = ReferenceChangeKind.RetireUnit,
                            Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        },
                    ],
                    [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stamp)],
                    []),
                CancellationToken.None);

            return result.Outcome;
        }

        var outcomes = await Task.WhenAll(RetireAsync(), RetireAsync());

        Assert.Single(outcomes, outcome => outcome == ReferenceAdministrationStoreOutcome.Applied);
        Assert.Single(outcomes, outcome => outcome == ReferenceAdministrationStoreOutcome.Conflict);
        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));
    }

    [SkippableFact]
    public async Task A_Retire_racing_a_Stock_write_never_leaves_a_retired_Unit_with_Stock_referencing_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stamp, _) = await UnitStateAsync(boxId);
        var retireOperationId = new ReferenceOperationId(Guid.NewGuid());

        async Task RetireAsync()
        {
            using var scope = Factory!.Services.CreateScope();
            var result = await Store(scope).ApplyAsync(
                Command(
                    inventoryId,
                    participantId,
                    Guid.NewGuid(),
                    [
                        new ProposedReferenceChange
                        {
                            Order = 1,
                            Kind = ReferenceChangeKind.RetireUnit,
                            Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        },
                    ],
                    [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stamp)],
                    [],
                    operationId: retireOperationId),
                CancellationToken.None);

            // Whichever way the race went, a Retire only ever ends as one of these two - and never as
            // a success that left Stock behind.
            Assert.Contains(
                result.Outcome,
                (ReferenceAdministrationStoreOutcome[])
                [ReferenceAdministrationStoreOutcome.Applied, ReferenceAdministrationStoreOutcome.Conflict]);
        }

        async Task AddStockAsync()
        {
            using var scope = Factory!.Services.CreateScope();
            var db = Db(scope);

            // Resolving the Unit and writing the Stock are one transaction on purpose. Resolution is
            // active-only, so a Unit this race has already retired resolves to nothing and the
            // mutation is reference_not_found - but only if the decision is still true when the write
            // lands. Deciding in one transaction and writing in another would reintroduce exactly the
            // phantom the Retire's Serializable range lock is here to prevent: the Retire could commit
            // in between, and the write would then land on a Unit that no longer exists.
            using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, CancellationToken.None);

            var resolved = await new SqlInventoryReferenceStore(db).ResolveUnitAsync(
                new InventoryId(inventoryId), boxId.ToString(), CancellationToken.None);

            if (resolved is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return;
            }

            db.StockEntries.Add(new StockEntryEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = boxId,
                LocationId = null,
                Name = "Steel Bolts",
                NormalizedName = "steel bolts",
                Quantity = 1m,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });

            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        // Both sides run for real. Serializable makes the Retire's conflict re-check take a range lock
        // over the very Stock rows the other side is inserting into, so SQL Server may legitimately
        // pick one of them as a deadlock victim - that is the isolation level working, not a bug.
        var victims = await Task.WhenAll(RunToleratingOneDeadlockAsync(RetireAsync), RunToleratingOneDeadlockAsync(AddStockAsync));

        Assert.True(
            victims.Count(victim => victim is not null) <= 1,
            "A deadlock has exactly one victim; both sides losing would mean something else went wrong.");

        // The production contract for a raw deadlock: it is never laundered into a semantic answer.
        // Nothing was applied and no ledger row exists, so the Turn reports a transient failure and the
        // work stays retryable - which is exactly what the bounded retry below stands in for.
        if (victims[0] is not null)
        {
            using var ledgerScope = Factory!.Services.CreateScope();
            Assert.Null(await Store(ledgerScope).FindRecordedAsync(
                new InventoryId(inventoryId), retireOperationId, CancellationToken.None));
        }

        // One bounded retry of whichever side lost, on a fresh scope, so the race converges instead of
        // leaving the invariant decided by who happened to be picked. A retry must never deadlock
        // again - by now the other side has committed and there is nothing left to race with.
        if (victims[0] is not null)
        {
            await RetireAsync();
        }

        if (victims[1] is not null)
        {
            await AddStockAsync();
        }

        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);
        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == boxId);
        var stockCount = await db.StockEntries.AsNoTracking().CountAsync(e => e.UnitId == boxId);

        // Either the Unit is still active, or nothing references it. A retired Unit with Stock
        // referencing it is the one state that must be unreachable, whichever way the race went.
        Assert.True(
            unit.RetiredAt is null || stockCount == 0,
            $"A retired Unit was left with {stockCount} Stock Entries referencing it.");
    }

    /// <summary>
    /// Runs one side of the race, returning the deadlock it lost to or null when it finished. Only
    /// SQL Server error 1205 - "chosen as the deadlock victim" - is tolerated; every other fault is
    /// rethrown, so this can never quietly absorb a real failure.
    /// </summary>
    private static async Task<SqlException?> RunToleratingOneDeadlockAsync(Func<Task> side)
    {
        try
        {
            await side();
            return null;
        }
        catch (Exception exception) when (DeadlockVictim(exception) is not null)
        {
            return DeadlockVictim(exception);
        }
    }

    private static SqlException? DeadlockVictim(Exception exception) => exception switch
    {
        SqlException { Number: DeadlockVictimErrorNumber } deadlock => deadlock,
        { InnerException: { } inner } => DeadlockVictim(inner),
        _ => null,
    };

}
