using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// One requested administration change, as proposed. Every field is untrusted text: nothing here is
/// identity, and nothing here is ever pattern-matched or guessed. <see cref="Order"/> is assigned by
/// the parser from the element's own position, never taken from the element, so a proposal cannot
/// reorder or collide the execution order. <see cref="Kind"/> comes from the tool that was called,
/// never from the element, so an array is homogeneous by construction.
/// </summary>
public sealed record ReferenceChangeRequest
{
    public required int Order { get; init; }

    public required ReferenceChangeKind Kind { get; init; }

    /// <summary>The name a create asks for.</summary>
    public string? Name { get; init; }

    /// <summary>The ordered initial aliases a Unit creation asks for; empty when it asked for none.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The Unit or Location to act on: an opaque identity, or an exact active name (for a Unit, any active term).</summary>
    public string? Reference { get; init; }

    /// <summary>The exact new display name a rename asks for.</summary>
    public string? NewName { get; init; }

    /// <summary>The single alias an alias add or removal names.</summary>
    public string? Alias { get; init; }
}

/// <summary>
/// Reads the one structured tool argument Unit and Location administration accepts: the untrusted
/// <c>changes</c> array a mutating administration tool carries.
///
/// It is deliberately unforgiving. A property this kind does not have, a value that is not a string,
/// a missing required value, or one element too many refuses the whole array - never a partly
/// understood batch, and never a silently narrowed one. The kind is supplied by the caller from the
/// *tool name*, so an element cannot name a kind of its own and a mixed batch cannot be expressed at
/// all.
/// </summary>
public static class ReferenceChangeSetParser
{
    /// <summary>The exact property set each kind accepts. Anything outside it refuses the array.</summary>
    private static readonly Dictionary<ReferenceChangeKind, string[]> KnownProperties = new()
    {
        [ReferenceChangeKind.CreateUnit] = ["name", "aliases"],
        [ReferenceChangeKind.RenameUnit] = ["unit", "newName"],
        [ReferenceChangeKind.AddUnitAlias] = ["unit", "alias"],
        [ReferenceChangeKind.RemoveUnitAlias] = ["unit", "alias"],
        [ReferenceChangeKind.RetireUnit] = ["unit"],
        [ReferenceChangeKind.CreateLocation] = ["name"],
        [ReferenceChangeKind.RenameLocation] = ["location", "newName"],
        [ReferenceChangeKind.RetireLocation] = ["location"],
    };

    /// <summary>
    /// Parses <paramref name="json"/> into ordered requests of exactly <paramref name="kind"/>. On
    /// failure <paramref name="code"/> is the machine code to answer with - <c>invalid_changes</c> or
    /// <c>too_many_changes</c> - and <paramref name="requests"/> is empty.
    /// </summary>
    public static bool TryParse(
        ReferenceChangeKind kind, string? json, out IReadOnlyList<ReferenceChangeRequest> requests, out string code)
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

            var parsed = new List<ReferenceChangeRequest>(elements.Count);
            for (var index = 0; index < elements.Count; index++)
            {
                if (!TryParseElement(kind, elements[index], index + 1, out var request))
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

    private static bool TryParseElement(
        ReferenceChangeKind kind, JsonElement element, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var known = KnownProperties[kind];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            // A property this kind does not have is not noise to skip past: it is a proposal asking
            // for something that was never agreed, and the safe reading of that is "no". That is also
            // what makes an element carrying its own "kind" a refusal rather than a mixed batch.
            if (!known.Contains(property.Name, StringComparer.Ordinal) || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return kind switch
        {
            ReferenceChangeKind.CreateUnit => TryCreateUnit(values, order, out request),
            ReferenceChangeKind.CreateLocation => TryOneValue(kind, values, "name", order, out request),
            ReferenceChangeKind.RenameUnit => TryRename(kind, values, "unit", order, out request),
            ReferenceChangeKind.RenameLocation => TryRename(kind, values, "location", order, out request),
            ReferenceChangeKind.AddUnitAlias or ReferenceChangeKind.RemoveUnitAlias =>
                TryAlias(kind, values, order, out request),
            ReferenceChangeKind.RetireUnit => TryReferenceOnly(kind, values, "unit", order, out request),
            ReferenceChangeKind.RetireLocation => TryReferenceOnly(kind, values, "location", order, out request),
            _ => false,
        };
    }

    private static bool TryCreateUnit(Dictionary<string, string> values, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, "name") is not { } name)
        {
            return false;
        }

        IReadOnlyList<string> aliases = [];
        if (values.TryGetValue("aliases", out var rawAliases))
        {
            // Present but listing nothing is a malformed request, not "no aliases": a caller that
            // meant none simply omits the property.
            var split = rawAliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length == 0)
            {
                return false;
            }

            aliases = split;
        }

        request = new ReferenceChangeRequest
        {
            Order = order,
            Kind = ReferenceChangeKind.CreateUnit,
            Name = name,
            Aliases = aliases,
        };

        return true;
    }

    private static bool TryOneValue(
        ReferenceChangeKind kind, Dictionary<string, string> values, string nameProperty, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, nameProperty) is not { } name)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Name = name };
        return true;
    }

    private static bool TryRename(
        ReferenceChangeKind kind,
        Dictionary<string, string> values,
        string referenceProperty,
        int order,
        out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, referenceProperty) is not { } reference || Required(values, "newName") is not { } newName)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Reference = reference, NewName = newName };
        return true;
    }

    private static bool TryAlias(
        ReferenceChangeKind kind, Dictionary<string, string> values, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, "unit") is not { } reference || Required(values, "alias") is not { } alias)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Reference = reference, Alias = alias };
        return true;
    }

    private static bool TryReferenceOnly(
        ReferenceChangeKind kind,
        Dictionary<string, string> values,
        string referenceProperty,
        int order,
        out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, referenceProperty) is not { } reference)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Reference = reference };
        return true;
    }

    /// <summary>A required value must be present and not blank; blank is exactly as absent as missing.</summary>
    private static string? Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
