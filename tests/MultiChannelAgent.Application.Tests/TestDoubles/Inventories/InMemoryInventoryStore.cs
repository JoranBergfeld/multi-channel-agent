using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryStore"/> for Application-layer unit tests. A single lock
/// makes "look up by ClientRequestId, then insert" one indivisible step per creator - mirroring the
/// unique (CreatedByParticipantId, ClientRequestId) index the real SQL store enforces - so concurrent
/// duplicate creation attempts converge on one Inventory exactly like production.
/// </summary>
public sealed class InMemoryInventoryStore : IInventoryStore
{
    private readonly object _gate = new();
    private readonly List<Inventory> _inventories = [];
    private readonly Dictionary<InventoryId, Unit> _reservedEachUnits = [];
    private readonly List<Membership> _memberships = [];
    private readonly Func<ParticipantId, string> _resolveDisplayName;

    public InMemoryInventoryStore(Func<ParticipantId, string> resolveDisplayName)
    {
        _resolveDisplayName = resolveDisplayName;
    }

    public IReadOnlyList<Inventory> Inventories => _inventories;

    public IReadOnlyDictionary<InventoryId, Unit> ReservedEachUnits => _reservedEachUnits;

    public IReadOnlyList<Membership> Memberships => _memberships;

    public Task<Inventory?> FindByClientRequestIdAsync(ParticipantId createdBy, string clientRequestId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var match = _inventories.FirstOrDefault(i => i.CreatedByParticipantId == createdBy && i.ClientRequestId == clientRequestId);
            return Task.FromResult(match);
        }
    }

    public Task<InventoryCreationResult> CreateAsync(Inventory inventory, Unit reservedEachUnit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var existing = _inventories.FirstOrDefault(i =>
                i.CreatedByParticipantId == inventory.CreatedByParticipantId && i.ClientRequestId == inventory.ClientRequestId);
            if (existing is not null)
            {
                return Task.FromResult(new InventoryCreationResult(existing, WasAlreadyCreated: true));
            }

            _inventories.Add(inventory);
            _reservedEachUnits[inventory.Id] = reservedEachUnit;
            _memberships.Add(Membership.CreateOwner(inventory.Id, inventory.CreatedByParticipantId, inventory.CreatedAt));

            return Task.FromResult(new InventoryCreationResult(inventory, WasAlreadyCreated: false));
        }
    }

    public Task<MembershipRole?> FindRoleAsync(InventoryId inventoryId, ParticipantId participantId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var membership = _memberships.FirstOrDefault(m => m.InventoryId == inventoryId && m.ParticipantId == participantId);
            return Task.FromResult<MembershipRole?>(membership?.Role);
        }
    }

    public Task<IReadOnlyList<AuthorizedInventoryRecord>> ListAuthorizedAsync(ParticipantId participantId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var records = _memberships
                .Where(m => m.ParticipantId == participantId)
                .Select(m =>
                {
                    var inventory = _inventories.Single(i => i.Id == m.InventoryId);
                    var owner = _memberships.Single(om => om.InventoryId == m.InventoryId && om.Role == MembershipRole.Owner);
                    return new AuthorizedInventoryRecord(
                        inventory.Id,
                        inventory.Name,
                        owner.ParticipantId,
                        _resolveDisplayName(owner.ParticipantId),
                        m.Role);
                })
                .ToList();

            return Task.FromResult<IReadOnlyList<AuthorizedInventoryRecord>>(records);
        }
    }

    /// <summary>Test-only helper to grant a Membership directly, bypassing <see cref="CreateAsync"/>.</summary>
    public void GrantMembership(InventoryId inventoryId, ParticipantId participantId, MembershipRole role, DateTimeOffset createdAt)
    {
        lock (_gate)
        {
            _memberships.Add(new Membership
            {
                InventoryId = inventoryId,
                ParticipantId = participantId,
                Role = role,
                CreatedAt = createdAt,
            });
        }
    }

    /// <summary>Test-only helper to remove a Membership, simulating access loss.</summary>
    public void RevokeMembership(InventoryId inventoryId, ParticipantId participantId)
    {
        lock (_gate)
        {
            _memberships.RemoveAll(m => m.InventoryId == inventoryId && m.ParticipantId == participantId);
        }
    }

    /// <summary>Test-only helper mirroring the governance stores' upsert-or-change-role behavior.</summary>
    public void SetRole(InventoryId inventoryId, ParticipantId participantId, MembershipRole role, DateTimeOffset createdAt)
    {
        lock (_gate)
        {
            var index = _memberships.FindIndex(m => m.InventoryId == inventoryId && m.ParticipantId == participantId);
            if (index >= 0)
            {
                _memberships[index] = _memberships[index] with { Role = role };
            }
            else
            {
                _memberships.Add(new Membership
                {
                    InventoryId = inventoryId,
                    ParticipantId = participantId,
                    Role = role,
                    CreatedAt = createdAt,
                });
            }
        }
    }

    /// <summary>Test-only helper to seed an Inventory row directly, without the Owner-Membership side effect <see cref="CreateAsync"/> has.</summary>
    public void AddInventoryRecord(Inventory inventory)
    {
        lock (_gate)
        {
            _inventories.Add(inventory);
        }
    }
}
