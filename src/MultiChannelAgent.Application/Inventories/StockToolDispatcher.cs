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

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (context.ActiveInventoryId is not { } inventoryId)
        {
            return Failed("no_active_inventory", "Select an Inventory in this conversation first.");
        }

        return proposal.ToolName switch
        {
            ListStockToolName => await DispatchListAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
            FindStockToolName => await DispatchFindAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
            _ => Failed("unknown_tool", $"'{proposal.ToolName}' is not a recognized tool."),
        };
    }

    private async Task<ModelDecision> DispatchListAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var includeZero = untrustedArgs.TryGetValue("includeZero", out var rawIncludeZero) && bool.TryParse(rawIncludeZero, out var parsed) && parsed;
        var nameFilter = untrustedArgs.GetValueOrDefault("nameFilter");
        var cursor = untrustedArgs.GetValueOrDefault("cursor");

        var result = await listingService.ListAsync(
            context.ParticipantId, inventoryId, includeZero, locationId: null, nameFilter, pageSize: null, cursor,
            context.ChannelConversationId.Value, now, cancellationToken);

        return result.Kind switch
        {
            StockAccessOutcomeKind.Completed => Completed(
                "completed",
                Summarize(result.View!),
                JsonSerializer.Serialize(new StockListPayload(1, "stock_list", result.View!.Rows, result.View.NextCursor, result.View.HasMore), PayloadOptions)),
            StockAccessOutcomeKind.NotFound => Failed("not_found", "No accessible Inventory is selected."),
            StockAccessOutcomeKind.Invalid => Failed("invalid_query", "That request could not be understood."),
            _ => Failed("forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchFindAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reference = untrustedArgs.GetValueOrDefault("reference");

        var result = await findingService.FindAsync(
            context.ParticipantId, inventoryId, reference, context.ChannelConversationId.Value, now, cancellationToken);

        return result.Kind switch
        {
            StockFindResultKind.Completed => Completed(
                "completed",
                $"Found {result.View!.Candidates[0].Name}.",
                JsonSerializer.Serialize(new StockFindPayload(1, "stock_find", result.View.Candidates, false), PayloadOptions)),
            StockFindResultKind.Ambiguous => Completed(
                "ambiguous",
                $"{result.View!.Candidates.Count} Stock Entries matched; narrow your request.",
                JsonSerializer.Serialize(new StockFindPayload(1, "stock_find", result.View.Candidates, result.View.HasMoreCandidates), PayloadOptions)),
            StockFindResultKind.NotFound => Failed("not_found", "No matching Stock Entry was found."),
            StockFindResultKind.Invalid => Failed("invalid_reference", "That request could not be understood."),
            _ => Failed("forbidden", "That request could not be completed."),
        };
    }

    private static string Summarize(StockListView view) => view.Rows.Count switch
    {
        0 => "No Stock Entries found.",
        1 => "1 Stock Entry found.",
        var n => $"{n} Stock Entries found.",
    };

    private static ModelDecision Completed(string code, string summary, string payload) => new()
    {
        Status = OutcomeStatus.Completed,
        Code = code,
        Summary = summary,
        Payload = payload,
    };

    private static ModelDecision Failed(string code, string summary) => new()
    {
        Status = OutcomeStatus.Failed,
        Code = code,
        Summary = summary,
    };

    private sealed record StockListPayload(int Version, string Kind, IReadOnlyList<StockRowView> Rows, string? NextCursor, bool HasMore);

    private sealed record StockFindPayload(int Version, string Kind, IReadOnlyList<StockRowView> Candidates, bool HasMoreCandidates);
}
