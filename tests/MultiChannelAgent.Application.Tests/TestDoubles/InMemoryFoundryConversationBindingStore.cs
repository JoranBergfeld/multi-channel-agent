using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="IFoundryConversationBindingStore"/> for Application-layer unit tests.
/// A single lock makes "look up, then insert" one indivisible step per (Participant,
/// ChannelConversation) pair, mirroring the real store's unique-index race resolution, so concurrent
/// callers converge on one binding exactly like production.
/// </summary>
public sealed class InMemoryFoundryConversationBindingStore : IFoundryConversationBindingStore
{
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<(ParticipantId, ChannelConversationId), FoundryConversationBinding> _bindings = [];
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource? _rotationInFlight;
    private FoundryConversationBinding? _uncommittedRotation;

    public IReadOnlyCollection<FoundryConversationBinding> Bindings => _bindings.Values;

    /// <summary>How many times the supersession seam - and not an ordinary read - was asked.</summary>
    public int SupersessionReadCount { get; private set; }

    /// <summary>
    /// Completes once a supersession read has parked behind the in-flight rotation, bounded so a
    /// caller that reads the committed generation instead fails the test rather than hanging the run.
    /// </summary>
    public Task SupersessionReadParkedAsync() => _parked.Task.WaitAsync(ParkTimeout);

    public Task<FoundryConversationBinding> GetOrCreateAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (participantId, channelConversationId);
            if (_bindings.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing);
            }

            var created = FoundryConversationBinding.CreateFirstGeneration(participantId, channelConversationId, now);
            _bindings[key] = created;
            return Task.FromResult(created);
        }
    }

    /// <summary>
    /// Answers only once no rotation is mid-flight for this pair, exactly as a read that takes the
    /// binding row's lock does: while a reset holds the row uncommitted this parks, and it resumes
    /// with whatever that reset left committed. An ordinary <see cref="GetOrCreateAsync"/> answers
    /// straight from the committed value throughout, which is how a test can tell the two apart.
    /// </summary>
    public async Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
        ParticipantId participantId, ChannelConversationId channelConversationId, CancellationToken cancellationToken)
    {
        Task? inFlight;
        lock (_gate)
        {
            SupersessionReadCount++;
            inFlight = _rotationInFlight?.Task;
        }

        if (inFlight is not null)
        {
            _parked.TrySetResult();
            await inFlight;
        }

        lock (_gate)
        {
            return _bindings.TryGetValue((participantId, channelConversationId), out var binding) ? binding : null;
        }
    }

    /// <summary>
    /// Starts a rotation that has written this pair's binding but has not committed - the state a
    /// real rotation transaction is in between its generation bump and its commit. Every ordinary
    /// read still sees the old generation until <see cref="CommitRotation"/>.
    /// </summary>
    public void BeginRotation(ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now)
    {
        lock (_gate)
        {
            _uncommittedRotation = Rotated(participantId, channelConversationId, now);
            _rotationInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Commits the in-flight rotation, publishing its generation and releasing anything parked behind it.</summary>
    public void CommitRotation()
    {
        TaskCompletionSource? inFlight;
        lock (_gate)
        {
            if (_uncommittedRotation is not { } rotated || _rotationInFlight is null)
            {
                throw new InvalidOperationException("No rotation is in flight to commit.");
            }

            _bindings[(rotated.ParticipantId, rotated.ChannelConversationId)] = rotated;
            inFlight = _rotationInFlight;
            _rotationInFlight = null;
            _uncommittedRotation = null;
        }

        inFlight.TrySetResult();
    }

    /// <summary>
    /// Starts a fresh Foundry conversation generation for this pair, exactly as the durable rotation
    /// does, so an Application-layer test can prove what a reset does to work either side of it
    /// without needing a database.
    /// </summary>
    public FoundryConversationBinding Rotate(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var rotated = Rotated(participantId, channelConversationId, now);
            _bindings[(participantId, channelConversationId)] = rotated;
            return rotated;
        }
    }

    private FoundryConversationBinding Rotated(
        ParticipantId participantId, ChannelConversationId channelConversationId, DateTimeOffset now)
    {
        var key = (participantId, channelConversationId);
        var current = _bindings.TryGetValue(key, out var existing)
            ? existing
            : FoundryConversationBinding.CreateFirstGeneration(participantId, channelConversationId, now);

        return current with
        {
            FoundryConversationId = new FoundryConversationId(Guid.NewGuid()),
            Generation = current.Generation + 1,
            CreatedAt = now,
        };
    }
}
