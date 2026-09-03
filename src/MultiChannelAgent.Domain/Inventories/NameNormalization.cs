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
