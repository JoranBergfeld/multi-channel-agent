using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// One signed-in web Participant Adding, Removing, and Setting Stock conversationally through the real
/// HTTP application boundary: exact decimal Quantity, Equivalent Stock rather than duplicates, an
/// existing Note kept, underflow refused, Set-to-zero held for confirmation, every typed refusal, one
/// audit fact per completed mutation, retries that cannot double an effect, and the authoritative
/// workspace projection agreeing after each change. Shared by the SQL Server-backed scenario and its
/// Docker-free SQLite twin so both prove the identical externally observable behavior.
/// </summary>
internal static class StockMutationScenario
{
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Mutating Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Mutation Warehouse");

        // Add creates Equivalent Stock at the exact decimal amount, with the Note it was given.
        var created = await CompleteAsync(factory, owner, "native-add-1", "add stock Steel Bolts quantity 12.5 note Blue box");
        var createdEntry = MutationEntry(created, "add");
        Assert.True(createdEntry.GetProperty("created").GetBoolean());
        Assert.Equal("Steel Bolts", createdEntry.GetProperty("name").GetString());
        Assert.Equal("each", createdEntry.GetProperty("unit").GetString());
        Assert.Null(createdEntry.GetProperty("location").GetString());
        Assert.Equal("Blue box", createdEntry.GetProperty("note").GetString());
        Assert.Equal("0", createdEntry.GetProperty("previousQuantity").GetString());
        Assert.Equal("12.5", createdEntry.GetProperty("quantity").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "12.5");

        // Add again increases the SAME Stock Entry and keeps its conflicting Note rather than
        // overwriting it - and says so.
        var increased = await CompleteAsync(factory, owner, "native-add-2", "add stock steel bolts quantity 2.25 note Red box");
        var increasedEntry = MutationEntry(increased, "add");
        Assert.False(increasedEntry.GetProperty("created").GetBoolean());
        Assert.Equal(createdEntry.GetProperty("stockEntryId").GetString(), increasedEntry.GetProperty("stockEntryId").GetString());
        Assert.Equal("14.75", increasedEntry.GetProperty("quantity").GetString());
        Assert.Equal("Blue box", increasedEntry.GetProperty("note").GetString());
        Assert.True(increasedEntry.GetProperty("notePreserved").GetBoolean());
        Assert.Equal(1, await CountStockEntriesAsync(factory, inventoryId));

        // Remove beyond the Quantity on hand is refused, and changes nothing.
        var underflow = await OutcomeAsync(factory, owner, "native-remove-1", "remove stock Steel Bolts quantity 20");
        Assert.Equal("conflict", underflow.GetProperty("category").GetString());
        Assert.Equal("insufficient_quantity", underflow.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "14.75");

        // Remove within it decreases exactly.
        var removed = await CompleteAsync(factory, owner, "native-remove-2", "remove stock Steel Bolts quantity 4.75");
        Assert.Equal("10", MutationEntry(removed, "remove").GetProperty("quantity").GetString());

        // Set replaces exactly.
        var set = await CompleteAsync(factory, owner, "native-set-1", "set stock Steel Bolts quantity 7.125");
        Assert.Equal("7.125", MutationEntry(set, "set").GetProperty("quantity").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // Set to zero clears stock, so it is held for explicit confirmation and applies nothing.
        var confirmation = await OutcomeAsync(factory, owner, "native-set-zero", "set stock Steel Bolts quantity 0");
        Assert.Equal("confirmation_required", confirmation.GetProperty("category").GetString());
        Assert.Equal("confirmation_required", confirmation.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // A Quantity that is not exact invariant decimal text is refused as invalid.
        var invalid = await OutcomeAsync(factory, owner, "native-invalid-1", "add stock Steel Bolts quantity lots");
        Assert.Equal("invalid", invalid.GetProperty("category").GetString());
        Assert.Equal("invalid_quantity", invalid.GetProperty("code").GetString());

        // Stock that is not there is simply not found.
        var missing = await OutcomeAsync(factory, owner, "native-missing-1", "remove stock Brass Rivets quantity 1");
        Assert.Equal("not_found", missing.GetProperty("category").GetString());

        // A Location this Inventory does not have is reported, never created implicitly.
        var unknownReference = await OutcomeAsync(factory, owner, "native-unknown-1", "add stock Steel Bolts quantity 1 in Loading Bay");
        Assert.Equal("not_found", unknownReference.GetProperty("category").GetString());
        Assert.Equal("reference_not_found", unknownReference.GetProperty("code").GetString());

        // Four completed mutations so far (create, increase, remove, set), and exactly one minimal
        // audit fact and one ledger row each. The refused ones left nothing behind at all.
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));
        Assert.Equal(4, await CountStockOperationsAsync(factory, inventoryId));

