using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

public sealed class SqlInventoryReferenceStoreSqlServerTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Batch_resolution_works_across_SQL_Server_column_and_OPENJSON_collations()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed batch lookup.");

        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var inventoryId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var locationId = Guid.NewGuid();

        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Import Owner",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Import Warehouse",
            NormalizedName = "import warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Units.Add(new UnitEntity
        {
            Id = unitId,
            InventoryId = inventoryId,
            CanonicalName = "Cardboard Box",
            NormalizedCanonicalName = "cardboard box",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            Term = "boxes",
            NormalizedTerm = "boxes",
            IsCanonical = false,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Locations.Add(new LocationEntity
        {
            Id = locationId,
            InventoryId = inventoryId,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await db.SaveChangesAsync();

        var store = new SqlInventoryReferenceStore(db);

        var units = await store.ResolveUnitsAsync(
            new InventoryId(inventoryId), ["boxes", "unknown"], CancellationToken.None);
        var locations = await store.ResolveLocationsAsync(
            new InventoryId(inventoryId), ["shelf a", "unknown"], CancellationToken.None);

        Assert.Equal(new ResolvedUnitReference(new UnitId(unitId), "Cardboard Box"), units["boxes"]);
        Assert.False(units.ContainsKey("unknown"));
        Assert.Equal(new ResolvedLocationReference(new LocationId(locationId), "Shelf A"), locations["shelf a"]);
        Assert.False(locations.ContainsKey("unknown"));
    }
}
