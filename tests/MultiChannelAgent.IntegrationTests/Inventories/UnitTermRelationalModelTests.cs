using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free regression coverage for the real SQL Server CI failure this ticket fixed:
/// SQL Server rejected the original model with "Introducing FOREIGN KEY constraint
/// 'FK_UnitTerms_Units_UnitId' on table 'UnitTerms' may cause cycles or multiple cascade paths"
/// because both Inventory -> UnitTerms and Inventory -> Units -> UnitTerms were cascade paths, and a
/// UnitTerm's InventoryId could disagree with its Unit's InventoryId. This inspects the compiled EF
/// Core model directly (no database connection needed) so it fails fast, locally, without Docker,
/// whenever the model regresses to either problem - complementing the real SQL Server Testcontainers
/// proof in <see cref="InventorySqlScenarioTests"/> that a fresh database actually migrates cleanly.
/// </summary>
public sealed class UnitTermRelationalModelTests
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
    public void UnitTerm_has_exactly_one_foreign_key_forming_a_single_cascade_path_through_Unit()
    {
        var model = BuildModel();
        var unitTermType = model.FindEntityType(typeof(UnitTermEntity))!;

        var foreignKeys = unitTermType.GetForeignKeys().ToList();

        Assert.Single(foreignKeys);
        var foreignKey = foreignKeys[0];
        Assert.Equal(typeof(UnitEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void UnitTerm_foreign_key_is_the_composite_InventoryId_UnitId_referencing_Unit_InventoryId_Id()
    {
        var model = BuildModel();
        var unitTermType = model.FindEntityType(typeof(UnitTermEntity))!;
        var foreignKey = unitTermType.GetForeignKeys().Single();

        Assert.Equal(
            new[] { nameof(UnitTermEntity.InventoryId), nameof(UnitTermEntity.UnitId) },
            foreignKey.Properties.Select(p => p.Name));
        Assert.Equal(
            new[] { nameof(UnitEntity.InventoryId), nameof(UnitEntity.Id) },
            foreignKey.PrincipalKey.Properties.Select(p => p.Name));

        // The referenced key must be an alternate key (not the primary key) on Unit, since Unit's
        // primary key is Id alone; this alternate key is what enforces that a UnitTerm can only
        // reference a Unit belonging to the same Inventory.
        Assert.NotSame(foreignKey.PrincipalKey, unitTermType.Model.FindEntityType(typeof(UnitEntity))!.FindPrimaryKey());
    }

    [Fact]
    public void UnitTerm_still_enforces_the_unique_InventoryId_NormalizedTerm_index()
    {
        var model = BuildModel();
        var unitTermType = model.FindEntityType(typeof(UnitTermEntity))!;

        var uniqueIndex = unitTermType.GetIndexes().SingleOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(UnitTermEntity.InventoryId),
                nameof(UnitTermEntity.NormalizedTerm),
            }));

        Assert.NotNull(uniqueIndex);
    }
}
