using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// The one place the durable Outcome row and the domain <see cref="Outcome"/> are translated between,
/// so the recorded semantic category (and the processing status derived from it) can never drift
/// between the write path and the read path.
/// </summary>
internal static class OutcomeEntityMapping
{
    public static OutcomeEntity ToEntity(Outcome outcome) => new()
    {
        TurnId = outcome.TurnId.Value,
        Status = outcome.Status == OutcomeStatus.Completed ? OutcomeEntityStatus.Completed : OutcomeEntityStatus.Failed,
        Category = ToEntityCategory(outcome.Category),
        Code = outcome.Code,
        Summary = outcome.Summary,
        Payload = outcome.Payload,
        PayloadExpiresAtTicks = outcome.PayloadExpiresAt?.UtcTicks,
        CreatedAt = outcome.CreatedAt,
    };

    public static Outcome ToDomain(OutcomeEntity entity) =>
        Outcome.Record(
            new TurnId(entity.TurnId),
            ToDomainCategory(entity.Category),
            entity.Code,
            entity.Summary,
            entity.CreatedAt,
            entity.Payload) with
        {
            // Read back rather than recomputed: a payload discarded by cleanup must stay discarded,
            // and one recorded under an earlier retention window keeps the expiry it was given.
            PayloadExpiresAt = entity.PayloadExpiresAtTicks is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null,
        };

    private static OutcomeEntityCategory ToEntityCategory(OutcomeCategory category) => category switch
    {
        OutcomeCategory.Completed => OutcomeEntityCategory.Completed,
        OutcomeCategory.ConfirmationRequired => OutcomeEntityCategory.ConfirmationRequired,
        OutcomeCategory.Ambiguous => OutcomeEntityCategory.Ambiguous,
        OutcomeCategory.NotFound => OutcomeEntityCategory.NotFound,
        OutcomeCategory.Forbidden => OutcomeEntityCategory.Forbidden,
        OutcomeCategory.Conflict => OutcomeEntityCategory.Conflict,
        OutcomeCategory.Invalid => OutcomeEntityCategory.Invalid,
        OutcomeCategory.TransientFailure => OutcomeEntityCategory.TransientFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled outcome category."),
    };

    private static OutcomeCategory ToDomainCategory(OutcomeEntityCategory category) => category switch
    {
        OutcomeEntityCategory.Completed => OutcomeCategory.Completed,
        OutcomeEntityCategory.ConfirmationRequired => OutcomeCategory.ConfirmationRequired,
        OutcomeEntityCategory.Ambiguous => OutcomeCategory.Ambiguous,
        OutcomeEntityCategory.NotFound => OutcomeCategory.NotFound,
        OutcomeEntityCategory.Forbidden => OutcomeCategory.Forbidden,
        OutcomeEntityCategory.Conflict => OutcomeCategory.Conflict,
        OutcomeEntityCategory.Invalid => OutcomeCategory.Invalid,
        OutcomeEntityCategory.TransientFailure => OutcomeCategory.TransientFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unhandled outcome category."),
    };
}
