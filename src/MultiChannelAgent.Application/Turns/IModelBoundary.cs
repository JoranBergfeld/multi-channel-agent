using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>One channel-neutral delivery requested by the model boundary's decision for a Turn.</summary>
public sealed record RequestedDelivery(string Channel, string Payload);

/// <summary>
/// The scripted model boundary's deterministic decision for one Turn: a terminal semantic outcome
/// plus zero or more requested Deliveries. This replaces the real Foundry model/tool loop for this
/// tracer scenario.
/// </summary>
public sealed record ModelDecision
{
    public required OutcomeStatus Status { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<RequestedDelivery> Deliveries { get; init; } = [];
}

/// <summary>
/// The only boundary between the durable Turn workflow and "model" behavior. The production
/// implementation for this ticket is a deterministic scripted responder; a real Foundry-backed
/// implementation is out of scope until model/tool work begins.
/// </summary>
public interface IModelBoundary
{
    Task<ModelDecision> DecideAsync(InboundTurn turn, CancellationToken cancellationToken);
}
