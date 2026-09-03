using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Atomically records the terminal result of processing one Turn: its <see cref="Outcome"/>, any
/// requested <see cref="Delivery"/> records, and inbox completion, as a single durable operation. This
/// is the invariant that keeps Turn processing retries safe: once this operation commits, the Turn can
/// never be re-claimed for processing, so a retry can never rerun model planning for it nor hit a
/// duplicate Outcome; and if it fails, none of the three effects are recorded, so a later retry always
/// finds the Turn exactly as it was before the attempt and can safely start over. Delivery dispatch
/// retries never rerun processing because Deliveries are only ever created here, together with the
/// Outcome that gates re-claiming.
/// </summary>
public interface ITurnResultStore
{
    Task RecordAsync(Outcome outcome, IReadOnlyList<Delivery> deliveries, CancellationToken cancellationToken);
}
