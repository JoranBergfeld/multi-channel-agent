using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>What the Participant themselves said, in this Turn, about a pending proposal.</summary>
public enum DirectConfirmationEvidence
{
    /// <summary>Nothing in this Turn is an explicit answer, so nothing may be confirmed or rejected by it.</summary>
    None,

    /// <summary>The Participant explicitly approved, in their own direct content, in this very Turn.</summary>
    Confirmed,

    /// <summary>The Participant explicitly declined, in their own direct content, in this very Turn.</summary>
    Rejected,
}

/// <summary>
/// Derives <see cref="DirectConfirmationEvidence"/> from a Turn, and from nothing else. This is the
/// only thing that may authorize executing a stored proposal, which is why it is deliberately dull:
/// it reads <see cref="InboundTurn.ContentText"/> - already restricted to
/// <see cref="ContentProvenance.Direct"/> parts - and looks for one explicit answer at the very
/// start of it.
///
/// Three consequences follow, and each of them is an acceptance criterion:
///
/// <list type="bullet">
/// <item>Quoted, forwarded, attached, retrieved, tool-produced, and model-derived text cannot
/// confirm, because none of it is in <see cref="InboundTurn.ContentText"/> at all.</item>
/// <item>A model proposing <c>confirm_inventory_operation</c> on its own confirms nothing, because
/// the model does not contribute to this at all.</item>
/// <item>An interrupted utterance confirms nothing, because a cut-off sentence is not a statement of
/// intent - and it is not read as a rejection either, since inventing a refusal from silence is its
/// own kind of guessing.</item>
/// </list>
///
/// Matching is anchored at the start and requires a whole word, so "please confirm the order with the
/// supplier" and "yesterday we counted 40" are ordinary requests, not approvals.
/// </summary>
public static class DirectConfirmationEvidenceReader
{
    // Longest first, so "do not" is never read as the "do it"-shaped start of something else and
    // "confirmed" is never truncated to "confirm".
    private static readonly string[] Affirmatives = ["go ahead", "confirmed", "approved", "confirm", "approve", "do it", "yes"];

    private static readonly string[] Negatives = ["rejected", "cancelled", "do not", "cancel", "reject", "don't", "stop", "no"];

    public static DirectConfirmationEvidence Read(InboundTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // Voice-originated Turns can never provide confirmation evidence. Voice Live provides no
        // trusted recognition-confidence signal, so all voice confirmation attempts are clarification-
        // only. The Participant must use visible text input to confirm or reject.
        if (turn.InputModality == InputModality.Voice)
        {
            return DirectConfirmationEvidence.None;
        }

        if (turn.WasInterrupted)
        {
            return DirectConfirmationEvidence.None;
        }

        var text = turn.ContentText.TrimStart();

        // Negatives are considered first: declining is the safe reading of an ambiguous answer, and
        // the two vocabularies share no leading word, so this ordering only ever matters if one is
        // later added carelessly.
        if (StartsWithAnswer(text, Negatives))
        {
            return DirectConfirmationEvidence.Rejected;
        }

        return StartsWithAnswer(text, Affirmatives) ? DirectConfirmationEvidence.Confirmed : DirectConfirmationEvidence.None;
    }

    private static bool StartsWithAnswer(string text, string[] answers)
    {
        foreach (var answer in answers)
        {
            if (!text.StartsWith(answer, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The answer must stand as its own word: "confirmation is needed" is not a confirmation,
            // and "nobody" is not a "no".
            if (text.Length == answer.Length || !char.IsLetterOrDigit(text[answer.Length]))
            {
                return true;
            }
        }

        return false;
    }
}
