using System.Text.Json;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Deterministic scripted model boundary used in place of a real Foundry-backed model. It only ever
/// parses <see cref="InboundTurn.ContentText"/> - it has no dependency on any store or service, and so
/// can never itself call SQL or resolve any Participant/Inventory identity. It recognizes a bounded
/// set of deterministic commands - two reads, six mutations, one batch, and the two confirmation
/// answers - each with a bounded clause grammar
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
    private const string MoveStockCommand = "move stock";
    private const string RenameStockCommand = "rename stock";
    private const string ForgetStockCommand = "forget stock";
    private const string ChangeStockCommand = "change stock";
    private const string ConfirmCommand = "confirm";
    private const string RejectCommand = "reject";
    private const string RenameStockToolName = "rename_stock";

    private const string ListUnitsCommand = "list units";
    private const string ListLocationsCommand = "list locations";
    private const string CreateUnitCommand = "create unit";
    private const string CreateLocationCommand = "create location";
    private const string RenameUnitCommand = "rename unit";
    private const string RenameLocationCommand = "rename location";
    private const string AddAliasCommand = "add alias";
    private const string RemoveAliasCommand = "remove alias";
    private const string RetireUnitCommand = "retire unit";
    private const string RetireLocationCommand = "retire location";

    /// <summary>The mutation commands this boundary recognizes, each mapped to the bounded tool it proposes.</summary>
    private static readonly (string Command, string ToolName)[] MutationCommands =
    [
        (AddStockCommand, "add_stock"),
        (RemoveStockCommand, "remove_stock"),
        (SetStockCommand, "set_stock"),
        (MoveStockCommand, "move_stock"),
        (RenameStockCommand, RenameStockToolName),
        (ForgetStockCommand, "forget_stock"),
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

        if (TryProposeReferenceRead(content, out var referenceReadProposal))
        {
            return Task.FromResult(referenceReadProposal!);
        }

        if (TryProposeReferenceAdministration(content, out var administrationProposal))
        {
            return Task.FromResult(administrationProposal!);
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

        if (TryProposeBatch(content, out var batchProposal))
        {
            return Task.FromResult(batchProposal!);
        }

        if (TryProposeConfirmation(content, out var confirmationProposal))
        {
            return Task.FromResult(confirmationProposal!);
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
        CopyFlag(clauses, "all", args, "all");
        CopyFlag(clauses, "to unlocated", args, "toUnlocated");

        // "rename stock X to Y" reads naturally, and the destination of a Rename is a name rather
        // than a place - so for that one command the "to" clause carries the new name.
        CopyValue(clauses, "to", args, toolName == RenameStockToolName ? "newName" : "to");

        proposal = ModelProposal.Tool(toolName, args);
        return true;
    }

    /// <summary>Parses <c>list units [page size N] [after CURSOR]</c> and its Location twin.</summary>
    private static bool TryProposeReferenceRead(string content, out ModelProposal? proposal)
    {
        proposal = null;

        foreach (var (command, toolName) in ((string, string)[])
                 [(ListUnitsCommand, "list_units"), (ListLocationsCommand, "list_locations")])
        {
            if (!StartsWithCommand(content, command, out var remainder)
                || !ConversationalClauses.TryParse(remainder, out var clauses))
            {
                continue;
            }

            var args = new Dictionary<string, string>();
            CopyValue(clauses, "page size", args, "pageSize");
            CopyValue(clauses, "after", args, "cursor");

            proposal = ModelProposal.Tool(toolName, args);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses the eight mutating administration commands into one homogeneous single-element
    /// <c>changes</c> array. Every value is untrusted text: the tool fixes the kind, and the
    /// deterministic services resolve and bound everything else. Identity never comes from here.
    /// </summary>
    private static bool TryProposeReferenceAdministration(string content, out ModelProposal? proposal)
    {
        proposal = null;

        // Longest command words first, so "create location" is never read as "create" plus a
        // reference that happens to start with "location".
        foreach (var (command, toolName, build) in AdministrationCommands)
        {
            if (!StartsWithCommand(content, command, out var remainder) || remainder.Length == 0)
            {
                continue;
            }

            var subject = remainder;
            IReadOnlyDictionary<string, string> clauses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var clauseStart = FindFirstClauseIndex(remainder);
            if (clauseStart >= 0)
            {
                subject = remainder[..clauseStart].Trim();
                if (!ConversationalClauses.TryParse(remainder[clauseStart..], out clauses))
                {
                    return false;
                }
            }

            if (subject.Length == 0)
            {
                return false;
            }

            if (build(subject, clauses) is not { } element)
            {
                return false;
            }

            proposal = ModelProposal.Tool(toolName, new Dictionary<string, string> { ["changes"] = $"[{element}]" });
            return true;
        }

        return false;
    }

    /// <summary>The eight mutating commands, each with the tool it proposes and how to shape its one element.</summary>
    private static readonly (string Command, string ToolName, Func<string, IReadOnlyDictionary<string, string>, string?> Build)[]
        AdministrationCommands =
    [
        (CreateLocationCommand, "create_locations", static (subject, _) => Element(("name", subject))),
        (CreateUnitCommand, "create_units", static (subject, clauses) => clauses.TryGetValue("aliases", out var aliases)
            ? Element(("name", subject), ("aliases", aliases))
            : Element(("name", subject))),
        (RenameLocationCommand, "rename_locations", static (subject, clauses) => clauses.TryGetValue("to", out var newName)
            ? Element(("location", subject), ("newName", newName))
            : null),
        (RenameUnitCommand, "rename_units", static (subject, clauses) => clauses.TryGetValue("to", out var newName)
            ? Element(("unit", subject), ("newName", newName))
            : null),

        // "add alias cartons to unit Cardboard Box": the subject is the alias, and the Unit arrives as
        // a "to unit" clause.
        (AddAliasCommand, "add_unit_aliases", static (subject, clauses) => UnitOf(clauses) is { } unit
            ? Element(("unit", unit), ("alias", subject))
            : null),

        // "remove alias cartons from unit Cardboard Box": the same shape with "from unit".
        (RemoveAliasCommand, "remove_unit_aliases", static (subject, clauses) => UnitOf(clauses) is { } unit
            ? Element(("unit", unit), ("alias", subject))
            : null),
        (RetireLocationCommand, "retire_locations", static (subject, _) => Element(("location", subject))),
        (RetireUnitCommand, "retire_units", static (subject, _) => Element(("unit", subject))),
    ];

    /// <summary>Reads the Unit an alias change names, however the sentence reached it.</summary>
    private static string? UnitOf(IReadOnlyDictionary<string, string> clauses)
    {
        foreach (var clause in (string[])["unit", "to unit", "from unit"])
        {
            if (clauses.TryGetValue(clause, out var unit))
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Builds one JSON object of untrusted string values, in the order given.</summary>
    private static string Element(params (string Name, string Value)[] properties) =>
        $"{{{string.Join(",", properties.Select(p => $"{JsonSerializer.Serialize(p.Name)}:{JsonSerializer.Serialize(p.Value)}"))}}}";

    /// <summary>
    /// Where a reference stops and its clauses begin. Only a clause keyword standing as its own word
    /// can start them, so a reference that merely contains one of those words (a "unit heater", say)
    /// stays part of the reference.
    /// </summary>
    private static int FindFirstClauseIndex(string reference)
    {
        var earliest = -1;

        foreach (var clause in (string[])
                 [" unit ", " in ", " unlocated", " quantity ", " note ", " to unlocated", " to unit ", " to ", " all",
                  " aliases ", " from unit "])
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
    /// <summary>
    /// Parses <c>change stock: &lt;sub&gt;; &lt;sub&gt;</c> into one batch tool call. Each
    /// sub-command is one of the six mutation verbs followed by the same reference-and-clauses shape
    /// a single-change command uses, so there is one grammar rather than two. A sub-command that is
    /// not recognized makes the whole command unrecognized: a partly understood batch is exactly what
    /// must never be proposed.
    /// </summary>
    private static bool TryProposeBatch(string content, out ModelProposal? proposal)
    {
        proposal = null;

        // "change stock: ..." is the natural way to write this, and a colon is not whitespace, so the
        // separator is folded to a space before the ordinary command match sees it.
        var normalized = content.Length > ChangeStockCommand.Length
            && content[..ChangeStockCommand.Length].Equals(ChangeStockCommand, StringComparison.OrdinalIgnoreCase)
            && content[ChangeStockCommand.Length] == ':'
                ? string.Concat(content[..ChangeStockCommand.Length], " ", content[(ChangeStockCommand.Length + 1)..])
                : content;

        if (!StartsWithCommand(normalized, ChangeStockCommand, out var remainder))
        {
            return false;
        }

        var body = remainder.StartsWith(':') ? remainder[1..].Trim() : remainder;
        if (body.Length == 0)
        {
            return false;
        }

        var elements = new List<string>();
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseSubCommand(part, out var element))
            {
                return false;
            }

            elements.Add(element!);
        }

        if (elements.Count == 0)
        {
            return false;
        }

        proposal = ModelProposal.Tool("apply_stock_changes", new Dictionary<string, string>
        {
            ["changes"] = $"[{string.Join(",", elements)}]",
        });

        return true;
    }

    /// <summary>Turns one sub-command into one JSON object of untrusted string values, using the same clause grammar.</summary>
    private static bool TryParseSubCommand(string part, out string? element)
    {
        element = null;

        foreach (var (command, toolName) in MutationCommands)
        {
            // "add stock Bolts" and "add Bolts" both read naturally inside a batch, so the bare verb
            // is accepted as well as the full command.
            // Every mutation command ends in " stock", so the bare verb is what precedes it.
            var verb = command[..^" stock".Length];

            if (!TryProposeReferenceCommand(part, command, toolName, out var proposal)
                && !TryProposeReferenceCommand(part, verb, toolName, out proposal))
            {
                continue;
            }

            var kind = toolName[..toolName.IndexOf('_')];
            var properties = proposal!.ToolCall!.UntrustedArgs
                .Select(pair => $"{JsonSerializer.Serialize(pair.Key)}:{JsonSerializer.Serialize(pair.Value)}")
                .Prepend($"\"kind\":{JsonSerializer.Serialize(kind)}");

            element = $"{{{string.Join(",", properties)}}}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses <c>confirm &lt;code&gt;</c> and <c>reject [code]</c>. The code is the only thing carried
    /// through: whether the Participant actually confirmed is decided by the application from this
    /// Turn's own direct content, never from this proposal.
    /// </summary>
    private static bool TryProposeConfirmation(string content, out ModelProposal? proposal)
    {
        proposal = null;

        foreach (var (command, toolName) in ((string, string)[])
                 [(ConfirmCommand, "confirm_inventory_operation"), (RejectCommand, "reject_inventory_operation")])
        {
            if (!StartsWithCommand(content, command, out var remainder))
            {
                continue;
            }

            var args = new Dictionary<string, string>();
            var token = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is not null)
            {
                args["token"] = token;
            }

            proposal = ModelProposal.Tool(toolName, args);
            return true;
        }

        return false;
    }
}
