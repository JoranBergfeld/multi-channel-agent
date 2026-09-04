using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ReferenceAdministrationFactsTests
{
    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, "create_unit")]
    [InlineData(ReferenceChangeKind.RenameUnit, "rename_unit")]
    [InlineData(ReferenceChangeKind.AddUnitAlias, "add_unit_alias")]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, "remove_unit_alias")]
    [InlineData(ReferenceChangeKind.RetireUnit, "retire_unit")]
    [InlineData(ReferenceChangeKind.CreateLocation, "create_location")]
    [InlineData(ReferenceChangeKind.RenameLocation, "rename_location")]
    [InlineData(ReferenceChangeKind.RetireLocation, "retire_location")]
    public void Every_kind_has_stable_machine_text_that_round_trips(ReferenceChangeKind kind, string text)
    {
        Assert.Equal(text, ReferenceAdministrationFacts.ToMachineText(kind));
        Assert.True(ReferenceAdministrationFacts.TryParse(text, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void Machine_text_is_exact_and_case_sensitive()
    {
        Assert.False(ReferenceAdministrationFacts.TryParse("Retire_Unit", out _));
        Assert.False(ReferenceAdministrationFacts.TryParse("retire", out _));
        Assert.False(ReferenceAdministrationFacts.TryParse(null, out _));
    }

    [Theory]
    [InlineData(ReferenceChangeKind.RetireUnit)]
    [InlineData(ReferenceChangeKind.RetireLocation)]
    public void Only_a_Retire_requires_confirmation_on_its_own(ReferenceChangeKind kind) =>
        Assert.True(ReferenceAdministrationFacts.RequiresConfirmation(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit)]
    [InlineData(ReferenceChangeKind.RenameUnit)]
    [InlineData(ReferenceChangeKind.AddUnitAlias)]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias)]
    [InlineData(ReferenceChangeKind.CreateLocation)]
    [InlineData(ReferenceChangeKind.RenameLocation)]
    public void Every_non_destructive_kind_applies_without_being_asked(ReferenceChangeKind kind) =>
        Assert.False(ReferenceAdministrationFacts.RequiresConfirmation(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.RetireUnit, MembershipRole.Owner)]
    [InlineData(ReferenceChangeKind.RetireLocation, MembershipRole.Owner)]
    [InlineData(ReferenceChangeKind.CreateUnit, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.RenameUnit, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.AddUnitAlias, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.CreateLocation, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.RenameLocation, MembershipRole.Editor)]
    public void Only_Retire_demands_the_Owner(ReferenceChangeKind kind, MembershipRole role) =>
        Assert.Equal(role, ReferenceAdministrationFacts.RequiredRole(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, ReferenceKind.Unit)]
    [InlineData(ReferenceChangeKind.RetireUnit, ReferenceKind.Unit)]
    [InlineData(ReferenceChangeKind.CreateLocation, ReferenceKind.Location)]
    [InlineData(ReferenceChangeKind.RetireLocation, ReferenceKind.Location)]
    public void Every_kind_names_the_reference_it_administers(ReferenceChangeKind kind, ReferenceKind reference) =>
        Assert.Equal(reference, ReferenceAdministrationFacts.ReferenceKindFor(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, AuditEventType.UnitCreated, "Unit:Created")]
    [InlineData(ReferenceChangeKind.RenameUnit, AuditEventType.UnitRenamed, "Unit:Renamed")]
    [InlineData(ReferenceChangeKind.AddUnitAlias, AuditEventType.UnitAliasAdded, "Unit:AliasAdded")]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, AuditEventType.UnitAliasRemoved, "Unit:AliasRemoved")]
    [InlineData(ReferenceChangeKind.RetireUnit, AuditEventType.UnitRetired, "Unit:Retired")]
    [InlineData(ReferenceChangeKind.CreateLocation, AuditEventType.LocationCreated, "Location:Created")]
    [InlineData(ReferenceChangeKind.RenameLocation, AuditEventType.LocationRenamed, "Location:Renamed")]
    [InlineData(ReferenceChangeKind.RetireLocation, AuditEventType.LocationRetired, "Location:Retired")]
    public void Every_kind_audits_one_minimal_fact(ReferenceChangeKind kind, AuditEventType eventType, string outcomeCode)
    {
        Assert.Equal(eventType, ReferenceAdministrationFacts.EventTypeFor(kind));
        Assert.Equal(outcomeCode, ReferenceAdministrationFacts.OutcomeCodeFor(kind));
    }

    [Fact]
    public void A_reference_operation_identity_is_derived_and_stable_across_retries()
    {
        var turnId = new TurnId(Guid.NewGuid());

        Assert.Equal(
            ReferenceOperationId.Derive(turnId, "retire_units", 0),
            ReferenceOperationId.Derive(turnId, "retire_units", 0));
        Assert.NotEqual(
            ReferenceOperationId.Derive(turnId, "retire_units", 0),
            ReferenceOperationId.Derive(turnId, "create_units", 0));
    }

    [Fact]
    public void A_reference_operation_identity_can_never_collide_with_a_stock_one()
    {
        var proposalId = ProposalId.NewId();
        var turnId = new TurnId(Guid.NewGuid());

        Assert.NotEqual(
            StockOperationId.DeriveForProposal(proposalId).Value,
            ReferenceOperationId.DeriveForProposal(proposalId).Value);
        Assert.NotEqual(
            StockOperationId.Derive(turnId, "create_units", 0).Value,
            ReferenceOperationId.Derive(turnId, "create_units", 0).Value);
    }
}
