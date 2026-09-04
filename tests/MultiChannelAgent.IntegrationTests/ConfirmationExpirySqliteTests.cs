using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The ten-minute confirmation lifetime, end to end, on controlled time - so it is proved as
/// behavior rather than asserted on a domain type alone, and without a test ever sleeping.
/// </summary>
public sealed class ConfirmationExpirySqliteTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
    private SqliteWebApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory(_time);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_confirmation_older_than_ten_minutes_executes_nothing()
    {
        var factory = _factory!;
        var owner = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Patient Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Expiry Warehouse");

        await OutcomeAsync(factory, owner, "native-add-1", "add stock Steel Bolts quantity 4");

        // Clearing stock is confirmed, so this leaves an exact proposal and an untouched Stock Entry.
        var clearing = await OutcomeAsync(factory, owner, "native-set-zero", "set stock Steel Bolts quantity 0");
        Assert.Equal("confirmation_required", clearing.GetProperty("category").GetString());
        var token = clearing.GetProperty("payload").GetProperty("token").GetString()!;

        _time.Advance(TimeSpan.FromMinutes(ConfirmationProposal.LifetimeMinutes) + TimeSpan.FromSeconds(1));

        var expired = await OutcomeAsync(factory, owner, "native-confirm-1", $"confirm {token}");

        Assert.Equal("conflict", expired.GetProperty("category").GetString());
        Assert.Equal("proposal_expired", expired.GetProperty("code").GetString());
        Assert.Equal(4m, await QuantityAsync(factory, inventoryId));
        Assert.Equal(0, await CountChangeSetsAsync(factory, inventoryId));

        // The sweep then frees the conversation's one pending slot rather than leaving it occupied.
        _time.Advance(TimeSpan.FromMinutes(5));
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ConfirmationProposalCleanupCoordinator>()
                .SweepAsync(CancellationToken.None);
        }

        Assert.Equal(nameof(ProposalStatus.Expired), await ProposalStatusAsync(factory));
    }

    private static async Task<JsonElement> OutcomeAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var turnId = await client.SubmitAcceptedTurnAsync(nativeMessageId, contentText);

        using (var scope = factory.Services.CreateScope())
        {
            Assert.Equal(
                1,
                await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>()
                    .ProcessPendingAsync(CancellationToken.None));
        }

        var outcome = await client.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);
        return outcome!.Value;
    }

    private static async Task<decimal> QuantityAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == inventoryId).Select(e => e.Quantity).SingleAsync();
    }

    private static async Task<int> CountChangeSetsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockChangeSetOperations.AsNoTracking().CountAsync(o => o.InventoryId == inventoryId);
    }

    private static async Task<string> ProposalStatusAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.ConfirmationProposals.AsNoTracking().Select(p => p.Status).SingleAsync();
    }
}
