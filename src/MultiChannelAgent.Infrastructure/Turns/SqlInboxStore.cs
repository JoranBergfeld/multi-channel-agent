using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL Server-backed durable inbox. Idempotency is additionally enforced by a unique index on
/// (ParticipantId, ChannelConversationId, NativeMessageId) - the full scope a native message id is
/// only ever unique within - which <see cref="AcceptAsync"/> resolves atomically against concurrent
/// duplicate-delivery races: the loser converges on the winner's Turn instead of leaking a raw
/// <see cref="DbUpdateException"/> to callers. Acceptance also assigns the Turn's durable place in
/// its ChannelConversation's order, and <see cref="ClaimPendingAsync"/> selects only conversation
/// heads from it, so per-conversation FIFO is a property of the database queries themselves rather
/// than of any worker's in-memory bookkeeping.
/// </summary>
public sealed class SqlInboxStore(MultiChannelAgentDbContext db) : IInboxStore
{
    /// <summary>
    /// How many times <see cref="AcceptAsync"/> may recompute a conversation sequence that a
    /// concurrent acceptance took first. Bounded so a genuinely broken database can never spin here;
    /// each retry only loses to a real, committed competitor, so contention resolves quickly.
    /// </summary>
    private const int MaxAcceptAttempts = 8;

    public async Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken)
    {
        var entity = await db.InboxEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.ParticipantId == key.ParticipantId.Value
                    && e.ChannelConversationId == key.ChannelConversationId.Value
                    && e.NativeMessageId == key.NativeMessageId,
                cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var entity = await db.InboxEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TurnId == turnId.Value, cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        // Acceptance assigns the Turn's durable place in its ChannelConversation's order. The next
        // sequence is read from the database and confirmed by the unique
        // (ChannelConversationId, ConversationSequence) index rather than trusted optimistically, so
        // two concurrent acceptances in one conversation can never share an order key.
        for (var attempt = 1; ; attempt++)
        {
            var nextSequence = await NextConversationSequenceAsync(turn.ChannelConversationId.Value, cancellationToken);

            db.InboxEntries.Add(new InboxEntryEntity
            {
                TurnId = turn.TurnId.Value,
                NativeMessageId = turn.NativeMessageId,
                ParticipantId = turn.ParticipantId.Value,
                ChannelConversationId = turn.ChannelConversationId.Value,
                ConversationSequence = nextSequence,
                ContentText = turn.ContentText,
                Locale = turn.Locale,
                TraceId = turn.TraceId,
                ReceivedAt = turn.ReceivedAt,
                CreatedAt = turn.ReceivedAt,
                Status = InboxEntryStatus.Pending,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new InboxAcceptResult(turn, WasAlreadyAccepted: false);
            }
            catch (DbUpdateException)
            {
                // Two concurrent deliveries of the same native message key can both observe absence
                // via FindByNativeMessageIdAsync and both reach this insert; the unique index on
                // (ParticipantId, ChannelConversationId, NativeMessageId) then lets exactly one of
                // them commit. Rather than parsing a provider-specific error code to confirm that
                // assumption, clear this failed attempt from the tracker and re-read by the same key:
                // if a row is there now, some other write genuinely committed it first, so this IS
                // that duplicate-delivery race and we converge on the winner.
                db.ChangeTracker.Clear();

                var winner = await FindByNativeMessageIdAsync(turn.NativeMessageKey, cancellationToken);
                if (winner is not null)
                {
                    return new InboxAcceptResult(winner, WasAlreadyAccepted: true);
                }

                // Otherwise this is either the other race the insert can lose - a concurrent
                // acceptance in the same conversation took the sequence first - or a real, unrelated
                // failure. Only the former is retryable, and only while attempts remain; anything
                // else must propagate untouched rather than be disguised as a retryable race.
                var sequenceTaken = await db.InboxEntries
                    .AsNoTracking()
                    .AnyAsync(
                        e => e.ChannelConversationId == turn.ChannelConversationId.Value && e.ConversationSequence == nextSequence,
                        cancellationToken);

                if (!sequenceTaken || attempt >= MaxAcceptAttempts)
                {
                    throw;
                }
            }
        }
    }

    public async Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken)
    {
        // Per-ChannelConversation FIFO is enforced by the claim itself: a conversation only ever
        // offers its head - the earliest-accepted Turn with no still-outstanding predecessor - so no
        // batch limit, extra pass, or lease boundary can ever hand a worker a Turn whose predecessor
        // has not completed. Different conversations are unaffected by each other and progress
        // concurrently. Ordering across conversations only decides which heads fit inside maxCount
        // (never the order within a conversation, which the sequence already fixes), so it uses the
        // conversation sequence with the conversation id as a stable tie-break: both are orderable on
        // every provider, unlike a DateTimeOffset. Safe without extra row locking: callers only claim
        // pending work while holding the "turn-processing" lease, so at most one worker runs this at
        // a time.
        var pending = await db.InboxEntries
            .Where(e => e.Status == InboxEntryStatus.Pending)
            .Where(e => !db.InboxEntries.Any(predecessor =>
                predecessor.ChannelConversationId == e.ChannelConversationId
                && predecessor.Status != InboxEntryStatus.Completed
                && predecessor.ConversationSequence < e.ConversationSequence))
            .OrderBy(e => e.ConversationSequence)
            .ThenBy(e => e.ChannelConversationId)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        return pending.Select(ToDomain).ToList();
    }

    private async Task<long> NextConversationSequenceAsync(string channelConversationId, CancellationToken cancellationToken)
    {
        var highest = await db.InboxEntries
            .AsNoTracking()
            .Where(e => e.ChannelConversationId == channelConversationId)
            .MaxAsync(e => (long?)e.ConversationSequence, cancellationToken);

        return (highest ?? 0L) + 1L;
    }

    private static InboundTurn ToDomain(InboxEntryEntity entity) => new()
    {
        TurnId = new TurnId(entity.TurnId),
        NativeMessageId = entity.NativeMessageId,
        ParticipantId = new ParticipantId(entity.ParticipantId),
        ChannelConversationId = new ChannelConversationId(entity.ChannelConversationId),
        ContentText = entity.ContentText,
        Locale = entity.Locale,
        TraceId = entity.TraceId,
        ReceivedAt = entity.ReceivedAt,
    };
}
