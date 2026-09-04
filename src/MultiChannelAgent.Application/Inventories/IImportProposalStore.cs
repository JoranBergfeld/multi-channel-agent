using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The one pending Initial Import per Participant and Inventory, and the raw file it came from.
///
/// The raw bytes live here for the proposal's ten minutes and nowhere else, and they are kept for
/// one reason: so that "the raw CSV is discarded" is a durable fact this system can be held to
/// rather than a claim about process memory. Every path out of Pending deletes them, so after
/// confirmation, rejection, supersession, or expiry only the digest and the minimal audit fact
/// remain.
///
/// Nothing serves them back to a Participant. No route rebuilds a preview from them: a preview is
/// the stored proposal, and the plaintext confirmation token it was issued with exists only in the
/// validate response, so a reloaded page cannot reconstruct one and uploads again instead.
/// </summary>
public interface IImportProposalStore
{
    /// <summary>
    /// Stores <paramref name="proposal"/> with its raw upload, superseding any proposal this
    /// Participant already had pending for this Inventory - and discarding that one's upload - in the
    /// same transaction. Returns whether something was superseded, so the caller can say so.
    /// </summary>
    Task<bool> StoreAsync(
        ImportProposal proposal, ReadOnlyMemory<byte> rawContent, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The one pending proposal for this Participant and Inventory, or null. Never returns a settled one.</summary>
    Task<ImportProposal?> FindPendingAsync(
        ParticipantId participantId, InventoryId inventoryId, CancellationToken cancellationToken);

    /// <summary>
    /// The raw bytes of a pending proposal, or null once they have been discarded. This is the
    /// observation seam for that lifecycle - how a test or an operator establishes that the file
    /// exists while the proposal is pending and is gone once it settles - and no shipped workflow
    /// calls it.
    /// </summary>
    Task<ReadOnlyMemory<byte>?> FindRawContentAsync(ImportProposalId proposalId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a proposal out of Pending, guarded, and discards its raw upload in the same transaction.
    /// Returns false when it was not Pending any more, which is how two callers racing to settle one
    /// proposal are resolved without either of them guessing.
    /// </summary>
    Task<bool> SettleAsync(
        ImportProposalId proposalId, ImportProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The status of a proposal, or null when there is no such row. For tests and diagnostics only.</summary>
    Task<ImportProposalStatus?> FindStatusAsync(ImportProposalId proposalId, CancellationToken cancellationToken);

    /// <summary>Settles every pending proposal whose ten minutes ran out before <paramref name="now"/>, bounded, discarding their uploads.</summary>
    Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken);

    /// <summary>Deletes settled proposals older than <paramref name="cutoff"/>, bounded.</summary>
    Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken);
}
