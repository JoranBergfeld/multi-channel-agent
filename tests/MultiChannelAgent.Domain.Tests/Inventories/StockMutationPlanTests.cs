using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockMutationPlanTests
{
    [Fact]
    public void Adding_to_nothing_creates_an_entry_at_the_requested_amount()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Add, currentQuantity: null, Quantity.Create(12.5m));

        Assert.Equal(StockMutationPlanKind.CreateEntry, plan.Kind);
        Assert.Equal("12.5", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Adding_to_existing_stock_increases_it_exactly()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Add, Quantity.Create(12.5m), Quantity.Create(2.25m));

        Assert.Equal(StockMutationPlanKind.ChangeQuantity, plan.Kind);
        Assert.Equal("14.75", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Adding_a_zero_amount_is_not_an_Add_at_all()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Add, Quantity.Create(5m), Quantity.Zero);

        Assert.Equal(StockMutationPlanKind.InvalidAmount, plan.Kind);
    }

    [Fact]
    public void Adding_past_the_storable_range_is_refused_rather_than_stored_wrong()
    {
        var plan = StockMutationPlan.For(
            StockMutationKind.Add, Quantity.Create(999_999_999_999_999_999m), Quantity.Create(1m));

        Assert.Equal(StockMutationPlanKind.OutOfBounds, plan.Kind);
    }

    [Fact]
    public void Removing_within_the_amount_on_hand_decreases_it_exactly()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, Quantity.Create(14.75m), Quantity.Create(4.75m));

        Assert.Equal(StockMutationPlanKind.ChangeQuantity, plan.Kind);
        Assert.Equal("10", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Removing_more_than_the_amount_on_hand_is_an_underflow_that_changes_nothing()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, Quantity.Create(3m), Quantity.Create(4m));

        Assert.Equal(StockMutationPlanKind.Underflow, plan.Kind);
    }

    [Fact]
    public void Removing_a_zero_amount_is_not_a_Remove_at_all()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, Quantity.Create(3m), Quantity.Zero);

        Assert.Equal(StockMutationPlanKind.InvalidAmount, plan.Kind);
    }

    [Fact]
    public void Removing_from_nothing_needs_a_target_that_exists()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, currentQuantity: null, Quantity.Create(1m));

        Assert.Equal(StockMutationPlanKind.TargetRequired, plan.Kind);
    }

    [Fact]
    public void Setting_replaces_the_amount_exactly()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Set, Quantity.Create(3m), Quantity.Create(7.125m));

        Assert.Equal(StockMutationPlanKind.ChangeQuantity, plan.Kind);
        Assert.Equal("7.125", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Setting_to_zero_needs_explicit_confirmation_and_plans_no_change()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Set, Quantity.Create(7m), Quantity.Zero);

        Assert.Equal(StockMutationPlanKind.ConfirmationRequired, plan.Kind);
    }

    [Fact]
    public void Setting_something_that_does_not_exist_needs_a_target_that_exists()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Set, currentQuantity: null, Quantity.Create(7m));

        Assert.Equal(StockMutationPlanKind.TargetRequired, plan.Kind);
    }

    [Theory]
    [InlineData(StockMutationKind.Add, true, AuditEventType.StockAdded, "Add:Created")]
    [InlineData(StockMutationKind.Add, false, AuditEventType.StockAdded, "Add:Increased")]
    [InlineData(StockMutationKind.Remove, false, AuditEventType.StockRemoved, "Remove:Decreased")]
    [InlineData(StockMutationKind.Set, false, AuditEventType.StockSet, "Set:Applied")]
    public void Every_applied_mutation_has_one_minimal_audit_fact_shape(
        StockMutationKind kind, bool createdEntry, AuditEventType expectedEventType, string expectedOutcomeCode)
    {
        Assert.Equal(expectedEventType, StockAuditFacts.EventTypeFor(kind));
        Assert.Equal(expectedOutcomeCode, StockAuditFacts.OutcomeCodeFor(kind, createdEntry));
    }
    [Theory]
    [InlineData(StockMutationKind.Add, "add")]
    [InlineData(StockMutationKind.Remove, "remove")]
    [InlineData(StockMutationKind.Set, "set")]
    [InlineData(StockMutationKind.Move, "move")]
    [InlineData(StockMutationKind.Rename, "rename")]
    [InlineData(StockMutationKind.Forget, "forget")]
    public void Every_mutation_kind_has_stable_machine_text_that_round_trips(StockMutationKind kind, string expected)
    {
        Assert.Equal(expected, StockMutationKinds.ToMachineText(kind));
        Assert.True(StockMutationKinds.TryParse(expected, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delete")]
    [InlineData("Add ")]
    public void Text_that_is_not_a_mutation_kind_does_not_parse(string? text)
    {
        Assert.False(StockMutationKinds.TryParse(text, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(StockMutationKind.Add, true)]
    [InlineData(StockMutationKind.Remove, true)]
    [InlineData(StockMutationKind.Set, true)]
    [InlineData(StockMutationKind.Move, false)]
    [InlineData(StockMutationKind.Rename, false)]
    [InlineData(StockMutationKind.Forget, false)]
    public void Only_Add_Remove_and_Set_state_an_amount_of_their_own(StockMutationKind kind, bool expected) =>
        Assert.Equal(expected, StockMutationKinds.IsQuantityChange(kind));

    [Theory]
    [InlineData(StockChangeEffectKind.Merged)]
    [InlineData(StockChangeEffectKind.RenameMerged)]
    [InlineData(StockChangeEffectKind.Forgotten)]
    public void An_effect_that_ends_a_Stock_Entrys_identity_retires_its_source(StockChangeEffectKind effect)
    {
        Assert.True(StockAuditFacts.RetiresSource(effect));
        Assert.True(StockAuditFacts.RequiresConfirmation(effect));
    }

    [Theory]
    [InlineData(StockChangeEffectKind.Created)]
    [InlineData(StockChangeEffectKind.QuantityIncreased)]
    [InlineData(StockChangeEffectKind.QuantityDecreased)]
    [InlineData(StockChangeEffectKind.QuantitySet)]
    [InlineData(StockChangeEffectKind.Placed)]
    [InlineData(StockChangeEffectKind.Split)]
    [InlineData(StockChangeEffectKind.SplitMerged)]
    [InlineData(StockChangeEffectKind.Renamed)]
    public void An_effect_that_keeps_every_identity_needs_no_confirmation(StockChangeEffectKind effect)
    {
        Assert.False(StockAuditFacts.RetiresSource(effect));
        Assert.False(StockAuditFacts.RequiresConfirmation(effect));
    }

    [Fact]
    public void Clearing_Stock_keeps_its_identity_but_is_still_deliberate()
    {
        Assert.False(StockAuditFacts.RetiresSource(StockChangeEffectKind.QuantityCleared));
        Assert.True(StockAuditFacts.RequiresConfirmation(StockChangeEffectKind.QuantityCleared));
    }

    [Theory]
    [InlineData(StockMutationKind.Move, AuditEventType.StockMoved)]
    [InlineData(StockMutationKind.Rename, AuditEventType.StockRenamed)]
    [InlineData(StockMutationKind.Forget, AuditEventType.StockForgotten)]
    public void Every_new_mutation_kind_appends_its_own_audit_event_type(StockMutationKind kind, AuditEventType expected) =>
        Assert.Equal(expected, StockAuditFacts.EventTypeFor(kind));

    [Theory]
    [InlineData(StockChangeEffectKind.Created, "Add:Created")]
    [InlineData(StockChangeEffectKind.QuantityIncreased, "Add:Increased")]
    [InlineData(StockChangeEffectKind.QuantityDecreased, "Remove:Decreased")]
    [InlineData(StockChangeEffectKind.QuantitySet, "Set:Applied")]
    [InlineData(StockChangeEffectKind.QuantityCleared, "Set:Cleared")]
    [InlineData(StockChangeEffectKind.Placed, "Move:Placed")]
    [InlineData(StockChangeEffectKind.Split, "Move:Split")]
    [InlineData(StockChangeEffectKind.SplitMerged, "Move:SplitMerged")]
    [InlineData(StockChangeEffectKind.Merged, "Move:Merged")]
    [InlineData(StockChangeEffectKind.Renamed, "Rename:Renamed")]
    [InlineData(StockChangeEffectKind.RenameMerged, "Rename:Merged")]
    [InlineData(StockChangeEffectKind.Forgotten, "Forget:Forgotten")]
    public void Every_effect_has_a_coarse_audit_outcome_code(StockChangeEffectKind effect, string expected)
    {
        var code = StockAuditFacts.OutcomeCodeFor(effect);

        Assert.Equal(expected, code);

        // The audit column bounds this at 64 characters, and an audit fact must never carry detail
        // beyond a coarse code, so an over-long one is a design error rather than a truncation.
        Assert.True(code.Length <= 64);
    }
}
