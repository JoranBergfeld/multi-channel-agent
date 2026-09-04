using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockMutationServiceTests
{
    private static readonly ParticipantId Editor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Viewer = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly InventoryId AnotherInventory = new(Guid.Parse("bbbbbbbb-4444-4444-4444-444444444444"));
    private static readonly UnitId EachUnit = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly UnitId BoxUnit = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    private static readonly LocationId ShelfA = new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    private static readonly LocationId ShelfB = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));
    private static readonly StockOperationId SomeOperation = new(Guid.Parse("99999999-9999-9999-9999-999999999999"));
    private static readonly StockOperationId AnotherOperation = new(Guid.Parse("aaaaaaaa-9999-9999-9999-999999999999"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        StockMutationService Service, InMemoryStockStore StockStore, InMemoryStockMutationStore MutationStore);

    private static Harness CreateHarness()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Editor, MembershipRole.Editor, Now);
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);

        // A second Inventory the same Editor may also mutate, so a test can prove one Inventory's
        // recorded operation is never re-reported into another - even under the same operation identity.
        inventoryStore.GrantMembership(AnotherInventory, Editor, MembershipRole.Editor, Now);

        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);

        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        referenceStore.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        referenceStore.AddUnit(SomeInventory, BoxUnit, "box");
        referenceStore.AddLocation(SomeInventory, ShelfA, "Shelf A");
        referenceStore.AddLocation(SomeInventory, ShelfB, "Shelf B");

        var mutationStore = new InMemoryStockMutationStore(stockStore);
        mutationStore.NameUnit(EachUnit, "each");
        mutationStore.NameUnit(BoxUnit, "box");
        mutationStore.NameLocation(ShelfA, "Shelf A");
        mutationStore.NameLocation(ShelfB, "Shelf B");

        return new Harness(
            new StockMutationService(stockStore, mutationStore, referenceStore, authorizationService),
            stockStore,
            mutationStore);
    }

    private static StockEntrySummary Row(
        string name, decimal quantity, string idHex, UnitId? unitId = null, LocationId? locationId = null, string? note = null) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        unitId ?? EachUnit,
        unitId == BoxUnit ? "box" : "each",
        locationId,
        locationId == ShelfA ? "Shelf A" : locationId == ShelfB ? "Shelf B" : null,
        note,
        Quantity.Create(quantity));

    private static Task<StockMutationResult> MutateAsync(
        Harness harness, ParticipantId participantId, StockMutationRequest request, StockOperationId? operationId = null) =>
        MutateInAsync(harness, participantId, SomeInventory, request, operationId);

    private static Task<StockMutationResult> MutateInAsync(
        Harness harness,
        ParticipantId participantId,
        InventoryId inventoryId,
        StockMutationRequest request,
        StockOperationId? operationId = null) =>
        harness.Service.MutateAsync(
            participantId, inventoryId, operationId ?? SomeOperation, request, "conversation-1", Now, CancellationToken.None);

    [Fact]
    public async Task Adding_to_stock_that_does_not_exist_yet_creates_it_at_the_exact_amount()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "12.5",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.True(result.View!.Created);
        Assert.Equal("Steel Bolts", result.View.Name);
        Assert.Equal("each", result.View.Unit);
        Assert.Null(result.View.Location);
        Assert.Equal("0", result.View.PreviousQuantity);
        Assert.Equal("12.5", result.View.Quantity);
    }

    [Fact]
    public async Task Adding_to_existing_Equivalent_Stock_increases_it_rather_than_duplicating_it()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 12.5m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "steel bolts",
            QuantityText = "2.25",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.False(result.View!.Created);
        Assert.Equal("14.75", result.View.Quantity);
        Assert.Single(await harness.StockStore.FindMatchesAsync(
            StockFindQuery.ByName(SomeInventory, "Steel Bolts", null, null), 10, CancellationToken.None));
    }

    [Fact]
    public async Task Adding_never_overwrites_an_existing_Note_and_says_it_kept_it()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 1m, "10000000", note: "Blue box"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            Note = "Red box",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("Blue box", result.View!.Note);
        Assert.True(result.View.NotePreserved);
    }

    [Fact]
    public async Task A_created_entry_keeps_the_Note_the_request_gave_it()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            Note = "Blue box",
        });

        Assert.Equal("Blue box", result.View!.Note);
        Assert.False(result.View.NotePreserved);
    }

    [Fact]
    public async Task An_Add_that_names_a_Unit_and_Location_creates_that_exact_Equivalent_Stock()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "3",
            UnitReference = "box",
            LocationReference = "Shelf A",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.True(result.View!.Created);
        Assert.Equal("box", result.View.Unit);
        Assert.Equal("Shelf A", result.View.Location);
    }

    [Fact]
    public async Task Removing_decreases_the_matched_entry_exactly()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 14.75m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "4.75",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("14.75", result.View!.PreviousQuantity);
        Assert.Equal("10", result.View.Quantity);
    }

    [Fact]
    public async Task Setting_replaces_the_matched_entrys_amount_exactly()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = "Steel Bolts",
            QuantityText = "7.125",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("7.125", result.View!.Quantity);
    }

    [Fact]
    public async Task A_Stock_Entry_can_be_targeted_by_its_opaque_identity()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 5m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = row.Id.ToString(),
            QuantityText = "2",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("2", result.View!.Quantity);
    }

    [Fact]
    public async Task Every_completed_mutation_appends_one_minimal_semantic_audit_fact()
    {
        var harness = CreateHarness();

        await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        var fact = Assert.Single(harness.MutationStore.AuditFacts);
        Assert.Equal(AuditEventType.StockAdded, fact.EventType);
        Assert.Equal("Add:Created", fact.OutcomeCode);
        Assert.Equal(SomeInventory, fact.InventoryId);
        Assert.Equal(Editor.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task Retrying_the_same_operation_re_reports_its_effect_instead_of_applying_it_twice()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "5",
        };

        var first = await MutateAsync(harness, Editor, request);
        var retry = await MutateAsync(harness, Editor, request);

        Assert.Equal("15", first.View!.Quantity);
        Assert.Equal(StockMutationResultKind.Completed, retry.Kind);
        Assert.Equal("15", retry.View!.Quantity);
        Assert.Single(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_genuinely_new_operation_applies_again_rather_than_being_mistaken_for_a_retry()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "5",
        };

        await MutateAsync(harness, Editor, request, SomeOperation);
        var second = await MutateAsync(harness, Editor, request, AnotherOperation);

        Assert.Equal("20", second.View!.Quantity);
        Assert.Equal(2, harness.MutationStore.AuditFacts.Count);
    }

    // A mutation commits in its own transaction; the terminal Outcome, Delivery, and inbox completion
    // commit in a second one. When the process dies between the two, the Turn is reprocessed and
    // derives the same operation identity - so a replay MUST re-report the recorded effect before any
    // re-planning against state the first attempt itself changed. Re-planning first would look at the
    // Stock the operation already emptied and tell the Participant that nothing happened, which is
    // both false and unrecoverable.
    [Fact]
    public async Task Replaying_an_operation_that_removed_all_the_stock_re_reports_it_rather_than_calling_it_an_underflow()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 10m, "10000000");
        harness.StockStore.Add(SomeInventory, row);
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "10",
        };

        var first = await MutateAsync(harness, Editor, request);
        Assert.Equal(StockMutationResultKind.Completed, first.Kind);
        Assert.Equal("0", first.View!.Quantity);

        var replay = await MutateAsync(harness, Editor, request);

        Assert.Equal(StockMutationResultKind.Completed, replay.Kind);
        Assert.Equal("completed", replay.Code);
        Assert.Equal("10", replay.View!.PreviousQuantity);
        Assert.Equal("0", replay.View.Quantity);
        Assert.False(replay.View.Created);

        // Nothing was applied a second time, and nothing was audited a second time.
        Assert.Single(harness.MutationStore.AuditFacts);
        Assert.Equal("0", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task Replaying_a_partial_Remove_re_reports_it_rather_than_underflowing_against_the_lower_amount()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 10m, "10000000");
        harness.StockStore.Add(SomeInventory, row);
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "7",
        };

        await MutateAsync(harness, Editor, request);
        var replay = await MutateAsync(harness, Editor, request);

        Assert.Equal(StockMutationResultKind.Completed, replay.Kind);
        Assert.Equal("10", replay.View!.PreviousQuantity);
        Assert.Equal("3", replay.View.Quantity);
        Assert.Single(harness.MutationStore.AuditFacts);
        Assert.Equal("3", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
    }

    // The recorded effect is the answer, so a replay never depends on the reference still resolving
    // the way it did. Here Equivalent Stock appeared elsewhere in the meantime, which would make the
    // very same reference ambiguous if it were resolved again.
    [Fact]
    public async Task Replaying_an_operation_answers_from_the_ledger_even_once_its_reference_became_ambiguous()
    {
        var harness = CreateHarness();
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "12.5",
        };

        var first = await MutateAsync(harness, Editor, request);
        Assert.True(first.View!.Created);

        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 3m, "20000000", locationId: ShelfA));

        var replay = await MutateAsync(harness, Editor, request);

        Assert.Equal(StockMutationResultKind.Completed, replay.Kind);
        Assert.Equal(first.View.StockEntryId, replay.View!.StockEntryId);
        Assert.True(replay.View.Created);
        Assert.Equal("0", replay.View.PreviousQuantity);
        Assert.Equal("12.5", replay.View.Quantity);
        Assert.Single(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_Viewer_replaying_an_Editors_operation_is_refused_before_the_ledger_is_ever_consulted()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "10",
        };
        await MutateAsync(harness, Editor, request);

        var replay = await MutateAsync(harness, Viewer, request);

        Assert.Equal(StockMutationResultKind.Forbidden, replay.Kind);
        Assert.Equal("forbidden", replay.Code);
        Assert.Null(replay.View);
    }

    [Fact]
    public async Task A_non_member_replaying_an_operation_cannot_learn_that_it_ever_happened()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "10",
        };
        await MutateAsync(harness, Editor, request);

        var replay = await MutateAsync(harness, Stranger, request);

        Assert.Equal(StockMutationResultKind.NotFound, replay.Kind);
        Assert.Equal("not_found", replay.Code);
        Assert.Null(replay.View);
    }

    // An operation identity is only ever meaningful within the Inventory it was applied to. Looking
    // one up from another Inventory must reveal nothing at all - not the Stock Entry, not the amount,
    // not that the operation exists - even for a Participant who may mutate both.
    [Fact]
    public async Task A_recorded_operation_is_invisible_to_the_same_operation_identity_in_another_Inventory()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 10m, "10000000");
        harness.StockStore.Add(SomeInventory, row);
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "10",
        };
        await MutateAsync(harness, Editor, request);

        var elsewhere = await MutateInAsync(harness, Editor, AnotherInventory, request);

        Assert.Equal(StockMutationResultKind.NotFound, elsewhere.Kind);
        Assert.Null(elsewhere.View);
        Assert.Single(harness.MutationStore.AuditFacts);
        Assert.Equal("0", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task A_Viewer_may_see_the_Inventory_but_may_not_change_its_Stock()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Viewer, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Null(result.View);
        Assert.Equal("5", harness.StockStore.Find(SomeInventory, Row("Steel Bolts", 5m, "10000000").Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task A_non_member_cannot_tell_this_Inventory_apart_from_one_that_does_not_exist()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Stranger, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task An_ambiguous_reference_offers_candidates_and_narrowing_instead_of_guessing()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000", locationId: ShelfA));
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 7m, "20000000", locationId: ShelfB));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Ambiguous, result.Kind);
        Assert.Equal(2, result.Candidates!.Candidates.Count);
        Assert.Equal(["Shelf A", "Shelf B"], result.Candidates.NarrowingHints.Locations);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task Naming_the_Location_makes_an_otherwise_ambiguous_reference_exact()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000", locationId: ShelfA));
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 7m, "20000000", locationId: ShelfB));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "1",
            LocationReference = "Shelf B",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("6", result.View!.Quantity);
        Assert.Equal("Shelf B", result.View.Location);
    }

    [Fact]
    public async Task Removing_stock_that_is_not_there_is_simply_not_found()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
    }

    [Fact]
    public async Task Setting_stock_that_is_not_there_never_quietly_creates_it()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = "Steel Bolts",
            QuantityText = "7",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task Removing_more_than_is_on_hand_is_refused_and_changes_nothing()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 3m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "3.0000000001",
        });

        Assert.Equal(StockMutationResultKind.Conflict, result.Kind);
        Assert.Equal("insufficient_quantity", result.Code);
        Assert.Equal("3", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task Setting_stock_to_zero_asks_for_confirmation_and_changes_nothing_yet()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 7m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = "Steel Bolts",
            QuantityText = "0",
        });

        Assert.Equal(StockMutationResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal("confirmation_required", result.Code);
        Assert.Null(result.View);
        Assert.Equal("7", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("1,5")]
    [InlineData("-3")]
    public async Task A_Quantity_that_is_not_exact_invariant_decimal_text_is_refused(string? quantityText)
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = quantityText,
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_quantity", result.Code);
    }

    [Fact]
    public async Task Adding_zero_is_not_an_Add_and_is_refused_as_invalid()    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "0",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_quantity", result.Code);
    }

    [Fact]
    public async Task An_Add_whose_total_could_no_longer_be_stored_exactly_is_refused_as_out_of_bounds()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 999_999_999_999_999_999m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("quantity_out_of_bounds", result.Code);
        Assert.Null(result.View);

        // The refusal is exactly that: the Stock Entry still carries what it did, and nothing was audited.
        Assert.Equal(
            "999999999999999999", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_request_that_names_no_Stock_Entry_at_all_is_refused_as_invalid()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "   ",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_reference", result.Code);
    }

    [Fact]
    public async Task A_Unit_this_Inventory_does_not_have_is_reported_rather_than_created()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            UnitReference = "pallet",
        });

        Assert.Equal(StockMutationResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal("reference_not_found", result.Code);
        Assert.Equal(StockReferenceKind.Unit, result.UnresolvedReference);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_Location_this_Inventory_does_not_have_is_reported_rather_than_created()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            LocationReference = "Loading Bay",
        });

        Assert.Equal(StockMutationResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal(StockReferenceKind.Location, result.UnresolvedReference);
    }

    [Fact]
    public async Task An_opaque_identity_that_matches_nothing_is_not_found_rather_than_an_invitation_to_create()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd").ToString(),
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_target_that_changed_since_the_request_was_planned_is_refused_without_disclosing_why()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));
        harness.MutationStore.ForceStateChanged = true;

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task A_Note_longer_than_a_Note_may_be_is_refused_before_it_reaches_a_column()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            Note = new string('n', StockEntry.MaxNoteLength + 1),
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_note", result.Code);
    }

    [Fact]
    public async Task A_name_longer_than_a_name_may_be_is_refused_before_it_reaches_a_column()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = new string('n', StockEntry.MaxNameLength + 1),
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_name", result.Code);
    }
}
