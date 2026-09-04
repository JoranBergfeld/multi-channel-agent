using System.Security.Cryptography;
using System.Text;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stable identity of one attempted reference administration change set. Like
/// <see cref="StockOperationId"/>, it is <em>derived</em> - never generated - from identities the
/// application already trusts, so retrying a Turn re-reports what it did instead of doing it twice,
/// and nothing a model proposes contributes to it.
///
/// It is a distinct type from <see cref="StockOperationId"/>, and its hash material is deliberately
/// shaped differently, because the two ledgers are separate tables: an identity that could belong to
/// either would make "what did this operation do" ambiguous.
/// </summary>
public readonly record struct ReferenceOperationId(Guid Value)
{
    public static ReferenceOperationId Derive(TurnId turnId, string toolName, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"reference|{turnId.Value:D}|{toolName}|{sequence}"));

        return new ReferenceOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    /// <summary>
    /// The identity a confirmed reference proposal's execution is recorded under, derived from the
    /// proposal rather than from the Turn that confirms it - the proposal is consumed by execution,
    /// and a Turn re-driven afterwards must still find what its own first attempt did.
    /// </summary>
    public static ReferenceOperationId DeriveForProposal(ProposalId proposalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"reference-proposal|{proposalId.Value:D}"));

        return new ReferenceOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString();
}
