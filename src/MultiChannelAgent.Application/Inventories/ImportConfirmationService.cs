using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>The semantic shape of a confirmation or rejection.</summary>
public enum ImportConfirmationResultKind
{
    /// <summary>The import ran, or had already run under this identity.</summary>
    Completed,

    /// <summary>The Participant cancelled it, and nothing was created.</summary>
    Rejected,

    /// <summary>There is no import this Participant may act on here.</summary>
    NotFound,

    Forbidden,

    /// <summary>It expired, or the Inventory is no longer empty. Nothing was created.</summary>
    Conflict,

    /// <summary>The token did not match. The proposal is deliberately left pending.</summary>
    Invalid,
}

/// <summary>What a completed import did: a count and the digest of the file it came from.</summary>
public sealed record ImportConfirmationView(int CreatedEntryCount, string FileDigest);

public sealed record ImportConfirmationResult(
    ImportConfirmationResultKind Kind, string Code, ImportConfirmationView? View = null);

/// <summary>
/// Executes or cancels one pending Initial Import. Confirmation hands the exact stored rows to the
/// atomic writer; it never reads, resolves, merges, or parses the raw file again.
/// </summary>
public sealed class ImportConfirmationService(
    InventoryAuthorizationService authorizationService,
    IImportProposalStore proposalStore,
    IImportExecutionStore executionStore)
{
    public async Task<ImportConfirmationResult> ConfirmAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? presentedToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(participantId, inventoryId, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var pending = await proposalStore.FindPendingAsync(participantId, inventoryId, cancellationToken);
        if (pending is null || !pending.BelongsTo(participantId, inventoryId))
        {
            return NotFound();
        }

        if (pending.IsExpired(now))
        {
            await proposalStore.SettleAsync(pending.Id, ImportProposalStatus.Expired, now, cancellationToken);
            return new ImportConfirmationResult(ImportConfirmationResultKind.Conflict, "proposal_expired");
        }

        // A typo must not destroy reviewed work. The token has enough entropy that a mismatch does
        // not need to burn the proposal as a brute-force defense.
        if (!ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return new ImportConfirmationResult(ImportConfirmationResultKind.Invalid, "proposal_token_mismatch");
        }

        return await ExecuteAsync(participantId, inventoryId, pending, now, cancellationToken);
    }

    /// <summary>Re-reports an already applied proposal from the ledger without touching Stock.</summary>
    public async Task<ImportConfirmationResult> ReplayAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        ImportProposalId proposalId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(participantId, inventoryId, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var recorded = await executionStore.FindRecordedAsync(
            inventoryId, ImportOperationId.DeriveForProposal(proposalId), cancellationToken);

        return recorded is null ? NotFound() : Completed(recorded);
    }

    public async Task<ImportConfirmationResult> RejectAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? presentedToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(participantId, inventoryId, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var pending = await proposalStore.FindPendingAsync(participantId, inventoryId, cancellationToken);
        if (pending is null || !pending.BelongsTo(participantId, inventoryId))
        {
            return NotFound();
        }

        // Declining is always safe without a token. When one is presented it must still match, so a
        // stale cancellation cannot settle a newer import that superseded the one the page showed.
        if (presentedToken is not null && !ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return new ImportConfirmationResult(ImportConfirmationResultKind.Invalid, "proposal_token_mismatch");
        }

        return await proposalStore.SettleAsync(pending.Id, ImportProposalStatus.Rejected, now, cancellationToken)
            ? new ImportConfirmationResult(ImportConfirmationResultKind.Rejected, "rejected")
            : NotFound();
    }

    private async Task<ImportConfirmationResult> ExecuteAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        ImportProposal pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await executionStore.ApplyAsync(
            new ImportExecutionCommand
            {
                OperationId = pending.ExecutionOperationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConsumesProposalId = pending.Id,
                FileDigest = pending.FileDigest,
                Entries = pending.Entries,
                EmptyStateVersion = pending.EmptyStateVersion,
                Now = now,
            },
            cancellationToken);

        if (result.Outcome == ImportExecutionOutcome.Conflict)
        {
            await proposalStore.SettleAsync(
                pending.Id, ImportProposalStatus.Conflicted, now, cancellationToken);
            return new ImportConfirmationResult(ImportConfirmationResultKind.Conflict, "state_changed");
        }

        return Completed(result.Recorded!);
    }

    private async Task<ImportConfirmationResult?> AuthorizeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId: null, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.NotFound => NotFound(),
            InventoryAuthorizationOutcome.Forbidden =>
                new ImportConfirmationResult(ImportConfirmationResultKind.Forbidden, "forbidden"),
            _ => null,
        };
    }

    private static ImportConfirmationResult Completed(RecordedImport recorded) => new(
        ImportConfirmationResultKind.Completed,
        "completed",
        new ImportConfirmationView(recorded.CreatedEntryCount, recorded.FileDigest.Value));

    private static ImportConfirmationResult NotFound() =>
        new(ImportConfirmationResultKind.NotFound, "proposal_not_found");
}
