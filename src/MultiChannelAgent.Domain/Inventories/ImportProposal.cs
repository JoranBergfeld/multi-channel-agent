namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Strongly typed identity of one stored import proposal.</summary>
public readonly record struct ImportProposalId(Guid Value)
{
    public static ImportProposalId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Where a stored import proposal ended up. Every status other than <see cref="Pending"/> is
/// terminal, so an import can execute at most once however many times it is confirmed.
/// </summary>
public enum ImportProposalStatus
{
    Pending,

    /// <summary>Executed. Set inside the very transaction that created the Stock Entries.</summary>
    Confirmed,

    /// <summary>The Participant cancelled it.</summary>
    Rejected,

    /// <summary>A newer validation for the same Participant and Inventory replaced it.</summary>
    Superseded,

    /// <summary>Its ten minutes ran out.</summary>
    Expired,

    /// <summary>Execution found the Inventory no longer empty, so nothing was created.</summary>
    Conflicted,
}

/// <summary>
/// The state an import was decided against: an Inventory holding no Stock Entries at all,
/// zero-quantity ones included.
///
/// It is deliberately not a row version, because the thing being asserted is an <em>absence</em>, and
/// an absence is a range rather than a row. Its enforcement is therefore a range lock: the execution
/// transaction runs at serializable isolation and re-asserts the same emptiness inside itself, so a
/// Stock Entry created between preview and confirmation makes the import conflict rather than land
/// into an Inventory that is no longer the one that was reviewed.
/// </summary>
public readonly record struct EmptyStateVersion(int ExpectedStockEntryCount)
{
    public static EmptyStateVersion Empty { get; } = new(0);
}

/// <summary>
/// One exact, immutable, server-stored Initial Import awaiting confirmation, bound to the Participant
/// and Inventory that produced it, the digest of the file it came from, and the empty state it was
/// decided against, carrying the hash of its single-use token and the exact Stock Entries it will
/// create.
///
/// Nothing here can be re-derived at confirmation time, and that is the whole point: the file is
/// discarded, the rows are what commit, and #34 requires confirmation to apply the stored proposal
/// "without reparsing".
///
/// This is a separate aggregate from <see cref="ConfirmationProposal"/> on purpose. That one is
/// bounded to <see cref="ConfirmationProposal.MaxChanges"/> changes, keyed one-pending-per
/// ChannelConversation, and carries expected versions of existing Stock Entries. An import carries up
/// to <see cref="ImportContract.MaxNormalizedEntries"/> entries, belongs to a signed-in browser
/// session rather than a conversation, and by definition touches no existing entry at all. Sharing
/// the type would mean relaxing all three of those rules for everybody.
/// </summary>
public sealed record ImportProposal
{
    /// <summary>The same ten minutes a conversational confirmation gets, taken from it so the two can never drift apart.</summary>
    public const int LifetimeMinutes = ConfirmationProposal.LifetimeMinutes;

    public required ImportProposalId Id { get; init; }

    public required ConfirmationTokenHash TokenHash { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required FileDigest FileDigest { get; init; }

    public required IReadOnlyList<ImportEntry> Entries { get; init; }

    public required EmptyStateVersion EmptyStateVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt => CreatedAt.AddMinutes(LifetimeMinutes);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>The ledger identity this execution is recorded under, fixed the moment the proposal exists.</summary>
    public ImportOperationId ExecutionOperationId => ImportOperationId.DeriveForProposal(Id);

    /// <summary>Every Unit the entries reference, in first-seen order, so execution can hold them all before it writes.</summary>
    public IReadOnlyList<UnitId> ReferencedUnitIds => [.. Entries.Select(entry => entry.UnitId).Distinct()];

    /// <summary>Every Location the entries reference, in first-seen order. Unlocated is the absence of one, so it contributes nothing.</summary>
    public IReadOnlyList<LocationId> ReferencedLocationIds =>
        [.. Entries.Select(entry => entry.LocationId).OfType<LocationId>().Distinct()];

    /// <summary>
    /// Whether this proposal belongs to exactly this Participant and Inventory. One that does not is
    /// treated as if it did not exist, so a token can never be replayed into another session or
    /// another Inventory.
    /// </summary>
    public bool BelongsTo(ParticipantId participantId, InventoryId inventoryId) =>
        ParticipantId == participantId && InventoryId == inventoryId;

    public static ImportProposal Create(
        ConfirmationTokenHash tokenHash,
        ParticipantId participantId,
        InventoryId inventoryId,
        FileDigest fileDigest,
        IReadOnlyList<ImportEntry> entries,
        EmptyStateVersion emptyStateVersion,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            throw new ArgumentException("An import proposal must carry at least one entry.", nameof(entries));
        }

        if (entries.Count > ImportContract.MaxNormalizedEntries)
        {
            throw new ArgumentException(
                $"An import proposal must not carry more than {ImportContract.MaxNormalizedEntries} entries.", nameof(entries));
        }

        return new ImportProposal
        {
            Id = ImportProposalId.NewId(),
            TokenHash = tokenHash,
            ParticipantId = participantId,
            InventoryId = inventoryId,
            FileDigest = fileDigest,
            // Copied rather than referenced: what the Participant reviewed must never drift because
            // the caller went on to mutate the list it handed in.
            Entries = entries.ToList(),
            EmptyStateVersion = emptyStateVersion,
            CreatedAt = createdAt,
        };
    }
}
