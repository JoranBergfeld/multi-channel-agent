using System.Security.Cryptography;
using System.Text;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stable identity of one attempted Inventory mutation. It is <em>derived</em> - never generated -
/// from identities the application already trusts: the durably accepted Turn, the tool being executed,
/// and that tool call's position within the Turn. Two consequences follow, and they are the whole
/// point of the type:
///
/// <list type="bullet">
/// <item>Retrying the same Turn derives the same identity, so a store that has already recorded that
/// identity can re-report the effect instead of applying a second one.</item>
/// <item>Nothing a model proposes contributes to it, so a hostile or buggy proposal can neither
/// collide with another operation's identity nor mint a fresh one to bypass the ledger.</item>
/// </list>
///
/// The derivation is a plain hash rather than a random value precisely so it survives a process
/// restart, a redeployment, and a different replica picking the Turn up.
/// </summary>
public readonly record struct StockOperationId(Guid Value)
{
    public static StockOperationId Derive(TurnId turnId, string toolName, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var material = $"{turnId.Value:D}|{toolName}|{sequence}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return new StockOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    /// <summary>
    /// The stable identity a confirmed proposal's execution is recorded under. It is derived from the
    /// proposal rather than from the Turn that confirms it, so the ledger key is fixed the moment the
    /// proposal is stored: the proposal is consumed by execution, and a Turn re-driven afterwards
    /// must still be able to find what its own first attempt did.
    ///
    /// The material is deliberately shaped unlike <see cref="Derive"/>'s, so no Turn, tool, and
    /// sequence triple can ever hash to a proposal's identity.
    /// </summary>
    public static StockOperationId DeriveForProposal(ProposalId proposalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"proposal|{proposalId.Value:D}"));

        return new StockOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString();
}
