using System.Security.Cryptography;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The SHA-256 of an uploaded file's exact bytes, as 64 lowercase hexadecimal characters.
///
/// It binds a stored proposal to the file that produced it, so a confirmation can state which file it
/// applied without the system retaining that file - and so two uploads are distinguishable in the
/// ledger by something that reveals nothing about their contents. It is computed over the bytes as
/// received, before any BOM is stripped, because the digest identifies the upload rather than the
/// text the parser derived from it.
///
/// A distinct type from <see cref="ConfirmationTokenHash"/> on purpose: both are 64 hex characters,
/// and one is a secret while the other is not.
/// </summary>
public readonly record struct FileDigest
{
    private FileDigest(string value) => Value = value;

    public string Value { get; }

    public static FileDigest Of(ReadOnlySpan<byte> content) => new(Convert.ToHexStringLower(SHA256.HashData(content)));

    public static bool TryParse(string? value, out FileDigest digest)
    {
        digest = default;

        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        digest = new FileDigest(value);
        return true;
    }

    public override string ToString() => Value;
}
