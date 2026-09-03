using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Deterministic scripted model boundary used in place of a real Foundry-backed model for this
/// tracer scenario. It echoes ordinary content back as one requested Delivery, and produces a failed
/// terminal Outcome for the reserved <see cref="FailureMarker"/> content, so both terminal paths are
/// reproducible without any external dependency.
/// </summary>
public sealed class ScriptedModelBoundary : IModelBoundary
{
    public const string FailureMarker = "trigger-scripted-failure";

    public Task<ModelDecision> DecideAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        if (turn.ContentText == FailureMarker)
        {
            return Task.FromResult(new ModelDecision
            {
                Status = OutcomeStatus.Failed,
                Code = "scripted_failure",
                Summary = "The scripted model boundary rejected this Turn.",
                Deliveries = [],
            });
        }

        var summary = $"Echoed: {turn.ContentText}";

        return Task.FromResult(new ModelDecision
        {
            Status = OutcomeStatus.Completed,
            Code = "echoed",
            Summary = summary,
            Deliveries = [new RequestedDelivery("synthetic", summary)],
        });
    }
}
