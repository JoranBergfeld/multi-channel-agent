using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Deterministic scripted model boundary used in place of a real Foundry-backed model. It only ever
/// parses <see cref="InboundTurn.ContentText"/> - it has no dependency on any store or service, and so
/// can never itself call SQL or resolve any Participant/Inventory identity. It recognizes exactly two
/// deterministic read commands and proposes their bounded tool call for <see cref="IToolDispatcher"/>
/// to execute under trusted context; unrecognized content, and the reserved
/// <see cref="FailureMarker"/>, keep the pre-existing direct echo/failure tracer behavior.
/// </summary>
public sealed class ScriptedModelBoundary : IModelBoundary
{
    public const string FailureMarker = "trigger-scripted-failure";

    private const string ListStockCommand = "list stock";
    private const string ListStockIncludingZeroCommand = "list stock including zero";
    private const string FindCommandPrefix = "find ";

    public Task<ModelProposal> ProposeAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        var content = turn.ContentText.Trim();

        if (content == FailureMarker)
        {
            return Task.FromResult(ModelProposal.Directly(new ModelDecision
            {
                Status = OutcomeStatus.Failed,
                Code = "scripted_failure",
                Summary = "The scripted model boundary rejected this Turn.",
            }));
        }

        if (string.Equals(content, ListStockIncludingZeroCommand, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ModelProposal.Tool("list_stock", new Dictionary<string, string> { ["includeZero"] = "true" }));
        }

        if (string.Equals(content, ListStockCommand, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ModelProposal.Tool("list_stock", new Dictionary<string, string>()));
        }

        if (content.Length > FindCommandPrefix.Length
            && content[..FindCommandPrefix.Length].Equals(FindCommandPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reference = content[FindCommandPrefix.Length..].Trim();
            if (reference.Length > 0)
            {
                return Task.FromResult(ModelProposal.Tool("find_stock", new Dictionary<string, string> { ["reference"] = reference }));
            }
        }

        var summary = $"Echoed: {turn.ContentText}";

        return Task.FromResult(ModelProposal.Directly(new ModelDecision
        {
            Status = OutcomeStatus.Completed,
            Code = "echoed",
            Summary = summary,
            Deliveries = [new RequestedDelivery("synthetic", summary)],
        }));
    }
}
