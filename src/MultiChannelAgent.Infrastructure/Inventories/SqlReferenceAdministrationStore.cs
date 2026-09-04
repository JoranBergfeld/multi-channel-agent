using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IReferenceAdministrationStore"/>: the one transaction Unit and
/// Location administration rests on.
///
/// One <see cref="ApplyAsync"/> call locks and verifies every touched reference, consumes the
/// proposal, verifies every term it means to claim, <em>re-checks every Retire against current
/// Stock Entries</em>, applies every change, appends one minimal semantic audit fact per change,
/// settles every other pending proposal that referenced a retired identity, and writes the ledger -
/// all inside one explicit transaction. Any failure rolls the whole thing back, so a caller that
/// sees <see cref="ReferenceAdministrationStoreOutcome.Conflict"/> can rely on nothing at all having
/// happened, including the proposal still being pending.
///
/// Three things here are deliberate rather than incidental:
///
/// <list type="bullet">
/// <item><b>Locking order.</b> Reference, then proposal, then the writes - the same order
/// <see cref="AssignedReferenceLocks"/> documents and <see cref="SqlStockChangeSetStore"/> follows,
/// so the two stores genuinely share one. Within references it is Units before Locations, then the
/// ordinal text of the identity, with a single guarded UPDATE per row that both takes the row's
/// exclusive lock and checks its expected version. Taking the proposal first would be the one
/// inversion available here, because this transaction later settles every other pending proposal that
/// referenced what it retires: a confirmation holding its own proposal and waiting on a reference
/// would be holding exactly what a competing Retire reaches for.</item>
/// <item><b>Serializable for a Retire.</b> Under read-committed, a Stock Entry could be decided
/// against an active Unit and inserted just after this transaction commits, leaving a retired Unit
/// with stock referencing it. A Retire's conflict check is a range query, so serializable isolation
/// makes the two serialize. It is scoped to sets that carry a Retire because nothing else needs
/// it.</item>
/// <item><b>Nothing is ever called a conflict that cannot be established.</b> A fault this store
/// cannot attribute to a version, a claimed term, or a blocked Retire propagates as the real fault
/// it is - the Turn then ends as a transient failure the Participant can simply ask again, which is
/// safe precisely because nothing was applied.</item>
/// </list>
///
/// The ledger commits with the state change rather than after it, exactly as
/// <see cref="SqlStockChangeSetStore"/> does: the terminal Outcome is written later, in its own
/// atomic write, and if the process dies in between, the Turn is reprocessed, finds its ledger row
/// through <see cref="FindRecordedByTurnAsync"/>, and re-reports instead of re-applying.
/// </summary>
public sealed class SqlReferenceAdministrationStore(
    MultiChannelAgentDbContext db, IConfirmationProposalStore proposalStore) : IReferenceAdministrationStore
{
    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);

    private static readonly JsonSerializerOptions AliasOptions = new();

    public async Task<RecordedReferenceChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, ReferenceOperationId operationId, CancellationToken cancellationToken)
    {
        var header = await db.ReferenceOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<RecordedReferenceChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken)
    {
        var header = await db.ReferenceOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.InventoryId == inventoryId.Value && o.ConfirmedByTurnId == turnId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<ReferenceAdministrationStoreResult> ApplyAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, already);
        }

        var retires = command.Changes.Where(change => change.RetiresReference).ToList();

        // Serializable only where it is actually needed: a Retire's "is anything still referencing
        // this" is a range query, and under read-committed a concurrent Stock insert could commit
        // just after this transaction does. Everything else is fully protected by the guarded
        // version checks and the filtered uniqueness indexes.
        await using var transaction = retires.Count > 0
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Lock and verify every touched reference first, in one globally agreed order. Each
            //    statement both takes the row's exclusive lock and asserts the version the proposal was
            //    decided against, so a reference that moved since stops the whole set here - and does
            //    so before this proposal has been consumed, leaving it pending.
            //
            //    References come before the proposal for the same reason they do in
            //    SqlStockChangeSetStore, and it is the same shared order: reference, then proposal,
            //    then the writes. This transaction will later settle every other pending proposal that
            //    referenced what it retires, so a confirmation that took its own proposal first would
            //    hold exactly what a competing Retire is reaching for while waiting on the very
            //    reference that Retire is holding.
            foreach (var expected in command.ExpectedVersions
                .OrderBy(version => version.Kind)
                .ThenBy(version => version.ReferenceId.ToString("D"), StringComparer.Ordinal))
            {
                if (!await LockAndVerifyAsync(command.InventoryId, expected, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 2. Consume the proposal, guarded, so a losing confirmation stops before anything is
            //    applied. Every write above it is rolled back with the rest if it does.
            if (command.ConsumesProposalId is { } proposalId)
            {
                var consumed = await db.ConfirmationProposals
                    .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(p => p.Status, nameof(ProposalStatus.Confirmed))
                            .SetProperty(p => p.SettledAt, command.Now)
                            .SetProperty(p => p.SettledAtTicks, command.Now.UtcTicks),
                        cancellationToken);

                if (consumed != 1)
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 3. Verify every term this set means to claim is still free. The filtered unique indexes
            //    are the real guarantee; this check turns the common case into a clean conflict rather
            //    than a caught index violation.
            foreach (var absence in command.ExpectedTermAbsences)
            {
                if (await TermIsTakenAsync(command.InventoryId, absence, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 4. The authoritative Retire check. The plan-time check told the Participant before they
            //    were asked; this one decides. Stock created in between makes the Retire fail, which
            //    is exactly what "confirmed Retire fails for currently referenced data" means.
            foreach (var retire in retires)
            {
                if (await AnyStockReferencesAsync(command.InventoryId, retire.Target, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 5. Apply the changes in the order the Participant reviewed.
            var recorded = new List<RecordedReferenceChange>(command.Changes.Count);
            foreach (var change in command.Changes.OrderBy(change => change.Order))
            {
                var applied = await ApplyChangeAsync(command, change, cancellationToken);
                if (applied is null)
                {
                    return await RolledBackConflictAsync(transaction);
                }

                recorded.Add(applied);
            }

            // 6. Settle every *other* pending proposal that depended on something this set retired -
            //    stock proposals included. The proposal being confirmed right now cannot be caught by
            //    this: step 2 already moved it out of Pending.
            foreach (var retire in retires)
            {
                await proposalStore.InvalidateReferencingAsync(
                    command.InventoryId, retire.Target.Kind, retire.Target.ReferenceId, command.Now, cancellationToken);
            }

            // 7. Ledger, effects, and one minimal semantic audit fact per change.
            db.ReferenceOperations.Add(new ReferenceOperationEntity
            {
                OperationId = command.OperationId.Value,
                InventoryId = command.InventoryId.Value,
                ConfirmedByTurnId = command.ConfirmedByTurnId.Value,
                ProposalId = command.ConsumesProposalId?.Value,
                AppliedAt = command.Now,
            });

            foreach (var change in recorded)
            {
                db.ReferenceEffects.Add(ToEntity(command.OperationId, change));

                db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
                    ReferenceAdministrationFacts.EventTypeFor(change.Kind),
                    AuditActorKind.Participant,
                    command.ActorId.ToString(),
                    command.InventoryId,
                    subjectParticipantId: null,
                    ReferenceAdministrationFacts.OutcomeCodeFor(change.Kind),
                    command.Now)));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ReferenceAdministrationStoreResult(
                ReferenceAdministrationStoreOutcome.Applied,
                new RecordedReferenceChangeSet(command.OperationId, command.ConsumesProposalId, recorded));
        }
        catch (DbUpdateException exception)
        {
            await db.AbandonAsync(transaction);

            // A competing writer may have been this very operation, applied by another replica. Its
            // ledger row is the authoritative record of what happened, so converge on re-reporting it
            // rather than claiming a conflict against ourselves.
            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, converged);
            }

            if (exception is DbUpdateConcurrencyException || await AnyClaimedTermTakenAsync(command, cancellationToken))
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Conflict, null);
            }

            throw;
        }
        catch (DbException)
        {
            // A guarded ExecuteUpdate that violates a filtered unique index raises the provider's own
            // exception rather than a DbUpdateException, and serializable isolation can additionally
            // produce a deadlock victim. Classify only what can actually be established; anything else
            // propagates as the fault it is, which the Turn reports as a transient failure - safe,
            // because nothing was applied.
            await db.AbandonAsync(transaction);

            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, converged);
            }

            if (await AnyClaimedTermTakenAsync(command, cancellationToken))
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Conflict, null);
            }

            throw;
        }
        catch
        {
            // Every other fault leaves the same debris, and several are reachable - a cancellation
            // between staging an insert and saving it, for one. The transaction would roll back on
            // dispose either way, but the ChangeTracker would not, and this DbContext serves a whole
            // batch of Turns.
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// Applies exactly one decided change, or returns null when a guarded statement touched no row -
    /// which cannot happen while the verify pass holds every row, and so must fail the whole set
    /// rather than be applied partially.
    ///
    /// Inserts are staged and flushed at <c>SaveChangesAsync</c> while updates run immediately, which
    /// is safe here because a change set can never touch one reference twice (the service refuses
    /// that outright), so no change in a set can depend on another's write being visible.
    /// </summary>
    private async Task<RecordedReferenceChange?> ApplyChangeAsync(
        ReferenceChangeSetCommand command, ProposedReferenceChange change, CancellationToken cancellationToken)
    {
        var inventoryId = command.InventoryId.Value;
        var referenceId = change.Target.ReferenceId;

        switch (change.Kind)
        {
            case ReferenceChangeKind.CreateUnit:
            {
                db.Units.Add(new UnitEntity
                {
                    Id = referenceId,
                    InventoryId = inventoryId,
                    CanonicalName = change.Target.Name,
                    NormalizedCanonicalName = change.Target.NormalizedName,
                    IsReserved = false,
                    ConcurrencyStamp = Guid.NewGuid(),
                    CreatedAt = command.Now,
                    RetiredAt = null,
                });

                foreach (var term in change.Terms)
                {
                    db.UnitTerms.Add(new UnitTermEntity
                    {
                        Id = Guid.NewGuid(),
                        InventoryId = inventoryId,
                        UnitId = referenceId,
                        Term = term.Term,
                        NormalizedTerm = term.NormalizedTerm,
                        IsCanonical = term.IsCanonical,

                        // Only the reserved `each` Unit's original five terms are fixed, and nothing
                        // here can ever create that Unit.
                        IsReserved = false,
                        CreatedAt = command.Now,
                        RetiredAt = null,
                    });
                }

                return Recorded(change) with
                {
                    Aliases = [.. change.Terms.Where(term => !term.IsCanonical).Select(term => term.Term)],
                };
            }

            case ReferenceChangeKind.RenameUnit:
            {
                var newName = change.NewName!;
                var newNormalizedName = change.NewNormalizedName!;

                var renamed = await db.Units
                    .Where(u => u.Id == referenceId && u.InventoryId == inventoryId && u.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(u => u.CanonicalName, newName)
                            .SetProperty(u => u.NormalizedCanonicalName, newNormalizedName)
                            .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                // The canonical term moves with the Unit's name; its aliases do not. StockEntries is
                // neither read nor written, so Equivalent Stock - keyed by UnitId - cannot change.
                var retermed = await db.UnitTerms
                    .Where(t => t.UnitId == referenceId && t.InventoryId == inventoryId && t.IsCanonical && t.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(t => t.Term, newName)
                            .SetProperty(t => t.NormalizedTerm, newNormalizedName),
                        cancellationToken);

                return renamed == 1 && retermed == 1 ? Recorded(change) with { NewName = newName } : null;
            }

            case ReferenceChangeKind.AddUnitAlias:
            {
                var term = change.Term!;

                db.UnitTerms.Add(new UnitTermEntity
                {
                    Id = Guid.NewGuid(),
                    InventoryId = inventoryId,
                    UnitId = referenceId,
                    Term = term.Term,
                    NormalizedTerm = term.NormalizedTerm,
                    IsCanonical = false,
                    IsReserved = false,
                    CreatedAt = command.Now,
                    RetiredAt = null,
                });

                return await BumpUnitAsync(inventoryId, referenceId, cancellationToken)
                    ? Recorded(change) with { Alias = term.Term }
                    : null;
            }

            case ReferenceChangeKind.RemoveUnitAlias:
            {
                var term = change.Term!;
                var normalized = term.NormalizedTerm;

                // Retired rather than deleted: the row - and what it used to mean - remains, which is
                // what keeps prior audits and prior proposals readable. Guarded on both protections so
                // a canonical or fixed term can never be removed even if a caller asked.
                var removed = await db.UnitTerms
                    .Where(t => t.UnitId == referenceId
                        && t.InventoryId == inventoryId
                        && t.NormalizedTerm == normalized
                        && !t.IsCanonical
                        && !t.IsReserved
                        && t.RetiredAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RetiredAt, command.Now), cancellationToken);

                if (removed != 1)
                {
                    return null;
                }

                return await BumpUnitAsync(inventoryId, referenceId, cancellationToken)
                    ? Recorded(change) with { Alias = term.Term }
                    : null;
            }

            case ReferenceChangeKind.RetireUnit:
            {
                // Guarded on IsReserved as well as on the Unit still being active: the reserved `each`
                // Unit can never be retired, and that must hold in the database rather than only in
                // the planner.
                var retired = await db.Units
                    .Where(u => u.Id == referenceId && u.InventoryId == inventoryId && u.RetiredAt == null && !u.IsReserved)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(u => u.RetiredAt, command.Now)
                            .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                if (retired != 1)
                {
                    return null;
                }

                // Every one of its terms leaves the active namespace with it, which is what returns
                // those names to the Inventory. The rows remain, so the identity does too.
                await db.UnitTerms
                    .Where(t => t.UnitId == referenceId && t.InventoryId == inventoryId && t.RetiredAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RetiredAt, command.Now), cancellationToken);

                return Recorded(change);
            }

            case ReferenceChangeKind.CreateLocation:
            {
                db.Locations.Add(new LocationEntity
                {
                    Id = referenceId,
                    InventoryId = inventoryId,
                    Name = change.Target.Name,
                    NormalizedName = change.Target.NormalizedName,
                    ConcurrencyStamp = Guid.NewGuid(),
                    CreatedAt = command.Now,
                    RetiredAt = null,
                });

                return Recorded(change);
            }

            case ReferenceChangeKind.RenameLocation:
            {
                var newName = change.NewName!;
                var newNormalizedName = change.NewNormalizedName!;

                var renamed = await db.Locations
                    .Where(l => l.Id == referenceId && l.InventoryId == inventoryId && l.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(l => l.Name, newName)
                            .SetProperty(l => l.NormalizedName, newNormalizedName)
                            .SetProperty(l => l.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                return renamed == 1 ? Recorded(change) with { NewName = newName } : null;
            }

            case ReferenceChangeKind.RetireLocation:
            {
                var retired = await db.Locations
                    .Where(l => l.Id == referenceId && l.InventoryId == inventoryId && l.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(l => l.RetiredAt, command.Now)
                            .SetProperty(l => l.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                return retired == 1 ? Recorded(change) : null;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unhandled reference change kind.");
        }
    }

    /// <summary>Moves a Unit's version because one of its terms changed, so a proposal decided against the old term set cannot still land.</summary>
    private async Task<bool> BumpUnitAsync(Guid inventoryId, Guid unitId, CancellationToken cancellationToken) =>
        await db.Units
            .Where(u => u.Id == unitId && u.InventoryId == inventoryId && u.RetiredAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid()), cancellationToken) == 1;

    /// <summary>Takes one reference's exclusive lock and asserts the version this set was decided against, in one statement.</summary>
    private async Task<bool> LockAndVerifyAsync(
        InventoryId inventoryId, ExpectedReferenceVersion expected, CancellationToken cancellationToken)
    {
        var freshStamp = Guid.NewGuid();

        var locked = expected.Kind == ReferenceKind.Unit
            ? await db.Units
                .Where(u => u.Id == expected.ReferenceId
                    && u.InventoryId == inventoryId.Value
                    && u.RetiredAt == null
                    && u.ConcurrencyStamp == expected.ConcurrencyStamp)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ConcurrencyStamp, freshStamp), cancellationToken)
            : await db.Locations
                .Where(l => l.Id == expected.ReferenceId
                    && l.InventoryId == inventoryId.Value
                    && l.RetiredAt == null
                    && l.ConcurrencyStamp == expected.ConcurrencyStamp)
                .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.ConcurrencyStamp, freshStamp), cancellationToken);

        return locked == 1;
    }

    private async Task<bool> TermIsTakenAsync(
        InventoryId inventoryId, ExpectedTermAbsence absence, CancellationToken cancellationToken) =>
        absence.Kind == ReferenceKind.Unit
            ? await db.UnitTerms
                .AsNoTracking()
                .AnyAsync(
                    t => t.InventoryId == inventoryId.Value && t.NormalizedTerm == absence.NormalizedTerm && t.RetiredAt == null,
                    cancellationToken)
            : await db.Locations
                .AsNoTracking()
                .AnyAsync(
                    l => l.InventoryId == inventoryId.Value && l.NormalizedName == absence.NormalizedTerm && l.RetiredAt == null,
                    cancellationToken);

    private async Task<bool> AnyClaimedTermTakenAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken)
    {
        foreach (var absence in command.ExpectedTermAbsences)
        {
            if (await TermIsTakenAsync(command.InventoryId, absence, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any Stock Entry still references what a Retire would withdraw. Administration never rewrites stock; it refuses.</summary>
    private async Task<bool> AnyStockReferencesAsync(
        InventoryId inventoryId, ProposedReferenceState target, CancellationToken cancellationToken)
    {
        var entries = db.StockEntries.AsNoTracking().Where(e => e.InventoryId == inventoryId.Value);

        entries = target.Kind == ReferenceKind.Unit
            ? entries.Where(e => e.UnitId == target.ReferenceId)
            : entries.Where(e => e.LocationId == target.ReferenceId);

        return await entries.AnyAsync(cancellationToken);
    }

    private async Task<ReferenceAdministrationStoreResult> RolledBackConflictAsync(IDbContextTransaction transaction)
    {
        await db.AbandonAsync(transaction);

        return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Conflict, null);
    }

    private async Task<RecordedReferenceChangeSet> ReadRecordedAsync(
        ReferenceOperationEntity header, CancellationToken cancellationToken)
    {
        var rows = await db.ReferenceEffects
            .AsNoTracking()
            .Where(e => e.OperationId == header.OperationId)
            .OrderBy(e => e.Order)
            .ToListAsync(cancellationToken);

        return new RecordedReferenceChangeSet(
            new ReferenceOperationId(header.OperationId),
            header.ProposalId is { } proposalId ? new ProposalId(proposalId) : null,
            rows.Select(ToRecorded).ToList());
    }

    private static RecordedReferenceChange ToRecorded(ReferenceEffectEntity row)
    {
        if (!ReferenceAdministrationFacts.TryParse(row.Kind, out var kind)
            || !Enum.TryParse<ReferenceKind>(row.ReferenceKind, ignoreCase: false, out var referenceKind))
        {
            throw new InvalidOperationException("A recorded reference change carried an unreadable kind.");
        }

        return new RecordedReferenceChange(row.Order, kind, referenceKind, row.ReferenceId, row.Name)
        {
            NewName = row.NewName,
            Alias = row.Alias,
            Aliases = row.AliasesJson is { } json
                ? JsonSerializer.Deserialize<List<string>>(json, AliasOptions) ?? []
                : [],
        };
    }

    private static ReferenceEffectEntity ToEntity(ReferenceOperationId operationId, RecordedReferenceChange change) => new()
    {
        Id = Guid.NewGuid(),
        OperationId = operationId.Value,
        Order = change.Order,
        Kind = ReferenceAdministrationFacts.ToMachineText(change.Kind),
        ReferenceKind = change.ReferenceKind.ToString(),
        ReferenceId = change.ReferenceId,
        Name = change.Name,
        NewName = change.NewName,
        Alias = change.Alias,
        AliasesJson = change.Aliases.Count == 0 ? null : JsonSerializer.Serialize(change.Aliases, AliasOptions),
    };

    /// <summary>
    /// The recorded form of one proposed change. The proposal's own state is exact - it is what the
    /// Participant reviewed - so nothing is re-read to build this.
    /// </summary>
    private static RecordedReferenceChange Recorded(ProposedReferenceChange change) =>
        new(change.Order, change.Kind, change.Target.Kind, change.Target.ReferenceId, change.Target.Name);
}
