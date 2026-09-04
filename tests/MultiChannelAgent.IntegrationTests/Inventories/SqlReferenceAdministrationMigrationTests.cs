using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The real upgrade, on real SQL Server, under the production migrations: a Stock proposal that was
/// Pending under the previous schema must not still be Pending afterwards.
///
/// The runtime contract this migration introduces - a Retire settles every pending proposal that
/// references the retired identity, found by joining <c>ConfirmationProposalReferences</c> - cannot
/// see a proposal stored before the table existed. Because <see cref="SqlStockChangeSetStore"/> pins
/// Stock Entry versions and never independently re-checks whether a Unit or Location has since been
/// retired, such a proposal could otherwise be confirmed after the very Retire that should have
/// killed it, creating or moving Stock at a reference that no longer exists.
///
/// Migrating back down first also exercises <c>Down</c>, which has to remain a valid schema
/// transition even though it deliberately does not resurrect anything it invalidated.
/// </summary>
public sealed class SqlReferenceAdministrationMigrationTests : SqlIntegrationTestBase
{
    /// <summary>The last migration before #33 - the schema a pre-deploy proposal was written under.</summary>
    private const string SchemaBeforeReferenceAdministration = "20260904020012_AddStockChangeSetLedger";

    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A schema-version-1 stock payload, exactly as the previous release wrote one.</summary>
    private const string EmptyStockChangesJson = """{"Version":1,"Changes":[]}""";

    [SkippableFact]
    public async Task A_proposal_left_Pending_by_the_previous_schema_is_settled_by_the_migration()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed migration proof.");

        var participantId = Guid.NewGuid();
        var inventoryId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        using (var scope = Factory!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

            // Back to exactly the schema the proposal below was written under. This also proves Down is
            // a valid transition against a database this migration has already been applied to.
            await db.Database.GetService<IMigrator>().MigrateAsync(SchemaBeforeReferenceAdministration);

            await SeedLegacyPendingProposalAsync(db, participantId, inventoryId, proposalId);

            // Forward again: this is the deploy.
            await db.Database.MigrateAsync();
        }

        using var verifyScope = Factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var store = new SqlConfirmationProposalStore(verifyDb);

        var row = await verifyDb.ConfirmationProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposalId);

        Assert.Equal(nameof(ProposalStatus.Conflicted), row.Status);
        Assert.NotNull(row.SettledAt);
        Assert.NotNull(row.SettledAtTicks);

        // The Kind backfill still reached it, so the row stays readable rather than becoming a poison
        // pill for the sweep that will eventually delete it.
        Assert.Equal(nameof(ProposalKind.Stock), row.Kind);

        // Nothing is waiting for this Participant any more, so a confirmation can only answer
        // proposal_not_found - the proposal can never execute against a reference nobody vouched for.
        Assert.Null(await store.FindPendingAsync(new ParticipantId(participantId), "web:profile-1", CancellationToken.None));
        Assert.Equal(ProposalStatus.Conflicted, await store.FindStatusAsync(new ProposalId(proposalId), CancellationToken.None));

        // It carries no reference index rows - it never could - which is exactly why settling it was
        // the only safe answer: a Retire would have looked straight past it.
        Assert.Empty(await verifyDb.ConfirmationProposalReferences.AsNoTracking()
            .Where(r => r.ProposalId == proposalId)
            .ToListAsync());
    }

    /// <summary>
    /// Inserts a Pending proposal with raw SQL, naming only columns that exist in the previous schema -
    /// the entity type has moved on, so EF can no longer write this row.
    /// </summary>
    private static async Task SeedLegacyPendingProposalAsync(
        MultiChannelAgentDbContext db, Guid participantId, Guid inventoryId, Guid proposalId)
    {
        var expiresAt = Now.AddMinutes(ConfirmationProposal.LifetimeMinutes);

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Participants (Id, DisplayName, CreatedAt, UpdatedAt)
             VALUES ({participantId}, 'Owner Person', {Now}, {Now});
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Inventories (Id, Name, NormalizedName, CreatedByParticipantId, ClientRequestId, CreatedAt)
             VALUES ({inventoryId}, 'Warehouse', 'warehouse', {participantId}, {proposalId.ToString()}, {Now});
             """);
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO ConfirmationProposals
                 (ProposalId, TokenHash, ParticipantId, ChannelConversationId, InventoryId, ProposedInTurnId,
                  Status, ChangesJson, ExpectedVersionsJson, ExpectedAbsencesJson,
                  CreatedAt, ExpiresAt, ExpiresAtTicks, SettledAt, SettledAtTicks)
             VALUES
                 ({proposalId}, {ConfirmationToken.HashOf(ConfirmationToken.Issue()).Value}, {participantId},
                  'web:profile-1', {inventoryId}, {Guid.NewGuid()},
                  {nameof(ProposalStatus.Pending)}, {EmptyStockChangesJson}, '[]', '[]',
                  {Now}, {expiresAt}, {expiresAt.UtcTicks}, NULL, NULL);
             """);
    }
}
