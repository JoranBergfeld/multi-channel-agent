using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Authentication;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Authentication;

/// <summary>
/// SQL-backed durable store for server-side authentication tickets. Save is upsert-by-key: since
/// every key originates from a cryptographically random <see cref="Guid"/> minted once per new
/// session (see <c>SqlServerTicketStore.StoreAsync</c>), a colliding key on initial store is not a
/// realistic race to guard against - unlike <see cref="MultiChannelAgent.Infrastructure.Turns.SqlInboxStore"/>,
/// no duplicate-key retry logic is needed here.
/// </summary>
public sealed class SqlAuthTicketRepository(MultiChannelAgentDbContext db, TimeProvider timeProvider) : IAuthTicketRepository
{
    public async Task SaveAsync(string key, byte[] protectedTicket, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await db.AuthTickets.FirstOrDefaultAsync(t => t.Key == key, cancellationToken);

        if (existing is null)
        {
            db.AuthTickets.Add(new AuthTicketEntity
            {
                Key = key,
                ProtectedTicket = protectedTicket,
                ExpiresAtUtc = expiresAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
        }
        else
        {
            existing.ProtectedTicket = protectedTicket;
            existing.ExpiresAtUtc = expiresAtUtc;
            existing.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<byte[]?> FindAsync(string key, CancellationToken cancellationToken) =>
        (await db.AuthTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Key == key, cancellationToken))?.ProtectedTicket;

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var existing = await db.AuthTickets.FirstOrDefaultAsync(t => t.Key == key, cancellationToken);
        if (existing is not null)
        {
            db.AuthTickets.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
