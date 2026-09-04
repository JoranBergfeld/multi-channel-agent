using System.Text.Json;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceToolDispatcherTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly TurnId _turnId = new(Guid.NewGuid());
    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryConfirmationProposalStore _proposals = new();

    private ReferenceToolDispatcher Dispatcher()
    {
        var authorization = new InventoryAuthorizationService(_inventories, _audits);

        return new ReferenceToolDispatcher(
            new ReferenceListingService(_catalog, authorization),
            new ReferenceAdministrationService(
                new ReferenceChangeResolver(_catalog, _references),
                new InMemoryReferenceAdministrationStore(_proposals),
                _proposals,
                authorization));
    }

    private TurnExecutionContext Context(InventoryId? activeInventoryId = null) => new(
        _turnId,
        _participantId,
        new ChannelConversationId("web-conversation-1"),
        new FoundryConversationId(Guid.NewGuid()),
        1,
        activeInventoryId ?? _inventoryId,
        TraceId: null);

    private Task<ModelDecision> DispatchAsync(string toolName, Dictionary<string, string> args, TurnExecutionContext? context = null) =>
        Dispatcher().DispatchAsync(
            new ToolCallProposal(toolName, args), context ?? Context(), DateTimeOffset.UnixEpoch, CancellationToken.None);

    [Fact]
    public async Task Without_an_Active_Inventory_the_answer_is_guidance_not_a_failure()
    {
        var decision = await DispatchAsync(
            ReferenceToolDispatcher.ListUnitsToolName,
            [],
            new TurnExecutionContext(
                _turnId,
                _participantId,
                new ChannelConversationId("web-conversation-1"),
                new FoundryConversationId(Guid.NewGuid()),
                1,
                null,
                TraceId: null));

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("no_active_inventory", decision.Code);
    }

    [Fact]
    public async Task Listing_Units_answers_a_typed_payload_a_Viewer_may_see()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddUnit(_inventoryId, "each", ["piece"], isReserved: true);
        _catalog.AddUnit(_inventoryId, "Cardboard Box", ["boxes"]);

        var decision = await DispatchAsync(ReferenceToolDispatcher.ListUnitsToolName, []);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("unit_list", payload.GetProperty("kind").GetString());
        Assert.Equal(2, payload.GetProperty("units").GetArrayLength());
        Assert.Equal("Cardboard Box", payload.GetProperty("units")[0].GetProperty("name").GetString());
        Assert.Equal("boxes", payload.GetProperty("units")[0].GetProperty("aliases")[0].GetString());
    }

    [Fact]
    public async Task Listing_Locations_answers_its_own_typed_payload()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddLocation(_inventoryId, "Shelf A");

        var decision = await DispatchAsync(ReferenceToolDispatcher.ListLocationsToolName, []);

        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("location_list", payload.GetProperty("kind").GetString());
        Assert.Equal("Shelf A", payload.GetProperty("locations")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_Viewer_creating_a_Location_is_forbidden()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.CreateLocationsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"name":"Shelf A"}]""" });

        Assert.Equal(OutcomeCategory.Forbidden, decision.Category);
        Assert.Equal("forbidden", decision.Code);
    }

    [Fact]
    public async Task An_Editor_creating_one_Location_completes_with_a_typed_payload()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.CreateLocationsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"name":"Shelf A"}]""" });

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("reference_changes", payload.GetProperty("kind").GetString());
        Assert.Equal("create_location", payload.GetProperty("changes")[0].GetProperty("operation").GetString());
    }

    [Fact]
    public async Task An_Owner_retiring_a_Unit_is_asked_first_and_the_answer_carries_the_code()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Owner, DateTimeOffset.UnixEpoch);
        var unitId = _catalog.AddUnit(_inventoryId, "Cardboard Box", []);
        _references.AddUnit(_inventoryId, unitId, "Cardboard Box");

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.RetireUnitsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"unit":"Cardboard Box"}]""" });

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Equal("confirmation_required", decision.Code);

        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("reference_proposal", payload.GetProperty("kind").GetString());
        Assert.Equal(ConfirmationToken.TextLength, payload.GetProperty("token").GetString()!.Length);

        // The token is a bearer secret: it belongs in the payload the Participant is shown, never in
        // the Outcome's permanent summary column.
        Assert.DoesNotContain(payload.GetProperty("token").GetString()!, decision.Summary);
        Assert.Equal(TimeSpan.FromMinutes(ConfirmationProposal.LifetimeMinutes), decision.PayloadRetention);
    }

    [Fact]
    public async Task An_unknown_reference_answers_not_found_with_its_bounded_suggestions()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, DateTimeOffset.UnixEpoch);
        var unitId = _catalog.AddUnit(_inventoryId, "Box Large", []);
        _references.AddUnit(_inventoryId, unitId, "Box Large");

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.AddUnitAliasesToolName,
            new Dictionary<string, string> { ["changes"] = """[{"unit":"box","alias":"bx"}]""" });

        Assert.Equal(OutcomeCategory.NotFound, decision.Category);
        Assert.Equal("reference_not_found", decision.Code);

        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("reference_suggestions", payload.GetProperty("kind").GetString());
        Assert.Equal("unit", payload.GetProperty("reference").GetString());
        Assert.Equal("Box Large", payload.GetProperty("suggestions")[0].GetString());
        Assert.Contains("Box Large", decision.Summary);
    }

    [Fact]
    public async Task A_malformed_change_array_is_invalid_and_names_the_bound_it_violated()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.CreateUnitsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"name":"Box","location":"Shelf A"}]""" });

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("invalid_changes", decision.Code);
    }

    [Fact]
    public async Task The_reserved_Unit_is_answered_as_a_typed_conflict()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Owner, DateTimeOffset.UnixEpoch);
        var eachId = _catalog.AddUnit(_inventoryId, "each", ["piece", "pieces", "pc", "pcs"], isReserved: true);
        _references.AddUnit(_inventoryId, eachId, "each", "piece", "pieces", "pc", "pcs");

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.RenameUnitsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"unit":"each","newName":"item"}]""" });

        Assert.Equal(OutcomeCategory.Conflict, decision.Category);
        Assert.Equal("reserved_unit", decision.Code);
    }

    [Fact]
    public async Task A_page_size_outside_the_bound_is_answered_by_its_own_code()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.ListUnitsToolName,
            new Dictionary<string, string> { ["pageSize"] = "9999" });

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("invalid_page_size", decision.Code);
    }

    [Fact]
    public void The_dispatcher_names_exactly_the_ten_tools_the_specification_lists() =>
        Assert.Equal(
            [
                "list_units",
                "create_units",
                "rename_units",
                "add_unit_aliases",
                "remove_unit_aliases",
                "retire_units",
                "list_locations",
                "create_locations",
                "rename_locations",
                "retire_locations",
            ],
            ReferenceToolDispatcher.ToolNames);
}
