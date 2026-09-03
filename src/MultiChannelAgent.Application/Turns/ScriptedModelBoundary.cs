using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Deterministic scripted model boundary used in place of a real Foundry-backed model. It only ever
/// parses <see cref="InboundTurn.ContentText"/> - it has no dependency on any store or service, and so
/// can never itself call SQL or resolve any Participant/Inventory identity. It recognizes exactly two
/// deterministic read commands, each with a bounded clause grammar
/// (<see cref="ConversationalClauses"/>) covering the filters and paging bounds a Participant can ask
/// for conversationally, and proposes their bounded tool call for <see cref="IToolDispatcher"/> to
/// execute under trusted context. Unrecognized content, and the reserved <see cref="FailureMarker"/>,
/// keep the pre-existing direct echo/failure tracer behavior.
///
/// Every clause value it produces is untrusted filter text: the dispatcher and the deterministic
/// services resolve and bound it, and identity never comes from here.
/// </summary>
public sealed class ScriptedModelBoundary : IModelBoundary
{
    public const string FailureMarker = "trigger-scripted-failure";

    private const string ListStockCommand = "list stock";
    private const string FindCommand = "find";

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

        if (TryProposeFind(content, out var findProposal))
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

    private static bool TryProposeFind(string content, out ModelProposal? proposal)
    {
        proposal = null;

        if (!StartsWithCommand(content, FindCommand, out var remainder) || remainder.Length == 0)
        {
            return false;
        }

        // Everything before the first narrowing clause is the reference itself; the clauses that
        // follow narrow it. A reference is always required - "find" alone names nothing to look for.
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

        proposal = ModelProposal.Tool("find_stock", args);
        return true;
    }

    /// <summary>
    /// Where a Find reference stops and its narrowing clauses begin. Only a clause keyword standing
    /// as its own word can start them, so a reference that merely contains one of those words (a
    /// "unit heater", say) stays part of the reference.
    /// </summary>
    private static int FindFirstClauseIndex(string reference)
    {
        var earliest = -1;

        foreach (var clause in (string[])[" unit ", " in ", " unlocated"])
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
