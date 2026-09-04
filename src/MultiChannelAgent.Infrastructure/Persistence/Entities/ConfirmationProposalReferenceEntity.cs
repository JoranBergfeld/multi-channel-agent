namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One Unit or Location that one stored proposal depends on, written when the proposal is stored.
///
/// This exists so retiring a reference can settle exactly the pending proposals that reference it -
/// including stock mutation proposals, which would otherwise create or move stock at a Unit or
/// Location that no longer exists. Scanning the serialized proposal for a Guid would work by
/// accident; a keyed, indexed table works by construction.
/// </summary>
public sealed class ConfirmationProposalReferenceEntity
{
    public Guid ProposalId { get; set; }

    /// <summary>The <c>ReferenceKind</c> as text, so the row is readable and the index is provider-neutral.</summary>
    public required string ReferenceKind { get; set; }

    public Guid ReferenceId { get; set; }
}
