using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stored form of a confirmation token: 64 lowercase hexadecimal characters of SHA-256. This -
/// and never the token itself - is what the <c>ConfirmationProposals</c> row carries, so the
/// authoritative record of a pending proposal reveals that one exists and nothing that could approve
/// it.
///
/// That protection is scoped to that table, and deliberately so: see <see cref="ConfirmationToken"/>
/// for where the plaintext does still live, and for how long.
/// </summary>
public readonly record struct ConfirmationTokenHash(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// The opaque, single-use secret that binds an explicit confirmation to one exact stored proposal.
///
/// The plaintext is generated from 32 cryptographically random bytes and only ever hashed into the
/// proposal itself: the <c>ConfirmationProposals</c> row holds <see cref="HashOf"/> and nothing more.
///
/// It is not, however, secret-free everywhere. The plaintext has to reach the Participant, and this
/// application guarantees that a Participant can recover a terminal answer after a disconnect, so the
/// answer that asks them to confirm keeps it: once in that Outcome's typed payload, and once in the
/// Delivery of that same payload. Both are durable by design. The residual exposure is bounded rather
/// than absent, and bounded three ways - the token is single use, it is refused once its proposal is
/// past ten minutes, and the Outcome payload carrying it is retained for exactly that window and then
/// discarded, leaving only the semantic answer behind. A Delivery of an already-dispatched answer may
/// outlive that window; what it then carries is a token that can no longer confirm anything. Nothing
/// logs either surface.
///
/// Guessing one means guessing 256 bits, so a wrong token can safely be answered without invalidating
/// the pending proposal - there is no brute-force attack to defend against by burning the
/// Participant's own proposal.
///
/// The token alone never authorizes anything: the application also requires that the current Turn's
/// direct content explicitly confirmed, and that the proposal is bound to this Participant,
/// ChannelConversation, and Inventory.
/// </summary>
public static class ConfirmationToken
{
    /// <summary>How many random bytes back one token. 256 bits, so a token is not guessable.</summary>
    public const int ByteLength = 32;

    /// <summary>The exact length of a token's text: 32 bytes in unpadded base64url.</summary>
    public const int TextLength = 43;

    /// <summary>The exact length of a hash's text: SHA-256 as lowercase hexadecimal.</summary>
    public const int HashTextLength = 64;

    public static string Issue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>
    /// Whether text could be a token at all. Checked before hashing so obviously malformed input is
    /// rejected without spending a hash on it, and so a caller can never accidentally hash - and then
    /// compare - a truncated or padded value.
    /// </summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TextLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static ConfirmationTokenHash HashOf(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new ConfirmationTokenHash(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    }

    /// <summary>
    /// Whether the presented text is the token behind <paramref name="storedHash"/>. The comparison
    /// is fixed-time so it cannot be turned into an oracle that leaks the stored hash a character at
    /// a time, and malformed text is refused before hashing rather than compared as a near-miss.
    /// </summary>
    public static bool Matches(ConfirmationTokenHash storedHash, string? presented)
    {
        if (!IsWellFormed(presented))
        {
            return false;
        }

        var presentedHash = HashOf(presented!);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash.Value), Encoding.ASCII.GetBytes(presentedHash.Value));
    }
}
