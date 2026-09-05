using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>One authorized Inventory and the version its projections were last invalidated at.</summary>
public sealed record AuthorizedInventoryVersion(Guid InventoryId, long Version);

/// <summary>
/// The complete current invalidation picture for one Participant: every Inventory they may see right
/// now, with its current version.
///
/// This is deliberately a whole-state read rather than a change feed. Invalidation is idempotent -
/// a client needs to know what version each Inventory is at, never the history of how it got there -
/// so sending the complete picture makes reconnecting a resynchronization rather than a replay. That
/// is what lets the stream over this reader carry no cursor at all without losing anything (see the
/// stream's own documentation), and it removes three failure modes a cursor would have: a retention
/// sweep aging out unseen entries, an identity gap where a later change becomes visible before an
/// earlier one commits, and Membership granted or revoked while the client was disconnected.
/// Authorization is re-read every time for that last reason.
/// </summary>
public sealed class InventoryInvalidationReader(IInventoryStore inventoryStore, IInventoryVersionStore versionStore)
{
    /// <summary>
    /// Reads every Inventory <paramref name="participantId"/> is currently authorized for, each paired
    /// with its current version, in a stable order so two reads of unchanged state are identical.
    /// </summary>
    /// <param name="participantId">The Participant whose authorized set and versions are requested.</param>
    /// <param name="cancellationToken">Cancels the durable reads behind the picture.</param>
    /// <returns>
    /// The Participant's authorized Inventories ordered by identity. An Inventory the version store
    /// holds no row for reports as version zero, because "never changed" and "changed zero times" are
    /// the same thing.
    /// </returns>
    public async Task<IReadOnlyList<AuthorizedInventoryVersion>> ReadAsync(
        ParticipantId participantId, CancellationToken cancellationToken)
    {
        var authorized = await inventoryStore.ListAuthorizedAsync(participantId, cancellationToken);
        if (authorized.Count == 0)
        {
            return [];
        }

        var ids = authorized.Select(record => record.InventoryId.Value).ToList();
        var versions = await versionStore.ReadAsync(ids, cancellationToken);

        return ids
            .Select(id => new AuthorizedInventoryVersion(id, versions.TryGetValue(id, out var version) ? version : 0L))
            .OrderBy(version => version.InventoryId)
            .ToList();
    }
}
