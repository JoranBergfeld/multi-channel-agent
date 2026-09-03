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
/// <see cref="DbUpdateException"/> to callers.
/// </summary>
public sealed class SqlInboxStore(MultiChannelAgentDbContext db) : IInboxStore
{
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
        db.InboxEntries.Add(new InboxEntryEntity
        {
            TurnId = turn.TurnId.Value,
            NativeMessageId = turn.NativeMessageId,
            ParticipantId = turn.ParticipantId.Value,
            ChannelConversationId = turn.ChannelConversationId.Value,
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
        }
        catch (DbUpdateException)
        {
            // Two concurrent deliveries of the same native message key can both observe absence via
            // FindByNativeMessageIdAsync and both reach this insert; the unique index on
            // (ParticipantId, ChannelConversationId, NativeMessageId) then lets exactly one of them
            // commit. Rather than parsing a provider-specific error code to confirm that assumption,
            // clear this failed attempt from the tracker and re-read by the same key: if a row is
            // there now, some other write genuinely committed it first, so this IS that
            // duplicate-delivery race and we converge on the winner. If no such row exists, this was
            // a real, unrelated failure (bad data, a dropped connection, ...) and must propagate
            // untouched rather than be disguised as a duplicate.
            db.ChangeTracker.Clear();

            var winner = await FindByNativeMessageIdAsync(turn.NativeMessageKey, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return new InboxAcceptResult(winner, WasAlreadyAccepted: true);
        }

        return new InboxAcceptResult(turn, WasAlreadyAccepted: false);
    }

    public async Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken)
    {
        // Safe without extra row locking: callers only claim pending work while holding the
        // "turn-processing" lease, so at most one worker runs this at a time.
        var pending = await db.InboxEntries
            .Where(e => e.Status == InboxEntryStatus.Pending)
            .OrderBy(e => e.ReceivedAt)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        return pending.Select(ToDomain).ToList();
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
