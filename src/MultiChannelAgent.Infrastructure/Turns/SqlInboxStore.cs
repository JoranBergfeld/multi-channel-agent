using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>SQL Server-backed durable inbox. Idempotency is additionally enforced by a unique index on <see cref="InboxEntryEntity.NativeMessageId"/>.</summary>
public sealed class SqlInboxStore(MultiChannelAgentDbContext db) : IInboxStore
{
    public async Task<InboundTurn?> FindByNativeMessageIdAsync(string nativeMessageId, CancellationToken cancellationToken)
    {
        var entity = await db.InboxEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.NativeMessageId == nativeMessageId, cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task AcceptAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        db.InboxEntries.Add(new InboxEntryEntity
        {
            TurnId = turn.TurnId.Value,
            NativeMessageId = turn.NativeMessageId,
            ChannelConversationId = turn.ChannelConversationId,
            ContentText = turn.ContentText,
            Locale = turn.Locale,
            TraceId = turn.TraceId,
            ReceivedAt = turn.ReceivedAt,
            CreatedAt = turn.ReceivedAt,
            Status = InboxEntryStatus.Pending,
        });

        await db.SaveChangesAsync(cancellationToken);
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

    public async Task MarkCompletedAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var entity = await db.InboxEntries.FirstAsync(e => e.TurnId == turnId.Value, cancellationToken);
        entity.Status = InboxEntryStatus.Completed;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static InboundTurn ToDomain(InboxEntryEntity entity) => new()
    {
        TurnId = new TurnId(entity.TurnId),
        NativeMessageId = entity.NativeMessageId,
        ChannelConversationId = entity.ChannelConversationId,
        ContentText = entity.ContentText,
        Locale = entity.Locale,
        TraceId = entity.TraceId,
        ReceivedAt = entity.ReceivedAt,
    };
}
