using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Deterministic scripted model boundary used in place of a real Foundry-backed model. It only ever
/// parses <see cref="InboundTurn.ContentText"/> - it has no dependency on any store or service, and so
/// can never itself call SQL or resolve any Participant/Inventory identity. It recognizes five
/// deterministic commands - two reads and three mutations - each with a bounded clause grammar
/// (<see cref="ConversationalClauses"/>) covering the filters, amounts, and paging bounds a
/// Participant can ask for conversationally, and proposes their bounded tool call for
/// <see cref="IToolDispatcher"/> to execute under trusted context. Unrecognized content, and the
/// reserved <see cref="FailureMarker"/>, keep the pre-existing direct echo/failure tracer behavior.
///
/// Every clause value it produces is untrusted filter text: the dispatcher and the deterministic
/// services resolve and bound it, and identity never comes from here.
/// </summary>
public sealed class ScriptedModelBoundary : IModelBoundary
{
    public const string FailureMarker = "trigger-scripted-failure";

    private const string ListStockCommand = "list stock";
    private const string FindCommand = "find";
    private const string AddStockCommand = "add stock";
    private const string RemoveStockCommand = "remove stock";
    private const string SetStockCommand = "set stock";

    /// <summary>The mutation commands this boundary recognizes, each mapped to the bounded tool it proposes.</summary>
    private static readonly (string Command, string ToolName)[] MutationCommands =
    [
        (AddStockCommand, "add_stock"),
        (RemoveStockCommand, "remove_stock"),
        (SetStockCommand, "set_stock"),
    ];

    public Task<ModelProposal> ProposeAsync(InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
    {
        // The Foundry conversation is the conversation this invocation continues. Without one the
        // application has not established where the Turn belongs, so answering anyway would answer
        // outside any conversation - fail closed instead.
        if (context.FoundryConversationId.Value == Guid.Empty || context.Generation < 1)
        {
            return Task.FromResult(ModelProposal.Directly(new ModelDecision
            {
                Category = OutcomeCategory.TransientFailure,
                Code = "no_conversation_binding",
                Summary = "This Turn has no established conversation to continue.",
            }));
        }

        var content = turn.ContentText.Trim();

        if (content == FailureMarker)
        {
            return Task.FromResult(ModelProposal.Directly(new ModelDecision
            {
                Category = OutcomeCategory.TransientFailure,
                Code = "scripted_failure",
                Summary = "The scripted model boundary could not answer this Turn.",
            }));
        }

        if (TryProposeList(content, out var listProposal))
        {
            return Task.FromResult(listProposal!);
        }

        // Longest command words first: "add stock"/"remove stock"/"set stock" each name a whole
        // command, and "find" must stay last so it never swallows one of them.
        foreach (var (command, toolName) in MutationCommands)
        {
            if (TryProposeReferenceCommand(content, command, toolName, out var mutationProposal))
            {
                return Task.FromResult(mutationProposal!);
            }
        }

        if (TryProposeReferenceCommand(content, FindCommand, "find_stock", out var findProposal))
        {
            return Task.FromResult(findProposal!);
        }

        var summary = $"Echoed: {turn.ContentText}";

        return Task.FromResult(ModelProposal.Directly(new ModelDecision
        {
            Category = OutcomeCategory.Completed,
            Code = "echoed",
            Summary = summary,
            Deliveries = [new RequestedDelivery("synthetic", summary)],
        }));
    }

    private static bool TryProposeList(string content, out ModelProposal? proposal)
    {
        proposal = null;

        if (!StartsWithCommand(content, ListStockCommand, out var remainder)
            || !ConversationalClauses.TryParse(remainder, out var clauses))
        {
            return false;
        }

        var args = new Dictionary<string, string>();
        CopyFlag(clauses, "including zero", args, "includeZero");
        CopyFlag(clauses, "unlocated", args, "unlocated");
        CopyValue(clauses, "named", args, "nameFilter");
        CopyValue(clauses, "unit", args, "unit");
        CopyValue(clauses, "in", args, "location");
        CopyValue(clauses, "page size", args, "pageSize");
        CopyValue(clauses, "after", args, "cursor");

        proposal = ModelProposal.Tool("list_stock", args);
        return true;
    }

    /// <summary>
    /// Parses a command shaped <c>&lt;command&gt; &lt;reference&gt; [clauses]</c>: everything before the
    /// first clause keyword is the reference itself, and the clauses that follow narrow or quantify
    /// it. A reference is always required - the command word alone names nothing to act on. Every
    /// value produced here is untrusted text; the dispatcher and the deterministic services resolve
    /// and bound it, and identity never comes from here.
    /// </summary>
    private static bool TryProposeReferenceCommand(string content, string command, string toolName, out ModelProposal? proposal)
    {
        proposal = null;

        if (!StartsWithCommand(content, command, out var remainder) || remainder.Length == 0)
        {
            return false;
        }

        var reference = remainder;
        IReadOnlyDictionary<string, string> clauses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var clauseStart = FindFirstClauseIndex(remainder);
        if (clauseStart >= 0)
        {
            reference = remainder[..clauseStart].Trim();
            if (!ConversationalClauses.TryParse(remainder[clauseStart..], out clauses))
            {
                return false;
            }
        }

        if (reference.Length == 0)
        {
            return false;
        }

        var args = new Dictionary<string, string> { ["reference"] = reference };
        CopyFlag(clauses, "unlocated", args, "unlocated");
        CopyValue(clauses, "unit", args, "unit");
        CopyValue(clauses, "in", args, "location");
        CopyValue(clauses, "quantity", args, "quantity");
        CopyValue(clauses, "note", args, "note");

        proposal = ModelProposal.Tool(toolName, args);
        return true;
    }

    /// <summary>
    /// Where a reference stops and its clauses begin. Only a clause keyword standing as its own word
    /// can start them, so a reference that merely contains one of those words (a "unit heater", say)
    /// stays part of the reference.
    /// </summary>
    private static int FindFirstClauseIndex(string reference)
    {
        var earliest = -1;

        foreach (var clause in (string[])[" unit ", " in ", " unlocated", " quantity ", " note "])
        {
            var index = reference.IndexOf(clause, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (earliest < 0 || index < earliest))
            {
                earliest = index;
            }
        }

        return earliest < 0 ? -1 : earliest + 1;
    }

    private static bool StartsWithCommand(string content, string command, out string remainder)
    {
        remainder = string.Empty;

        if (content.Length < command.Length || !content[..command.Length].Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (content.Length > command.Length && !char.IsWhiteSpace(content[command.Length]))
        {
            return false;
        }

        remainder = content[command.Length..].Trim();
        return true;
    }

    private static void CopyFlag(
        IReadOnlyDictionary<string, string> clauses, string clause, Dictionary<string, string> args, string argName)
    {
        if (clauses.ContainsKey(clause))
        {
            args[argName] = "true";
        }
    }

    private static void CopyValue(
        IReadOnlyDictionary<string, string> clauses, string clause, Dictionary<string, string> args, string argName)
    {
        if (clauses.TryGetValue(clause, out var value))
        {
            args[argName] = value;
        }
    }
}
