using System.Security.Cryptography;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stable identity of one Initial Import execution. Like <see cref="StockOperationId"/> and
/// <see cref="ReferenceOperationId"/> it is <em>derived</em> - never generated - so a confirmation
/// re-driven after a crash re-reports what it did instead of importing a second time.
///
/// Its hash material is shaped so it can never equal either of the others: three ledgers, three
/// identity spaces, and no way for "what did this operation do" to be ambiguous.
/// </summary>
public readonly record struct ImportOperationId(Guid Value)
{
    public static ImportOperationId DeriveForProposal(ImportProposalId proposalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"import-proposal|{proposalId.Value:D}"));

        return new ImportOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString();
}
