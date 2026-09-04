using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// One requested change, as proposed. Every field is untrusted text: nothing here is identity, and
/// nothing here is ever pattern-matched or guessed. <see cref="Order"/> is assigned by the parser
/// from the request's own position, never taken from the request, so a proposal cannot reorder or
/// collide the execution order.
/// </summary>
public sealed record StockChangeRequest
{
    public required int Order { get; init; }

    public required StockMutationKind Kind { get; init; }

    /// <summary>The Stock Entry to act on: an opaque identity, or an exact name.</summary>
    public string? Reference { get; init; }

    /// <summary>Invariant decimal text. Required by Add, Remove, and Set; optional for a partial Move.</summary>
    public string? QuantityText { get; init; }

    /// <summary>A Move of everything on hand, stated instead of an amount.</summary>
    public bool MoveAll { get; init; }

    /// <summary>Narrows the target by Unit: an opaque identity, an exact canonical name, or an exact active alias.</summary>
    public string? UnitReference { get; init; }

    /// <summary>Narrows the target by Location: an opaque identity or an exact name.</summary>
    public string? LocationReference { get; init; }

    /// <summary>Narrows the target to Stock kept nowhere in particular.</summary>
    public bool UnlocatedOnly { get; init; }

    /// <summary>Where a Move sends stock: an opaque Location identity or an exact Location name.</summary>
    public string? DestinationLocationReference { get; init; }

    /// <summary>A Move to the unlocated state. Its own flag, because "unlocated" is the absence of a Location and never a Location's name.</summary>
    public bool DestinationUnlocated { get; init; }

    /// <summary>The exact new display name a Rename asks for.</summary>
    public string? NewName { get; init; }

    /// <summary>A Note, only ever applied when a change creates a Stock Entry.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Reads the one structured tool argument this application accepts: the untrusted <c>changes</c>
/// array a batch tool call carries.
///
/// It is deliberately unforgiving. A property it does not know, a value that is not a string, a kind
/// spelled differently, or one element too many is a refusal - never a partly understood batch, and
/// never a silently narrowed one. That matters more here than anywhere else in the tool surface: a
/// batch is the only argument with internal structure, so it is the only one where "ignore what you
/// do not understand" could quietly change what commits.
/// </summary>
public static class StockChangeSetParser
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "kind", "reference", "quantity", "all", "unit", "location", "unlocated", "to", "toUnlocated", "newName", "note",
    };

    /// <summary>
    /// Parses <paramref name="json"/> into ordered requests. On failure <paramref name="code"/> is the
    /// machine code to answer with - <c>invalid_changes</c> or <c>too_many_changes</c> - and
    /// <paramref name="requests"/> is empty.
    /// </summary>
    public static bool TryParse(string? json, out IReadOnlyList<StockChangeRequest> requests, out string code)
    {
        requests = [];
        code = "invalid_changes";

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var elements = document.RootElement.EnumerateArray().ToList();
            if (elements.Count == 0)
            {
                return false;
            }

            if (elements.Count > ConfirmationProposal.MaxChanges)
            {
                code = "too_many_changes";
                return false;
            }

            var parsed = new List<StockChangeRequest>(elements.Count);
            for (var index = 0; index < elements.Count; index++)
            {
                if (!TryParseElement(elements[index], index + 1, out var request))
                {
                    return false;
                }

                parsed.Add(request!);
            }

            requests = parsed;
            code = string.Empty;
            return true;
        }
    }

    private static bool TryParseElement(JsonElement element, int order, out StockChangeRequest? request)
    {
        request = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // A property this application does not know is not noise to skip past: it is a proposal
            // asking for something that was never agreed, and the safe reading of that is "no".
            if (!KnownProperties.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        if (!values.TryGetValue("kind", out var kindText) || !StockMutationKinds.TryParse(kindText, out var kind))
        {
            return false;
        }

        request = new StockChangeRequest
        {
            Order = order,
            Kind = kind,
            Reference = Optional(values, "reference"),
            QuantityText = Optional(values, "quantity"),
            MoveAll = Flag(values, "all"),
            UnitReference = Optional(values, "unit"),
            LocationReference = Optional(values, "location"),
            UnlocatedOnly = Flag(values, "unlocated"),
            DestinationLocationReference = Optional(values, "to"),
            DestinationUnlocated = Flag(values, "toUnlocated"),
            NewName = Optional(values, "newName"),
            Note = Optional(values, "note"),
        };

        return true;
    }

    private static string? Optional(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>A flag is an explicit "true" and nothing else, so stray text can only ever leave a change narrower.</summary>
    private static bool Flag(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed) && parsed;
}
