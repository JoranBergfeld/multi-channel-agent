using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Sends one requested <see cref="Delivery"/> to its channel. Delivery is retried independently of
/// Turn processing, so a failed send must not throw for expected transient conditions used in tests.
/// </summary>
public interface IDeliverySender
{
    Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken);
}
