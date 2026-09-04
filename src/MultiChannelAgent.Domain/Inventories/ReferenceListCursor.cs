using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// An opaque, deterministic keyset cursor for a catalog list: the last returned row's
/// <see cref="ReferenceOrderKey"/> together with the <see cref="ReferenceKind"/> it was issued for.
/// A list always resumes strictly after that exact key, so paging stays stable as unrelated
/// references are created - and only ever within the same question, because a cursor issued for
/// Units is refused by a Location list.
///
/// The wire form is base64url JSON: opaque to callers, but not intended to hide anything - it
/// carries the same fields already visible in the row it was derived from.
/// </summary>
public sealed record ReferenceListCursor(ReferenceKind Kind, ReferenceOrderKey OrderKey)
{
    /// <summary>Bumped whenever this cursor's payload shape changes, so an old cursor is refused rather than misread.</summary>
    public const int Version = 1;

    public bool Matches(ReferenceKind kind) => Kind == kind;

    public string Encode()
    {
        var json = JsonSerializer.Serialize(new CursorPayload(Version, Kind.ToString(), OrderKey.NormalizedName, OrderKey.IdOrderKey));
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes <paramref name="cursor"/>. A null or blank cursor decodes successfully to an absent
    /// cursor (<paramref name="result"/> is null, meaning "start from the first page") rather than
    /// being treated as invalid; only a non-blank value that fails to decode returns false.
    /// </summary>
    public static bool TryDecode(string? cursor, out ReferenceListCursor? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            var padded = base64.Length % 4 == 0 ? base64 : base64 + new string('=', 4 - (base64.Length % 4));
            var payload = JsonSerializer.Deserialize<CursorPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));

            if (payload is null
                || payload.Version != Version
                || !Enum.TryParse<ReferenceKind>(payload.Kind, ignoreCase: false, out var kind)
                || string.IsNullOrEmpty(payload.NormalizedName)
                || string.IsNullOrEmpty(payload.IdOrderKey))
            {
                return false;
            }

            result = new ReferenceListCursor(kind, new ReferenceOrderKey(payload.NormalizedName, payload.IdOrderKey));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("n")] string NormalizedName,
        [property: JsonPropertyName("i")] string IdOrderKey);
}
