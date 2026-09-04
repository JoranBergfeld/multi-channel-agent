using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IReferenceAdministrationStore"/> for Application-layer unit tests. It
/// honours exactly the contract the SQL store must: replay by identity and by Turn, single-use
/// proposal consumption, expected versions, expected term absences, the authoritative retire
/// re-check, and settling every other pending proposal that referenced a retired identity.
/// </summary>
public sealed class InMemoryReferenceAdministrationStore(InMemoryConfirmationProposalStore? proposalStore = null)
    : IReferenceAdministrationStore
{
    private readonly Dictionary<(InventoryId, ReferenceOperationId), RecordedReferenceChangeSet> _byOperation = [];
    private readonly Dictionary<(InventoryId, TurnId), RecordedReferenceChangeSet> _byTurn = [];
    private readonly Dictionary<(ReferenceKind, Guid), Guid> _versions = [];
    private readonly HashSet<(ReferenceKind, string)> _takenTerms = [];
    private readonly Dictionary<(ReferenceKind, Guid), int> _stockReferences = [];

    /// <summary>Every audit fact this store appended, in order - the same minimal facts the SQL store writes.</summary>
    public List<AuditFact> Audits { get; } = [];

    /// <summary>Forces the next apply to see a different version for this reference, exactly as a competing writer would.</summary>
    public void SetVersion(ReferenceKind kind, Guid referenceId, Guid concurrencyStamp) =>
        _versions[(kind, referenceId)] = concurrencyStamp;

    /// <summary>Marks a normalized term as taken, so an expected absence for it becomes a conflict.</summary>
    public void TakeTerm(ReferenceKind kind, string normalizedTerm) => _takenTerms.Add((kind, normalizedTerm));

    /// <summary>Sets how many Stock Entries reference something at execution time, which is what a Retire is re-checked against.</summary>
    public void SetStockReferences(ReferenceKind kind, Guid referenceId, int count) =>
        _stockReferences[(kind, referenceId)] = count;

    public Task<RecordedReferenceChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, ReferenceOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(_byOperation.GetValueOrDefault((inventoryId, operationId)));

    public Task<RecordedReferenceChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken) =>
        Task.FromResult(_byTurn.GetValueOrDefault((inventoryId, turnId)));

    public async Task<ReferenceAdministrationStoreResult> ApplyAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_byOperation.TryGetValue((command.InventoryId, command.OperationId), out var already))
        {
            return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, already);
        }

        // The SQL store does everything below in one transaction, so a conflict discovered after the
        // proposal was consumed still leaves it exactly as it was. This double has no transaction, so
        // it refuses before consuming rather than rolling back afterwards.
        foreach (var expected in command.ExpectedVersions)
        {
            if (_versions.TryGetValue((expected.Kind, expected.ReferenceId), out var current)
                && current != expected.ConcurrencyStamp)
            {
                return Conflict();
            }
        }

        foreach (var absence in command.ExpectedTermAbsences)
        {
            if (_takenTerms.Contains((absence.Kind, absence.NormalizedTerm)))
            {
                return Conflict();
            }
        }

        // The authoritative retire check: current at execution, not at proposal.
        foreach (var change in command.Changes.Where(change => change.RetiresReference))
        {
            if (_stockReferences.GetValueOrDefault((change.Target.Kind, change.Target.ReferenceId)) > 0)
            {
                return Conflict();
            }
        }

        if (command.ConsumesProposalId is { } proposalId
            && proposalStore is not null
            && !await proposalStore.SettleAsync(proposalId, ProposalStatus.Confirmed, command.Now, cancellationToken))
        {
            return Conflict();
        }

        var recorded = new List<RecordedReferenceChange>(command.Changes.Count);
        foreach (var change in command.Changes.OrderBy(change => change.Order))
        {
            recorded.Add(new RecordedReferenceChange(
                change.Order,
                change.Kind,
                change.Target.Kind,
                change.Target.ReferenceId,
                change.Target.Name)
            {
                NewName = change.NewName,
                Alias = change.Term?.Term,
                Aliases = [.. change.Terms.Where(term => !term.IsCanonical).Select(term => term.Term)],
            });

            Audits.Add(AuditFact.Create(
                ReferenceAdministrationFacts.EventTypeFor(change.Kind),
                AuditActorKind.Participant,
                command.ActorId.ToString(),
                command.InventoryId,
                subjectParticipantId: null,
                ReferenceAdministrationFacts.OutcomeCodeFor(change.Kind),
                command.Now));

            // Applying a change moves the reference's version, exactly as a fresh stamp does in SQL.
            _versions[(change.Target.Kind, change.Target.ReferenceId)] = Guid.NewGuid();

            if (change.RetiresReference && proposalStore is not null)
            {
                await proposalStore.InvalidateReferencingAsync(
                    command.InventoryId, change.Target.Kind, change.Target.ReferenceId, command.Now, cancellationToken);
            }
        }

        var result = new RecordedReferenceChangeSet(command.OperationId, command.ConsumesProposalId, recorded);
        _byOperation[(command.InventoryId, command.OperationId)] = result;
        _byTurn[(command.InventoryId, command.ConfirmedByTurnId)] = result;

        return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Applied, result);
    }

    private static ReferenceAdministrationStoreResult Conflict() =>
        new(ReferenceAdministrationStoreOutcome.Conflict, null);
}
