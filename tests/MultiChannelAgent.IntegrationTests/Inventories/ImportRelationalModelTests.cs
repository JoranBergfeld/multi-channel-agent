using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

public sealed class ImportRelationalModelTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlServer("Server=none")
            .Options;
        using var context = new MultiChannelAgentDbContext(options);
        return context.Model;
    }

    [Fact]
    public void One_import_may_be_pending_per_Participant_and_Inventory_and_the_database_says_so()
    {
        var proposal = BuildModel().FindEntityType(typeof(ImportProposalEntity))!;
        var index = proposal.GetIndexes()
            .Single(candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual(
                [
                    nameof(ImportProposalEntity.ParticipantId),
                    nameof(ImportProposalEntity.InventoryId),
                ]));

        Assert.True(index.IsUnique);
        Assert.Equal($"Status = '{nameof(ImportProposalStatus.Pending)}'", index.GetFilter());

        var participantForeignKey = proposal.GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ParticipantEntity));
        Assert.Equal(DeleteBehavior.NoAction, participantForeignKey.DeleteBehavior);
    }

    [Fact]
    public void A_token_can_never_back_two_imports_and_hashes_are_bounded()
    {
        var proposal = BuildModel().FindEntityType(typeof(ImportProposalEntity))!;
        var index = proposal.GetIndexes()
            .Single(candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(ImportProposalEntity.TokenHash)]));

        Assert.True(index.IsUnique);
        Assert.Equal(ConfirmationToken.HashTextLength, proposal.FindProperty(nameof(ImportProposalEntity.TokenHash))!.GetMaxLength());
        Assert.Equal(64, proposal.FindProperty(nameof(ImportProposalEntity.FileDigest))!.GetMaxLength());
    }

    [Fact]
    public void A_raw_upload_belongs_to_exactly_one_proposal_and_cannot_outlive_it()
    {
        var upload = BuildModel().FindEntityType(typeof(ImportUploadEntity))!;

        Assert.Equal(
            [nameof(ImportUploadEntity.ProposalId)],
            upload.FindPrimaryKey()!.Properties.Select(property => property.Name));

        var foreignKey = Assert.Single(upload.GetForeignKeys());
        Assert.Equal(typeof(ImportProposalEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void An_import_ledger_is_keyed_by_operation_unique_per_proposal_and_actor_bound()
    {
        var operation = BuildModel().FindEntityType(typeof(ImportOperationEntity))!;

        Assert.Equal(
            [nameof(ImportOperationEntity.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(property => property.Name));

        var proposalIndex = operation.GetIndexes()
            .Single(candidate => candidate.Properties.Single().Name == nameof(ImportOperationEntity.ProposalId));
        Assert.True(proposalIndex.IsUnique);
        Assert.Equal(64, operation.FindProperty(nameof(ImportOperationEntity.FileDigest))!.GetMaxLength());

        var actorForeignKey = operation.GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ParticipantEntity));
        Assert.Equal(
            [nameof(ImportOperationEntity.ActorId)],
            actorForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.NoAction, actorForeignKey.DeleteBehavior);
    }

    [Fact]
    public void The_import_ledger_survives_its_proposal_because_replay_must_outlive_it()
    {
        var operation = BuildModel().FindEntityType(typeof(ImportOperationEntity))!;

        Assert.DoesNotContain(
            operation.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ImportProposalEntity));
    }
}
