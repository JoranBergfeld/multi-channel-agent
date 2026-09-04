using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class StockChangeSetServiceTests
{
    private static readonly ParticipantId Editor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Viewer = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly UnitId EachUnit = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly LocationId ShelfA = new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    private static readonly LocationId ShelfB = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));
    private static readonly StockOperationId SomeOperation = new(Guid.Parse("99999999-9999-9999-9999-999999999999"));
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private const string Conversation = "conversation-1";

    private sealed record Harness(
        StockChangeSetService Service,
        InMemoryStockStore StockStore,
        InMemoryConfirmationProposalStore ProposalStore,
        InMemoryStockChangeSetStore ChangeSetStore);

    private static Harness CreateHarness()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Editor, MembershipRole.Editor, Now);
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);

        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);

        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        referenceStore.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        referenceStore.AddLocation(SomeInventory, ShelfA, "Shelf A");
        referenceStore.AddLocation(SomeInventory, ShelfB, "Shelf B");

        var proposalStore = new InMemoryConfirmationProposalStore();
        var changeSetStore = new InMemoryStockChangeSetStore(stockStore, proposalStore);

        return new Harness(
            new StockChangeSetService(
                new StockChangeResolver(stockStore, referenceStore), changeSetStore, proposalStore, authorizationService),
            stockStore,
            proposalStore,
            changeSetStore);
    }

    private static StockEntrySummary Seed(
        Harness harness, string name, string quantity, LocationId? locationId = null, string? note = null) =>
        harness.StockStore.CreateRow(
            SomeInventory,
            name,
            EachUnit,
            "each",
            locationId,
            locationId == ShelfA ? "Shelf A" : locationId == ShelfB ? "Shelf B" : null,
            note,
            Quantity.Create(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

    private static Task<StockChangeSetResult> ApplyAsync(
        Harness harness,
        IReadOnlyList<StockChangeRequest> requests,
        ParticipantId? participantId = null,
        TurnId? turnId = null,
        DateTimeOffset? now = null) =>
        harness.Service.ApplyAsync(
            participantId ?? Editor,
            SomeInventory,
            turnId ?? TurnId.NewId(),
            SomeOperation,
            requests,
            Conversation,
            now ?? Now,
            CancellationToken.None);

    private static StockChangeRequest MoveAll(string reference, string toLocation, int order = 1) => new()
    {
        Order = order,
        Kind = StockMutationKind.Move,
        Reference = reference,
        MoveAll = true,
        DestinationLocationReference = toLocation,
    };

    [Fact]
    public async Task A_lone_low_risk_change_applies_immediately_and_reports_the_exact_read_back()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "10");

        var result = await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A")]);

        Assert.Equal(StockChangeSetResultKind.Completed, result.Kind);
        var change = Assert.Single(result.Applied!.Changes);
        Assert.Equal("placed", change.Effect);
        Assert.Equal(source.Id.ToString(), change.SurvivingStockEntryId);
        Assert.Null(change.RetiredStockEntryId);
        Assert.Equal(ShelfA, harness.StockStore.Find(SomeInventory, source.Id)!.LocationId);
        Assert.Null(await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task A_lone_low_risk_change_appends_exactly_one_audit_fact()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "10");

        await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A")]);

        var audit = Assert.Single(harness.ChangeSetStore.AuditFacts);
        Assert.Equal(AuditEventType.StockMoved, audit.EventType);
        Assert.Equal("Move:Placed", audit.OutcomeCode);
    }

    [Fact]
    public async Task A_lone_merge_retiring_Move_is_proposed_rather_than_applied()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "10");
        Seed(harness, "Steel Bolts", "4", ShelfA);

        var result = await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A") with { UnlocatedOnly = true }]);

        AssertProposed(harness, result);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task A_lone_merge_retiring_Rename_is_proposed_rather_than_applied()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "4");
        Seed(harness, "Brass Rivets", "6");

        var result = await ApplyAsync(harness,
        [
            new StockChangeRequest { Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets" },
        ]);

        AssertProposed(harness, result);
        Assert.Equal("Steel Bolts", harness.StockStore.Find(SomeInventory, source.Id)!.Name);
    }

    [Fact]
    public async Task A_Forget_is_proposed_rather_than_applied()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "0");

        var result = await ApplyAsync(harness,
            [new StockChangeRequest { Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts" }]);

        AssertProposed(harness, result);
        Assert.NotNull(harness.StockStore.Find(SomeInventory, source.Id));
    }

    [Fact]
    public async Task A_Set_to_zero_is_proposed_rather_than_applied()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "4");

        var result = await ApplyAsync(harness,
        [
            new StockChangeRequest { Order = 1, Kind = StockMutationKind.Set, Reference = "Steel Bolts", QuantityText = "0" },
        ]);

        AssertProposed(harness, result);
        Assert.Equal("4", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task Every_batch_of_more_than_one_change_is_proposed_even_when_each_change_is_low_risk()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "10");
        Seed(harness, "Brass Rivets", "6");

        var result = await ApplyAsync(harness,
        [
            new StockChangeRequest { Order = 1, Kind = StockMutationKind.Add, Reference = "Steel Bolts", QuantityText = "1" },
            new StockChangeRequest { Order = 2, Kind = StockMutationKind.Add, Reference = "Brass Rivets", QuantityText = "2" },
        ]);

        AssertProposed(harness, result);
        Assert.Equal(2, result.Proposal!.Changes.Count);
    }

    [Fact]
    public async Task A_proposal_carries_a_single_use_token_the_Participant_can_read_exactly_once()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "0");

        var result = await ApplyAsync(harness,
            [new StockChangeRequest { Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts" }]);

        var token = result.Proposal!.Token;
        Assert.Equal(ConfirmationToken.TextLength, token.Length);
        Assert.True(ConfirmationToken.IsWellFormed(token));

        // Only the hash is stored, so the plaintext exists in this answer and nowhere else.
        var stored = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!;
        Assert.True(ConfirmationToken.Matches(stored.TokenHash, token));
        Assert.DoesNotContain(token, stored.TokenHash.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proposal_carries_the_exact_effects_including_the_survivor_and_the_retired_source()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "4");
        var colliding = Seed(harness, "Brass Rivets", "6");

        var result = await ApplyAsync(harness,
        [
            new StockChangeRequest { Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets" },
        ]);

        var change = Assert.Single(result.Proposal!.Changes);
        Assert.Equal("rename_merged", change.Effect);
        Assert.Equal(colliding.Id.ToString(), change.SurvivingStockEntryId);
        Assert.Equal(source.Id.ToString(), change.RetiredStockEntryId);
        Assert.Equal("10", change.Destination!.Quantity);
        Assert.Equal("4", change.TransferredQuantity);
    }

    [Fact]
    public async Task A_proposal_pins_the_version_of_every_existing_Stock_Entry_it_would_touch()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "10");
        var destination = Seed(harness, "Steel Bolts", "4", ShelfA);

        await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A") with { UnlocatedOnly = true }]);

        var stored = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!;
        Assert.Equal(2, stored.ExpectedVersions.Count);
        Assert.All(
            stored.ExpectedVersions,
            version => Assert.Equal(harness.StockStore.VersionOf(SomeInventory, version.StockEntryId), version.ConcurrencyStamp));
        Assert.Equal(
            new HashSet<StockEntryId> { source.Id, destination.Id },
            stored.ExpectedVersions.Select(version => version.StockEntryId).ToHashSet());
    }

    [Fact]
    public async Task A_stored_proposal_expires_ten_minutes_after_it_was_made()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "0");

        var result = await ApplyAsync(harness,
            [new StockChangeRequest { Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts" }]);

        var stored = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!;
        Assert.Equal(Now.AddMinutes(10), stored.ExpiresAt);
        Assert.Equal(Now.AddMinutes(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture), result.Proposal!.ExpiresAt);
    }

    [Fact]
    public async Task A_new_proposal_supersedes_the_pending_one_in_the_same_conversation()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "0");
        Seed(harness, "Brass Rivets", "0");

        await ApplyAsync(harness, [new StockChangeRequest { Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts" }]);
        var first = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!;

        await ApplyAsync(harness, [new StockChangeRequest { Order = 1, Kind = StockMutationKind.Forget, Reference = "Brass Rivets" }]);
        var second = (await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None))!;

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(ProposalStatus.Superseded, await harness.ProposalStore.FindStatusAsync(first.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_batch_whose_first_refusal_is_ambiguous_refuses_the_whole_batch_and_applies_nothing()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "1");
        Seed(harness, "Steel Bolts", "2", ShelfA);
        var untouched = Seed(harness, "Brass Rivets", "6");

        var result = await ApplyAsync(harness,
        [
            new StockChangeRequest { Order = 1, Kind = StockMutationKind.Add, Reference = "Steel Bolts", QuantityText = "1" },
            new StockChangeRequest { Order = 2, Kind = StockMutationKind.Add, Reference = "Brass Rivets", QuantityText = "2" },
        ]);

        Assert.Equal(StockChangeSetResultKind.Ambiguous, result.Kind);
        Assert.Equal("ambiguous", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Null(await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None));
        Assert.Equal("6", harness.StockStore.Find(SomeInventory, untouched.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task A_batch_that_names_the_same_Stock_Entry_twice_is_refused_rather_than_planned_against_stale_state()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "10");

        var result = await ApplyAsync(harness,
        [
            new StockChangeRequest { Order = 1, Kind = StockMutationKind.Add, Reference = "Steel Bolts", QuantityText = "1" },
            new StockChangeRequest { Order = 2, Kind = StockMutationKind.Remove, Reference = "Steel Bolts", QuantityText = "2" },
        ]);

        Assert.Equal(StockChangeSetResultKind.Invalid, result.Kind);
        Assert.Equal("conflicting_changes", result.Code);
        Assert.Null(await harness.ProposalStore.FindPendingAsync(Editor, Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_change_set_is_invalid()
    {
        var harness = CreateHarness();

        var result = await ApplyAsync(harness, []);

        Assert.Equal(StockChangeSetResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_changes", result.Code);
    }

    [Fact]
    public async Task A_Viewer_may_see_this_Inventory_but_may_not_change_or_propose_anything()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "10");

        var result = await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A")], Viewer);

        Assert.Equal(StockChangeSetResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Null(await harness.ProposalStore.FindPendingAsync(Viewer, Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task A_non_member_cannot_tell_this_Inventory_apart_from_one_that_does_not_exist()
    {
        var harness = CreateHarness();
        Seed(harness, "Steel Bolts", "10");

        var result = await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A")], Stranger);

        Assert.Equal(StockChangeSetResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
        Assert.Null(result.Applied);
        Assert.Null(result.Proposal);
    }

    [Fact]
    public async Task A_Turn_that_already_applied_a_change_set_is_answered_from_the_ledger_and_never_re_planned()
    {
        var harness = CreateHarness();
        var source = Seed(harness, "Steel Bolts", "10");
        var turnId = TurnId.NewId();

        var first = await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A")], turnId: turnId);
        Assert.Equal(StockChangeSetResultKind.Completed, first.Kind);

        // The same Turn, re-driven after a crash. Re-planning would now see the Stock somewhere else
        // and refuse; the ledger answers with what that Turn actually did.
        var replay = await ApplyAsync(harness, [MoveAll("Steel Bolts", "Shelf A")], turnId: turnId);

        Assert.Equal(StockChangeSetResultKind.Completed, replay.Kind);
        Assert.Equal(
            first.Applied!.Changes.Select(change => change.SurvivingStockEntryId),
            replay.Applied!.Changes.Select(change => change.SurvivingStockEntryId));
        Assert.Equal(source.Id.ToString(), Assert.Single(replay.Applied.Changes).SurvivingStockEntryId);
        Assert.Single(harness.ChangeSetStore.AuditFacts);
    }

    private static void AssertProposed(Harness harness, StockChangeSetResult result)
    {
        Assert.Equal(StockChangeSetResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal("confirmation_required", result.Code);
        Assert.NotNull(result.Proposal);
        Assert.Equal(ConfirmationToken.TextLength, result.Proposal!.Token.Length);
        Assert.Null(result.Applied);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
    }
}
