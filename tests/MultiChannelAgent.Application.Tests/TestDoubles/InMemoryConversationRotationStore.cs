using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IConversationRotationStore"/> for Application-layer unit tests. A
/// single lock makes reading the current generation and advancing it one indivisible step, mirroring
/// the real store's guarded update, so concurrent resets advance one generation each exactly like
/// production.
/// </summary>
public sealed class InMemoryConversationRotationStore(InMemoryFoundryConversationBindingStore bindings)
    : IConversationRotationStore
{
    private readonly object _gate = new();

    /// <summary>What the next rotation should report about a confirmation waiting in this conversation.</summary>
    public bool HasPendingConfirmation { get; set; }

    public Task<ConversationRotationResult> RotateAsync(
        ParticipantId participantId,
        ChannelConversationId channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var rotated = bindings.Rotate(participantId, channelConversationId, now);
            var cleared = HasPendingConfirmation;
            HasPendingConfirmation = false;

            return Task.FromResult(new ConversationRotationResult(rotated, cleared));
        }
    }
}
