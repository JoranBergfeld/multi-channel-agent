using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Turns;

/// <summary>
/// The application boundary's view of "New conversation": what a channel is told happened, in a shape
/// it can render, with no store or persistence vocabulary leaking through.
/// </summary>
public class ConversationRotationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private const string Conversation = "web:profile-1";

    private readonly InMemoryFoundryConversationBindingStore _bindings = new();

    [Fact]
    public async Task Starting_a_new_conversation_reports_the_fresh_generation()
    {
        var store = new InMemoryConversationRotationStore(_bindings);
        var service = new ConversationRotationService(store);

        var view = await service.RotateAsync(Participant, Conversation, Now, CancellationToken.None);

        Assert.Equal(2, view.Generation);
        Assert.True(Guid.TryParse(view.FoundryConversationId, out _));
        Assert.False(view.ClearedPendingConfirmation);
    }

    [Fact]
    public async Task Starting_a_new_conversation_says_so_when_something_was_waiting_to_be_confirmed()
    {
        var store = new InMemoryConversationRotationStore(_bindings) { HasPendingConfirmation = true };
        var service = new ConversationRotationService(store);

        var view = await service.RotateAsync(Participant, Conversation, Now, CancellationToken.None);

        // Told, not silently discarded: a Participant who was mid-confirmation deserves to know their
        // proposal stopped being confirmable rather than discovering it by typing "confirm".
        Assert.True(view.ClearedPendingConfirmation);
    }

    [Fact]
    public async Task Each_new_conversation_advances_the_generation_again()
    {
        var store = new InMemoryConversationRotationStore(_bindings);
        var service = new ConversationRotationService(store);

        var first = await service.RotateAsync(Participant, Conversation, Now, CancellationToken.None);
        var second = await service.RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.NotEqual(first.FoundryConversationId, second.FoundryConversationId);
    }
}
