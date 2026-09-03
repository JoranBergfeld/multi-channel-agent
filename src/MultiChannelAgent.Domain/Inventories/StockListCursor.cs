using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// An opaque, deterministic pagination cursor encoding the last returned row's
/// <see cref="StockEntryOrderKey"/>. Bounded pagination for List always resumes strictly after that
/// exact key via keyset pagination, so paging remains stable even as unrelated rows are inserted. The
/// wire form is base64url JSON: opaque to callers, but not intended to hide anything sensitive - it
/// carries the same fields already visible in the row it was derived from.
/// </summary>
public sealed record StockListCursor(StockEntryOrderKey OrderKey)
{
    public static StockListCursor FromRow(StockEntrySummary row) => new(StockEntryOrderKey.From(row));

    public string Encode()
    {
        var json = JsonSerializer.Serialize(new CursorPayload(
            OrderKey.NormalizedName, OrderKey.UnitOrderKey, OrderKey.LocationOrderKey, OrderKey.IdOrderKey));
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes <paramref name="cursor"/>. A null or blank cursor decodes successfully to an absent
    /// cursor (<paramref name="result"/> is null, meaning "start from the first page") rather than
    /// being treated as invalid; only a non-blank value that fails to decode as this cursor's exact
    /// shape returns false.
    /// </summary>
    public static bool TryDecode(string? cursor, out StockListCursor? result)
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
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var payload = JsonSerializer.Deserialize<CursorPayload>(json);

            if (payload is null
                || string.IsNullOrEmpty(payload.NormalizedName)
                || string.IsNullOrEmpty(payload.UnitOrderKey)
                || payload.LocationOrderKey is null
                || string.IsNullOrEmpty(payload.IdOrderKey))
            {
                return false;
            }

            result = new StockListCursor(new StockEntryOrderKey(
                payload.NormalizedName, payload.UnitOrderKey, payload.LocationOrderKey, payload.IdOrderKey));
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
        [property: JsonPropertyName("n")] string NormalizedName,
        [property: JsonPropertyName("u")] string UnitOrderKey,
        [property: JsonPropertyName("l")] string LocationOrderKey,
        [property: JsonPropertyName("i")] string IdOrderKey);
}
