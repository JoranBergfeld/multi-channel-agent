using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceAdministrationServiceTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly TurnId _turnId = new(Guid.NewGuid());
    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryConfirmationProposalStore _proposals = new();
    private readonly InMemoryReferenceAdministrationStore _administration;

    private const string ConversationId = "web-conversation-1";

    public ReferenceAdministrationServiceTests() => _administration = new InMemoryReferenceAdministrationStore(_proposals);

    private ReferenceAdministrationService Service() => new(
        new ReferenceChangeResolver(_catalog, _references),
        _administration,
        _proposals,
        new InventoryAuthorizationService(_inventories, _audits));

    private void GrantRole(MembershipRole role) =>
        _inventories.GrantMembership(_inventoryId, _participantId, role, DateTimeOffset.UnixEpoch);

    private UnitId SeedUnit(string canonicalName, params string[] aliases)
    {
        var unitId = _catalog.AddUnit(_inventoryId, canonicalName, aliases);
        _references.AddUnit(_inventoryId, unitId, [canonicalName, .. aliases]);

        return unitId;
    }

    private LocationId SeedLocation(string name)
    {
        var locationId = _catalog.AddLocation(_inventoryId, name);
        _references.AddLocation(_inventoryId, locationId, name);

        return locationId;
    }

    private static ReferenceChangeRequest Create(string name, int order = 1) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.CreateLocation,
        Name = name,
    };

    private Task<ReferenceAdministrationResult> ApplyAsync(params ReferenceChangeRequest[] requests) =>
        Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "create_locations", 0),
            requests,
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

    [Fact]
    public async Task A_non_member_is_told_nothing_that_would_reveal_the_Inventory_exists()
    {
        var result = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:NotAMember");
    }

    [Fact]
    public async Task A_Viewer_may_not_create_reference_data_and_the_denial_is_audited()
    {
        GrantRole(MembershipRole.Viewer);

        var result = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
    }

    [Fact]
    public async Task An_Editor_creating_one_Location_applies_immediately()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.Completed, result.Kind);
        var change = Assert.Single(result.Applied!.Changes);
        Assert.Equal("create_location", change.Operation);
        Assert.Equal("Shelf A", change.Name);
        Assert.Equal("Location:Created", Assert.Single(_administration.Audits).OutcomeCode);
        Assert.Null(await _proposals.FindPendingAsync(_participantId, ConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task An_Editor_may_not_Retire_and_the_denial_is_audited()
    {
        GrantRole(MembershipRole.Editor);
        SeedUnit("Cardboard Box");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "retire_units", 0),
            [new ReferenceChangeRequest { Order = 1, Kind = ReferenceChangeKind.RetireUnit, Reference = "Cardboard Box" }],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.Forbidden, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
        Assert.Empty(_administration.Audits);
    }

    [Fact]
    public async Task An_Owner_retiring_an_unused_Unit_is_asked_first_and_nothing_is_applied_yet()
    {
        GrantRole(MembershipRole.Owner);
        var boxId = SeedUnit("Cardboard Box");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "retire_units", 0),
            [new ReferenceChangeRequest { Order = 1, Kind = ReferenceChangeKind.RetireUnit, Reference = "Cardboard Box" }],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal("confirmation_required", result.Code);
        Assert.Equal(ConfirmationToken.TextLength, result.Proposal!.Token.Length);
        Assert.Equal("retire_unit", Assert.Single(result.Proposal.Changes).Operation);
        Assert.Empty(_administration.Audits);

        var pending = await _proposals.FindPendingAsync(_participantId, ConversationId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(ProposalKind.ReferenceAdministration, pending!.Kind);
        Assert.Equal(MembershipRole.Owner, pending.RequiredRole);
        Assert.Equal([new UnitId(boxId.Value)], pending.ReferencedUnitIds);
    }

    [Fact]
    public async Task Every_batch_of_more_than_one_change_is_proposed_rather_than_applied()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync(Create("Shelf A"), Create("Shelf B", order: 2));

        Assert.Equal(ReferenceAdministrationResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal(2, result.Proposal!.Changes.Count);
        Assert.Empty(_administration.Audits);
    }

    [Fact]
    public async Task One_refusal_refuses_the_whole_set_and_nothing_is_applied()
    {
        GrantRole(MembershipRole.Editor);
        SeedLocation("Shelf B");

        var result = await ApplyAsync(Create("Shelf A"), Create("SHELF B", order: 2));

        Assert.Equal(ReferenceAdministrationResultKind.Conflict, result.Kind);
        Assert.Equal("name_in_use", result.Code);
        Assert.Empty(_administration.Audits);
        Assert.Null(await _proposals.FindPendingAsync(_participantId, ConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task Two_changes_claiming_one_term_are_refused_rather_than_left_to_the_index()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync(Create("Shelf A"), Create("shelf a", order: 2));

        Assert.Equal(ReferenceAdministrationResultKind.Invalid, result.Kind);
        Assert.Equal("conflicting_changes", result.Code);
    }

    [Fact]
    public async Task Two_changes_acting_on_one_reference_are_refused()
    {
        GrantRole(MembershipRole.Editor);
        SeedUnit("Cardboard Box");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "add_unit_aliases", 0),
            [
                new ReferenceChangeRequest
                {
                    Order = 1, Kind = ReferenceChangeKind.AddUnitAlias, Reference = "Cardboard Box", Alias = "cartons",
                },
                new ReferenceChangeRequest
                {
                    Order = 2, Kind = ReferenceChangeKind.AddUnitAlias, Reference = "Cardboard Box", Alias = "kartons",
                },
            ],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.Invalid, result.Kind);
        Assert.Equal("conflicting_changes", result.Code);
    }

    [Fact]
    public async Task An_unknown_reference_answers_reference_not_found_with_bounded_suggestions()
    {
        GrantRole(MembershipRole.Editor);
        SeedUnit("Box Large");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "add_unit_aliases", 0),
            [new ReferenceChangeRequest { Order = 1, Kind = ReferenceChangeKind.AddUnitAlias, Reference = "box", Alias = "bx" }],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal("reference_not_found", result.Code);
        Assert.Equal(ReferenceKind.Unit, result.UnresolvedReference);
        Assert.Equal(["Box Large"], result.Suggestions);
    }

    [Fact]
    public async Task A_Turn_that_already_applied_its_changes_re_reports_them_instead_of_re_planning()
    {
        GrantRole(MembershipRole.Editor);

        var first = await ApplyAsync(Create("Shelf A"));
        var replay = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.Completed, first.Kind);
        Assert.Equal(ReferenceAdministrationResultKind.Completed, replay.Kind);
        Assert.Equal(first.Applied!.Changes[0].ReferenceId, replay.Applied!.Changes[0].ReferenceId);
        Assert.Single(_administration.Audits);
    }

    [Fact]
    public async Task A_replay_is_answered_only_after_authorization_so_it_discloses_nothing()
    {
        GrantRole(MembershipRole.Editor);
        await ApplyAsync(Create("Shelf A"));

        _inventories.RevokeMembership(_inventoryId, _participantId);
        var replay = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.NotFound, replay.Kind);
    }

    [Fact]
    public async Task An_empty_change_set_is_invalid()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync();

        Assert.Equal(ReferenceAdministrationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_changes", result.Code);
    }
}
