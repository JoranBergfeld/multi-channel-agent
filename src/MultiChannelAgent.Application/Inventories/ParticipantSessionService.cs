using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Upserts the canonical Participant the first time (and every time) an active tenant member's
/// authenticated Entra identity is observed - so signing in always resolves to the same person
/// across channels and refreshes their display name from the latest claims.
/// </summary>
public sealed class ParticipantSessionService(IParticipantStore store)
{
    public async Task<Participant> EnsureParticipantAsync(ParticipantId id, string displayName, CancellationToken cancellationToken)
    {
        var participant = Participant.Create(id, displayName);
        await store.UpsertAsync(participant, cancellationToken);
        return participant;
    }
}
