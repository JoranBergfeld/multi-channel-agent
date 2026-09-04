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
/// The whole confirmed stock mutation protocol through the real HTTP application boundary: Move that
/// splits, Move that merges and therefore asks first, Rename that preserves identity, Rename that
/// collides and therefore asks first, Forget refused while Stock is on hand and confirmed once it is
/// empty, atomic batches, a failed batch that changes nothing, replacement and interruption and an
/// Inventory switch all invalidating a pending proposal, a Viewer who may not confirm, and retries
/// that return the recorded Outcome without ever re-planning. Shared by the SQL Server-backed
/// scenario and its Docker-free SQLite twin so both prove the identical externally observable
/// behavior.
/// </summary>
internal static class ConfirmedStockMutationScenario
{
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Confirming Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Confirmation Warehouse");
        await SeedLocationAsync(factory, inventoryId, "Shelf A");
        await SeedLocationAsync(factory, inventoryId, "Shelf B");

        await CompleteAsync(factory, owner, "native-seed-1", "add stock Steel Bolts quantity 10");
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "10");

        // 2. A partial Move keeps every identity, so it applies immediately.
        var split = await CompleteAsync(factory, owner, "native-move-1", "move stock Steel Bolts quantity 3 to Shelf A");
        var splitChange = SingleChange(split, "stock_changes");
        Assert.Equal("split", splitChange.GetProperty("effect").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7");
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "3", location: "Shelf A");
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, "StockMoved"));
        Assert.Equal(1, await CountChangeSetsAsync(factory, inventoryId));

        // 3. A Move that merges away a Stock Entry ends an identity, so it is proposed rather than applied.
        var unlocatedId = await StockEntryIdAsync(factory, inventoryId, "steel bolts", located: false);
        var shelfAId = await StockEntryIdAsync(factory, inventoryId, "steel bolts", located: true);
        var mergeProposal = await OutcomeAsync(factory, owner, "native-move-2", "move stock Steel Bolts unlocated all to Shelf A");
        var proposedMerge = SingleChange(mergeProposal, "stock_proposal");
        Assert.Equal("confirmation_required", mergeProposal.GetProperty("category").GetString());
        Assert.Equal("merged", proposedMerge.GetProperty("effect").GetString());
        Assert.Equal(shelfAId.ToString(), proposedMerge.GetProperty("survivingStockEntryId").GetString());
        Assert.Equal(unlocatedId.ToString(), proposedMerge.GetProperty("retiredStockEntryId").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7");
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, "StockMoved"));
        Assert.Equal(1, await CountChangeSetsAsync(factory, inventoryId));

        // 4. Rejecting it changes nothing at all.
        var rejected = await CompleteAsync(factory, owner, "native-reject-1", "reject");
        Assert.Equal("rejected", rejected.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7");
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "3", location: "Shelf A");
        Assert.Equal(1, await CountChangeSetsAsync(factory, inventoryId));

        // 5. Confirming a fresh proposal executes it exactly once, naming both identities.
        var reproposed = await OutcomeAsync(factory, owner, "native-move-3", "move stock Steel Bolts unlocated all to Shelf A");
        var mergeToken = TokenOf(reproposed);
        var merged = await CompleteAsync(factory, owner, "native-confirm-1", $"confirm {mergeToken}");
        var appliedMerge = SingleChange(merged, "stock_changes");
        Assert.Equal("merged", appliedMerge.GetProperty("effect").GetString());
        Assert.Equal(shelfAId.ToString(), appliedMerge.GetProperty("survivingStockEntryId").GetString());
        Assert.Equal(unlocatedId.ToString(), appliedMerge.GetProperty("retiredStockEntryId").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "10", location: "Shelf A");
        Assert.Equal(1, await CountStockEntriesAsync(factory, inventoryId));
        Assert.Equal(2, await CountAuditsAsync(factory, inventoryId, "StockMoved"));
        Assert.Equal(2, await CountChangeSetsAsync(factory, inventoryId));

        // A confirmed proposal is settled inside the very transaction that applied it, and must be as
        // sweepable as one that was rejected - otherwise the commonest terminal state is retained for
        // the life of the database.
        Assert.Equal(0, await CountUnsweepableSettledProposalsAsync(factory));

        // 6. The token is single use: presenting it again finds nothing pending.
        var reused = await OutcomeAsync(factory, owner, "native-confirm-2", $"confirm {mergeToken}");
        Assert.Equal("not_found", reused.GetProperty("category").GetString());
        Assert.Equal("proposal_not_found", reused.GetProperty("code").GetString());
        Assert.Equal(2, await CountChangeSetsAsync(factory, inventoryId));

        // 7. A Rename with no collision preserves the Stock Entry's identity, so it applies immediately.
        var renamed = await CompleteAsync(factory, owner, "native-rename-1", "rename stock Steel Bolts to Brass Rivets");
        var renamedChange = SingleChange(renamed, "stock_changes");
        Assert.Equal("renamed", renamedChange.GetProperty("effect").GetString());
        Assert.Equal(shelfAId.ToString(), renamedChange.GetProperty("survivingStockEntryId").GetString());
        Assert.Equal(JsonValueKind.Null, renamedChange.GetProperty("retiredStockEntryId").ValueKind);
        await AssertProjectionAsync(owner, inventoryId, "Brass Rivets", "10", location: "Shelf A");

        // 8. A Rename that collides with Equivalent Stock retires an identity, so it asks first.
        var collidingId = await SeedStockAsync(factory, inventoryId, "Copper Pins", 2m, "Shelf A");
        var collision = await OutcomeAsync(
            factory, owner, "native-rename-2", "rename stock Copper Pins in Shelf A to Brass Rivets");
        var proposedCollision = SingleChange(collision, "stock_proposal");
        Assert.Equal("confirmation_required", collision.GetProperty("category").GetString());
        Assert.Equal("rename_merged", proposedCollision.GetProperty("effect").GetString());
        Assert.Equal(shelfAId.ToString(), proposedCollision.GetProperty("survivingStockEntryId").GetString());
        Assert.Equal(collidingId.ToString(), proposedCollision.GetProperty("retiredStockEntryId").GetString());

        var mergedRename = await CompleteAsync(factory, owner, "native-confirm-3", $"confirm {TokenOf(collision)}");
        var appliedCollision = SingleChange(mergedRename, "stock_changes");
        Assert.Equal(shelfAId.ToString(), appliedCollision.GetProperty("survivingStockEntryId").GetString());
        Assert.Equal(collidingId.ToString(), appliedCollision.GetProperty("retiredStockEntryId").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Brass Rivets", "12", location: "Shelf A");

        // One audit fact for the identity-preserving Rename in step 7, and one for this merging one.
        Assert.Equal(2, await CountAuditsAsync(factory, inventoryId, "StockRenamed"));

        // 9. Forget can never stand in for Remove.
        var forgetRefused = await OutcomeAsync(factory, owner, "native-forget-1", "forget stock Brass Rivets");
        Assert.Equal("conflict", forgetRefused.GetProperty("category").GetString());
        Assert.Equal("forget_requires_zero_quantity", forgetRefused.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Brass Rivets", "12", location: "Shelf A");

        // 10. Clearing stock asks first, and so does forgetting the empty record it leaves behind.
        var clearProposal = await OutcomeAsync(factory, owner, "native-set-zero", "set stock Brass Rivets quantity 0");
        Assert.Equal("quantity_cleared", SingleChange(clearProposal, "stock_proposal").GetProperty("effect").GetString());
        await CompleteAsync(factory, owner, "native-confirm-4", $"confirm {TokenOf(clearProposal)}");
        await AssertProjectionAsync(owner, inventoryId, "Brass Rivets", "0", location: "Shelf A", includeZero: true);

        var forgetProposal = await OutcomeAsync(factory, owner, "native-forget-2", "forget stock Brass Rivets");
        Assert.Equal("forgotten", SingleChange(forgetProposal, "stock_proposal").GetProperty("effect").GetString());
        await CompleteAsync(factory, owner, "native-confirm-5", $"confirm {TokenOf(forgetProposal)}");
        Assert.Equal(0, await CountStockEntriesAsync(factory, inventoryId));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, "StockForgotten"));
        await AssertAbsentFromProjectionAsync(owner, inventoryId, "Brass Rivets");

        // 11. Every batch is proposed, however low-risk each of its changes is on its own.
        var auditsBeforeBatch = await CountAuditsAsync(factory, inventoryId, "StockAdded");
        var ledgersBeforeBatch = await CountChangeSetsAsync(factory, inventoryId);
        var batch = await OutcomeAsync(
            factory, owner, "native-batch-1", "change stock: add Copper Nails quantity 4; add Zinc Screws quantity 5");
        Assert.Equal("confirmation_required", batch.GetProperty("category").GetString());
        Assert.Equal(2, Changes(batch, "stock_proposal").Count);
        Assert.Equal(0, await CountStockEntriesAsync(factory, inventoryId));

        var appliedBatch = await CompleteAsync(factory, owner, "native-confirm-6", $"confirm {TokenOf(batch)}");
        Assert.Equal(2, Changes(appliedBatch, "stock_changes").Count);
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");
        await AssertProjectionAsync(owner, inventoryId, "Zinc Screws", "5");
        Assert.Equal(auditsBeforeBatch + 2, await CountAuditsAsync(factory, inventoryId, "StockAdded"));
        Assert.Equal(ledgersBeforeBatch + 1, await CountChangeSetsAsync(factory, inventoryId));

        // 12. A batch whose state moved underneath it changes nothing at all.
        var auditsBeforeConflict = await CountAuditsAsync(factory, inventoryId, "StockAdded");
        var ledgersBeforeConflict = await CountChangeSetsAsync(factory, inventoryId);
        var conflictBatch = await OutcomeAsync(
            factory, owner, "native-batch-2", "change stock: add Copper Nails quantity 1; add Zinc Screws quantity 1");
        Assert.Equal("confirmation_required", conflictBatch.GetProperty("category").GetString());
        await BumpConcurrencyStampAsync(factory, inventoryId, "copper nails");

        var conflicted = await OutcomeAsync(factory, owner, "native-confirm-7", $"confirm {TokenOf(conflictBatch)}");
        Assert.Equal("conflict", conflicted.GetProperty("category").GetString());
        Assert.Equal("state_changed", conflicted.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");
        await AssertProjectionAsync(owner, inventoryId, "Zinc Screws", "5");
        Assert.Equal(auditsBeforeConflict, await CountAuditsAsync(factory, inventoryId, "StockAdded"));
        Assert.Equal(ledgersBeforeConflict, await CountChangeSetsAsync(factory, inventoryId));

        // 13. A replacement proposal makes the previous one unconfirmable.
        var firstProposal = await OutcomeAsync(factory, owner, "native-replace-1", "set stock Copper Nails quantity 0");
        var staleToken = TokenOf(firstProposal);
        await OutcomeAsync(factory, owner, "native-replace-2", "change stock: add Copper Nails quantity 1; add Zinc Screws quantity 1");

        var stale = await OutcomeAsync(factory, owner, "native-confirm-8", $"confirm {staleToken}");
        Assert.Equal("invalid", stale.GetProperty("category").GetString());
        Assert.Equal("proposal_token_mismatch", stale.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");

        // 14. An interrupted Turn authorizes nothing and invalidates whatever was pending.
        var interruptedTurnId = await owner.SubmitInterruptedTurnAsync("native-interrupt-1", "confirm");
        Assert.Equal(1, await ProcessPendingAsync(factory));
        var interrupted = await owner.GetOutcomeAsync(interruptedTurnId);
        Assert.NotNull(interrupted);
        Assert.NotEqual("completed", interrupted!.Value.GetProperty("category").GetString());
        Assert.DoesNotContain("Copper Nails", interrupted.Value.GetProperty("summary").GetString());
        Assert.Equal(0, await CountPendingProposalsAsync(factory));
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");

        // 15. Switching the Active Inventory invalidates a pending proposal.
        var switchProposal = await OutcomeAsync(factory, owner, "native-switch-1", "set stock Copper Nails quantity 0");
        var switchToken = TokenOf(switchProposal);
        var otherInventoryId = await owner.CreateAndSelectInventoryAsync("Second Warehouse");
        await owner.SelectInventoryAsync(inventoryId);

        var afterSwitch = await OutcomeAsync(factory, owner, "native-confirm-9", $"confirm {switchToken}");
        Assert.Equal("not_found", afterSwitch.GetProperty("category").GetString());
        Assert.Equal("proposal_not_found", afterSwitch.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");
        Assert.NotEqual(inventoryId, otherInventoryId);

        // 16. A Viewer may see this Inventory but may neither propose nor confirm anything in it.
        var viewerProposal = await OutcomeAsync(factory, owner, "native-viewer-seed", "set stock Copper Nails quantity 0");
        var viewerToken = TokenOf(viewerProposal);
        var viewer = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Watching Viewer");
        await owner.GrantMembershipAsync(inventoryId, viewer.ParticipantIdentifier, "Viewer");
        await viewer.SelectInventoryAsync(inventoryId);

        var forbidden = await OutcomeAsync(factory, viewer, "native-viewer-1", $"confirm {viewerToken}");
        Assert.Equal("forbidden", forbidden.GetProperty("category").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");

        // 17. Retrying a confirming native message returns its recorded Outcome and re-plans nothing.
        var auditsBeforeRetry = await CountAuditsAsync(factory, inventoryId, "StockAdded");
        var ledgersBeforeRetry = await CountChangeSetsAsync(factory, inventoryId);
        var duplicate = await owner.SubmitTurnAsync("native-confirm-6", $"confirm {TokenOf(batch)}");
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, Changes(duplicateBody, "stock_changes").Count);
        Assert.Equal(0, await ProcessPendingAsync(factory));
        Assert.Equal(auditsBeforeRetry, await CountAuditsAsync(factory, inventoryId, "StockAdded"));
        Assert.Equal(ledgersBeforeRetry, await CountChangeSetsAsync(factory, inventoryId));
        await AssertProjectionAsync(owner, inventoryId, "Copper Nails", "4");
    }

    // ---- Payload readers ----

    private static IReadOnlyList<JsonElement> Changes(JsonElement outcome, string expectedKind)
    {
        var payload = outcome.GetProperty("payload");
        Assert.Equal(expectedKind, payload.GetProperty("kind").GetString());
        return payload.GetProperty("changes").EnumerateArray().ToList();
    }

    private static JsonElement SingleChange(JsonElement outcome, string expectedKind) => Assert.Single(Changes(outcome, expectedKind));

    private static string TokenOf(JsonElement outcome)
    {
        var payload = outcome.GetProperty("payload");
        Assert.Equal("stock_proposal", payload.GetProperty("kind").GetString());
        var token = payload.GetProperty("token").GetString()!;
        Assert.True(ConfirmationToken.IsWellFormed(token));
        return token;
    }

    // ---- Turn driving ----

    private static async Task<JsonElement> OutcomeAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var turnId = await client.SubmitAcceptedTurnAsync(nativeMessageId, contentText);
        Assert.Equal(1, await ProcessPendingAsync(factory));

        var outcome = await client.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);
        return outcome!.Value;
    }

    private static async Task<JsonElement> CompleteAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var outcome = await OutcomeAsync(factory, client, nativeMessageId, contentText);
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal("completed", outcome.GetProperty("category").GetString());
        return outcome;
    }

    private static async Task<int> ProcessPendingAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>()
            .ProcessPendingAsync(CancellationToken.None);
    }

    // ---- Projection assertions ----

    private static async Task AssertProjectionAsync(
        ConversationTestClient client,
        Guid inventoryId,
        string name,
        string expectedQuantity,
        string? location = null,
        bool includeZero = false)
    {
        var row = await FindProjectionRowAsync(client, inventoryId, name, location, includeZero);

        Assert.NotNull(row);
        Assert.Equal(expectedQuantity, row!.Value.GetProperty("quantity").GetString());
    }

    private static async Task AssertAbsentFromProjectionAsync(ConversationTestClient client, Guid inventoryId, string name)
    {
        Assert.Null(await FindProjectionRowAsync(client, inventoryId, name, location: null, includeZero: true));
        Assert.Null(await FindProjectionRowAsync(client, inventoryId, name, location: "Shelf A", includeZero: true));
    }

    private static async Task<JsonElement?> FindProjectionRowAsync(
        ConversationTestClient client, Guid inventoryId, string name, string? location, bool includeZero)
    {
        var query = includeZero ? "?includeZero=true" : string.Empty;
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock{query}"));
        var projection = await response.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var row in projection.GetProperty("rows").EnumerateArray())
        {
            var rowLocation = row.GetProperty("location");
            var locationMatches = location is null
                ? rowLocation.ValueKind == JsonValueKind.Null
                : rowLocation.ValueKind == JsonValueKind.String && rowLocation.GetString() == location;

            if (row.GetProperty("name").GetString() == name && locationMatches)
            {
                return row;
            }
        }

        return null;
    }

    // ---- Database reads and seeding ----

    private static async Task<int> CountStockEntriesAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockEntries.AsNoTracking().CountAsync(e => e.InventoryId == inventoryId);
    }

    private static async Task<int> CountAuditsAsync(WebApplicationFactory<Program> factory, Guid inventoryId, string eventType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InventoryAudits.AsNoTracking().CountAsync(a => a.InventoryId == inventoryId && a.EventType == eventType);
    }

    private static async Task<int> CountChangeSetsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockChangeSetOperations.AsNoTracking().CountAsync(o => o.InventoryId == inventoryId);
    }

    /// <summary>
    /// How many settled proposals the retention sweep could never see, because they carry no settle
    /// instant in the form it compares on. Always zero: every terminal transition records both forms.
    /// </summary>
    private static async Task<int> CountUnsweepableSettledProposalsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.ConfirmationProposals
            .AsNoTracking()
            .CountAsync(p => p.Status != nameof(ProposalStatus.Pending) && p.SettledAtTicks == null);
    }

    /// <summary>
    /// How many proposals are still awaiting confirmation anywhere. Only one Participant has proposed
    /// anything at the point this is asserted, so zero here means their own proposal was invalidated.
    /// </summary>
    private static async Task<int> CountPendingProposalsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.ConfirmationProposals.AsNoTracking().CountAsync(p => p.Status == nameof(ProposalStatus.Pending));
    }

    private static async Task<Guid> StockEntryIdAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string normalizedName, bool located)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var rows = db.StockEntries.AsNoTracking().Where(e => e.InventoryId == inventoryId && e.NormalizedName == normalizedName);
        rows = located ? rows.Where(e => e.LocationId != null) : rows.Where(e => e.LocationId == null);

        return await rows.Select(e => e.Id).SingleAsync();
    }

    private static async Task SeedLocationAsync(WebApplicationFactory<Program> factory, Guid inventoryId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedStockAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string name, decimal quantity, string? locationName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unitId = await db.Units.AsNoTracking().Where(u => u.InventoryId == inventoryId).Select(u => u.Id).FirstAsync();
        var locationId = locationName is null
            ? (Guid?)null
            : await db.Locations
                .AsNoTracking()
                .Where(l => l.InventoryId == inventoryId && l.NormalizedName == NameNormalization.Normalize(locationName))
                .Select(l => l.Id)
                .SingleAsync();

        var stockEntryId = Guid.NewGuid();
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = stockEntryId,
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return stockEntryId;
    }

    /// <summary>
    /// Simulates a competing writer touching a row a pending proposal was decided against. The stamp -
    /// not the Quantity - is the version, so this is exactly what must make a confirmation conflict.
    /// </summary>
    private static async Task BumpConcurrencyStampAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string normalizedName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        await db.StockEntries
            .Where(e => e.InventoryId == inventoryId && e.NormalizedName == normalizedName)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ConcurrencyStamp, Guid.NewGuid()));
    }
}
