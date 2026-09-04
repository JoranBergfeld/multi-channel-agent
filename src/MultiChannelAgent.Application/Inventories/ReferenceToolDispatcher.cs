using System.Text.Json;
using System.Text.Json.Serialization;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Executes the ten Unit and Location administration tool calls the specification names -
/// list_units/create_units/rename_units/add_unit_aliases/remove_unit_aliases/retire_units and
/// list_locations/create_locations/rename_locations/retire_locations - always under the trusted
/// <see cref="TurnExecutionContext"/>, never the proposal's own untrusted arguments.
///
/// Those arguments are only ever a bounded page size, an opaque cursor, and one <c>changes</c> array
/// whose element shape is fixed by the tool that was called. None of them is identity: the
/// Participant, the Inventory, the conversation, the Turn, and the role all come from trusted context
/// alone, so a malicious or buggy proposal cannot widen what a Turn may touch.
/// </summary>
public sealed class ReferenceToolDispatcher(
    ReferenceListingService listingService, ReferenceAdministrationService administrationService) : IToolDispatcher
{
    public const string ListUnitsToolName = "list_units";
    public const string CreateUnitsToolName = "create_units";
    public const string RenameUnitsToolName = "rename_units";
    public const string AddUnitAliasesToolName = "add_unit_aliases";
    public const string RemoveUnitAliasesToolName = "remove_unit_aliases";
    public const string RetireUnitsToolName = "retire_units";
    public const string ListLocationsToolName = "list_locations";
    public const string CreateLocationsToolName = "create_locations";
    public const string RenameLocationsToolName = "rename_locations";
    public const string RetireLocationsToolName = "retire_locations";

    /// <summary>The exact ten tools this dispatcher owns, in the order the specification lists them.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
    [
        ListUnitsToolName,
        CreateUnitsToolName,
        RenameUnitsToolName,
        AddUnitAliasesToolName,
        RemoveUnitAliasesToolName,
        RetireUnitsToolName,
        ListLocationsToolName,
        CreateLocationsToolName,
        RenameLocationsToolName,
        RetireLocationsToolName,
    ];

    /// <summary>The one mutating tool name to change kind mapping. The tool fixes the kind, which is what makes a change array homogeneous by construction.</summary>
    private static readonly Dictionary<string, ReferenceChangeKind> MutatingTools = new(StringComparer.Ordinal)
    {
        [CreateUnitsToolName] = ReferenceChangeKind.CreateUnit,
        [RenameUnitsToolName] = ReferenceChangeKind.RenameUnit,
        [AddUnitAliasesToolName] = ReferenceChangeKind.AddUnitAlias,
        [RemoveUnitAliasesToolName] = ReferenceChangeKind.RemoveUnitAlias,
        [RetireUnitsToolName] = ReferenceChangeKind.RetireUnit,
        [CreateLocationsToolName] = ReferenceChangeKind.CreateLocation,
        [RenameLocationsToolName] = ReferenceChangeKind.RenameLocation,
        [RetireLocationsToolName] = ReferenceChangeKind.RetireLocation,
    };

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActiveInventoryId is not { } inventoryId)
        {
            // Guidance, not a failure: the Participant simply has no Inventory selected for this
            // conversation yet, and the answer tells them how to proceed.
            return Semantic(OutcomeCategory.Invalid, "no_active_inventory", "Select an Inventory in this conversation first.");
        }

        if (proposal.ToolName == ListUnitsToolName)
        {
            return await DispatchListUnitsAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken);
        }

        if (proposal.ToolName == ListLocationsToolName)
        {
            return await DispatchListLocationsAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken);
        }

        if (MutatingTools.TryGetValue(proposal.ToolName, out var kind))
        {
            return await DispatchChangesAsync(kind, proposal, context, inventoryId, now, cancellationToken);
        }

        // Unreachable through the router, which only sends names in ToolNames; kept so this dispatcher
        // is total over its own input rather than throwing on one.
        return SystemFailure("unknown_tool", $"'{proposal.ToolName}' is not a recognized tool.");
    }

    private async Task<ModelDecision> DispatchListUnitsAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await listingService.ListUnitsAsync(
            context.ParticipantId,
            inventoryId,
            ParsePageSize(untrustedArgs),
            untrustedArgs.GetValueOrDefault("cursor"),
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return result.Kind switch
        {
            ReferenceListResultKind.Completed => Answered(
                OutcomeCategory.Completed,
                "completed",
                SummarizeUnits(result.View!),
                JsonSerializer.Serialize(
                    new UnitListPayload(1, "unit_list", result.View!.Units, result.View.NextCursor, result.View.HasMore),
                    PayloadOptions)),
            ReferenceListResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No accessible Inventory is selected."),
            ReferenceListResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidListSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchListLocationsAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await listingService.ListLocationsAsync(
            context.ParticipantId,
            inventoryId,
            ParsePageSize(untrustedArgs),
            untrustedArgs.GetValueOrDefault("cursor"),
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return result.Kind switch
        {
            ReferenceListResultKind.Completed => Answered(
                OutcomeCategory.Completed,
                "completed",
                SummarizeLocations(result.View!),
                JsonSerializer.Serialize(
                    new LocationListPayload(1, "location_list", result.View!.Locations, result.View.NextCursor, result.View.HasMore),
                    PayloadOptions)),
            ReferenceListResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No accessible Inventory is selected."),
            ReferenceListResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidListSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchChangesAsync(
        ReferenceChangeKind kind,
        ToolCallProposal proposal,
        TurnExecutionContext context,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ReferenceChangeSetParser.TryParse(kind, proposal.UntrustedArgs.GetValueOrDefault("changes"), out var requests, out var code))
        {
            return Semantic(OutcomeCategory.Invalid, code, InvalidChangesSummary(code));
        }

        // Derived from the durably accepted Turn and the tool being executed - both trusted, both
        // stable across retries - so replaying this Turn re-reports the recorded effect instead of
        // applying a second one. Nothing the model proposes contributes to it.
        var operationId = ReferenceOperationId.Derive(context.TurnId, proposal.ToolName, sequence: 0);

        var result = await administrationService.ApplyAsync(
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
            ReferenceAdministrationResultKind.Completed => AppliedChanges(result.Applied!),
            ReferenceAdministrationResultKind.ConfirmationRequired => ConfirmationRequired(result.Proposal!),
            ReferenceAdministrationResultKind.ReferenceNotFound => ReferenceNotFound(result),
            ReferenceAdministrationResultKind.NotFound => Semantic(
                OutcomeCategory.NotFound, result.Code, NotFoundSummary(result.Code)),
            ReferenceAdministrationResultKind.Conflict => Semantic(
                OutcomeCategory.Conflict, result.Code, ConflictSummary(result.Code)),
            ReferenceAdministrationResultKind.Invalid => Semantic(
                OutcomeCategory.Invalid, result.Code, InvalidChangesSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    /// <summary>
    /// The typed read-back one applied administration change set leaves behind. Shared with
    /// <see cref="StockToolDispatcher"/>, which reaches it when a confirmation it dispatched turns
    /// out to have executed an administration proposal - so the reference vocabulary is shaped in
    /// exactly one place.
    /// </summary>
    internal static ModelDecision AppliedChanges(ReferenceChangeSetView applied) => Answered(
        OutcomeCategory.Completed,
        "completed",
        SummarizeChanges(applied.Changes),
        JsonSerializer.Serialize(new ReferenceChangesPayload(1, "reference_changes", applied.Changes), PayloadOptions));

    private static ModelDecision ConfirmationRequired(ReferenceProposalView proposal)
    {
        var payload = JsonSerializer.Serialize(
            new ReferenceProposalPayload(1, "reference_proposal", proposal.Token, proposal.ExpiresAt, proposal.Changes),
            PayloadOptions);

        return new ModelDecision
        {
            Category = OutcomeCategory.ConfirmationRequired,
            Code = "confirmation_required",
            Summary = SummarizeProposal(proposal),
            Payload = payload,

            // The token is a bearer secret with a ten-minute life, retained for exactly that window
            // rather than the ordinary payload retention - once the proposal expires the token means
            // nothing, and keeping it readable for a day would buy nothing.
            PayloadRetention = TimeSpan.FromMinutes(ConfirmationProposal.LifetimeMinutes),
            Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, payload)],
        };
    }

    private static ModelDecision ReferenceNotFound(ReferenceAdministrationResult result)
    {
        var noun = result.UnresolvedReference == ReferenceKind.Location ? "Location" : "Unit";
        var suggestions = result.Suggestions ?? [];

        var summary = suggestions.Count == 0
            ? $"That {noun} does not exist in this Inventory."
            : $"That {noun} does not exist in this Inventory. This Inventory has {string.Join(", ", suggestions)}.";

        return Answered(
            OutcomeCategory.NotFound,
            "reference_not_found",
            summary,
            JsonSerializer.Serialize(
                new ReferenceSuggestionsPayload(
                    1, "reference_suggestions", noun.ToLowerInvariant(), suggestions),
                PayloadOptions));
    }

    /// <summary>
    /// States exactly what will happen and what it costs, then asks. It names no Inventory and no
    /// other Participant - and deliberately no token: the payload beside it carries the code, and
    /// repeating a bearer secret into the Outcome's permanent summary column would keep it long after
    /// the payload it belongs to has been discarded.
    /// </summary>
    private static string SummarizeProposal(ReferenceProposalView proposal)
    {
        var lines = proposal.Changes.Select(DescribeChange).ToList();
        var opening = lines.Count == 1
            ? $"This needs your confirmation: {lines[0]}"
            : $"These {lines.Count} changes apply together, or not at all: {string.Join(" ", lines)}";

        return $"{opening} Reply with \"confirm\" followed by the confirmation code shown with this "
            + "answer to apply it, or \"reject\" to leave everything as it is.";
    }

    private static string SummarizeChanges(IReadOnlyList<ReferenceChangeView> changes) =>
        string.Join(" ", changes.Select(DescribeChange));

    /// <summary>One administration change in plain words, always naming what it acts on and what it leaves behind.</summary>
    private static string DescribeChange(ReferenceChangeView change) => change.Operation switch
    {
        "create_unit" => change.Aliases.Count == 0
            ? $"Create the Unit {change.Name}."
            : $"Create the Unit {change.Name} with the aliases {string.Join(", ", change.Aliases)}.",
        "rename_unit" => $"Rename the Unit {change.Name} to {change.NewName}. Stock keeps its Unit and its Quantity.",
        "add_unit_alias" => $"Let {change.Alias} also mean the Unit {change.Name}.",
        "remove_unit_alias" => $"Stop {change.Alias} meaning the Unit {change.Name}.",
        "retire_unit" => $"Retire the Unit {change.Name}. It stops being usable, and no Stock is changed.",
        "create_location" => $"Create the Location {change.Name}.",
        "rename_location" => $"Rename the Location {change.Name} to {change.NewName}. Stock stays exactly where it is.",
        "retire_location" => $"Retire the Location {change.Name}. It stops being usable, and no Stock is changed.",
        _ => $"Change {change.Name}.",
    };

    private static string SummarizeUnits(UnitListView view) => view.Units.Count switch
    {
        0 => "No Units found.",
        1 => "1 Unit found.",
        var n => view.HasMore ? $"{n} Units shown; more remain." : $"{n} Units found.",
    };

    private static string SummarizeLocations(LocationListView view) => view.Locations.Count switch
    {
        0 => "No Locations found.",
        1 => "1 Location found.",
        var n => view.HasMore ? $"{n} Locations shown; more remain." : $"{n} Locations found.",
    };

    /// <summary>Names the current-state conflict a refused change ran into, without disclosing anything else about it.</summary>
    private static string ConflictSummary(string code) => code switch
    {
        "term_in_use" => "That name or alias already means something else in this Inventory, so nothing was changed.",
        "name_in_use" => "A Location here already has that name, so nothing was changed.",
        "reserved_unit" => "The reserved each Unit cannot be renamed or retired.",
        "reserved_term" => "That is one of the reserved each Unit's fixed aliases, so it cannot be removed.",
        "canonical_term" => "That is the Unit's own name, not one of its aliases, so it cannot be removed as one.",
        "reference_in_use" => "Stock still uses that, so it cannot be retired. Move or remove that Stock first.",
        "no_change" => "That would leave everything exactly as it is, so nothing was changed.",
        "state_changed" => "That changed while this request was being prepared, so nothing was changed. Ask again.",
        _ => "That request conflicts with this Inventory's reference data, so nothing was changed.",
    };

    private static string NotFoundSummary(string code) => code switch
    {
        "alias_not_found" => "That Unit does not have that alias, so there was nothing to remove.",
        _ => "No accessible Inventory is selected.",
    };

    /// <summary>Names the bound a rejected request violated, rather than only that it was rejected.</summary>
    private static string InvalidChangesSummary(string code) => code switch
    {
        "invalid_changes" => "State each change plainly - what to create, rename, alias, or retire.",
        "too_many_changes" => $"Ask for at most {ConfirmationProposal.MaxChanges} changes at a time.",
        "conflicting_changes" => "Two of those changes act on the same Unit or Location, or ask for the same name. Ask for them one at a time.",
        "invalid_name" =>
            $"A Unit name or alias must be 1 to {Unit.MaxNameLength} characters, and a Location name 1 to {Location.MaxNameLength}.",
        "invalid_reference" => "Name the Unit or Location to change.",
        _ => "That request could not be understood.",
    };

    private static string InvalidListSummary(string code) => code switch
    {
        "invalid_page_size" => $"Ask for between 1 and {ReferenceListQuery.MaxPageSize} at a time.",
        "invalid_cursor" => "That page marker belongs to a different request; start the list again.",
        _ => "That request could not be understood.",
    };

    /// <summary>
    /// Reads an untrusted page size. An unparseable value is treated as "not asked for" (the bounded
    /// default applies); a parseable but out-of-range one is passed through so the request is
    /// answered as invalid rather than silently widened or narrowed.
    /// </summary>
    private static int? ParsePageSize(IReadOnlyDictionary<string, string> untrustedArgs) =>
        untrustedArgs.TryGetValue("pageSize", out var raw) && int.TryParse(raw, out var parsed) ? parsed : null;

    private static ModelDecision Answered(OutcomeCategory category, string code, string summary, string payload) => new()
    {
        Category = category,
        Code = code,
        Summary = summary,
        Payload = payload,
        Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, payload)],
    };

    private static ModelDecision Semantic(OutcomeCategory category, string code, string summary) => new()
    {
        Category = category,
        Code = code,
        Summary = summary,
        Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, summary)],
    };

    private static ModelDecision SystemFailure(string code, string summary) => new()
    {
        Category = OutcomeCategory.TransientFailure,
        Code = code,
        Summary = summary,
    };

    private sealed record UnitListPayload(
        int Version, string Kind, IReadOnlyList<UnitView> Units, string? NextCursor, bool HasMore);

    private sealed record LocationListPayload(
        int Version, string Kind, IReadOnlyList<LocationView> Locations, string? NextCursor, bool HasMore);

    private sealed record ReferenceChangesPayload(int Version, string Kind, IReadOnlyList<ReferenceChangeView> Changes);

    private sealed record ReferenceProposalPayload(
        int Version, string Kind, string Token, string ExpiresAt, IReadOnlyList<ReferenceChangeView> Changes);

    /// <summary>The bounded deterministic alternatives an unknown reference offers. Never a nearest-match guess.</summary>
    private sealed record ReferenceSuggestionsPayload(
        int Version, string Kind, string Reference, IReadOnlyList<string> Suggestions);
}
