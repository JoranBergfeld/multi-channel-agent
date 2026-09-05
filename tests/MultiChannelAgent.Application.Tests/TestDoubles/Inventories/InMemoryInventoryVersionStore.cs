using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryVersionStore"/> for Application-layer unit tests. Like the
/// SQL store it stands in for, an Inventory it holds no version for is simply absent from the result
/// rather than reported as zero - so a caller that forgot to treat "never changed" as version zero
/// fails here exactly as it would in production.
/// </summary>
public sealed class InMemoryInventoryVersionStore : IInventoryVersionStore
{
    private readonly Dictionary<Guid, long> _versions = [];

    public void Set(Guid inventoryId, long version) => _versions[inventoryId] = version;

    public Task<IReadOnlyDictionary<Guid, long>> ReadAsync(
        IReadOnlyCollection<Guid> inventoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventoryIds);

        IReadOnlyDictionary<Guid, long> result = _versions
            .Where(pair => inventoryIds.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return Task.FromResult(result);
    }
}
