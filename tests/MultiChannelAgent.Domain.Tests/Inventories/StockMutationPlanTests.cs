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
}
