using System.Text.Json;
using System.Text.Json.Serialization;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Executes list_stock/find_stock/add_stock/remove_stock/set_stock/move_stock/rename_stock/
/// forget_stock/apply_stock_changes/confirm_inventory_operation/reject_inventory_operation tool calls
/// proposed by the model boundary, always under the trusted
/// <see cref="TurnExecutionContext"/> assembled by <see cref="TurnExecutionContextFactory"/> - never
/// the proposal's own untrusted arguments, which are only ever free-form filter text (for example
/// <c>includeZero</c> or <c>reference</c>), never identity. A malicious or buggy proposal cannot widen
/// access by smuggling a Participant/Inventory id into its args: this dispatcher never reads any such
/// key from them.
/// </summary>
public sealed class StockToolDispatcher(
    StockListingService listingService,
    StockFindingService findingService,
    StockMutationService mutationService,
    StockChangeSetService changeSetService,
    StockConfirmationService confirmationService) : IToolDispatcher
{
    public const string ListStockToolName = "list_stock";
    public const string FindStockToolName = "find_stock";
    public const string AddStockToolName = "add_stock";
    public const string RemoveStockToolName = "remove_stock";
    public const string SetStockToolName = "set_stock";
    public const string MoveStockToolName = "move_stock";
    public const string RenameStockToolName = "rename_stock";
    public const string ForgetStockToolName = "forget_stock";
    public const string ApplyStockChangesToolName = "apply_stock_changes";
    public const string ConfirmToolName = "confirm_inventory_operation";
    public const string RejectToolName = "reject_inventory_operation";

    /// <summary>
    /// The channel-neutral response part every answered read leaves behind. It names the conversation
    /// itself, not any one channel: each adapter renders this same part for its own medium, and
    /// Delivery dispatch retries it independently of the processing that produced it.
    /// </summary>
    public const string ResponseChannel = "conversation";

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.ActiveInventoryId is not { } inventoryId)
        {
            // Guidance, not a failure: the Participant simply has no Inventory selected for this
            // conversation yet, and the answer tells them how to proceed.
            return Semantic(OutcomeCategory.Invalid, "no_active_inventory", "Select an Inventory in this conversation first.");
        }

        return proposal.ToolName switch
        {
            ListStockToolName => await DispatchListAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
            FindStockToolName => await DispatchFindAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
            AddStockToolName => await DispatchMutationAsync(
                Domain.Inventories.StockMutationKind.Add, proposal, context, inventoryId, now, cancellationToken),
            RemoveStockToolName => await DispatchMutationAsync(
                Domain.Inventories.StockMutationKind.Remove, proposal, context, inventoryId, now, cancellationToken),
            // A Set to zero clears stock, which #31 could only refuse because it had nowhere to put a
            // proposal. It is decided exactly like any other change, so it takes the change-set path
            // and comes back as an exact proposal; every other Set keeps the shipped single-mutation
            // path and its own ledger.
            SetStockToolName when IsClearingSet(proposal.UntrustedArgs) => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Set, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            SetStockToolName => await DispatchMutationAsync(
                Domain.Inventories.StockMutationKind.Set, proposal, context, inventoryId, now, cancellationToken),
            MoveStockToolName => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Move, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            RenameStockToolName => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Rename, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            ForgetStockToolName => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Forget, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            ApplyStockChangesToolName => await DispatchBatchAsync(proposal, context, inventoryId, now, cancellationToken),
            ConfirmToolName => await DispatchConfirmAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
            RejectToolName => await DispatchRejectAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),

            // An unrecognized tool name is the model proposing something this application cannot
            // execute - a model/system failure, not an answer to the Participant's request.
            _ => SystemFailure("unknown_tool", $"'{proposal.ToolName}' is not a recognized tool."),
        };
    }

    private async Task<ModelDecision> DispatchListAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Every value here is untrusted free-form filter text: a bad or hostile value can only ever
        // make the request narrower, malformed, or unresolvable - never wider, and never a different
        // Participant's or Inventory's data, which come from trusted context alone.
        var request = new StockListRequest
        {
            IncludeZero = ParseFlag(untrustedArgs, "includeZero"),
            UnitReference = untrustedArgs.GetValueOrDefault("unit"),
            LocationReference = untrustedArgs.GetValueOrDefault("location"),
            UnlocatedOnly = ParseFlag(untrustedArgs, "unlocated"),
            NameFilter = untrustedArgs.GetValueOrDefault("nameFilter"),
            PageSize = ParsePageSize(untrustedArgs),
            Cursor = untrustedArgs.GetValueOrDefault("cursor"),
        };

        var result = await listingService.ListAsync(
            context.ParticipantId, inventoryId, request, context.ChannelConversationId.Value, now, cancellationToken);

        return result.Kind switch
        {
            StockAccessOutcomeKind.Completed => Completed(
                "completed",
                Summarize(result.View!),
                JsonSerializer.Serialize(new StockListPayload(1, "stock_list", result.View!.Rows, result.View.NextCursor, result.View.HasMore), PayloadOptions)),
            StockAccessOutcomeKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No accessible Inventory is selected."),
            StockAccessOutcomeKind.ReferenceNotFound => Semantic(
                OutcomeCategory.NotFound,
                "reference_not_found",
                UnresolvedReferenceSummary(result.UnresolvedReference)),
            StockAccessOutcomeKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidRequestSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchFindAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var request = new StockFindRequest
        {
            Reference = untrustedArgs.GetValueOrDefault("reference"),
            UnitReference = untrustedArgs.GetValueOrDefault("unit"),
            LocationReference = untrustedArgs.GetValueOrDefault("location"),
            UnlocatedOnly = ParseFlag(untrustedArgs, "unlocated"),
        };

        var result = await findingService.FindAsync(
            context.ParticipantId, inventoryId, request, context.ChannelConversationId.Value, now, cancellationToken);

        return result.Kind switch
        {
            StockFindResultKind.Completed => Completed(
                "completed",
                $"Found {result.View!.Candidates[0].Name}.",
                JsonSerializer.Serialize(
                    new StockFindPayload(1, "stock_find", result.View.Candidates, false, NarrowingHintsPayload.None), PayloadOptions)),
            StockFindResultKind.Ambiguous => Ambiguous(
                "ambiguous",
                SummarizeAmbiguity(result.View!),
                JsonSerializer.Serialize(
                    new StockFindPayload(
                        1,
                        "stock_find",
                        result.View!.Candidates,
                        result.View.HasMoreCandidates,
                        NarrowingHintsPayload.From(result.View.NarrowingHints)),
                    PayloadOptions)),
            StockFindResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No matching Stock Entry was found."),
            StockFindResultKind.ReferenceNotFound => Semantic(
                OutcomeCategory.NotFound,
                "reference_not_found",
                UnresolvedReferenceSummary(result.UnresolvedReference)),
            StockFindResultKind.Invalid => Semantic(OutcomeCategory.Invalid, "invalid_reference", "That request could not be understood."),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchMutationAsync(
        Domain.Inventories.StockMutationKind kind,
        ToolCallProposal proposal,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var untrustedArgs = proposal.UntrustedArgs;

        // Every value here is untrusted: a name, an amount as text, exact Unit/Location references, a
        // Note. None of them is identity, and none of them can widen what this Turn is allowed to
        // touch - the Inventory and the Participant come from trusted context alone.
        var request = new StockMutationRequest
        {
            Kind = kind,
            Reference = untrustedArgs.GetValueOrDefault("reference"),
            QuantityText = untrustedArgs.GetValueOrDefault("quantity"),
            UnitReference = untrustedArgs.GetValueOrDefault("unit"),
            LocationReference = untrustedArgs.GetValueOrDefault("location"),
            UnlocatedOnly = ParseFlag(untrustedArgs, "unlocated"),
            Note = untrustedArgs.GetValueOrDefault("note"),
        };

        // The operation's identity is derived from the durably accepted Turn and the tool being
        // executed - both trusted, both stable across retries - so replaying this Turn re-reports the
        // recorded effect instead of applying a second one. Nothing the model proposes contributes to
        // it, so a hostile proposal can neither mint a fresh identity nor collide with another's.
        var operationId = Domain.Inventories.StockOperationId.Derive(context.TurnId, proposal.ToolName, sequence: 0);

        var result = await mutationService.MutateAsync(
            context.ParticipantId, inventoryId, operationId, request, context.ChannelConversationId.Value, now, cancellationToken);

        return result.Kind switch
        {
            StockMutationResultKind.Completed => Completed(
                "completed",
                SummarizeMutation(kind, result.View!),
                JsonSerializer.Serialize(
                    new StockMutationPayload(1, "stock_mutation", OperationName(kind), result.View!), PayloadOptions)),
            StockMutationResultKind.ConfirmationRequired => Semantic(
                OutcomeCategory.ConfirmationRequired,
                "confirmation_required",
                "Setting Stock to zero clears it, so it needs your explicit confirmation first."),
            StockMutationResultKind.Ambiguous => Ambiguous(
                "ambiguous",
                SummarizeAmbiguity(result.Candidates!),
                JsonSerializer.Serialize(
                    new StockFindPayload(
                        1,
                        "stock_find",
                        result.Candidates!.Candidates,
                        result.Candidates.HasMoreCandidates,
                        NarrowingHintsPayload.From(result.Candidates.NarrowingHints)),
                    PayloadOptions)),
            StockMutationResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No matching Stock Entry was found."),
            StockMutationResultKind.ReferenceNotFound => Semantic(
                OutcomeCategory.NotFound, "reference_not_found", UnresolvedReferenceSummary(result.UnresolvedReference)),
            StockMutationResultKind.Conflict => Semantic(OutcomeCategory.Conflict, result.Code, ConflictSummary(result.Code)),
            StockMutationResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidMutationSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    /// <summary>The stable machine name for the mutation a payload describes.</summary>
    private static string OperationName(Domain.Inventories.StockMutationKind kind) => kind switch
    {
        Domain.Inventories.StockMutationKind.Add => "add",
        Domain.Inventories.StockMutationKind.Remove => "remove",
        Domain.Inventories.StockMutationKind.Set => "set",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    /// <summary>
    /// The exact read-back a clear low-risk mutation owes the Participant: what changed, where, and
    /// what it now is. When a proposed Note was deliberately not applied, it says so rather than
    /// letting the Note disappear without comment.
    /// </summary>
    private static string SummarizeMutation(Domain.Inventories.StockMutationKind kind, StockMutationView view)
    {
        var placement = view.Location is null ? "unlocated" : $"in {view.Location}";
        var opening = kind switch
        {
            Domain.Inventories.StockMutationKind.Add => view.Created
                ? $"Created {view.Name} ({placement}) at {view.Quantity} {view.Unit}."
                : $"Added to {view.Name} ({placement}): now {view.Quantity} {view.Unit}.",
            Domain.Inventories.StockMutationKind.Remove => $"Removed from {view.Name} ({placement}): now {view.Quantity} {view.Unit}.",
            Domain.Inventories.StockMutationKind.Set => $"Set {view.Name} ({placement}) to {view.Quantity} {view.Unit}.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
        };

        return view.NotePreserved ? $"{opening} Its existing Note was kept unchanged." : opening;
    }

    /// <summary>Names the current-state conflict a refused mutation ran into, without disclosing anything else about it.</summary>
    private static string ConflictSummary(string code) => code switch
    {
        "insufficient_quantity" => "That is more than the Quantity on hand, so nothing was changed.",
        "state_changed" => "That Stock changed while this request was being prepared, so nothing was changed. Ask again.",
        _ => "That request conflicts with current Stock, so nothing was changed.",
    };

    /// <summary>Names the bound a rejected mutation violated, rather than only that it was rejected.</summary>
    private static string InvalidMutationSummary(string code) => code switch
    {
        "invalid_quantity" => "State a Quantity as a plain decimal number - positive for Add and Remove.",
        "quantity_out_of_bounds" =>
            $"That Quantity is larger than an Inventory can record ({Domain.Inventories.Quantity.MaxIntegerDigits} digits "
            + $"before the decimal point and {Domain.Inventories.Quantity.MaxScale} after it).",
        "invalid_name" => $"A Stock Entry name must be 1 to {Domain.Inventories.StockEntry.MaxNameLength} characters.",
        "invalid_note" => $"A Note must not exceed {Domain.Inventories.StockEntry.MaxNoteLength} characters.",
        "invalid_reference" => "Name the Stock Entry to change.",
        _ => "That request could not be understood.",
    };

    /// <summary>Names the reference that did not resolve, so the Participant can correct that one.</summary>
    private static string UnresolvedReferenceSummary(StockReferenceKind? reference) => reference switch
    {
        StockReferenceKind.Unit => "That Unit does not exist in this Inventory.",
        StockReferenceKind.Location => "That Location does not exist in this Inventory.",
        _ => "That Unit or Location does not exist in this Inventory.",
    };

    /// <summary>Names the bound the request violated, rather than only that it was rejected.</summary>
    private static string InvalidRequestSummary(string code) => code switch
    {
        "invalid_page_size" => $"Ask for between 1 and {Domain.Inventories.StockListQuery.MaxPageSize} Stock Entries at a time.",
        "invalid_cursor" => "That page marker belongs to a different request; start the list again.",
        "invalid_location_filter" => "Ask for a Location or for unlocated Stock, not both.",
        _ => "That request could not be understood.",
    };

    /// <summary>Reads an untrusted boolean flag; anything that is not an explicit "true" is simply absent.</summary>
    private static bool ParseFlag(IReadOnlyDictionary<string, string> untrustedArgs, string key) =>
        untrustedArgs.TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) && parsed;

    /// <summary>
    /// Reads an untrusted page size. An unparseable value is treated as "not asked for" (the bounded
    /// default applies); a parseable but out-of-range one is passed through so the request is
    /// answered as invalid rather than silently widened or narrowed.
    /// </summary>
    private static int? ParsePageSize(IReadOnlyDictionary<string, string> untrustedArgs) =>
        untrustedArgs.TryGetValue("pageSize", out var raw) && int.TryParse(raw, out var parsed) ? parsed : null;

    /// <summary>
    /// States exactly what was matched and shown, never more. When more matched than the cap allows,
    /// the wording says so instead of implying the shown candidates are all of them, and it offers
    /// the narrowing the matches genuinely differ by.
    /// </summary>
    private static string SummarizeAmbiguity(StockFindView view)
    {
        var shown = view.Candidates.Count;
        var opening = view.HasMoreCandidates
            ? $"More than {shown} Stock Entries match; showing the first {shown}."
            : $"{shown} Stock Entries match.";

        var narrowings = new List<string>();
        if (view.NarrowingHints.Units.Count > 0)
        {
            narrowings.Add($"unit ({string.Join(", ", view.NarrowingHints.Units)})");
        }

        if (view.NarrowingHints.Locations.Count > 0)
        {
            narrowings.Add($"location ({string.Join(", ", view.NarrowingHints.Locations)})");
        }

        if (view.NarrowingHints.IncludesUnlocated)
        {
            narrowings.Add("unlocated stock");
        }

        return narrowings.Count == 0
            ? $"{opening} Choose one."
            : $"{opening} Narrow by {string.Join(" or ", narrowings)}.";
    }

    private static string Summarize(StockListView view) => view.Rows.Count switch
    {
        0 => "No Stock Entries found.",
        1 => "1 Stock Entry found.",
        var n => $"{n} Stock Entries found.",
    };

    private static ModelDecision Completed(string code, string summary, string payload) =>
        Answered(OutcomeCategory.Completed, code, summary, payload);

    private static ModelDecision Ambiguous(string code, string summary, string payload) =>
        Answered(OutcomeCategory.Ambiguous, code, summary, payload);

    private static ModelDecision Answered(OutcomeCategory category, string code, string summary, string payload) => new()
    {
        Category = category,
        Code = code,
        Summary = summary,
        Payload = payload,

        // The typed payload is the answer's channel-neutral content; the summary alone would lose the
        // rows/candidates the Participant asked for.
        Deliveries = [new RequestedDelivery(ResponseChannel, payload)],
    };

    // A deterministic domain answer the Participant asked for - "nothing matched", "you may not see
    // that", "that could not be understood" - is a completed piece of processing carrying its own
    // semantic category, never the system reporting that it failed.
    private static ModelDecision Semantic(OutcomeCategory category, string code, string summary) => new()
    {
        Category = category,
        Code = code,
        Summary = summary,

        // A semantic answer is still an answer the Participant is owed, so it leaves the same durable
        // response part behind - here the summary is the whole answer.
        Deliveries = [new RequestedDelivery(ResponseChannel, summary)],
    };

    // A failure produced no answer, so there is nothing to deliver and no response part is requested.
    // The Turn is still finished: this decision is recorded as its terminal transient_failure Outcome
    // and its inbox entry is completed in the same atomic write, so nothing reprocesses it - the
    // recorded Outcome is what a caller reads, and asking again is a new Turn. (A Turn only stays
    // pending for a later pass when recording the result itself fails, which is a different case
    // entirely: nothing durable was written at all.)
    private static ModelDecision SystemFailure(string code, string summary) => new()
    {
        Category = OutcomeCategory.TransientFailure,
        Code = code,
        Summary = summary,
    };

    private sealed record StockListPayload(int Version, string Kind, IReadOnlyList<StockRowView> Rows, string? NextCursor, bool HasMore);

    /// <summary>The typed read-back one applied mutation leaves behind, versioned like every other payload.</summary>
    private sealed record StockMutationPayload(int Version, string Kind, string Operation, StockMutationView Entry);

    private sealed record StockFindPayload(
        int Version,
        string Kind,
        IReadOnlyList<StockRowView> Candidates,
        bool HasMoreCandidates,
        NarrowingHintsPayload NarrowingHints);

    /// <summary>The narrowing a client can offer as choices; empty lists mean "nothing here would change the answer".</summary>
    private sealed record NarrowingHintsPayload(IReadOnlyList<string> Units, IReadOnlyList<string> Locations, bool IncludesUnlocated)
    {
        public static readonly NarrowingHintsPayload None = new([], [], false);

        public static NarrowingHintsPayload From(StockNarrowingHints hints) =>
            new(hints.Units, hints.Locations, hints.IncludesUnlocated);
    }
    /// <summary>
    /// Whether this Set asks for zero - the one Set that ends up with an empty Stock Entry and so has
    /// to be confirmed. An unparseable amount is not a clearing Set: it is a malformed one, and the
    /// shipped path already answers it exactly.
    /// </summary>
    private static bool IsClearingSet(IReadOnlyDictionary<string, string> untrustedArgs) =>
        Domain.Inventories.Quantity.TryParseInvariant(untrustedArgs.GetValueOrDefault("quantity"), out var amount)
        && !amount.IsOnHand;

    /// <summary>
    /// Reads one change from a single-change tool's untrusted arguments. Every value is untrusted
    /// text - a name, an amount, exact Unit/Location references, a destination, a new name. None of
    /// them is identity, and none can widen what this Turn may touch.
    /// </summary>
    private static IReadOnlyList<StockChangeRequest> SingleChange(
        Domain.Inventories.StockMutationKind kind, IReadOnlyDictionary<string, string> untrustedArgs) =>
    [
        new StockChangeRequest
        {
            Order = 1,
            Kind = kind,
            Reference = untrustedArgs.GetValueOrDefault("reference"),
            QuantityText = untrustedArgs.GetValueOrDefault("quantity"),
            MoveAll = ParseFlag(untrustedArgs, "all"),
            UnitReference = untrustedArgs.GetValueOrDefault("unit"),
            LocationReference = untrustedArgs.GetValueOrDefault("location"),
            UnlocatedOnly = ParseFlag(untrustedArgs, "unlocated"),
            DestinationLocationReference = untrustedArgs.GetValueOrDefault("to"),
            DestinationUnlocated = ParseFlag(untrustedArgs, "toUnlocated"),
            NewName = untrustedArgs.GetValueOrDefault("newName"),
            Note = untrustedArgs.GetValueOrDefault("note"),
        },
    ];

    private async Task<ModelDecision> DispatchBatchAsync(
        ToolCallProposal proposal,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!StockChangeSetParser.TryParse(proposal.UntrustedArgs.GetValueOrDefault("changes"), out var requests, out var code))
        {
            return Semantic(OutcomeCategory.Invalid, code, InvalidChangeSetSummary(code));
        }

        return await DispatchChangeSetAsync(requests, proposal, context, inventoryId, now, cancellationToken);
    }

    private async Task<ModelDecision> DispatchChangeSetAsync(
        IReadOnlyList<StockChangeRequest> requests,
        ToolCallProposal proposal,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Derived from the durably accepted Turn and the tool being executed - both trusted, both
        // stable across retries - so replaying this Turn re-reports the recorded effect instead of
        // applying a second one. Nothing the model proposes contributes to it.
        var operationId = Domain.Inventories.StockOperationId.Derive(context.TurnId, proposal.ToolName, sequence: 0);

        var result = await changeSetService.ApplyAsync(
            context.ParticipantId,
            inventoryId,
            context.TurnId,
            operationId,
            requests,
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return result.Kind switch
        {
            StockChangeSetResultKind.Completed => Completed(
                "completed",
                SummarizeChanges(result.Applied!),
                JsonSerializer.Serialize(new StockChangesPayload(1, "stock_changes", result.Applied!.Changes), PayloadOptions)),
            StockChangeSetResultKind.ConfirmationRequired => ConfirmationRequired(result.Proposal!),
            StockChangeSetResultKind.Ambiguous => Ambiguous(
                "ambiguous",
                SummarizeAmbiguity(result.Candidates!),
                JsonSerializer.Serialize(
                    new StockFindPayload(
                        1,
                        "stock_find",
                        result.Candidates!.Candidates,
                        result.Candidates.HasMoreCandidates,
                        NarrowingHintsPayload.From(result.Candidates.NarrowingHints)),
                    PayloadOptions)),
            StockChangeSetResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No matching Stock Entry was found."),
            StockChangeSetResultKind.ReferenceNotFound => Semantic(
                OutcomeCategory.NotFound, "reference_not_found", UnresolvedReferenceSummary(result.UnresolvedReference)),
            StockChangeSetResultKind.Conflict => Semantic(OutcomeCategory.Conflict, result.Code, ChangeSetConflictSummary(result.Code)),
            StockChangeSetResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidChangeSetSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchConfirmAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The token is the only thing the model may supply here. Whether the Participant actually
        // confirmed comes from trusted context, derived from their own direct content in this Turn -
        // so a tool call the model invented on its own approves nothing.
        var result = await confirmationService.ConfirmAsync(
            context.ParticipantId,
            inventoryId,
            context.TurnId,
            untrustedArgs.GetValueOrDefault("token"),
            context.Confirmation,
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return ToDecision(result);
    }

    private async Task<ModelDecision> DispatchRejectAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await confirmationService.RejectAsync(
            context.ParticipantId,
            inventoryId,
            context.TurnId,
            untrustedArgs.GetValueOrDefault("token"),
            context.Confirmation,
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return ToDecision(result);
    }

    private static ModelDecision ToDecision(StockConfirmationResult result) => result.Kind switch
    {
        StockConfirmationResultKind.Completed => Completed(
            "completed",
            SummarizeChanges(result.Applied!),
            JsonSerializer.Serialize(new StockChangesPayload(1, "stock_changes", result.Applied!.Changes), PayloadOptions)),
        StockConfirmationResultKind.Rejected => Semantic(
            OutcomeCategory.Completed, "rejected", "That change was not made, and nothing was changed."),
        StockConfirmationResultKind.NotFound => Semantic(
            OutcomeCategory.NotFound, result.Code, "There is nothing waiting for your confirmation here."),
        StockConfirmationResultKind.Conflict => Semantic(OutcomeCategory.Conflict, result.Code, ConfirmationConflictSummary(result.Code)),
        StockConfirmationResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidConfirmationSummary(result.Code)),
        _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
    };

    private static ModelDecision ConfirmationRequired(StockProposalView proposal)
    {
        var payload = JsonSerializer.Serialize(
            new StockProposalPayload(1, "stock_proposal", proposal.Token, proposal.ExpiresAt, proposal.Changes), PayloadOptions);

        return new ModelDecision
        {
            Category = OutcomeCategory.ConfirmationRequired,
            Code = "confirmation_required",
            Summary = SummarizeProposal(proposal),
            Payload = payload,

            // The proposal is the answer's channel-neutral content: the summary alone would lose the
            // exact effects the Participant is being asked to approve.
            Deliveries = [new RequestedDelivery(ResponseChannel, payload)],
        };
    }

    /// <summary>States exactly what will happen and what it costs in identity, then asks. It names no Inventory and no other Participant.</summary>
    private static string SummarizeProposal(StockProposalView proposal)
    {
        var lines = proposal.Changes.Select(DescribeChange).ToList();
        var opening = lines.Count == 1
            ? $"This needs your confirmation: {lines[0]}"
            : $"These {lines.Count} changes apply together, or not at all: {string.Join(" ", lines)}";

        return $"{opening} Reply with \"confirm {proposal.Token}\" to apply it, or \"reject\" to leave everything as it is.";
    }

    private static string SummarizeChanges(StockChangeSetView applied) =>
        string.Join(" ", applied.Changes.Select(DescribeChange));

    /// <summary>One change in plain words, always naming the surviving Stock Entry and any identity that ends.</summary>
    private static string DescribeChange(StockChangeView change)
    {
        var placement = change.Source.Location is null ? "unlocated" : $"in {change.Source.Location}";
        var destination = change.Destination?.Location is null ? "unlocated" : $"in {change.Destination.Location}";

        return change.Effect switch
        {
            "created" => $"Create {change.Source.Name} ({placement}) at {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_increased" => $"Add to {change.Source.Name} ({placement}): {change.Source.PreviousQuantity} becomes {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_decreased" => $"Remove from {change.Source.Name} ({placement}): {change.Source.PreviousQuantity} becomes {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_set" => $"Set {change.Source.Name} ({placement}) to {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_cleared" => $"Clear {change.Source.Name} ({placement}), leaving 0 {change.Source.Unit} and an empty record.",
            "placed" => $"Move all {change.Source.Quantity} {change.Source.Unit} of {change.Source.Name} from {placement} to {destination}.",
            "split" => $"Move {change.TransferredQuantity} {change.Source.Unit} of {change.Source.Name} from {placement} to {destination}, leaving {change.Source.Quantity}.",
            "split_merged" => $"Move {change.TransferredQuantity} {change.Source.Unit} of {change.Source.Name} into the {change.Destination!.PreviousQuantity} already {destination}, leaving {change.Source.Quantity}.",
            "merged" => $"Merge all {change.TransferredQuantity} {change.Source.Unit} of {change.Source.Name} from {placement} into the entry {destination}, which becomes {change.Destination!.Quantity}. The {placement} entry is retired.",
            "renamed" => $"Rename {change.Source.Name} ({placement}) to {change.NewName ?? change.Source.Name}.",
            "rename_merged" => $"Merge {change.Source.Name} ({placement}) into {change.Destination!.Name}, which becomes {change.Destination.Quantity} {change.Destination.Unit}. The {change.Source.Name} entry is retired.",
            "forgotten" => $"Forget the empty {change.Source.Name} ({placement}) entry permanently.",
            _ => $"Change {change.Source.Name} ({placement}).",
        };
    }

    private static string ChangeSetConflictSummary(string code) => code switch
    {
        "insufficient_quantity" => "That is more than the Quantity on hand, so nothing was changed.",
        "forget_requires_zero_quantity" => "Only an empty Stock Entry can be forgotten. Remove or Set its Quantity to zero first.",
        "no_change" => "That would leave everything exactly as it is, so nothing was changed.",
        "state_changed" => "That Stock changed while this request was being prepared, so nothing was changed. Ask again.",
        _ => "That request conflicts with current Stock, so nothing was changed.",
    };

    private static string ConfirmationConflictSummary(string code) => code switch
    {
        "proposal_expired" => "That confirmation is older than ten minutes, so nothing was changed. Ask again to get a fresh one.",
        "state_changed" => "That Stock changed since this was proposed, so nothing was changed. Ask again.",
        _ => "That could no longer be applied, so nothing was changed.",
    };

    private static string InvalidConfirmationSummary(string code) => code switch
    {
        "confirmation_evidence_missing" => "Confirm in your own words - for example \"confirm\" followed by the code - and nothing else will do it for you.",
        "rejection_evidence_missing" => "Say so in your own words - for example \"reject\" - to leave everything as it is.",
        "proposal_token_mismatch" => "That confirmation code does not match what is waiting here, so nothing was changed.",
        _ => "That request could not be understood.",
    };

    private static string InvalidChangeSetSummary(string code) => code switch
    {
        "invalid_changes" => "State each change plainly - what to change, and by how much.",
        "too_many_changes" => $"Ask for at most {Domain.Inventories.ConfirmationProposal.MaxChanges} changes at a time.",
        "conflicting_changes" => "Two of those changes act on the same Stock Entry. Ask for them one at a time.",
        "invalid_destination" => "Name one destination: either a Location, or unlocated stock.",
        "invalid_quantity" => "State a Quantity as a plain decimal number, or ask to move all of it - not both.",
        "invalid_name" => $"A Stock Entry name must be 1 to {Domain.Inventories.StockEntry.MaxNameLength} characters.",
        "invalid_note" => $"A Note must not exceed {Domain.Inventories.StockEntry.MaxNoteLength} characters.",
        "invalid_reference" => "Name the Stock Entry to change.",
        "quantity_out_of_bounds" =>
            $"That Quantity is larger than an Inventory can record ({Domain.Inventories.Quantity.MaxIntegerDigits} digits "
            + $"before the decimal point and {Domain.Inventories.Quantity.MaxScale} after it).",
        _ => "That request could not be understood.",
    };

    /// <summary>The exact proposal a Participant is being asked to approve, versioned like every other payload.</summary>
    private sealed record StockProposalPayload(
        int Version, string Kind, string Token, string ExpiresAt, IReadOnlyList<StockChangeView> Changes);

    /// <summary>The typed read-back one applied change set leaves behind.</summary>
    private sealed record StockChangesPayload(int Version, string Kind, IReadOnlyList<StockChangeView> Changes);
}
