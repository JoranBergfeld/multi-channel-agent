using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockFindingServiceTests
{
    private static readonly ParticipantId Viewer = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StockEntrySummary Row(string name, string idHex) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        EachUnit,
        "each",
        null,
        null,
        null,
        Quantity.Create(1m));

    private static (StockFindingService Service, InMemoryStockStore StockStore) CreateService()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();

        return (new StockFindingService(stockStore, authorizationService), stockStore);
    }

    [Fact]
    public async Task No_match_is_not_found()
    {
        var (service, _) = CreateService();

        var result = await service.FindAsync(Viewer, SomeInventory, "Bolts", null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.NotFound, result.Kind);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task Exactly_one_match_by_name_is_completed()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));

        var result = await service.FindAsync(Viewer, SomeInventory, "  bolts  ", null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        var candidate = Assert.Single(result.View!.Candidates);
        Assert.Equal("Bolts", candidate.Name);
        Assert.False(result.View.HasMoreCandidates);
    }

    [Fact]
    public async Task An_opaque_id_reference_matches_by_id_before_any_name_matching()
    {
        var (service, stockStore) = CreateService();
        var target = Row("Bolts", "10000000");
        stockStore.Add(SomeInventory, target);
        stockStore.Add(SomeInventory, Row("Nuts", "20000000"));

        var result = await service.FindAsync(Viewer, SomeInventory, target.Id.ToString(), null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Completed, result.Kind);
        Assert.Equal(target.Id.ToString(), Assert.Single(result.View!.Candidates).Id);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task Two_to_five_matches_are_ambiguous_with_every_candidate(int count)
    {
        var (service, stockStore) = CreateService();
        for (var i = 0; i < count; i++)
        {
            stockStore.Add(SomeInventory, Row("Bolts", $"{i + 1:00000000}"));
        }

        var result = await service.FindAsync(Viewer, SomeInventory, "Bolts", null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Ambiguous, result.Kind);
        Assert.Equal(count, result.View!.Candidates.Count);
        Assert.False(result.View.HasMoreCandidates);
    }

    [Fact]
    public async Task More_than_five_matches_are_ambiguous_capped_at_five_with_more_flagged()
    {
        var (service, stockStore) = CreateService();
        for (var i = 0; i < 8; i++)
        {
            stockStore.Add(SomeInventory, Row("Bolts", $"{i + 1:00000000}"));
        }

        var result = await service.FindAsync(Viewer, SomeInventory, "Bolts", null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Ambiguous, result.Kind);
        Assert.Equal(5, result.View!.Candidates.Count);
        Assert.True(result.View.HasMoreCandidates);
    }

    [Fact]
    public async Task A_non_member_gets_not_found_never_a_distinct_forbidden_signal()
    {
        var (service, stockStore) = CreateService();
        stockStore.Add(SomeInventory, Row("Bolts", "10000000"));

        var result = await service.FindAsync(Stranger, SomeInventory, "Bolts", null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.NotFound, result.Kind);
        Assert.Null(result.View);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_reference_is_invalid_not_a_500(string? reference)
    {
        var (service, _) = CreateService();

        var result = await service.FindAsync(Viewer, SomeInventory, reference, null, Now, CancellationToken.None);

        Assert.Equal(StockFindResultKind.Invalid, result.Kind);
    }
}
