using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public sealed class StockChangePlanTests
{
    private static Quantity Q(string text)
    {
        Assert.True(Quantity.TryParseInvariant(text, out var quantity));
        return quantity;
    }

    [Fact]
    public void Adding_to_nothing_plans_to_create_the_Stock_Entry()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Add, currentQuantity: null, Q("5"));

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Created, plan.Effect);
        Assert.Equal("5", plan.SourceResultingQuantity.ToInvariantText());
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Adding_to_existing_stock_plans_to_increase_it()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Add, Q("12.5"), Q("2.25"));

        Assert.Equal(StockChangeEffectKind.QuantityIncreased, plan.Effect);
        Assert.Equal("14.75", plan.SourceResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Removing_more_than_is_on_hand_is_an_underflow_and_plans_nothing()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Remove, Q("3"), Q("4"));

        Assert.Equal(StockChangePlanOutcome.Underflow, plan.Outcome);
    }

    [Fact]
    public void Setting_to_zero_plans_to_clear_and_needs_confirmation()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Set, Q("7"), Quantity.Zero);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.QuantityCleared, plan.Effect);
        Assert.Equal("0", plan.SourceResultingQuantity.ToInvariantText());
        Assert.True(plan.RequiresConfirmation);
        Assert.False(plan.RetiresSource);
    }

    [Fact]
    public void Setting_stock_that_is_not_there_needs_a_target_rather_than_inventing_one()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Set, currentQuantity: null, Q("4"));

        Assert.Equal(StockChangePlanOutcome.TargetRequired, plan.Outcome);
    }

    [Fact]
    public void Moving_all_of_it_somewhere_empty_relocates_the_Stock_Entry_itself()
    {
        var plan = StockChangePlan.ForMove(Q("10"), requestedAmount: null, destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Placed, plan.Effect);
        Assert.Equal("10", plan.TransferredQuantity.ToInvariantText());
        Assert.Equal("10", plan.SourceResultingQuantity.ToInvariantText());
        Assert.False(plan.RetiresSource);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_all_of_it_into_Equivalent_Stock_merges_and_retires_the_source()
    {
        var plan = StockChangePlan.ForMove(Q("10"), requestedAmount: null, destinationIsSamePlacement: false, Q("4"));

        Assert.Equal(StockChangeEffectKind.Merged, plan.Effect);
        Assert.Equal("10", plan.TransferredQuantity.ToInvariantText());
        Assert.Equal("0", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("14", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.True(plan.RetiresSource);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_part_of_it_somewhere_empty_splits_without_retiring_anything()
    {
        var plan = StockChangePlan.ForMove(Q("10"), Q("3"), destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangeEffectKind.Split, plan.Effect);
        Assert.Equal("7", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("3", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_part_of_it_into_Equivalent_Stock_merges_without_retiring_the_source()
    {
        var plan = StockChangePlan.ForMove(Q("10"), Q("3"), destinationIsSamePlacement: false, Q("2"));

        Assert.Equal(StockChangeEffectKind.SplitMerged, plan.Effect);
        Assert.Equal("7", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("5", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_more_than_is_on_hand_is_an_underflow()
    {
        var plan = StockChangePlan.ForMove(Q("2"), Q("3"), destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.Underflow, plan.Outcome);
    }

    [Fact]
    public void Moving_a_non_positive_amount_is_not_a_Move_at_all()
    {
        var plan = StockChangePlan.ForMove(Q("2"), Quantity.Zero, destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.InvalidAmount, plan.Outcome);
    }

    [Fact]
    public void Moving_stock_to_where_it_already_is_changes_nothing()
    {
        var plan = StockChangePlan.ForMove(Q("2"), requestedAmount: null, destinationIsSamePlacement: true, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void A_merge_that_could_not_be_stored_exactly_is_refused_rather_than_rounded()
    {
        var nearLimit = Quantity.Create(999_999_999_999_999_999m);

        var plan = StockChangePlan.ForMove(nearLimit, requestedAmount: null, destinationIsSamePlacement: false, nearLimit);

        Assert.Equal(StockChangePlanOutcome.OutOfBounds, plan.Outcome);
    }

    [Fact]
    public void Renaming_without_a_collision_preserves_the_Stock_Entrys_identity()
    {
        var plan = StockChangePlan.ForRename("Steel Bolts", "Brass Rivets", "steel bolts", Q("4"), collidingQuantity: null);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Renamed, plan.Effect);
        Assert.Equal("4", plan.SourceResultingQuantity.ToInvariantText());
        Assert.False(plan.RetiresSource);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Renaming_only_the_capitalisation_still_changes_the_displayed_name()
    {
        // The normalized name is unchanged, so no Equivalent Stock can possibly collide with it, and
        // the entry keeps its identity - but the Participant did ask for a different displayed name.
        var plan = StockChangePlan.ForRename("steel bolts", "Steel Bolts", "steel bolts", Q("4"), collidingQuantity: null);

        Assert.Equal(StockChangeEffectKind.Renamed, plan.Effect);
    }

    [Fact]
    public void Renaming_a_Stock_Entry_to_the_name_it_already_displays_changes_nothing()
    {
        var plan = StockChangePlan.ForRename("Steel Bolts", "Steel Bolts", "steel bolts", Q("4"), collidingQuantity: null);

        Assert.Equal(StockChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Renaming_into_a_collision_merges_and_retires_the_source()
    {
        var plan = StockChangePlan.ForRename("Steel Bolts", "Brass Rivets", "steel bolts", Q("4"), Q("6"));

        Assert.Equal(StockChangeEffectKind.RenameMerged, plan.Effect);
        Assert.Equal("4", plan.TransferredQuantity.ToInvariantText());
        Assert.Equal("0", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("10", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.True(plan.RetiresSource);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Forgetting_an_empty_Stock_Entry_is_planned_and_needs_confirmation()
    {
        var plan = StockChangePlan.ForForget(Quantity.Zero);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Forgotten, plan.Effect);
        Assert.True(plan.RetiresSource);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Forgetting_Stock_that_is_still_on_hand_is_refused_so_it_cannot_bypass_Remove()
    {
        var plan = StockChangePlan.ForForget(Q("0.0000000001"));

        Assert.Equal(StockChangePlanOutcome.ForgetRequiresZeroQuantity, plan.Outcome);
    }

    [Fact]
    public void A_plan_that_decided_nothing_never_claims_to_retire_or_to_need_confirmation()
    {
        var plan = StockChangePlan.ForForget(Q("1"));

        Assert.False(plan.RetiresSource);
        Assert.False(plan.RequiresConfirmation);
    }
}
