using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The single registered <see cref="IToolDispatcher"/>. It owns one decision and nothing else: which
/// dispatcher a tool name belongs to.
///
/// The set is closed and explicit. A name in neither set never reaches any dispatcher and is reported
/// as the model/system failure it is - a proposal this application cannot execute - rather than being
/// silently ignored or passed on to whichever dispatcher happened to be asked first.
/// </summary>
public sealed class InventoryToolRouter : IToolDispatcher
{
    private readonly IToolDispatcher _stockDispatcher;
    private readonly IToolDispatcher _referenceDispatcher;

    private static readonly HashSet<string> StockTools = new(StockToolDispatcher.ToolNames, StringComparer.Ordinal);

    private static readonly HashSet<string> ReferenceTools = new(ReferenceToolDispatcher.ToolNames, StringComparer.Ordinal);

    /// <summary>
    /// Takes the two dispatcher contracts rather than the concrete classes, so a test can supply
    /// recording doubles and prove routing without standing up either real dispatcher. Production
    /// resolution passes the concrete ones through the explicit factory registration.
    /// </summary>
    public InventoryToolRouter(IToolDispatcher stockDispatcher, IToolDispatcher referenceDispatcher)
    {
        _stockDispatcher = stockDispatcher;
        _referenceDispatcher = referenceDispatcher;
    }

    public async Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (StockTools.Contains(proposal.ToolName))
        {
            return await _stockDispatcher.DispatchAsync(proposal, context, now, cancellationToken);
        }

        if (ReferenceTools.Contains(proposal.ToolName))
        {
            return await _referenceDispatcher.DispatchAsync(proposal, context, now, cancellationToken);
        }

        // An unrecognized tool name is the model proposing something this application cannot execute -
        // a model/system failure, not an answer to the Participant's request.
        return new ModelDecision
        {
            Category = OutcomeCategory.TransientFailure,
            Code = "unknown_tool",
            Summary = $"'{proposal.ToolName}' is not a recognized tool.",
        };
    }
}
