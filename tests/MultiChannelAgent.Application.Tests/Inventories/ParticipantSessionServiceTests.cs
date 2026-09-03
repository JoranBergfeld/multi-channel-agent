using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ParticipantSessionServiceTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task EnsureParticipantAsync_upserts_the_canonical_participant()
    {
        var store = new InMemoryParticipantStore();
        var service = new ParticipantSessionService(store);

        var participant = await service.EnsureParticipantAsync(SomeParticipant, "Ada Lovelace", CancellationToken.None);

        Assert.Equal(SomeParticipant, participant.Id);
        Assert.Equal("Ada Lovelace", participant.DisplayName);
        Assert.Equal("Ada Lovelace", store.Participants[SomeParticipant].DisplayName);
    }

    // A returning Participant's display name must refresh from the latest authenticated claims
    // rather than staying frozen at whatever value was first observed.
    [Fact]
    public async Task EnsureParticipantAsync_refreshes_the_display_name_on_subsequent_calls()
    {
        var store = new InMemoryParticipantStore();
        var service = new ParticipantSessionService(store);

        await service.EnsureParticipantAsync(SomeParticipant, "Ada Lovelace", CancellationToken.None);
        await service.EnsureParticipantAsync(SomeParticipant, "Ada L.", CancellationToken.None);

        Assert.Equal("Ada L.", store.Participants[SomeParticipant].DisplayName);
    }
}
