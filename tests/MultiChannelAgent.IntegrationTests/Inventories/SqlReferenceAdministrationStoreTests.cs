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
/// The one transaction Unit and Location administration rests on: apply everything with its audits,
/// its ledger, its proposal consumption, and its retirement-driven invalidation - or change nothing
/// at all. It also proves the claim every Participant depends on: a Rename never touches a single
/// Stock Entry.
/// </summary>
public sealed class SqlReferenceAdministrationStoreTests : SqlIntegrationTestBase
{
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

    private async Task<Guid> SeedStockAsync(Guid inventoryId, Guid unitId, Guid? locationId, string name)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);
        var stockEntryId = Guid.NewGuid();

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = stockEntryId,
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = 1m,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await db.SaveChangesAsync();

        return stockEntryId;
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
        Guid? proposalId = null) => new()
        {
            OperationId = ReferenceOperationId.Derive(new TurnId(turnId), "reference_tool", 0),
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
    public async Task Creating_a_Unit_writes_it_its_terms_its_audit_and_its_ledger_together()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var unitId = Guid.NewGuid();
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
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", false),
                        Terms =
                        [
                            UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false),
                            UnitTerm.Create("boxes", isCanonical: false, isReserved: false),
                        ],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "cardboard box"), new ExpectedTermAbsence(ReferenceKind.Unit, "boxes")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == unitId);
        Assert.Equal("Cardboard Box", unit.CanonicalName);
        Assert.False(unit.IsReserved);
        Assert.Null(unit.RetiredAt);
        Assert.NotEqual(Guid.Empty, unit.ConcurrencyStamp);

        var terms = await db.UnitTerms.AsNoTracking().Where(t => t.UnitId == unitId).ToListAsync();
        Assert.Equal(2, terms.Count);
        Assert.Single(terms, term => term.IsCanonical && term.NormalizedTerm == "cardboard box");
        Assert.All(terms, term => Assert.False(term.IsReserved));

        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitCreated)));
        Assert.Equal(1, await db.ReferenceOperations.AsNoTracking().CountAsync(o => o.InventoryId == inventoryId));
        Assert.Equal(1, await db.ReferenceEffects.AsNoTracking().CountAsync());
    }

    [SkippableFact]
    public async Task Renaming_a_Unit_preserves_every_identity_and_rewrites_no_Stock_Entry_at_all()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedStockAsync(inventoryId, boxId, shelfId, "Steel Bolts");
        await SeedStockAsync(inventoryId, eachUnitId, null, "Brass Rivets");

        List<StockEntryEntity> before;
        Guid stampBefore;
        using (var snapshotScope = Factory!.Services.CreateScope())
        {
            before = await Db(snapshotScope).StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
            stampBefore = (await Db(snapshotScope).Units.AsNoTracking().FirstAsync(u => u.Id == boxId)).ConcurrencyStamp;
        }

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
                        Kind = ReferenceChangeKind.RenameUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        NewName = "Carton",
                        NewNormalizedName = "carton",
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == boxId);
        Assert.Equal(boxId, unit.Id);
        Assert.Equal("Carton", unit.CanonicalName);
        Assert.Equal("carton", unit.NormalizedCanonicalName);

        var canonical = await db.UnitTerms.AsNoTracking().FirstAsync(t => t.UnitId == boxId && t.IsCanonical);
        Assert.Equal("Carton", canonical.Term);
        Assert.Equal("carton", canonical.NormalizedTerm);
        Assert.Single(await db.UnitTerms.AsNoTracking().Where(t => t.UnitId == boxId && !t.IsCanonical).ToListAsync());

        // The claim every Participant depends on: nothing in StockEntries moved - not a name, not a
        // Unit, not a Location, not a Quantity, and not a concurrency stamp. Equivalent Stock is keyed
        // by UnitId, which never changed, so it cannot have changed either.
        var after = await db.StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(e => (e.Id, e.UnitId, e.LocationId, e.Name, e.NormalizedName, e.Quantity, e.ConcurrencyStamp)),
            after.Select(e => (e.Id, e.UnitId, e.LocationId, e.Name, e.NormalizedName, e.Quantity, e.ConcurrencyStamp)));

        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRenamed)));
    }

    [SkippableFact]
    public async Task Renaming_a_Location_preserves_its_identity_and_rewrites_no_Stock_Entry()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedStockAsync(inventoryId, eachUnitId, shelfId, "Steel Bolts");

        Guid stampBefore;
        List<StockEntryEntity> before;
        using (var snapshotScope = Factory!.Services.CreateScope())
        {
            stampBefore = (await Db(snapshotScope).Locations.AsNoTracking().FirstAsync(l => l.Id == shelfId)).ConcurrencyStamp;
            before = await Db(snapshotScope).StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        }

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
                        Kind = ReferenceChangeKind.RenameLocation,
                        Target = new ProposedReferenceState(ReferenceKind.Location, shelfId, "Shelf A", "shelf a", false),
                        NewName = "Aisle 3",
                        NewNormalizedName = "aisle 3",
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Location, shelfId, stampBefore)],
                [new ExpectedTermAbsence(ReferenceKind.Location, "aisle 3")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var location = await db.Locations.AsNoTracking().FirstAsync(l => l.Id == shelfId);
        Assert.Equal("Aisle 3", location.Name);
        Assert.Equal("aisle 3", location.NormalizedName);

        var after = await db.StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(
            before.Select(e => (e.Id, e.LocationId, e.ConcurrencyStamp)),
            after.Select(e => (e.Id, e.LocationId, e.ConcurrencyStamp)));
    }

    [SkippableFact]
    public async Task Retiring_a_Unit_keeps_its_identity_and_frees_every_one_of_its_terms()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);
        var (stampBefore, _) = await UnitStateAsync(boxId);

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
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                []),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == boxId);
        Assert.Equal(boxId, unit.Id);
        Assert.Equal("Cardboard Box", unit.CanonicalName);
        Assert.NotNull(unit.RetiredAt);

        var terms = await db.UnitTerms.AsNoTracking().Where(t => t.UnitId == boxId).ToListAsync();
        Assert.Equal(2, terms.Count);
        Assert.All(terms, term => Assert.NotNull(term.RetiredAt));

        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));
    }

    [SkippableFact]
    public async Task A_freed_term_can_be_claimed_again_by_a_new_Unit()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stampBefore, _) = await UnitStateAsync(boxId);

        using (var retireScope = Factory!.Services.CreateScope())
        {
            await Store(retireScope).ApplyAsync(
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
                    [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                    []),
                CancellationToken.None);
        }

        var replacementId = Guid.NewGuid();
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
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(
                            ReferenceKind.Unit, replacementId, "Cardboard Box", "cardboard box", false),
                        Terms = [UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false)],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "cardboard box")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal(2, await Db(readScope).Units.AsNoTracking().CountAsync(u => u.NormalizedCanonicalName == "cardboard box"));
    }

    [SkippableFact]
    public async Task A_Retire_that_Stock_now_references_changes_nothing_even_though_the_proposal_was_clean()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stampBefore, _) = await UnitStateAsync(boxId);

        // Decided when nothing referenced it; executed after something does.
        await SeedStockAsync(inventoryId, boxId, null, "Steel Bolts");

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
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                []),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);

        var (_, retiredAt) = await UnitStateAsync(boxId);
        Assert.Null(retiredAt);
        Assert.Equal(0, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal(0, await Db(readScope).ReferenceOperations.AsNoTracking().CountAsync());
    }

    [SkippableFact]
    public async Task A_change_set_whose_expected_version_moved_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);

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
                        Kind = ReferenceChangeKind.RenameUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        NewName = "Carton",
                        NewNormalizedName = "carton",
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, Guid.NewGuid())],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal("Cardboard Box", (await Db(readScope).Units.AsNoTracking().FirstAsync(u => u.Id == boxId)).CanonicalName);
        Assert.Equal(0, await Db(readScope).ReferenceEffects.AsNoTracking().CountAsync());
    }

    [SkippableFact]
    public async Task A_change_set_whose_term_was_claimed_meanwhile_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        await SeedUnitAsync(inventoryId, "Carton", []);

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
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, Guid.NewGuid(), "Carton", "carton", false),
                        Terms = [UnitTerm.Create("Carton", isCanonical: true, isReserved: false)],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal(1, await Db(readScope).Units.AsNoTracking().CountAsync(u => u.NormalizedCanonicalName == "carton"));
    }

    [SkippableFact]
    public async Task Consuming_a_proposal_and_applying_it_happen_in_one_transaction()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stampBefore, _) = await UnitStateAsync(boxId);
        var turnId = Guid.NewGuid();

        using var scope = Factory!.Services.CreateScope();
        var proposalStore = new SqlConfirmationProposalStore(Db(scope));

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(turnId),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RetireUnit,
                    Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
            [],
            DateTimeOffset.UnixEpoch);

        await proposalStore.StoreAsync(proposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var first = await Store(scope).ApplyAsync(
            Command(inventoryId, participantId, turnId, proposal.ReferenceChanges, proposal.ExpectedReferenceVersions, [], proposal.Id.Value),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, first.Outcome);
        Assert.Equal(ProposalStatus.Confirmed, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));

        // The proposal is single use: a second execution under a *different* operation identity loses
        // on the guarded consume and changes nothing.
        using var secondScope = Factory!.Services.CreateScope();
        var second = await Store(secondScope).ApplyAsync(
            Command(inventoryId, participantId, Guid.NewGuid(), proposal.ReferenceChanges, proposal.ExpectedReferenceVersions, [], proposal.Id.Value),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, second.Outcome);
        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));
    }

    [SkippableFact]
    public async Task Retiring_a_Location_settles_a_pending_stock_proposal_that_depended_on_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        var stockEntryId = await SeedStockAsync(inventoryId, eachUnitId, null, "Steel Bolts");

        Guid locationStamp;
        Guid entryStamp;
        using (var snapshotScope = Factory!.Services.CreateScope())
        {
            locationStamp = (await Db(snapshotScope).Locations.AsNoTracking().FirstAsync(l => l.Id == shelfId)).ConcurrencyStamp;
            entryStamp = (await Db(snapshotScope).StockEntries.AsNoTracking().FirstAsync(e => e.Id == stockEntryId)).ConcurrencyStamp;
        }

        using var scope = Factory!.Services.CreateScope();
        var proposalStore = new SqlConfirmationProposalStore(Db(scope));

        // An ordinary pending stock proposal that would place Stock in Shelf A.
        var stockProposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(Guid.NewGuid()),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Move,
                    Effect = StockChangeEffectKind.Placed,
                    Source = new ProposedEntryState(
                        new StockEntryId(stockEntryId), "Steel Bolts", "steel bolts", new UnitId(eachUnitId), "each",
                        null, null, null, Quantity.Create(1m), Quantity.Create(1m), false),
                    Destination = new ProposedEntryState(
                        new StockEntryId(stockEntryId), "Steel Bolts", "steel bolts", new UnitId(eachUnitId), "each",
                        new LocationId(shelfId), "Shelf A", null, Quantity.Create(1m), Quantity.Create(1m), false),
                },
            ],
            [new ExpectedEntryVersion(new StockEntryId(stockEntryId), entryStamp)],
            [],
            DateTimeOffset.UnixEpoch);

        await proposalStore.StoreAsync(stockProposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RetireLocation,
                        Target = new ProposedReferenceState(ReferenceKind.Location, shelfId, "Shelf A", "shelf a", false),
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Location, shelfId, locationStamp)],
                []),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);
        Assert.Equal(ProposalStatus.Conflicted, await proposalStore.FindStatusAsync(stockProposal.Id, CancellationToken.None));
        Assert.Null(await proposalStore.FindPendingAsync(new ParticipantId(participantId), "web-conversation-1", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_instead_of_doing_it_twice()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var turnId = Guid.NewGuid();
        var command = Command(
            inventoryId,
            participantId,
            turnId,
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.CreateLocation,
                    Target = new ProposedReferenceState(ReferenceKind.Location, Guid.NewGuid(), "Shelf A", "shelf a", false),
                },
            ],
            [],
            [new ExpectedTermAbsence(ReferenceKind.Location, "shelf a")]);

        using var scope = Factory!.Services.CreateScope();
        var store = Store(scope);

        var first = await store.ApplyAsync(command, CancellationToken.None);
        var replay = await store.ApplyAsync(command, CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, first.Outcome);
        Assert.Equal(ReferenceAdministrationStoreOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(first.Recorded!.Changes[0].ReferenceId, replay.Recorded!.Changes[0].ReferenceId);
        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.LocationCreated)));

        using var readScope = Factory!.Services.CreateScope();
        var byTurn = await Store(readScope).FindRecordedByTurnAsync(
            new InventoryId(inventoryId), new TurnId(turnId), CancellationToken.None);

        Assert.NotNull(byTurn);
        Assert.Equal(first.Recorded.Changes[0].ReferenceId, byTurn!.Changes[0].ReferenceId);
    }
}
