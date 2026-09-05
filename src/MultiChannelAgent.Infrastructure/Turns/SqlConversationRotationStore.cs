using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL-backed <see cref="IConversationRotationStore"/>. The generation increment is guarded by the
/// generation it was read at, so two resets racing the same conversation can never both write the
/// same next generation: the loser sees zero rows updated, re-reads, and rotates from where the
/// winner left it - the same bounded converge-on-the-winner shape
/// <see cref="SqlInboxStore.AcceptAsync"/> uses for its own guarded write. The pending confirmation
/// is settled in the same transaction, so no window exists in which history has rotated but a stale
/// "confirm" would still fire.
///
/// Both writes are set-based statements over exactly one (Participant, ChannelConversation): the
/// Active Inventory selection, Membership, every other conversation's proposals, and the Import
/// proposals keyed by (Participant, Inventory) are not reachable from either predicate.
/// </summary>
public sealed class SqlConversationRotationStore(
    MultiChannelAgentDbContext db, IFoundryConversationBindingStore bindingStore) : IConversationRotationStore
{
    /// <summary>
    /// How many times a rotation may lose the guarded update to a concurrent reset before giving up.
    /// Bounded so a genuinely broken database can never spin here; each retry only ever loses to a
    /// real, committed competitor, so contention resolves immediately.
    /// </summary>
    private const int MaxRotateAttempts = 8;

    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);
    private static readonly string ResetStatus = nameof(ProposalStatus.ConversationReset);

    public async Task<ConversationRotationResult> RotateAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Establishes the binding when this conversation has never been used, so "New
            // conversation" is meaningful even as a Participant's very first action - which is why
            // that first reset lands on generation 2 rather than 1.
            var current = await bindingStore.GetOrCreateAsync(participantId, channelConversationId, now, cancellationToken);
            var nextConversationId = Guid.NewGuid();
            var nextGeneration = current.Generation + 1;

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                try
                {
                    var rotated = await db.FoundryConversationBindings
                        .Where(e => e.ParticipantId == participantId.Value
                            && e.ChannelConversationId == channelConversationId.Value
                            && e.Generation == current.Generation)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(e => e.FoundryConversationId, nextConversationId)
                                .SetProperty(e => e.Generation, nextGeneration)
                                .SetProperty(e => e.CreatedAt, now),
                            cancellationToken);

                    if (rotated != 0)
                    {
                        var cleared = await db.ConfirmationProposals
                            .Where(p => p.ParticipantId == participantId.Value
                                && p.ChannelConversationId == channelConversationId.Value
                                && p.Status == PendingStatus)
                            .ExecuteUpdateAsync(
                                setters => setters
                                    .SetProperty(p => p.Status, ResetStatus)
                                    .SetProperty(p => p.SettledAt, now)
                                    .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
                                cancellationToken);

                        await transaction.CommitAsync(cancellationToken);

                        // ExecuteUpdate deliberately bypasses the ChangeTracker, so a binding this
                        // very scope materialized moments ago - GetOrCreateAsync tracks the row it
                        // inserts - is now a stale snapshot carrying the generation this rotation
                        // just replaced. One DbContext serves a whole batch of work, so leaving it
                        // there is exactly how a later save in the same scope would write the old
                        // generation back over the new one.
                        db.ChangeTracker.Clear();

                        return new ConversationRotationResult(
                            current with
                            {
                                FoundryConversationId = new FoundryConversationId(nextConversationId),
                                Generation = nextGeneration,
                                CreatedAt = now,
                            },
                            ClearedPendingConfirmation: cleared > 0);
                    }

                    // A concurrent reset advanced the generation between the read and this update.
                    // Abandon the attempt and start over from whatever that reset left behind, so two
                    // resets advance two generations rather than one silently overwriting the other.
                    await db.AbandonAsync(transaction);
                }
                catch
                {
                    // A fault or a cancellation. Abandoning is what stops a failed rotation leaving
                    // this shared scope holding a rolled-back phantom binding for the next unrelated
                    // save in the same scope to resolve reads against.
                    await db.AbandonAsync(transaction);
                    throw;
                }
            }

            if (attempt >= MaxRotateAttempts)
            {
                throw new InvalidOperationException(
                    $"Could not rotate the conversation for Participant {participantId} after {MaxRotateAttempts} attempts.");
            }
        }
    }
}
