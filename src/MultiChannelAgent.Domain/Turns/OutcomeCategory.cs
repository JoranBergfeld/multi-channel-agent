namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// The semantic category of a terminal result, independent of whether processing itself succeeded.
/// These are the categories every tool result and conversational answer is expressed in: a request
/// that was understood, authorized, and answered has a category even when the answer is "nothing
/// matched" or "you may not see that".
/// </summary>
public enum OutcomeCategory
{
    /// <summary>The request was carried out and answered.</summary>
    Completed,

    /// <summary>The request needs explicit confirmation before it can be carried out.</summary>
    ConfirmationRequired,

    /// <summary>The reference matched several Stock Entries; candidates are offered for clarification.</summary>
    Ambiguous,

    /// <summary>Nothing matched the request - or nothing the requester may know exists.</summary>
    NotFound,

    /// <summary>The requester may see the target but not perform the request.</summary>
    Forbidden,

    /// <summary>The request conflicts with current state (for example a semantic no-op).</summary>
    Conflict,

    /// <summary>The request itself could not be understood or was out of bounds.</summary>
    Invalid,

    /// <summary>
    /// The system, model, or a dependency failed to produce an answer. This is the only category that
    /// means "processing failed" rather than "here is the answer".
    /// </summary>
    TransientFailure,
}

/// <summary>The stable machine text each <see cref="OutcomeCategory"/> is exposed as at the application boundary.</summary>
public static class OutcomeCategoryExtensions
{
    public static string ToMachineText(this OutcomeCategory category) => category switch
    {
        OutcomeCategory.Completed => "completed",
        OutcomeCategory.ConfirmationRequired => "confirmation_required",
        OutcomeCategory.Ambiguous => "ambiguous",
        OutcomeCategory.NotFound => "not_found",
        OutcomeCategory.Forbidden => "forbidden",
        OutcomeCategory.Conflict => "conflict",
        OutcomeCategory.Invalid => "invalid",
        OutcomeCategory.TransientFailure => "transient_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled outcome category."),
    };
}
