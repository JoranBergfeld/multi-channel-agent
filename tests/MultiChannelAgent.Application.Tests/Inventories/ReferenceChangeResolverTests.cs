using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceChangeResolverTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryInventoryReferenceStore _references = new();

    private ReferenceChangeResolver Resolver() => new(_catalog, _references);

    private UnitId SeedUnit(string canonicalName, params string[] aliases)
    {
        var unitId = _catalog.AddUnit(_inventoryId, canonicalName, aliases);
        _references.AddUnit(_inventoryId, unitId, [canonicalName, .. aliases]);

        return unitId;
    }

    private UnitId SeedReservedEach()
    {
        var unitId = _catalog.AddUnit(_inventoryId, "each", ["piece", "pieces", "pc", "pcs"], isReserved: true);
        _references.AddUnit(_inventoryId, unitId, "each", "piece", "pieces", "pc", "pcs");

        return unitId;
    }

    private LocationId SeedLocation(string name)
    {
        var locationId = _catalog.AddLocation(_inventoryId, name);
        _references.AddLocation(_inventoryId, locationId, name);

        return locationId;
    }

    private static ReferenceChangeRequest Request(
        ReferenceChangeKind kind,
        string? name = null,
        string[]? aliases = null,
        string? reference = null,
        string? newName = null,
        string? alias = null) => new()
        {
            Order = 1,
            Kind = kind,
            Name = name,
            Aliases = aliases ?? [],
            Reference = reference,
            NewName = newName,
            Alias = alias,
        };

    [Fact]
    public async Task Creating_a_Unit_decides_its_identity_its_terms_and_the_terms_it_claims()
    {
        SeedReservedEach();

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateUnit, name: " Cardboard  Box ", aliases: ["Boxes"]), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        var change = resolution.Change!;
        Assert.Equal(ReferenceChangeKind.CreateUnit, change.Kind);
        Assert.NotEqual(Guid.Empty, change.Target.ReferenceId);
        Assert.Equal("Cardboard Box", change.Target.Name);
        Assert.Equal("cardboard box", change.Target.NormalizedName);
        Assert.Equal(["Cardboard Box", "Boxes"], change.Terms.Select(term => term.Term));
        Assert.Empty(resolution.ExpectedVersions!);
        Assert.Equal(["cardboard box", "boxes"], resolution.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
        Assert.All(resolution.ExpectedAbsences!, absence => Assert.Equal(ReferenceKind.Unit, absence.Kind));
    }

    [Fact]
    public async Task Creating_a_Unit_whose_term_is_already_taken_is_a_typed_conflict()
    {
        SeedReservedEach();

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateUnit, name: "PCS"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("term_in_use", resolution.Code);
    }

    [Fact]
    public async Task Renaming_a_Unit_pins_the_version_it_was_decided_against_and_claims_the_new_term()
    {
        SeedReservedEach();
        var boxId = SeedUnit("Cardboard Box", "boxes");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameUnit, reference: "boxes", newName: "Carton"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        var change = resolution.Change!;
        Assert.Equal(boxId.Value, change.Target.ReferenceId);
        Assert.Equal("Cardboard Box", change.Target.Name);
        Assert.Equal("Carton", change.NewName);
        Assert.Equal("carton", change.NewNormalizedName);
        Assert.Equal([boxId.Value], resolution.ExpectedVersions!.Select(version => version.ReferenceId));
        Assert.Equal(["carton"], resolution.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
    }

    [Fact]
    public async Task Renaming_a_Unit_only_in_its_display_form_claims_nothing_new()
    {
        var boxId = SeedUnit("Cardboard Box");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId,
            Request(ReferenceChangeKind.RenameUnit, reference: boxId.Value.ToString(), newName: "CARDBOARD BOX"),
            CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        Assert.Empty(resolution.ExpectedAbsences!);
    }

    [Fact]
    public async Task The_reserved_Unit_is_refused_by_name_or_by_identity()
    {
        var eachId = SeedReservedEach();

        var byAlias = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameUnit, reference: "pcs", newName: "items"), CancellationToken.None);
        var byId = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: eachId.Value.ToString()), CancellationToken.None);

        Assert.Equal("reserved_unit", byAlias.Code);
        Assert.Equal("reserved_unit", byId.Code);
    }

    [Fact]
    public async Task A_fixed_alias_can_never_be_removed()
    {
        SeedReservedEach();

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RemoveUnitAlias, reference: "each", alias: "pcs"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("reserved_term", resolution.Code);
    }

    [Fact]
    public async Task A_non_reserved_alias_added_to_the_reserved_Unit_can_be_removed_again()
    {
        var eachId = SeedReservedEach();

        var added = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.AddUnitAlias, reference: "each", alias: "stuks"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, added.Kind);
        Assert.Equal(eachId.Value, added.Change!.Target.ReferenceId);
        Assert.Equal("stuks", added.Change.Term!.Term);
        Assert.False(added.Change.Term.IsReserved);
        Assert.Equal(["stuks"], added.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
    }

    [Fact]
    public async Task A_Units_own_name_is_not_one_of_its_aliases()
    {
        SeedUnit("Cardboard Box", "boxes");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId,
            Request(ReferenceChangeKind.RemoveUnitAlias, reference: "boxes", alias: "Cardboard Box"),
            CancellationToken.None);

        Assert.Equal("canonical_term", resolution.Code);
    }

    [Fact]
    public async Task A_term_that_is_not_an_alias_of_that_Unit_is_not_found()
    {
        SeedUnit("Cardboard Box", "boxes");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RemoveUnitAlias, reference: "boxes", alias: "cartons"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.NotFound, resolution.Kind);
        Assert.Equal("alias_not_found", resolution.Code);
    }

    [Fact]
    public async Task An_unused_Unit_resolves_for_Retire_and_pins_its_version()
    {
        var boxId = SeedUnit("Cardboard Box");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: "Cardboard Box"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(ReferenceChangeKind.RetireUnit, resolution.Change!.Kind);
        Assert.Equal([boxId.Value], resolution.ExpectedVersions!.Select(version => version.ReferenceId));
        Assert.Empty(resolution.ExpectedAbsences!);
    }

    [Fact]
    public async Task A_Unit_a_Stock_Entry_still_references_is_refused_before_anyone_is_asked_to_confirm()
    {
        var boxId = SeedUnit("Cardboard Box");
        _catalog.SetStockReferences(ReferenceKind.Unit, boxId.Value, 2);

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: "Cardboard Box"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("reference_in_use", resolution.Code);
    }

    [Fact]
    public async Task A_Location_a_Stock_Entry_is_still_placed_in_is_refused()
    {
        var shelfId = SeedLocation("Shelf A");
        _catalog.SetStockReferences(ReferenceKind.Location, shelfId.Value, 1);

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireLocation, reference: "Shelf A"), CancellationToken.None);

        Assert.Equal("reference_in_use", resolution.Code);
    }

    [Fact]
    public async Task An_unknown_Unit_answers_reference_not_found_with_bounded_deterministic_suggestions()
    {
        SeedReservedEach();
        SeedUnit("Box Large");
        SeedUnit("Box Small");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: "box"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.ReferenceNotFound, resolution.Kind);
        Assert.Equal("reference_not_found", resolution.Code);
        Assert.Equal(ReferenceKind.Unit, resolution.UnresolvedReference);
        Assert.Equal(["Box Large", "Box Small"], resolution.Suggestions);
        Assert.True(resolution.Suggestions!.Count <= IReferenceCatalogStore.MaxSuggestions);
    }

    [Fact]
    public async Task A_retired_reference_is_exactly_as_unknown_as_one_that_never_existed()
    {
        var boxId = SeedUnit("Cardboard Box");
        _references.RetireUnit(_inventoryId, boxId);

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameUnit, reference: "Cardboard Box", newName: "Carton"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.ReferenceNotFound, resolution.Kind);
    }

    [Fact]
    public async Task A_change_that_names_no_reference_at_all_is_invalid()
    {
        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireLocation), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_reference", resolution.Code);
    }

    [Fact]
    public async Task Creating_a_Location_decides_its_identity_and_claims_its_name()
    {
        SeedLocation("Shelf B");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateLocation, name: "  Shelf   A "), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(ReferenceKind.Location, resolution.Change!.Target.Kind);
        Assert.Equal("Shelf A", resolution.Change.Target.Name);
        Assert.Equal(["shelf a"], resolution.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
        Assert.All(resolution.ExpectedAbsences!, absence => Assert.Equal(ReferenceKind.Location, absence.Kind));
    }

    [Fact]
    public async Task Creating_a_Location_whose_name_is_taken_is_a_typed_conflict()
    {
        SeedLocation("Shelf A");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateLocation, name: "SHELF A"), CancellationToken.None);

        Assert.Equal("name_in_use", resolution.Code);
    }

    [Fact]
    public async Task Renaming_a_Location_to_exactly_what_it_is_called_is_a_typed_no_op()
    {
        SeedLocation("Shelf A");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameLocation, reference: "Shelf A", newName: "Shelf A"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("no_change", resolution.Code);
    }

    [Fact]
    public async Task An_oversized_name_is_invalid_rather_than_a_conflict()
    {
        var resolution = await Resolver().ResolveAsync(
            _inventoryId,
            Request(ReferenceChangeKind.CreateUnit, name: new string('b', Unit.MaxNameLength + 1)),
            CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_name", resolution.Code);
    }
}
