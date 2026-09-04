using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class StockChangeResolverTests
{
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly InMemoryStockStore _stock = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly UnitId _each = new(Guid.NewGuid());
    private readonly LocationId _shelfA = new(Guid.NewGuid());
    private readonly LocationId _shelfB = new(Guid.NewGuid());

    public StockChangeResolverTests()
    {
        _references.AddUnit(_inventory, _each, "each", "piece", "pieces", "pc", "pcs");
        _references.AddLocation(_inventory, _shelfA, "Shelf A");
        _references.AddLocation(_inventory, _shelfB, "Shelf B");
    }

    private StockChangeResolver Resolver() => new(_stock, _references);

    private StockEntrySummary Seed(string name, string quantity, LocationId? locationId = null, string? note = null) =>
        _stock.CreateRow(
            _inventory,
            name,
            _each,
            "each",
            locationId,
            locationId == _shelfA ? "Shelf A" : locationId == _shelfB ? "Shelf B" : null,
            note,
            Quantity.Create(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

    private async Task<StockChangeResolution> ResolveAsync(StockChangeRequest request) =>
        await Resolver().ResolveAsync(_inventory, request, CancellationToken.None);

    [Fact]
    public async Task Moving_part_of_a_Stock_Entry_to_an_empty_Location_splits_it()
    {
        var source = Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            QuantityText = "3",
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal(StockChangeResolutionKind.Resolved, resolution.Kind);
        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Split, change.Effect);
        Assert.Equal(source.Id, change.Source.StockEntryId);
        Assert.Equal("7", change.Source.ResultingQuantity.ToInvariantText());
        Assert.Null(change.Destination!.StockEntryId);
        Assert.Equal(_shelfA, change.Destination.LocationId);
        Assert.Equal("3", change.Destination.ResultingQuantity.ToInvariantText());
        Assert.Single(resolution.ExpectedVersions!);
        Assert.NotNull(resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Moving_all_of_a_Stock_Entry_into_Equivalent_Stock_merges_and_names_both_identities()
    {
        var source = Seed("Steel Bolts", "10");
        var destination = Seed("Steel Bolts", "4", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            UnlocatedOnly = true,
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Merged, change.Effect);
        Assert.Equal(destination.Id, change.SurvivingStockEntryId);
        Assert.Equal(source.Id, change.RetiredStockEntryId);
        Assert.Equal("14", change.Destination!.ResultingQuantity.ToInvariantText());
        Assert.Equal(2, resolution.ExpectedVersions!.Count);
        Assert.Null(resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Moving_all_of_a_Stock_Entry_to_an_empty_Location_relocates_it_and_keeps_its_identity()
    {
        var source = Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Placed, change.Effect);
        Assert.Equal(source.Id, change.SurvivingStockEntryId);
        Assert.Null(change.RetiredStockEntryId);
        Assert.Equal(_shelfA, change.Destination!.LocationId);
    }

    [Fact]
    public async Task Moving_Stock_to_the_unlocated_state_is_a_destination_of_its_own()
    {
        Seed("Steel Bolts", "10", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationUnlocated = true,
        });

        Assert.Equal(StockChangeEffectKind.Placed, resolution.Change!.Effect);
        Assert.Null(resolution.Change.Destination!.LocationId);
    }

    [Fact]
    public async Task A_Move_that_names_no_destination_at_all_is_invalid()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Move, Reference = "Steel Bolts", MoveAll = true,
        });

        Assert.Equal(StockChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_destination", resolution.Code);
    }

    [Fact]
    public async Task A_Move_that_names_both_a_Location_and_the_unlocated_state_is_invalid()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
            DestinationUnlocated = true,
        });

        Assert.Equal("invalid_destination", resolution.Code);
    }

    [Fact]
    public async Task A_Move_that_states_both_an_amount_and_all_is_invalid()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            QuantityText = "3",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal(StockChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_quantity", resolution.Code);
    }

    [Fact]
    public async Task A_Move_to_where_the_Stock_already_is_conflicts_rather_than_pretending_to_work()
    {
        Seed("Steel Bolts", "10", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal(StockChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("no_change", resolution.Code);
    }

    [Fact]
    public async Task A_Move_to_a_Location_this_Inventory_does_not_have_is_reported_never_created()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Loading Bay",
        });

        Assert.Equal(StockChangeResolutionKind.ReferenceNotFound, resolution.Kind);
        Assert.Equal(StockReferenceKind.Location, resolution.UnresolvedReference);
    }

    [Fact]
    public async Task A_split_carries_the_sources_Note_to_the_Stock_Entry_it_creates()
    {
        Seed("Steel Bolts", "10", note: "Blue box");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            QuantityText = "3",
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal("Blue box", resolution.Change!.Destination!.Note);
    }

    [Fact]
    public async Task Renaming_without_a_collision_preserves_identity_and_carries_the_exact_new_name()
    {
        var source = Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = " Brass  Rivets ",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Renamed, change.Effect);
        Assert.Equal(source.Id, change.SurvivingStockEntryId);
        Assert.Equal("Brass  Rivets", change.NewName);
        Assert.Equal("brass rivets", change.NewNormalizedName);
    }

    [Fact]
    public async Task Renaming_into_Equivalent_Stock_merges_and_names_the_survivor_and_the_retired_source()
    {
        var source = Seed("Steel Bolts", "4");
        var colliding = Seed("Brass Rivets", "6");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.RenameMerged, change.Effect);
        Assert.Equal(colliding.Id, change.SurvivingStockEntryId);
        Assert.Equal(source.Id, change.RetiredStockEntryId);
        Assert.Equal("10", change.Destination!.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public async Task A_Rename_only_collides_with_Equivalent_Stock_at_the_same_Unit_and_Location()
    {
        Seed("Steel Bolts", "4");
        Seed("Brass Rivets", "6", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets",
        });

        Assert.Equal(StockChangeEffectKind.Renamed, resolution.Change!.Effect);
    }

    [Theory]
    [InlineData(null, "invalid_name")]
    [InlineData("", "invalid_name")]
    [InlineData("   ", "invalid_name")]
    public async Task A_Rename_must_state_a_name(string? newName, string expectedCode)
    {
        Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = newName,
        });

        Assert.Equal(StockChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal(expectedCode, resolution.Code);
    }

    [Fact]
    public async Task Forgetting_an_empty_Stock_Entry_resolves_and_retires_it()
    {
        var source = Seed("Steel Bolts", "0");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Forgotten, change.Effect);
        Assert.Equal(source.Id, change.RetiredStockEntryId);
        Assert.Null(change.SurvivingStockEntryId);
    }

    [Fact]
    public async Task Forgetting_Stock_that_is_still_on_hand_conflicts_so_it_cannot_bypass_Remove()
    {
        Seed("Steel Bolts", "1");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts",
        });

        Assert.Equal(StockChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("forget_requires_zero_quantity", resolution.Code);
    }

    [Fact]
    public async Task An_ambiguous_reference_offers_candidates_rather_than_choosing_one()
    {
        Seed("Steel Bolts", "1");
        Seed("Steel Bolts", "2", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts",
        });

        Assert.Equal(StockChangeResolutionKind.Ambiguous, resolution.Kind);
        Assert.Equal("ambiguous", resolution.Code);
        Assert.Equal(2, resolution.Candidates!.Candidates.Count);
    }

    [Fact]
    public async Task A_reference_that_matches_nothing_is_simply_not_found()
    {
        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Move, Reference = "Brass Rivets", MoveAll = true, DestinationUnlocated = true,
        });

        Assert.Equal(StockChangeResolutionKind.NotFound, resolution.Kind);
        Assert.Equal("not_found", resolution.Code);
    }

    [Fact]
    public async Task Adding_to_nothing_resolves_to_creating_Equivalent_Stock_at_the_reserved_each_Unit()
    {
        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Add, Reference = "Brass Rivets", QuantityText = "4", Note = "Blue box",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Created, change.Effect);
        Assert.Null(change.Source.StockEntryId);
        Assert.Equal(_each, change.Source.UnitId);
        Assert.Equal("Blue box", change.Source.Note);
        Assert.Empty(resolution.ExpectedVersions!);
        Assert.Equal(new ExpectedEquivalentStockAbsence("brass rivets", _each, null), resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Adding_to_existing_Stock_never_rewrites_its_Note()
    {
        Seed("Steel Bolts", "4", note: "Blue box");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Add, Reference = "Steel Bolts", QuantityText = "1", Note = "Red box",
        });

        Assert.Equal(StockChangeEffectKind.QuantityIncreased, resolution.Change!.Effect);
        Assert.Equal("Blue box", resolution.Change.Source.Note);
    }

    [Fact]
    public async Task Setting_Stock_to_zero_resolves_to_clearing_it_and_asks_to_be_confirmed()
    {
        Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Set, Reference = "Steel Bolts", QuantityText = "0",
        });

        Assert.Equal(StockChangeEffectKind.QuantityCleared, resolution.Change!.Effect);
        Assert.True(StockAuditFacts.RequiresConfirmation(resolution.Change.Effect));
    }

    [Fact]
    public async Task Removing_more_than_is_on_hand_conflicts_and_resolves_no_change()
    {
        Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Remove, Reference = "Steel Bolts", QuantityText = "5",
        });

        Assert.Equal(StockChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("insufficient_quantity", resolution.Code);
        Assert.Null(resolution.Change);
    }
    [Fact]
    public async Task Relocating_a_Stock_Entry_pins_the_placement_it_expects_to_still_be_empty()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        // The row moves into a placement that must still hold no Equivalent Stock when it lands, so
        // a competing writer who fills it turns into a clean conflict rather than an index violation.
        Assert.Equal(StockChangeEffectKind.Placed, resolution.Change!.Effect);
        Assert.Equal(new ExpectedEquivalentStockAbsence("steel bolts", _each, _shelfA), resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Renaming_pins_the_name_it_expects_to_still_be_free()
    {
        Seed("Steel Bolts", "4", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets",
        });

        Assert.Equal(StockChangeEffectKind.Renamed, resolution.Change!.Effect);
        Assert.Equal(new ExpectedEquivalentStockAbsence("brass rivets", _each, _shelfA), resolution.ExpectedAbsence);
    }
}
