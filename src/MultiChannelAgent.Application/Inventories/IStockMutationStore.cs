using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How a mutation command was settled by the store.</summary>
public enum StockMutationStoreOutcome
{
    /// <summary>The mutation was applied, and its state change, audit fact, and ledger row were committed together.</summary>
    Applied,

    /// <summary>This exact operation identity had already been applied; the recorded effect is returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>The target moved under the caller's feet, so nothing was applied. The caller must ask again against current state.</summary>
    StateChanged,
}

/// <summary>
/// The durable semantic facts of one applied mutation - exactly what a retry of the same operation
/// identity must be able to re-report without touching Inventory state again. Deliberately semantic:
/// no row versions, concurrency stamps, audit identities, or SQL detail ever appear here.
/// </summary>
public sealed record RecordedStockMutation(
    StockEntryId StockEntryId,
    string Name,
    string UnitCanonicalName,
    string? LocationName,
    string? Note,
    Quantity PreviousQuantity,
    Quantity ResultingQuantity,
    bool CreatedEntry,
    bool NotePreserved);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="StockMutationStoreOutcome.StateChanged"/>.</summary>
public sealed record StockMutationStoreResult(StockMutationStoreOutcome Outcome, RecordedStockMutation? Recorded);

/// <summary>
/// One fully decided mutation, ready to apply. Everything ambiguous has already been resolved by
/// <see cref="StockMutationService"/>: the target (or the exact Equivalent Stock key to create), the
/// resulting Quantity, and the Quantity the caller observed while deciding.
/// </summary>
public sealed record StockMutationCommand
{
    /// <summary>The derived, retry-stable identity of this operation. The store's idempotency ledger is keyed by it.</summary>
    public required StockOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Editor-or-better Membership authorized this mutation; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    public required StockMutationKind Kind { get; init; }

    /// <summary>The requested amount, recorded for completeness; the store writes <see cref="ResultingQuantity"/>.</summary>
    public required Quantity Amount { get; init; }

    public required Quantity ResultingQuantity { get; init; }

    /// <summary>The existing Stock Entry to change; null exactly when this command creates one.</summary>
    public StockEntryId? StockEntryId { get; init; }

    /// <summary>
    /// The Quantity the caller read while planning. The store refuses rather than applies when the row
    /// no longer carries it, so a plan decided against a state nobody holds any more never lands.
    /// Null exactly when <see cref="StockEntryId"/> is.
    /// </summary>
    public Quantity? ExpectedQuantity { get; init; }

    /// <summary>The display name for a created Stock Entry; null when changing an existing one.</summary>
    public string? NewEntryName { get; init; }

    /// <summary>The resolved Unit for a created Stock Entry; null when changing an existing one.</summary>
    public UnitId? NewEntryUnitId { get; init; }

    /// <summary>The resolved Location for a created Stock Entry; null means unlocated (or that an existing entry is being changed).</summary>
    public LocationId? NewEntryLocationId { get; init; }

    /// <summary>The Note for a created Stock Entry. A quantity mutation never rewrites an existing entry's Note, so this is only ever set on the create path.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// True when the request proposed a Note that was deliberately not applied because the target
    /// Stock Entry already existed. Recorded so the answer can say the existing Note was kept rather
    /// than dropping the proposal silently.
    /// </summary>
    public required bool NotePreserved { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The single write seam for a stock mutation. One call must, in one transaction: refuse if this
/// operation identity was already applied (returning what it did), refuse if the target changed since
/// the caller planned, and otherwise change the Stock Entry, append the minimal semantic audit fact,
/// and record the operation ledger row together. Partial application is never acceptable: a caller
/// that sees <see cref="StockMutationStoreOutcome.StateChanged"/> must be able to rely on nothing
/// having happened at all.
/// </summary>
public interface IStockMutationStore
{
    /// <summary>
    /// The effect this operation identity already had in this Inventory, or null when it has never
    /// been applied there.
    ///
    /// This exists so a caller can answer a replay from the ledger <em>before</em> it re-plans against
    /// current state. A mutation commits in its own transaction and the Turn's Outcome commits in a
    /// second one, so a Turn replayed after a crash between them meets Stock its own first attempt
    /// already changed: re-planning first would see the amount it removed as missing and report an
    /// underflow, telling the Participant nothing happened when in fact everything did.
    ///
    /// The lookup is scoped to <paramref name="inventoryId"/> - the Inventory from trusted context,
    /// never one an operation identity could name for itself - so a recorded operation can never be
    /// re-reported into, or disclosed through, a different Inventory.
    /// </summary>
    Task<RecordedStockMutation?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken);

    Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken);
}