        // Retrying the very same native message never applies a second effect: the recorded Outcome
        // comes straight back, no Turn is reprocessed, and Stock is untouched.
        var duplicate = await owner.SubmitTurnAsync("native-add-2", "add stock steel bolts quantity 2.25 note Red box");
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("14.75", MutationEntry(duplicateBody, "add").GetProperty("quantity").GetString());
        Assert.Equal(0, await ProcessPendingAsync(factory));
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));
        Assert.Equal(4, await CountStockOperationsAsync(factory, inventoryId));
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // A Viewer may see this Inventory but may not change it, and the refusal touches nothing.
        var viewer = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Watching Viewer");
        await owner.GrantMembershipAsync(inventoryId, viewer.ParticipantIdentifier, "Viewer");
        await viewer.SelectInventoryAsync(inventoryId);

        var forbidden = await OutcomeAsync(factory, viewer, "native-viewer-1", "add stock Steel Bolts quantity 1");
        Assert.Equal("forbidden", forbidden.GetProperty("category").GetString());
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // An ambiguous reference offers candidates rather than guessing which Stock Entry was meant.
        await SeedLocatedStockAsync(factory, inventoryId, "Steel Bolts", 3m, "Shelf A");
        var ambiguous = await OutcomeAsync(factory, owner, "native-ambiguous-1", "add stock Steel Bolts quantity 1");
        Assert.Equal("ambiguous", ambiguous.GetProperty("category").GetString());
        Assert.Equal("stock_find", ambiguous.GetProperty("payload").GetProperty("kind").GetString());
        Assert.Equal(2, ambiguous.GetProperty("payload").GetProperty("candidates").EnumerateArray().Count());
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));

        // Naming the Location makes it exact again.
        var narrowed = await CompleteAsync(factory, owner, "native-narrowed-1", "add stock Steel Bolts quantity 1 in Shelf A");
        var narrowedEntry = MutationEntry(narrowed, "add");
        Assert.Equal("Shelf A", narrowedEntry.GetProperty("location").GetString());
        Assert.Equal("4", narrowedEntry.GetProperty("quantity").GetString());
    }

    /// <summary>Submits one Turn, drives processing deterministically, and returns its recorded terminal Outcome.</summary>
    private static async Task<JsonElement> OutcomeAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var turnId = await client.SubmitAcceptedTurnAsync(nativeMessageId, contentText);
        Assert.Equal(1, await ProcessPendingAsync(factory));

        var outcome = await client.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);
        return outcome!.Value;
    }

    /// <summary>The same, asserting the Turn completed rather than merely reaching some terminal Outcome.</summary>
    private static async Task<JsonElement> CompleteAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var outcome = await OutcomeAsync(factory, client, nativeMessageId, contentText);
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal("completed", outcome.GetProperty("category").GetString());
        return outcome;
    }

    private static JsonElement MutationEntry(JsonElement outcome, string expectedOperation)
    {
        var payload = outcome.GetProperty("payload");
        Assert.Equal("stock_mutation", payload.GetProperty("kind").GetString());
        Assert.Equal(expectedOperation, payload.GetProperty("operation").GetString());
        return payload.GetProperty("entry");
    }

    /// <summary>
    /// Asserts the authoritative workspace projection - the very endpoint the Inventory panel refetches
    /// once a terminal Outcome arrives - already reports what the conversation just changed.
    /// </summary>
    private static async Task AssertProjectionAsync(
        ConversationTestClient client, Guid inventoryId, string name, string expectedQuantity)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock"));
        var projection = await response.Content.ReadFromJsonAsync<JsonElement>();

        var row = projection.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == name && r.GetProperty("location").ValueKind == JsonValueKind.Null);

        Assert.Equal(expectedQuantity, row.GetProperty("quantity").GetString());
    }

    private static async Task<int> ProcessPendingAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>()
            .ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<int> CountStockEntriesAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockEntries.AsNoTracking().CountAsync(e => e.InventoryId == inventoryId);
    }

    private static async Task<int> CountStockAuditsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InventoryAudits.AsNoTracking().CountAsync(a =>
            a.InventoryId == inventoryId
            && (a.EventType == "StockAdded" || a.EventType == "StockRemoved" || a.EventType == "StockSet"));
    }

    private static async Task<int> CountStockOperationsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockOperations.AsNoTracking().CountAsync(o => o.InventoryId == inventoryId);
    }

    /// <summary>
    /// Seeds a second Stock Entry with the same name in a real Location, so the next reference to that
    /// name genuinely matches more than one Stock Entry.
    /// </summary>
    private static async Task SeedLocatedStockAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string name, decimal quantity, string locationName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unitId = db.Units.AsNoTracking().Single(u => u.InventoryId == inventoryId).Id;
        var locationId = Guid.NewGuid();

        db.Locations.Add(new LocationEntity
        {
            Id = locationId,
            InventoryId = inventoryId,
            Name = locationName,
            NormalizedName = NameNormalization.Normalize(locationName),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}
