using System.Globalization;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Shared name normalization for comparison and display: folds case and collapses whitespace only.
/// Deliberately never stems, singularizes, or infers synonyms - "warehouse" and "warehouses" (or
/// "each" and "eaches") must remain distinct names/terms per <c>CONTEXT.md</c>.
/// </summary>
public static class NameNormalization
{
    /// <summary>
    /// The tidy <em>display</em> form of a name: trimmed, with runs of internal whitespace collapsed
    /// to one space, and case left exactly as written. <see cref="Normalize"/> is what comparison and
    /// uniqueness use; this is what is stored and shown, so "Cardboard   Box" is kept as
    /// "Cardboard Box" rather than as typed, and never as "cardboard box".
    /// </summary>
    public static string Collapse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasWhitespace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                }

                lastWasWhitespace = true;
                continue;
            }

            lastWasWhitespace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasWhitespace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                }

                lastWasWhitespace = true;
                continue;
            }

            lastWasWhitespace = false;
            builder.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
