using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>One channel-neutral delivery requested by a Turn's decision.</summary>
public sealed record RequestedDelivery(string Channel, string Payload);

/// <summary>
/// A terminal semantic decision for one Turn: a semantic <see cref="Category"/> with its machine
/// <see cref="Code"/> and human summary, an optional versioned JSON payload (see
/// <see cref="Domain.Turns.Outcome.Payload"/>), and zero or more requested Deliveries. The processing
/// status is never chosen here - it follows from the category - so a deterministic semantic answer
/// can never be recorded as the system having failed.
/// </summary>
public sealed record ModelDecision
{
    public required OutcomeCategory Category { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public string? Payload { get; init; }

    /// <summary>
    /// How long the recorded Outcome should retain <see cref="Payload"/>, when the ordinary window is
    /// too generous for what it carries. Null takes <see cref="Outcome.PayloadRetention"/>.
    /// </summary>
    public TimeSpan? PayloadRetention { get; init; }

    public IReadOnlyList<RequestedDelivery> Deliveries { get; init; } = [];
}

/// <summary>
/// A bounded read tool call the model boundary proposes: an untrusted tool name plus untrusted
/// string arguments (free-form filter text only - never Participant/Inventory/conversation identity,
/// which the model is never given access to). Only <see cref="IToolDispatcher"/>, injecting trusted
/// <see cref="TurnExecutionContext"/>, ever actually executes it.
/// </summary>
public sealed record ToolCallProposal(string ToolName, IReadOnlyDictionary<string, string> UntrustedArgs);

/// <summary>The shape of one <see cref="IModelBoundary.ProposeAsync"/> result.</summary>
public enum ModelProposalKind
{
    /// <summary>A terminal decision with no tool call - the pre-existing echo/failure tracer behavior.</summary>
    Direct,

    /// <summary>A bounded read tool call that <see cref="IToolDispatcher"/> must execute under trusted context.</summary>
    ToolCall,
}

/// <summary>One <see cref="IModelBoundary.ProposeAsync"/> result: either terminal directly, or a tool call to dispatch.</summary>
public sealed record ModelProposal
{
    public required ModelProposalKind Kind { get; init; }

    public ModelDecision? Direct { get; init; }

    public ToolCallProposal? ToolCall { get; init; }

    public static ModelProposal Directly(ModelDecision decision) => new() { Kind = ModelProposalKind.Direct, Direct = decision };

    public static ModelProposal Tool(string toolName, IReadOnlyDictionary<string, string> untrustedArgs) =>
        new() { Kind = ModelProposalKind.ToolCall, ToolCall = new ToolCallProposal(toolName, untrustedArgs) };
}

/// <summary>
/// The trusted, application-established context one model invocation runs in: the Foundry
/// conversation this Turn belongs to (and its generation), plus the Turn's locale. It carries no
/// Participant, Inventory, or authorization identity - the model is never given any of those - and it
/// is never derived from anything the model proposes. Its Foundry conversation is stable for a
/// Participant's ChannelConversation, so a conversation's history stays coherent across Turns whether
/// they are answered directly or through a tool call.
/// </summary>
public sealed record ModelInvocationContext(FoundryConversationId FoundryConversationId, int Generation, string? Locale);

/// <summary>
/// The only boundary between the durable Turn workflow and "model" behavior. It only ever proposes -
/// it must never itself call SQL, a service, or trust anything about the caller's identity. The
/// production implementation for this ticket is a deterministic scripted responder; a real
/// Foundry-backed implementation is out of scope until real model/tool integration begins.
/// </summary>
public interface IModelBoundary
{
    Task<ModelProposal> ProposeAsync(InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Executes one <see cref="ToolCallProposal"/> under trusted <see cref="TurnExecutionContext"/> -
/// assembled by <see cref="TurnExecutionContextFactory"/>, never by the model - producing the terminal
/// <see cref="ModelDecision"/>. Recognizes only a bounded, explicit set of tool names; an unrecognized
/// one is a failed decision, never silently ignored.
/// </summary>
public interface IToolDispatcher
{
    Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken);
}
