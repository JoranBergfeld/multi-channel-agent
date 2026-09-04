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

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The ledger identity this proposal's execution is recorded under. Derived from the proposal
    /// itself rather than from whichever Turn confirms it, so the identity is fixed the moment the
    /// proposal exists and a re-driven confirmation cannot mint a second one.
    /// </summary>
    public StockOperationId ExecutionOperationId => StockOperationId.DeriveForProposal(Id);

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
            Changes = changes.OrderBy(change => change.Order).ToList(),
            ExpectedVersions = expectedVersions.ToList(),
            ExpectedAbsences = expectedAbsences.ToList(),
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
