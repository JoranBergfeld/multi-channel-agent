using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Strongly typed identity of one stored confirmation proposal.</summary>
public readonly record struct ProposalId(Guid Value)
{
    public static ProposalId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Where a stored proposal ended up. Every status other than <see cref="Pending"/> is terminal:
/// nothing ever returns to pending, so a proposal can be executed at most once no matter how many
/// times it is confirmed.
/// </summary>
public enum ProposalStatus
{
    /// <summary>Awaiting an explicit direct confirmation. At most one of these may exist per Participant and ChannelConversation.</summary>
    Pending,

    /// <summary>Executed. Set inside the very transaction that applied the changes.</summary>
    Confirmed,

    /// <summary>The Participant explicitly rejected it in direct content.</summary>
    Rejected,

    /// <summary>A newer proposal replaced it in the same conversation.</summary>
    Superseded,

    /// <summary>Its ten-minute lifetime ran out.</summary>
    Expired,

    /// <summary>The Participant lost access to the Inventory it was bound to.</summary>
    AccessLost,

    /// <summary>The conversation's Active Inventory changed, so the proposal no longer describes what the Participant is working in.</summary>
    InventorySwitched,

    /// <summary>An interrupted Turn arrived, so nothing said in this conversation may be treated as approval.</summary>
    Interrupted,

    /// <summary>Execution found current state no longer matching the expected versions, so nothing was applied.</summary>
    Conflicted,
}

/// <summary>
/// Exactly what one Stock Entry looks like before and after one proposed change. When
/// <see cref="StockEntryId"/> is null this state describes an entry the change will create, and the
/// name, Unit, and Location are the Equivalent Stock key it will be created at.
///
/// Everything the executor needs is here - identities, the resolved Unit and Location, the display
/// names for reporting, the Note, and both exact amounts - which is precisely why confirmation never
/// re-resolves anything and never recomputes against newer state.
/// </summary>
public sealed record ProposedEntryState(
    StockEntryId? StockEntryId,
    string Name,
    string NormalizedName,
    UnitId UnitId,
    string UnitCanonicalName,
    LocationId? LocationId,
    string? LocationName,
    string? Note,
    Quantity PreviousQuantity,
    Quantity ResultingQuantity,
    bool Retired);

/// <summary>
/// One exactly-decided change within a proposal. Which fields carry meaning is fixed by
/// <see cref="Effect"/>; the executor switches on it and reads only those fields.
/// </summary>
public sealed record ProposedChange
{
    /// <summary>1-based position within the proposal. Execution follows it, so effects apply in the order the Participant reviewed.</summary>
    public required int Order { get; init; }

    public required StockMutationKind Kind { get; init; }

    public required StockChangeEffectKind Effect { get; init; }

    public required ProposedEntryState Source { get; init; }

    /// <summary>Where stock lands, when the effect has a destination distinct from the source's own row.</summary>
    public ProposedEntryState? Destination { get; init; }

    public Quantity TransferredQuantity { get; init; } = Quantity.Zero;

    /// <summary>The exact new display name; set only for <see cref="StockChangeEffectKind.Renamed"/> and <see cref="StockChangeEffectKind.RenameMerged"/>.</summary>
    public string? NewName { get; init; }

    /// <summary>The normalized form of <see cref="NewName"/>, computed once while planning so the executor never re-normalizes.</summary>
    public string? NewNormalizedName { get; init; }

    public bool RetiresSource => StockAuditFacts.RetiresSource(Effect);

    /// <summary>
    /// The Stock Entry that still exists once this change is applied. When a merge retires the
    /// source, that is the destination; otherwise it is the entry itself. Null only while the
    /// surviving entry is still to be created.
    /// </summary>
    public StockEntryId? SurvivingStockEntryId => RetiresSource ? Destination?.StockEntryId : Source.StockEntryId;

    /// <summary>The Stock Entry whose identity this change ends, or null when it ends none.</summary>
    public StockEntryId? RetiredStockEntryId => RetiresSource ? Source.StockEntryId : null;
}

/// <summary>
/// The version one existing Stock Entry carried when the proposal was made. Execution refuses unless
/// the row still carries it, so a proposal decided against a state nobody holds any more can never
/// land. The stamp - not the Quantity - is the version: an unrelated write that happened to restore
/// the same amount still changes the stamp, and must still invalidate the proposal.
/// </summary>
public sealed record ExpectedEntryVersion(StockEntryId StockEntryId, Guid ConcurrencyStamp);

/// <summary>
/// The Equivalent Stock key a proposal expects to still be empty, because it intends to create it.
/// Enforced at execution by the same filtered unique indexes that define Equivalent Stock, so a
/// competing writer that created it first turns into a conflict rather than a duplicate.
/// </summary>
public sealed record ExpectedEquivalentStockAbsence(string NormalizedName, UnitId UnitId, LocationId? LocationId);

/// <summary>
/// Which kind of work one stored proposal describes. The two payloads are disjoint: a
/// <see cref="Stock"/> proposal carries only stock changes, and a
/// <see cref="ReferenceAdministration"/> proposal carries only reference changes. They share
/// everything else - the single pending slot per Participant and ChannelConversation, the ten-minute
/// single-use token, the binding predicate, and the whole status state machine - which is exactly
/// what "one pending proposal across stock, import, and administration" means.
/// </summary>
public enum ProposalKind
{
    Stock,
    ReferenceAdministration,
}

/// <summary>
/// The exact reference one administration change acts on, as it stood when the proposal was made.
/// For a change that <em>creates</em> a reference, <see cref="ReferenceId"/> is the identity the
/// execution will mint - decided here, at proposal time - so confirming creates exactly the identity
/// that was reviewed rather than a fresh one.
/// </summary>
public sealed record ProposedReferenceState(
    ReferenceKind Kind, Guid ReferenceId, string Name, string NormalizedName, bool Reserved);

/// <summary>
/// One exactly-decided administration change. Which fields carry meaning is fixed by
/// <see cref="Kind"/>; the executor switches on it and reads only those:
///
/// <list type="bullet">
/// <item><c>CreateUnit</c>: <see cref="Terms"/> (canonical first, then aliases).</item>
/// <item><c>RenameUnit</c> / <c>RenameLocation</c>: <see cref="NewName"/> and <see cref="NewNormalizedName"/>.</item>
/// <item><c>AddUnitAlias</c> / <c>RemoveUnitAlias</c>: <see cref="Term"/>.</item>
/// <item><c>CreateLocation</c>: nothing beyond <see cref="Target"/>, which already carries the name.</item>
/// <item><c>RetireUnit</c> / <c>RetireLocation</c>: nothing beyond <see cref="Target"/>.</item>
/// </list>
/// </summary>
public sealed record ProposedReferenceChange
{
    /// <summary>1-based position within the proposal. Execution follows it, so effects apply in the order the Participant reviewed.</summary>
    public required int Order { get; init; }

    public required ReferenceChangeKind Kind { get; init; }

    public required ProposedReferenceState Target { get; init; }

    /// <summary>The exact new display name; set only for <c>RenameUnit</c> and <c>RenameLocation</c>.</summary>
    public string? NewName { get; init; }

    /// <summary>The normalized form of <see cref="NewName"/>, computed once while planning so the executor never re-normalizes.</summary>
    public string? NewNormalizedName { get; init; }

    /// <summary>The single term an alias add establishes or an alias removal ends.</summary>
    public UnitTerm? Term { get; init; }

    /// <summary>The full ordered term set a Unit creation establishes, canonical first.</summary>
    public IReadOnlyList<UnitTerm> Terms { get; init; } = [];

    /// <summary>True when this change brings a reference into existence, so there is no version to pin and nothing to lock.</summary>
    public bool CreatesReference =>
        Kind is ReferenceChangeKind.CreateUnit or ReferenceChangeKind.CreateLocation;

    /// <summary>True when this change withdraws a reference, which is what makes the whole proposal Owner-only and confirmable.</summary>
    public bool RetiresReference => ReferenceAdministrationFacts.RequiresConfirmation(Kind);
}

/// <summary>
/// The version one existing Unit or Location carried when the proposal was made. Execution refuses
/// unless the row still carries it, so a proposal decided against a state nobody holds any more can
/// never land - exactly as <see cref="ExpectedEntryVersion"/> does for a Stock Entry.
/// </summary>
public sealed record ExpectedReferenceVersion(ReferenceKind Kind, Guid ReferenceId, Guid ConcurrencyStamp);

/// <summary>
/// A normalized term - a Unit term or a Location name - the proposal expects to still be free,
/// because it intends to claim it. Enforced at execution by the same filtered unique indexes that
/// define the namespace, so a competing writer that claimed it first turns into a typed conflict
/// rather than a duplicate.
/// </summary>
public sealed record ExpectedTermAbsence(ReferenceKind Kind, string NormalizedTerm);

/// <summary>
/// One exact, immutable, server-stored set of changes awaiting explicit confirmation, bound to the
/// Participant, ChannelConversation, Inventory, and Turn that produced it, carrying the expected
/// versions it was decided against and the hash of its single-use token.
///
/// Nothing here can be re-derived at confirmation time: the whole point is that what the Participant
/// reviewed is exactly what commits.
/// </summary>
public sealed record ConfirmationProposal
{
    /// <summary>How long a confirmation stays valid. Ten minutes, per the specification.</summary>
    public const int LifetimeMinutes = 10;

    /// <summary>How many changes one proposal may carry - the bound on what a Participant can actually review in one answer.</summary>
    public const int MaxChanges = 25;

    public required ProposalId Id { get; init; }

    public required ConfirmationTokenHash TokenHash { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required string ChannelConversationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Turn that produced this proposal, recorded so a proposal is traceable to the request it came from.</summary>
    public required TurnId ProposedInTurnId { get; init; }

    public required IReadOnlyList<ProposedChange> Changes { get; init; }

    public required IReadOnlyList<ExpectedEntryVersion> ExpectedVersions { get; init; }

    public required IReadOnlyList<ExpectedEquivalentStockAbsence> ExpectedAbsences { get; init; }

    /// <summary>Which payload this proposal carries. Set by the factory, never by a caller.</summary>
    public required ProposalKind Kind { get; init; }

    /// <summary>The exact administration changes; empty for a stock proposal.</summary>
    public IReadOnlyList<ProposedReferenceChange> ReferenceChanges { get; init; } = [];

    /// <summary>The versions every existing Unit and Location this proposal touches carried when it was made; empty for a stock proposal.</summary>
    public IReadOnlyList<ExpectedReferenceVersion> ExpectedReferenceVersions { get; init; } = [];

    /// <summary>The normalized terms this proposal expects to still be free; empty for a stock proposal.</summary>
    public IReadOnlyList<ExpectedTermAbsence> ExpectedTermAbsences { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The ledger identity this proposal's execution is recorded under. Derived from the proposal
    /// itself rather than from whichever Turn confirms it, so the identity is fixed the moment the
    /// proposal exists and a re-driven confirmation cannot mint a second one.
    /// </summary>
    public StockOperationId ExecutionOperationId => StockOperationId.DeriveForProposal(Id);

    /// <summary>
    /// The reference ledger identity this proposal's execution is recorded under. Like
    /// <see cref="ExecutionOperationId"/> it is derived from the proposal rather than from whichever
    /// Turn confirms it, and its hash material is shaped so it can never equal a stock identity.
    /// </summary>
    public ReferenceOperationId ReferenceExecutionOperationId => ReferenceOperationId.DeriveForProposal(Id);

    /// <summary>
    /// The least Membership role a Participant must still hold to execute this proposal. Only a
    /// Retire raises it, so every stock proposal reports Editor and the shipped confirmation path is
    /// unchanged by this ticket.
    /// </summary>
    public MembershipRole RequiredRole => ReferenceChanges.Any(change => change.RetiresReference)
        ? MembershipRole.Owner
        : MembershipRole.Editor;

    /// <summary>
    /// Every Unit this proposal depends on. Retiring one of them must settle this proposal, because
    /// what it describes could no longer be applied - and that is true of a stock proposal that would
    /// create stock at a Unit just as much as of an administration proposal that would rename one.
    /// </summary>
    public IReadOnlyList<UnitId> ReferencedUnitIds =>
    [
        .. StockReferenceDependencies.UnitsOf(Changes, ExpectedAbsences)
            .Concat(ReferenceChanges
                .Where(change => change.Target.Kind == ReferenceKind.Unit)
                .Select(change => new UnitId(change.Target.ReferenceId)))
            .Distinct(),
    ];

    /// <summary>Every Location this proposal depends on. See <see cref="ReferencedUnitIds"/>.</summary>
    public IReadOnlyList<LocationId> ReferencedLocationIds =>
    [
        .. StockReferenceDependencies.LocationsOf(Changes, ExpectedAbsences)
            .Concat(ReferenceChanges
                .Where(change => change.Target.Kind == ReferenceKind.Location)
                .Select(change => new LocationId(change.Target.ReferenceId)))
            .Distinct(),
    ];

    public DateTimeOffset ExpiresAt => CreatedAt.AddMinutes(LifetimeMinutes);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Whether this proposal belongs to exactly this trusted context. A proposal that does not is
    /// treated as if it did not exist, so a token can never be replayed into another Participant's
    /// conversation or another Inventory.
    /// </summary>
    public bool BelongsTo(ParticipantId participantId, string channelConversationId, InventoryId inventoryId) =>
        ParticipantId == participantId
        && string.Equals(ChannelConversationId, channelConversationId, StringComparison.Ordinal)
        && InventoryId == inventoryId;

    public static ConfirmationProposal Create(
        ConfirmationTokenHash tokenHash,
        ParticipantId participantId,
        string? channelConversationId,
        InventoryId inventoryId,
        TurnId proposedInTurnId,
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> expectedVersions,
        IReadOnlyList<ExpectedEquivalentStockAbsence> expectedAbsences,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelConversationId);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(expectedVersions);
        ArgumentNullException.ThrowIfNull(expectedAbsences);

        if (changes.Count == 0)
        {
            throw new ArgumentException("A proposal must carry at least one change.", nameof(changes));
        }

        if (changes.Count > MaxChanges)
        {
            throw new ArgumentException($"A proposal must not carry more than {MaxChanges} changes.", nameof(changes));
        }

        if (changes.Select(change => change.Order).Distinct().Count() != changes.Count)
        {
            throw new ArgumentException("Change order must be unique within a proposal.", nameof(changes));
        }

        // Every existing row this proposal will write to must be pinned to the version it was decided
        // against. A missing one is not a small omission: it is the difference between "apply what was
        // reviewed" and "overwrite whatever is there now".
        var versioned = expectedVersions.Select(version => version.StockEntryId).ToHashSet();
        foreach (var change in changes)
        {
            RequireVersioned(change.Source, versioned, nameof(expectedVersions));
            if (change.Destination is { } destination)
            {
                RequireVersioned(destination, versioned, nameof(expectedVersions));
            }
        }

        return new ConfirmationProposal
        {
            Id = ProposalId.NewId(),
            TokenHash = tokenHash,
            ParticipantId = participantId,
            ChannelConversationId = channelConversationId.Trim(),
            InventoryId = inventoryId,
            ProposedInTurnId = proposedInTurnId,
            Kind = ProposalKind.Stock,
            Changes = changes.OrderBy(change => change.Order).ToList(),
            ExpectedVersions = expectedVersions.ToList(),
            ExpectedAbsences = expectedAbsences.ToList(),
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// Creates a reference administration proposal. It enforces the exact parallel of every rule
    /// <see cref="Create"/> enforces for stock - non-empty, bounded by <see cref="MaxChanges"/>,
    /// unique order, and an expected version for every <em>existing</em> reference it touches - over
    /// its own inputs, and passes no stock at all, so no stock invariant is relaxed or bypassed.
    /// </summary>
    public static ConfirmationProposal CreateForReferences(
        ConfirmationTokenHash tokenHash,
        ParticipantId participantId,
        string? channelConversationId,
        InventoryId inventoryId,
        TurnId proposedInTurnId,
        IReadOnlyList<ProposedReferenceChange> referenceChanges,
        IReadOnlyList<ExpectedReferenceVersion> expectedReferenceVersions,
        IReadOnlyList<ExpectedTermAbsence> expectedTermAbsences,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelConversationId);
        ArgumentNullException.ThrowIfNull(referenceChanges);
        ArgumentNullException.ThrowIfNull(expectedReferenceVersions);
        ArgumentNullException.ThrowIfNull(expectedTermAbsences);

        if (referenceChanges.Count == 0)
        {
            throw new ArgumentException("A proposal must carry at least one change.", nameof(referenceChanges));
        }

        if (referenceChanges.Count > MaxChanges)
        {
            throw new ArgumentException($"A proposal must not carry more than {MaxChanges} changes.", nameof(referenceChanges));
        }

        if (referenceChanges.Select(change => change.Order).Distinct().Count() != referenceChanges.Count)
        {
            throw new ArgumentException("Change order must be unique within a proposal.", nameof(referenceChanges));
        }

        // Every reference that already exists must be pinned to the version this proposal was decided
        // against. A reference this proposal creates has no version to pin: its safety comes from an
        // expected term absence and the filtered uniqueness index.
        var versioned = expectedReferenceVersions.Select(version => (version.Kind, version.ReferenceId)).ToHashSet();
        foreach (var change in referenceChanges.Where(change => !change.CreatesReference))
        {
            if (!versioned.Contains((change.Target.Kind, change.Target.ReferenceId)))
            {
                throw new ArgumentException(
                    "Every existing Unit or Location a proposal touches must carry an expected version.",
                    nameof(expectedReferenceVersions));
            }
        }

        return new ConfirmationProposal
        {
            Id = ProposalId.NewId(),
            TokenHash = tokenHash,
            ParticipantId = participantId,
            ChannelConversationId = channelConversationId.Trim(),
            InventoryId = inventoryId,
            ProposedInTurnId = proposedInTurnId,
            Kind = ProposalKind.ReferenceAdministration,
            Changes = [],
            ExpectedVersions = [],
            ExpectedAbsences = [],
            ReferenceChanges = referenceChanges.OrderBy(change => change.Order).ToList(),
            ExpectedReferenceVersions = expectedReferenceVersions.ToList(),
            ExpectedTermAbsences = expectedTermAbsences.ToList(),
            CreatedAt = createdAt,
        };
    }

    private static void RequireVersioned(ProposedEntryState state, HashSet<StockEntryId> versioned, string parameterName)
    {
        // A state with no identity is one this proposal creates; its safety comes from an expected
        // absence and the Equivalent Stock uniqueness index, not from a version.
        if (state.StockEntryId is { } id && !versioned.Contains(id))
        {
            throw new ArgumentException("Every existing Stock Entry a proposal touches must carry an expected version.", parameterName);
        }
    }
}
