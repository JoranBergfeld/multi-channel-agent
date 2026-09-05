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

        return entity is null ? null : await ToDomainAsync(entity, cancellationToken);
    }

    public async Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var entity = await db.InboxEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TurnId == turnId.Value, cancellationToken);

        return entity is null ? null : await ToDomainAsync(entity, cancellationToken);
    }

    public async Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var captured = await db.InboxEntries
            .AsNoTracking()
            .Where(e => e.TurnId == turnId.Value)
            .Select(e => new { e.FoundryConversationId, e.FoundryConversationGeneration })
            .FirstOrDefaultAsync(cancellationToken);

        return captured is { FoundryConversationId: { } conversationId, FoundryConversationGeneration: { } generation }
            ? new CapturedConversationBinding(new FoundryConversationId(conversationId), generation)
            : null;
    }

    public async Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)
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
                Channel = turn.Channel,
                PrincipalKind = turn.Principal.Kind,
                PrincipalSubject = turn.Principal.Subject,
                PrincipalTenantId = turn.Principal.TenantId,
                Capabilities = turn.Capabilities,
                Locale = turn.Locale,
                TraceId = turn.TraceId,
                WasInterrupted = turn.WasInterrupted,
                FoundryConversationId = binding.FoundryConversationId.Value,
                FoundryConversationGeneration = binding.Generation,
                ReceivedAt = turn.ReceivedAt,
                ReceivedAtTicks = turn.ReceivedAt.UtcTicks,
                CreatedAt = turn.ReceivedAt,
                Status = InboxEntryStatus.Pending,
            });

            // The ordered content parts are inserted with the entry itself, in the same
            // SaveChangesAsync: a Turn is never durably accepted without the content it was accepted
            // for, provenance and order included.
            foreach (var part in turn.ContentParts)
            {
                db.InboxContentParts.Add(new InboxContentPartEntity
                {
                    TurnId = turn.TurnId.Value,
                    Order = part.Order,
                    Provenance = part.Provenance,
                    Text = part.Text,
                });
            }

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
        // concurrently.
        //
        // Ordering across conversations decides only which heads fit inside maxCount, and it is
        // strictly a fairness question: whoever has waited longest goes first. It therefore orders by
        // the acceptance instant, NOT by the conversation sequence - that sequence counts how many
        // Turns a conversation has already answered, so ordering by it would push a long-running
        // conversation's head behind every brand-new conversation's first Turn and let a steady
        // trickle of new conversations starve it indefinitely. The instant is compared as UTC ticks
        // because a DateTimeOffset is not orderable on every provider, with the Turn identity as a
        // total, stable tie-break so one backlog always yields the same batch.
        //
        // Safe without extra row locking: callers only claim pending work while holding the
        // "turn-processing" lease, so at most one worker runs this at a time.
        var pending = await db.InboxEntries
            .Where(e => e.Status == InboxEntryStatus.Pending)
            .Where(e => !db.InboxEntries.Any(predecessor =>
                predecessor.ChannelConversationId == e.ChannelConversationId
                && predecessor.Status != InboxEntryStatus.Completed
                && predecessor.ConversationSequence < e.ConversationSequence))
            .OrderBy(e => e.ReceivedAtTicks)
            .ThenBy(e => e.TurnId)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        return await ToDomainAsync(pending, cancellationToken);
    }

    private async Task<long> NextConversationSequenceAsync(string channelConversationId, CancellationToken cancellationToken)
    {
        var highest = await db.InboxEntries
            .AsNoTracking()
            .Where(e => e.ChannelConversationId == channelConversationId)
            .MaxAsync(e => (long?)e.ConversationSequence, cancellationToken);

        return (highest ?? 0L) + 1L;
    }

    private async Task<InboundTurn> ToDomainAsync(InboxEntryEntity entity, CancellationToken cancellationToken) =>
        (await ToDomainAsync([entity], cancellationToken))[0];

    /// <summary>
    /// Rehydrates whole Turns, content parts included, in one extra query for the whole batch rather
    /// than one per Turn.
    /// </summary>
    private async Task<IReadOnlyList<InboundTurn>> ToDomainAsync(
        IReadOnlyList<InboxEntryEntity> entities, CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return [];
        }

        var turnIds = entities.Select(e => e.TurnId).ToList();
        var parts = await db.InboxContentParts
            .AsNoTracking()
            .Where(p => turnIds.Contains(p.TurnId))
            .ToListAsync(cancellationToken);

        var partsByTurn = parts
            .GroupBy(p => p.TurnId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TurnContentPart>)group
                    .OrderBy(p => p.Order)
                    .Select(p => new TurnContentPart { Order = p.Order, Provenance = p.Provenance, Text = p.Text })
                    .ToList());

        return entities.Select(entity => new InboundTurn
        {
            TurnId = new TurnId(entity.TurnId),
            NativeMessageId = entity.NativeMessageId,
            ParticipantId = new ParticipantId(entity.ParticipantId),
            ChannelConversationId = new ChannelConversationId(entity.ChannelConversationId),
            Channel = entity.Channel,
            Principal = new ChannelPrincipal
            {
                Kind = entity.PrincipalKind,
                Subject = entity.PrincipalSubject,
                TenantId = entity.PrincipalTenantId,
            },
            Capabilities = entity.Capabilities,
            // Content parts are written in the same transaction as the entry itself, so an accepted
            // Turn without them is a broken invariant, not a case to paper over with a guess.
            ContentParts = partsByTurn.TryGetValue(entity.TurnId, out var turnParts)
                ? turnParts
                : throw new InvalidOperationException($"Accepted Turn {entity.TurnId} has no durable content parts."),
            Locale = entity.Locale,
            TraceId = entity.TraceId,
            WasInterrupted = entity.WasInterrupted,
            ReceivedAt = entity.ReceivedAt,
        }).ToList();
    }
}
