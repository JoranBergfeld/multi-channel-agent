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
        harness.Service.MutateAsync(
            participantId, SomeInventory, operationId ?? SomeOperation, request, "conversation-1", Now, CancellationToken.None);

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
}
