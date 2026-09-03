using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The durable response part a conversational read leaves behind: one answered List Turn records
/// exactly one channel-neutral Delivery, with its own identity, distinct from the Turn and the
/// Outcome. Duplicate submission never reprocesses the Turn nor adds a second response part, and
/// Delivery dispatch (including a retry pass) never reruns processing.
/// </summary>
internal static class StockReadDeliveryScenario
{
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var participant = await ConversationTestClient.SignInAsync(httpClient, "Reading Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Read Warehouse");
        await SeedStockEntryAsync(factory, inventoryId, "Steel Bolts", 12m);

        var turnId = await participant.SubmitAcceptedTurnAsync("native-read-1", "list stock");
        Assert.Equal(1, await ProcessPendingAsync(factory));

        var outcome = await participant.GetOutcomeAsync(turnId);
        Assert.Equal("completed", outcome!.Value.GetProperty("status").GetString());
        Assert.Equal("stock_list", outcome.Value.GetProperty("payload").GetProperty("kind").GetString());

        // Exactly one response part, with its own identity - never the Turn's or the Outcome's.
        var delivery = Assert.Single(outcome.Value.GetProperty("deliveries").EnumerateArray());
        var deliveryId = delivery.GetProperty("deliveryId").GetGuid();
        Assert.NotEqual(Guid.Empty, deliveryId);
        Assert.NotEqual(turnId, deliveryId);
        Assert.Equal("conversation", delivery.GetProperty("channel").GetString());
        Assert.Equal("pending", delivery.GetProperty("status").GetString());
        Assert.Equal(1, await CountDeliveriesAsync(factory, turnId));

        // At-least-once redelivery of the same native message: same Turn, no reprocessing, and no
        // second response part.
        var duplicate = await participant.SubmitTurnAsync("native-read-1", "list stock");
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal(0, await ProcessPendingAsync(factory));
        Assert.Equal(1, await CountDeliveriesAsync(factory, turnId));

        // Delivery is dispatched and retried independently: a second dispatch pass neither reruns
        // processing nor creates or re-sends anything.
        Assert.Equal(1, await DispatchPendingAsync(factory));
        Assert.Equal(0, await DispatchPendingAsync(factory));

        var afterDispatch = await participant.GetOutcomeAsync(turnId);
        var dispatched = Assert.Single(afterDispatch!.Value.GetProperty("deliveries").EnumerateArray());
        Assert.Equal(deliveryId, dispatched.GetProperty("deliveryId").GetGuid());
        Assert.Equal("delivered", dispatched.GetProperty("status").GetString());
        Assert.Equal(1, dispatched.GetProperty("attempts").GetInt32());
        Assert.Equal(
            outcome.Value.GetProperty("summary").GetString(),
            afterDispatch.Value.GetProperty("summary").GetString());
        Assert.Equal(1, await CountDeliveriesAsync(factory, turnId));
    }

    private static async Task SeedStockEntryAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string name, decimal quantity)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unit = db.Units.Single(u => u.InventoryId == inventoryId);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unit.Id,
            LocationId = null,
            LocationUniquenessKey = Guid.Empty,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountDeliveriesAsync(WebApplicationFactory<Program> factory, Guid turnId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.Deliveries.AsNoTracking().CountAsync(d => d.TurnId == turnId);
    }

    private static async Task<int> ProcessPendingAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>().ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<int> DispatchPendingAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DeliveryDispatchCoordinator>().DispatchPendingAsync(CancellationToken.None);
    }
}
