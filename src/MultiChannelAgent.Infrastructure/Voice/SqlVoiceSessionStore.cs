using System.Data;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Voice;

/// <summary>
/// SQL Server-backed durable store for <see cref="VoiceSession"/> lifecycle persistence.
///
/// <see cref="TryAdmitAsync"/> enforces the per-participant uniqueness constraint via the filtered
/// unique index on (ParticipantId WHERE OccupiesSlot = 1), and the global concurrent-session cap via
/// a SERIALIZABLE COUNT with <c>UPDLOCK, HOLDLOCK</c> on the OccupiesSlot index. The serializable
/// isolation prevents phantom-insert races where two replicas both see N−1 sessions and both insert
/// the Nth.
/// </summary>
public sealed class SqlVoiceSessionStore(MultiChannelAgentDbContext db) : IVoiceSessionStore
{
    public async Task<VoiceAdmissionResult> TryAdmitAsync(
        VoiceSession session, int globalCap, CancellationToken cancellationToken)
    {
        // Run the entire check-and-insert under a serializable transaction so the COUNT and INSERT
        // are atomic: no phantom final-slot race is possible between replicas.
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        try
        {
            // Check per-participant uniqueness FIRST (gives AlreadyActive priority over GlobalCapReached).
            var hasExisting = await db.VoiceSessions
                .AnyAsync(e => e.ParticipantId == session.ParticipantId.Value && e.OccupiesSlot, cancellationToken);

            if (hasExisting)
            {
                await transaction.RollbackAsync(cancellationToken);
                return VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.AlreadyActive);
            }

            // Count slot-occupying sessions under SERIALIZABLE isolation. On SQL Server the
            // serializable isolation combined with the IX_VoiceSessions_OccupiesSlot index makes
            // this COUNT take a key-range lock, preventing phantom inserts by concurrent replicas.
            var occupyingCount = await db.VoiceSessions
                .CountAsync(e => e.OccupiesSlot, cancellationToken);

            if (occupyingCount >= globalCap)
            {
                await transaction.RollbackAsync(cancellationToken);
                return VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.GlobalCapReached);
            }

            db.VoiceSessions.Add(ToEntity(session));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Another replica admitted this same participant between our check and insert.
                // The filtered unique index on (ParticipantId WHERE OccupiesSlot = 1) caught it.
                await transaction.RollbackAsync(cancellationToken);
                return VoiceAdmissionResult.Denied(VoiceAdmissionDenialReason.AlreadyActive);
            }

            await transaction.CommitAsync(cancellationToken);
            return VoiceAdmissionResult.Success(session);
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken cancellationToken)
    {
        var entity = await db.VoiceSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id.Value, cancellationToken);

        return entity is null ? null : ToDomain(entity);
    }

    public async Task<bool> UpdateAsync(
        VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken cancellationToken)
    {
        var expectedStatusString = expectedStatus.ToString();

        var affected = await db.VoiceSessions
            .Where(e => e.Id == session.Id.Value && e.Status == expectedStatusString)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.Status, session.Status.ToString())
                .SetProperty(e => e.OccupiesSlot, session.OccupiesSlot)
                .SetProperty(e => e.ControlSessionId, session.ControlSessionId)
                .SetProperty(e => e.LastHeartbeatAtTicks, session.LastHeartbeatAt.UtcTicks)
                .SetProperty(e => e.EndedAtTicks, session.EndedAt.HasValue ? session.EndedAt.Value.UtcTicks : (long?)null)
                .SetProperty(e => e.IdleExpiresAtTicks, session.IdleExpiresAt.UtcTicks)
                .SetProperty(e => e.WarningIssued, session.WarningIssued),
                cancellationToken);

        return affected > 0;
    }

    public async Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var endedStatus = VoiceSessionStatus.Ended.ToString();
        var activeStatus = VoiceSessionStatus.Active.ToString();
        var nowTicks = now.UtcTicks;

        var entities = await db.VoiceSessions
            .AsNoTracking()
            .Where(e => e.Status != endedStatus)
            .Where(e => e.ExpiresAtTicks <= nowTicks || (e.Status == activeStatus && e.IdleExpiresAtTicks <= nowTicks))
            .ToListAsync(cancellationToken);

        return entities.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<VoiceSession>> FindByOwnerInstanceAsync(
        string ownerInstanceId, CancellationToken cancellationToken)
    {
        var endedStatus = VoiceSessionStatus.Ended.ToString();

        var entities = await db.VoiceSessions
            .AsNoTracking()
            .Where(e => e.Status != endedStatus && e.OwnerInstanceId == ownerInstanceId)
            .ToListAsync(cancellationToken);

        return entities.Select(ToDomain).ToList();
    }

    private static VoiceSessionEntity ToEntity(VoiceSession s) => new()
    {
        Id = s.Id.Value,
        ParticipantId = s.ParticipantId.Value,
        ChannelConversationId = s.ChannelConversationId.Value,
        ControlSessionId = s.ControlSessionId,
        OwnerInstanceId = s.OwnerInstanceId,
        Status = s.Status.ToString(),
        OccupiesSlot = s.OccupiesSlot,
        StartedAtTicks = s.StartedAt.UtcTicks,
        LastHeartbeatAtTicks = s.LastHeartbeatAt.UtcTicks,
        EndedAtTicks = s.EndedAt?.UtcTicks,
        ExpiresAtTicks = s.ExpiresAt.UtcTicks,
        WarningAtTicks = s.WarningAt.UtcTicks,
        IdleExpiresAtTicks = s.IdleExpiresAt.UtcTicks,
        WarningIssued = s.WarningIssued,
    };

    private static VoiceSession ToDomain(VoiceSessionEntity e) =>
        VoiceSession.Reconstitute(
            new VoiceSessionId(e.Id),
            new ParticipantId(e.ParticipantId),
            new ChannelConversationId(e.ChannelConversationId),
            e.ControlSessionId,
            e.OwnerInstanceId,
            Enum.Parse<VoiceSessionStatus>(e.Status),
            e.OccupiesSlot,
            new DateTimeOffset(e.StartedAtTicks, TimeSpan.Zero),
            new DateTimeOffset(e.LastHeartbeatAtTicks, TimeSpan.Zero),
            e.EndedAtTicks.HasValue ? new DateTimeOffset(e.EndedAtTicks.Value, TimeSpan.Zero) : null,
            new DateTimeOffset(e.ExpiresAtTicks, TimeSpan.Zero),
            new DateTimeOffset(e.WarningAtTicks, TimeSpan.Zero),
            new DateTimeOffset(e.IdleExpiresAtTicks, TimeSpan.Zero),
            e.WarningIssued);

    /// <summary>
    /// Returns <see langword="true"/> only for the known unique-index violation from the filtered
    /// unique index on (ParticipantId WHERE OccupiesSlot = 1). Does not swallow arbitrary
    /// <see cref="DbUpdateException"/>.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // SQL Server: error 2601 (unique index violation) or 2627 (unique constraint violation).
        // SQLite: "UNIQUE constraint failed".
        var inner = ex.InnerException;
        if (inner is null) return false;

        var message = inner.Message;

        // Microsoft.Data.SqlClient: Number property on SqlException
        if (inner is Microsoft.Data.SqlClient.SqlException sqlEx)
            return sqlEx.Number is 2601 or 2627;

        // SQLite fallback for integration tests
        return message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
    }
}
