using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class FoundryConversationBindingTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ChannelConversationId SomeConversation = new("conversation-abc");

    [Fact]
    public void CreateFirstGeneration_starts_at_generation_one_with_a_fresh_foundry_conversation_id()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var binding = FoundryConversationBinding.CreateFirstGeneration(SomeParticipant, SomeConversation, createdAt);

        Assert.Equal(SomeParticipant, binding.ParticipantId);
        Assert.Equal(SomeConversation, binding.ChannelConversationId);
        Assert.Equal(1, binding.Generation);
        Assert.Equal(createdAt, binding.CreatedAt);
        Assert.NotEqual(default, binding.FoundryConversationId.Value);
    }

    [Fact]
    public void CreateFirstGeneration_called_twice_produces_distinct_foundry_conversation_ids()
    {
        var first = FoundryConversationBinding.CreateFirstGeneration(SomeParticipant, SomeConversation, DateTimeOffset.UtcNow);
        var second = FoundryConversationBinding.CreateFirstGeneration(SomeParticipant, SomeConversation, DateTimeOffset.UtcNow);

        Assert.NotEqual(first.FoundryConversationId, second.FoundryConversationId);
    }
}
