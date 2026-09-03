using System.Text.Json;
using System.Text.Json.Serialization;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Executes list_stock/find_stock tool calls proposed by the model boundary, always under the trusted
/// <see cref="TurnExecutionContext"/> assembled by <see cref="TurnExecutionContextFactory"/> - never
/// the proposal's own untrusted arguments, which are only ever free-form filter text (for example
/// <c>includeZero</c> or <c>reference</c>), never identity. A malicious or buggy proposal cannot widen
/// access by smuggling a Participant/Inventory id into its args: this dispatcher never reads any such
/// key from them.
/// </summary>
public sealed class StockToolDispatcher(StockListingService listingService, StockFindingService findingService) : IToolDispatcher
{
    public const string ListStockToolName = "list_stock";
    public const string FindStockToolName = "find_stock";

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

    // A failure has produced no answer yet, and the Turn stays retryable, so it deliberately leaves
    // no response part: sending one would tell the Participant something a later retry contradicts.
    private static ModelDecision SystemFailure(string code, string summary) => new()
    {
        Category = OutcomeCategory.TransientFailure,
        Code = code,
        Summary = summary,
    };

    private sealed record StockListPayload(int Version, string Kind, IReadOnlyList<StockRowView> Rows, string? NextCursor, bool HasMore);

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
}
