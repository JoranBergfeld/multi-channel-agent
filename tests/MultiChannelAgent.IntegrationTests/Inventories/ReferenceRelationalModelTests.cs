using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free assertions on the compiled EF Core model for the rules Unit and Location
/// administration rests on: the shared Unit term namespace and flat Location names are unique over
/// <em>active</em> rows only, and the proposal reference index has exactly one cascade path (SQL
/// Server rejects a model with two, as the shipped <see cref="UnitTermRelationalModelTests"/>
/// records from a real CI failure).
/// </summary>
public sealed class ReferenceRelationalModelTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new MultiChannelAgentDbContext(options);
        return context.Model;
    }

    [Fact]
    public void Unit_terms_are_unique_across_active_terms_only()
    {
        var index = BuildModel()
            .FindEntityType(typeof(UnitTermEntity))!
            .GetIndexes()
            .Single(i => i.Properties
                .Select(p => p.Name)
                .SequenceEqual([nameof(UnitTermEntity.InventoryId), nameof(UnitTermEntity.NormalizedTerm)]));

        Assert.True(index.IsUnique);
        Assert.Equal("RetiredAt IS NULL", index.GetFilter());
    }

    [Fact]
    public void Location_names_are_unique_across_active_Locations_only()
    {
        var index = BuildModel()
            .FindEntityType(typeof(LocationEntity))!
            .GetIndexes()
            .Single(i => i.Properties
                .Select(p => p.Name)
                .SequenceEqual([nameof(LocationEntity.InventoryId), nameof(LocationEntity.NormalizedName)]));

        Assert.True(index.IsUnique);
        Assert.Equal("RetiredAt IS NULL", index.GetFilter());
    }

    [Fact]
    public void A_proposal_reference_cascades_only_from_its_proposal()
    {
        var foreignKeys = BuildModel()
            .FindEntityType(typeof(ConfirmationProposalReferenceEntity))!
            .GetForeignKeys()
            .ToList();

        var foreignKey = Assert.Single(foreignKeys);
        Assert.Equal(typeof(ConfirmationProposalEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void A_reference_effect_cascades_only_from_its_ledger_header()
    {
        var foreignKeys = BuildModel()
            .FindEntityType(typeof(ReferenceEffectEntity))!
            .GetForeignKeys()
            .ToList();

        var foreignKey = Assert.Single(foreignKeys);
        Assert.Equal(typeof(ReferenceOperationEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void One_Turn_records_at_most_one_reference_operation_per_Inventory()
    {
        var index = BuildModel()
            .FindEntityType(typeof(ReferenceOperationEntity))!
            .GetIndexes()
            .Single(i => i.Properties
                .Select(p => p.Name)
                .SequenceEqual([nameof(ReferenceOperationEntity.InventoryId), nameof(ReferenceOperationEntity.ConfirmedByTurnId)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void One_proposal_can_be_consumed_by_at_most_one_reference_operation()
    {
        var index = BuildModel()
            .FindEntityType(typeof(ReferenceOperationEntity))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(ReferenceOperationEntity.ProposalId)]));

        Assert.True(index.IsUnique);
        Assert.Equal("ProposalId IS NOT NULL", index.GetFilter());
    }
}
