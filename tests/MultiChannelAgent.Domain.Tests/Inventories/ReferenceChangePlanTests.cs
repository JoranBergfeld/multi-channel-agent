using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ReferenceChangePlanTests
{
    private static IReadOnlySet<string> Terms(params string[] terms) => terms.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<UnitTerm> UnitTerms() =>
    [
        UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false),
        UnitTerm.Create("boxes", isCanonical: false, isReserved: false),
    ];

    private static IReadOnlyList<UnitTerm> ReservedEachTerms() =>
        Unit.CreateReservedEach(new InventoryId(Guid.NewGuid()), DateTimeOffset.UnixEpoch).Terms();

    [Fact]
    public void Creating_a_Unit_produces_its_canonical_term_first_then_its_aliases_in_order()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("  Cardboard   Box ", ["Boxes", "BX"], Terms("each", "piece"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Cardboard Box", plan.DisplayName);
        Assert.Equal("cardboard box", plan.NormalizedName);
        Assert.Equal(["Cardboard Box", "Boxes", "BX"], plan.Terms.Select(term => term.Term));
        Assert.Equal(["cardboard box", "boxes", "bx"], plan.Terms.Select(term => term.NormalizedTerm));
        Assert.True(plan.Terms[0].IsCanonical);
        Assert.All(plan.Terms, term => Assert.False(term.IsReserved));
    }

    [Fact]
    public void Creating_a_Unit_whose_name_already_identifies_an_active_Unit_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("EACH", [], Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Unit_whose_alias_already_identifies_an_active_Unit_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("Cardboard Box", ["PCS"], Terms("each", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Unit_that_would_claim_one_term_twice_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("Box", ["boxes", "BOXES"], Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Unit_whose_alias_repeats_its_own_canonical_name_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("Box", ["box"], Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Creating_a_Unit_without_a_name_is_invalid(string? name) =>
        Assert.Equal(ReferenceChangePlanOutcome.InvalidName, ReferenceChangePlan.ForCreateUnit(name, [], Terms()).Outcome);

    [Fact]
    public void Creating_a_Unit_with_an_oversized_name_is_invalid() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.InvalidName,
            ReferenceChangePlan.ForCreateUnit(new string('b', Unit.MaxNameLength + 1), [], Terms()).Outcome);

    [Fact]
    public void Creating_a_Unit_with_an_oversized_alias_is_invalid() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.InvalidName,
            ReferenceChangePlan.ForCreateUnit("Box", [new string('b', Unit.MaxNameLength + 1)], Terms()).Outcome);

    [Fact]
    public void Renaming_a_Unit_to_a_free_term_is_planned()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "Carton", Terms("each", "boxes"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Carton", plan.DisplayName);
        Assert.Equal("carton", plan.NormalizedName);
    }

    [Fact]
    public void Renaming_a_Unit_only_in_its_display_form_is_planned_because_it_can_collide_with_nothing()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "CARDBOARD BOX", Terms("each", "cardboard box"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("CARDBOARD BOX", plan.DisplayName);
        Assert.Equal("cardboard box", plan.NormalizedName);
    }

    [Fact]
    public void Renaming_a_Unit_to_exactly_what_it_is_called_is_a_no_op()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", " Cardboard  Box ", Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Renaming_a_Unit_onto_another_active_term_is_refused()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "Piece", Terms("each", "piece"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Renaming_a_Unit_onto_one_of_its_own_aliases_is_refused_because_promoting_one_would_be_a_merge()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "Boxes", Terms("each", "boxes"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void The_reserved_Unit_can_never_be_renamed()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(isReserved: true, "each", "each", "item", Terms());

        Assert.Equal(ReferenceChangePlanOutcome.ReservedUnit, plan.Outcome);
    }

    [Fact]
    public void Adding_a_free_alias_is_planned()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias(" Cartons ", UnitTerms(), Terms("each", "piece"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Cartons", plan.Term!.Term);
        Assert.Equal("cartons", plan.Term.NormalizedTerm);
        Assert.False(plan.Term.IsCanonical);
        Assert.False(plan.Term.IsReserved);
    }

    [Fact]
    public void Adding_an_alias_the_Unit_already_carries_is_a_no_op()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("BOXES", UnitTerms(), Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Adding_an_alias_that_repeats_the_Units_own_name_is_a_no_op()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("cardboard box", UnitTerms(), Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Adding_an_alias_that_already_identifies_another_active_Unit_is_refused()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("pcs", UnitTerms(), Terms("each", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void A_reserved_term_can_never_be_reassigned_to_another_Unit()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("piece", UnitTerms(), Terms("each", "piece", "pieces", "pc", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void A_non_reserved_alias_may_be_added_to_the_reserved_Unit()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("stuks", ReservedEachTerms(), Terms("each", "piece", "pieces", "pc", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.False(plan.Term!.IsReserved);
    }

    [Fact]
    public void Removing_an_alias_the_Unit_carries_is_planned()
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias("BOXES", UnitTerms());

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("boxes", plan.Term!.NormalizedTerm);
    }

    [Fact]
    public void Removing_a_term_the_Unit_does_not_carry_finds_nothing()
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias("cartons", UnitTerms());

        Assert.Equal(ReferenceChangePlanOutcome.AliasNotFound, plan.Outcome);
    }

    [Fact]
    public void A_Units_own_name_is_not_one_of_its_aliases()
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias("Cardboard Box", UnitTerms());

        Assert.Equal(ReferenceChangePlanOutcome.CanonicalTerm, plan.Outcome);
    }

    [Theory]
    [InlineData("piece")]
    [InlineData("pieces")]
    [InlineData("pc")]
    [InlineData("pcs")]
    public void A_fixed_alias_of_the_reserved_Unit_can_never_be_removed(string alias)
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias(alias, ReservedEachTerms());

        Assert.Equal(ReferenceChangePlanOutcome.ReservedTerm, plan.Outcome);
    }

    [Fact]
    public void An_unused_Unit_may_be_retired()
    {
        var plan = ReferenceChangePlan.ForRetireUnit(isReserved: false, stockReferenceCount: 0);

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
    }

    [Fact]
    public void A_Unit_a_Stock_Entry_still_references_may_not_be_retired()
    {
        var plan = ReferenceChangePlan.ForRetireUnit(isReserved: false, stockReferenceCount: 1);

        Assert.Equal(ReferenceChangePlanOutcome.ReferenceInUse, plan.Outcome);
    }

    [Fact]
    public void The_reserved_Unit_can_never_be_retired()
    {
        var plan = ReferenceChangePlan.ForRetireUnit(isReserved: true, stockReferenceCount: 0);

        Assert.Equal(ReferenceChangePlanOutcome.ReservedUnit, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Location_is_planned_when_no_active_Location_carries_that_name()
    {
        var plan = ReferenceChangePlan.ForCreateLocation("  Shelf   A ", Terms("shelf b"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Shelf A", plan.DisplayName);
        Assert.Equal("shelf a", plan.NormalizedName);
    }

    [Fact]
    public void Creating_a_Location_whose_name_is_already_taken_is_refused() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.NameInUse,
            ReferenceChangePlan.ForCreateLocation("SHELF A", Terms("shelf a")).Outcome);

    [Fact]
    public void Creating_a_Location_with_an_oversized_name_is_invalid() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.InvalidName,
            ReferenceChangePlan.ForCreateLocation(new string('s', Location.MaxNameLength + 1), Terms()).Outcome);

    [Fact]
    public void Renaming_a_Location_to_a_free_name_is_planned()
    {
        var plan = ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", "Aisle 3", Terms("shelf b"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Aisle 3", plan.DisplayName);
        Assert.Equal("aisle 3", plan.NormalizedName);
    }

    [Fact]
    public void Renaming_a_Location_only_in_its_display_form_is_planned()
    {
        var plan = ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", "SHELF A", Terms("shelf a"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("SHELF A", plan.DisplayName);
    }

    [Fact]
    public void Renaming_a_Location_to_exactly_what_it_is_called_is_a_no_op() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.NoChange,
            ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", " Shelf  A ", Terms()).Outcome);

    [Fact]
    public void Renaming_a_Location_onto_another_active_Location_is_refused() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.NameInUse,
            ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", "Shelf B", Terms("shelf b")).Outcome);

    [Fact]
    public void An_unused_Location_may_be_retired() =>
        Assert.Equal(ReferenceChangePlanOutcome.Planned, ReferenceChangePlan.ForRetireLocation(stockReferenceCount: 0).Outcome);

    [Fact]
    public void A_Location_a_Stock_Entry_is_still_placed_in_may_not_be_retired() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.ReferenceInUse,
            ReferenceChangePlan.ForRetireLocation(stockReferenceCount: 3).Outcome);
}
