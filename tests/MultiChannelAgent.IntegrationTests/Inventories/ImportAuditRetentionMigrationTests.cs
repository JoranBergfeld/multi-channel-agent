using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MultiChannelAgent.Infrastructure.Persistence.Migrations;

namespace MultiChannelAgent.IntegrationTests.Inventories;

public sealed class ImportAuditRetentionMigrationTests
{
    [Fact]
    public void The_backfill_default_is_removed_before_the_retention_index_is_created()
    {
        var operations = new AddInventoryAuditOccurredAtTicks().UpOperations.ToList();

        var addIndex = operations.FindIndex(operation =>
            operation is CreateIndexOperation { Name: "IX_InventoryAudits_OccurredAtUtcTicks" });
        var removeDefault = operations.FindIndex(operation =>
            operation is AlterColumnOperation
            {
                Name: "OccurredAtUtcTicks",
                Table: "InventoryAudits",
                DefaultValue: null,
            });

        Assert.True(removeDefault >= 0, "The temporary zero default must be removed after existing audits are backfilled.");
        Assert.True(addIndex >= 0);
        Assert.True(
            removeDefault < addIndex,
            "A column-omitting writer must fail rather than create an audit that immediately qualifies for retention deletion.");
    }
}
