using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>Scripted <see cref="IDeliverySender"/> whose outcome is fixed by the test.</summary>
public sealed class ScriptedDeliverySender(bool succeeds) : IDeliverySender
{
    public List<Delivery> Attempts { get; } = [];

    public Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        Attempts.Add(delivery);
        return Task.FromResult(succeeds);
    }
}
