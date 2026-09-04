using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stored form of a confirmation token: 64 lowercase hexadecimal characters of SHA-256. This -
/// and never the token itself - is what a pending proposal carries, so someone who can read the
/// database can see that a proposal exists but can never confirm it.
/// </summary>
public readonly record struct ConfirmationTokenHash(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// The opaque, single-use secret that binds an explicit confirmation to one exact stored proposal.
///
/// The plaintext is generated from 32 cryptographically random bytes, handed to the Participant
/// exactly once in the answer that asks them to confirm, and then forgotten by this process. Only
/// <see cref="HashOf"/> is ever persisted. Guessing one means guessing 256 bits, so a wrong token can
/// safely be answered without invalidating the pending proposal - there is no brute-force attack to
/// defend against by burning the Participant's own proposal.
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
