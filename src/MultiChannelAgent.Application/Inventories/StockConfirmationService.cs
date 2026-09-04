using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for confirming or rejecting a stored proposal.</summary>
public enum StockConfirmationResultKind
{
    /// <summary>The stored proposal was executed - or had already been executed by this very Turn.</summary>
    Completed,

    /// <summary>The stored proposal was explicitly rejected and will never execute.</summary>
    Rejected,

    /// <summary>There is no proposal this Participant may act on. Deliberately identical whether it never existed, belongs to someone else, or was already settled.</summary>
    NotFound,

    Forbidden,

    /// <summary>The proposal could no longer execute: it expired, or current state no longer matches what was proposed.</summary>
    Conflict,

    /// <summary>The request itself could not authorize anything - no direct explicit answer, or a token that does not match.</summary>
    Invalid,
}

/// <summary>The semantic result of a confirmation or rejection. Never names a Stock Entry, an Inventory, or another Participant on a refusal.</summary>
public sealed record StockConfirmationResult(
    StockConfirmationResultKind Kind, string Code, StockChangeSetView? Applied = null);

/// <summary>
/// Executes or rejects the one stored proposal a Participant has pending in this conversation.
///
/// Four things must all hold before anything is applied, and each of them is an acceptance criterion:
/// the Participant is still an Editor of the Inventory; the current Turn's <em>direct</em> content
/// explicitly confirmed (a model proposing a confirmation tool call is not evidence of anything); the
/// presented token matches the stored hash; and the proposal is still Pending, still bound to this
/// Participant, ChannelConversation, and Inventory, and not yet expired.
///
/// Execution then consumes the proposal and applies every change in one transaction, so two
/// confirmations of one proposal can never both execute. Nothing is ever re-resolved or re-planned:
/// what the Participant reviewed is exactly what commits.
/// </summary>
public sealed class StockConfirmationService(
    IConfirmationProposalStore proposalStore,
    IStockChangeSetStore changeSetStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<StockConfirmationResult> ConfirmAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string? presentedToken,
        DirectConfirmationEvidence evidence,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return refusal;
        }

        // Asked first, and asked of the ledger rather than of the proposal: a confirmation consumes
        // its proposal, so a Turn re-driven after a crash between the mutation transaction and the
        // Outcome transaction has nothing pending left to find. Without this it would report "no
        // proposal" after having applied everything.
        if (await changeSetStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyExecuted)
        {
            return Completed(alreadyExecuted);
        }

        // The model does not get a vote. Only what the authenticated Participant themselves said, in
        // this Turn, in direct content, can approve a mutation.
        if (evidence != DirectConfirmationEvidence.Confirmed)
        {
            return Invalid("confirmation_evidence_missing");
        }

        var pending = await proposalStore.FindPendingAsync(participantId, channelConversationId, cancellationToken);
        if (pending is null)
        {
            return NotFound();
        }

        // The Active Inventory moved since this proposal was made, so it no longer describes what the
        // Participant is working in. Settle it and answer as if it were not there - which, for this
        // Inventory, it is not.
        if (!pending.BelongsTo(participantId, channelConversationId, inventoryId))
        {
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.InventorySwitched, now, cancellationToken);
            return NotFound();
        }

        if (pending.IsExpired(now))
        {
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.Expired, now, cancellationToken);
            return new StockConfirmationResult(StockConfirmationResultKind.Conflict, "proposal_expired");
        }

        // A wrong token deliberately leaves the proposal pending. The token is 256 bits, so there is
        // no brute-force attack to defend against by burning the Participant's own proposal - and a
        // mistyped confirmation should not destroy work they still mean to approve.
        if (!ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return Invalid("proposal_token_mismatch");
        }

        var stored = await changeSetStore.ApplyAsync(
            new StockChangeSetCommand
            {
                OperationId = pending.ExecutionOperationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = pending.Id,
                Changes = pending.Changes,
                ExpectedVersions = pending.ExpectedVersions,
                ExpectedAbsences = pending.ExpectedAbsences,
                Now = now,
            },
            cancellationToken);

        if (stored.Outcome == StockChangeSetStoreOutcome.Conflict)
        {
            // Current state no longer matches what was reviewed, and nothing was applied. The proposal
            // describes a change that can never commit now, so it is settled rather than left to be
            // confirmed again into the same conflict.
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.Conflicted, now, cancellationToken);
            return new StockConfirmationResult(StockConfirmationResultKind.Conflict, "state_changed");
        }

        return Completed(stored.Recorded!);
    }

    public async Task<StockConfirmationResult> RejectAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string? presentedToken,
        DirectConfirmationEvidence evidence,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return refusal;
        }

        if (evidence != DirectConfirmationEvidence.Rejected)
        {
            return Invalid("rejection_evidence_missing");
        }

        var pending = await proposalStore.FindPendingAsync(participantId, channelConversationId, cancellationToken);
        if (pending is null || !pending.BelongsTo(participantId, channelConversationId, inventoryId))
        {
            return NotFound();
        }

        // A token is optional when rejecting: declining is always safe, and a Participant should never
        // have to quote a token to stop something from happening. When one is presented it must still
        // be the right one, so a stale rejection cannot settle a proposal that replaced it.
        if (presentedToken is not null && !ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return Invalid("proposal_token_mismatch");
        }

        return await proposalStore.SettleAsync(pending.Id, ProposalStatus.Rejected, now, cancellationToken)
            ? new StockConfirmationResult(StockConfirmationResultKind.Rejected, "rejected")
            : NotFound();
    }

    private async Task<StockConfirmationResult?> AuthorizeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.NotFound => NotFound(),
            InventoryAuthorizationOutcome.Forbidden => new StockConfirmationResult(StockConfirmationResultKind.Forbidden, "forbidden"),
            _ => null,
        };
    }

    private static StockConfirmationResult Completed(RecordedStockChangeSet recorded) => new(
        StockConfirmationResultKind.Completed,
        "completed",
        new StockChangeSetView(recorded.Effects.Select(effect => StockChangeSetService.ToChangeView(effect)).ToList()));

    private static StockConfirmationResult NotFound() =>
        new(StockConfirmationResultKind.NotFound, "proposal_not_found");

    private static StockConfirmationResult Invalid(string code) => new(StockConfirmationResultKind.Invalid, code);
}
