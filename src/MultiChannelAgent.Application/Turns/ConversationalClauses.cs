using System.Text.RegularExpressions;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Parses the bounded clause grammar the scripted model boundary understands: a command word
/// followed by zero or more named clauses in any order, for example
/// <c>list stock including zero in Shelf A page size 5</c> or <c>add stock Steel Bolts quantity 5 in Shelf A</c>. Clause values are free-form text and stay
/// untrusted - they are only ever passed on as filter arguments, never as identity - and a command
/// carrying anything that is not a recognized clause is deliberately not recognized at all, so it
/// falls back to the plain echo rather than being answered as a narrower request than was asked.
/// </summary>
public static partial class ConversationalClauses
{
    /// <summary>Clauses that stand alone; anything a caller writes after them belongs to the next clause.</summary>
    private static readonly string[] FlagClauses = ["including zero", "unlocated", "to unlocated", "all"];

    // Longer phrases precede the shorter words they contain, so the more specific reading always
    // wins: "to unlocated" before "to" and "unlocated", so a destination of "nowhere in particular"
    // is read as its own flag rather than as a Location called "unlocated"; and "to unit"/"from unit"
    // before "to", "from", and "unit", so "add alias cartons to unit Cardboard Box" names one Unit
    // rather than a "to" clause with nothing in it followed by a "unit" clause.
    [GeneratedRegex(
        @"\b(including zero|to unlocated|to unit|from unit|unlocated|named|unit|in|page size|after|quantity|note|aliases|alias|from|to|all)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClauseScanner { get; }

    /// <summary>
    /// Parses <paramref name="text"/> into clause name/value pairs. Returns false when any text sits
    /// outside a recognized clause, so a partially understood command is never treated as understood.
    /// </summary>
    public static bool TryParse(string text, out IReadOnlyDictionary<string, string> clauses)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        clauses = parsed;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        var matches = ClauseScanner.Matches(trimmed);
        if (matches.Count == 0 || matches[0].Index != 0)
        {
            return false;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var valueStart = match.Index + match.Length;
            var valueEnd = i + 1 < matches.Count ? matches[i + 1].Index : trimmed.Length;
            var value = trimmed[valueStart..valueEnd].Trim();
            var name = match.Value.ToLowerInvariant();

            if (FlagClauses.Contains(name))
            {
                if (value.Length > 0)
                {
                    return false;
                }

                parsed[name] = "true";
                continue;
            }

            if (value.Length == 0)
            {
                return false;
            }

            parsed[name] = value;
        }

        return true;
    }
}
