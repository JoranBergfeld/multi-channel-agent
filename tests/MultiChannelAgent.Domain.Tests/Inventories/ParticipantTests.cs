using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ParticipantTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));

    [Fact]
    public void Create_marks_a_newly_observed_participant_active()
    {
        var participant = Participant.Create(SomeParticipant, "Ada Lovelace");

        Assert.True(participant.IsActive);
    }

    [Fact]
    public void Create_trims_the_display_name()
    {
        var participant = Participant.Create(SomeParticipant, "  Ada Lovelace  ");

        Assert.Equal("Ada Lovelace", participant.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_display_name(string? blank)
    {
        Assert.Throws<ArgumentException>(() => Participant.Create(SomeParticipant, blank));
    }
}
