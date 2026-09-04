using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The whole Unit and Location administration protocol through the real HTTP application boundary:
/// a Viewer who may only list, an Editor who may create/rename/alias, an Owner who alone may Retire
/// and only after confirming, the shared collision-free term namespace, the immutable reserved
/// `each` Unit, flat unique Locations, renames that never touch Stock, a Retire refused while Stock
/// references it, a confirmed Retire that keeps the identity and invalidates a pending proposal that
/// referenced it, bounded deterministic suggestions, minimal semantic audits for outcomes and
/// denials, and retries that return the recorded Outcome without ever re-planning.
///
/// Shared by the SQL Server-backed scenario and its Docker-free SQLite twin so both prove the
/// identical externally observable behavior.
/// </summary>
internal static class ReferenceAdministrationScenario
{
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Administering Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Reference Warehouse");

        // 1. Every Inventory starts with the reserved `each` Unit and its four fixed aliases, and no
        //    Locations at all - unlocated is the absence of a reference, never a row.
        var initialUnits = await CompleteAsync(factory, owner, "ref-list-1", "list units");
        var initialUnitsPayload = PayloadOf(initialUnits, "unit_list");
        Assert.Equal(1, initialUnitsPayload.GetProperty("units").GetArrayLength());
        Assert.Equal("each", initialUnitsPayload.GetProperty("units")[0].GetProperty("name").GetString());
        Assert.Equal(4, initialUnitsPayload.GetProperty("units")[0].GetProperty("aliases").GetArrayLength());

        var initialLocations = await CompleteAsync(factory, owner, "ref-list-2", "list locations");
        Assert.Equal(0, PayloadOf(initialLocations, "location_list").GetProperty("locations").GetArrayLength());

        // 2. An Editor-level create applies immediately and is audited once.
        var createdUnit = await CompleteAsync(factory, owner, "ref-create-1", "create unit Cardboard Box aliases boxes, bx");
        var createdUnitChange = SingleChange(createdUnit, "reference_changes");
        Assert.Equal("create_unit", createdUnitChange.GetProperty("operation").GetString());
        var boxUnitId = createdUnitChange.GetProperty("referenceId").GetString()!;
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitCreated)));

        await CompleteAsync(factory, owner, "ref-create-2", "create location Shelf A");
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.LocationCreated)));

        // 3. Unit names and aliases share one namespace: a term that already means something is refused.
        var collision = await OutcomeAsync(factory, owner, "ref-create-3", "create unit BOXES");
        Assert.Equal("conflict", collision.GetProperty("category").GetString());
        Assert.Equal("term_in_use", collision.GetProperty("code").GetString());

        // A Location name that is already taken is refused by its own code, and Locations have no
        // aliases to collide with.
        var locationCollision = await OutcomeAsync(factory, owner, "ref-create-4", "create location SHELF A");
        Assert.Equal("name_in_use", locationCollision.GetProperty("code").GetString());

        // 4. The reserved Unit and its fixed aliases cannot be renamed, retired, removed, or reassigned.
        foreach (var (nativeId, command, expected) in ((string, string, string)[])
                 [
                     ("ref-reserved-1", "rename unit each to item", "reserved_unit"),
                     ("ref-reserved-2", "retire unit pcs", "reserved_unit"),
                     ("ref-reserved-3", "remove alias pcs from unit each", "reserved_term"),
                     ("ref-reserved-4", "add alias piece to unit Cardboard Box", "term_in_use"),
                     ("ref-reserved-5", "remove alias each from unit each", "canonical_term"),
                 ])
        {
            var refused = await OutcomeAsync(factory, owner, nativeId, command);
            Assert.Equal("conflict", refused.GetProperty("category").GetString());
            Assert.Equal(expected, refused.GetProperty("code").GetString());
        }

        // A non-reserved alias may still be taught to `each`, and removed again.
        await CompleteAsync(factory, owner, "ref-alias-1", "add alias stuks to unit each");
        await CompleteAsync(factory, owner, "ref-alias-2", "remove alias stuks from unit each");
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitAliasAdded)));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitAliasRemoved)));

        // 5. Stock can be added against the new Unit and Location, using an alias.
        await CompleteAsync(factory, owner, "ref-stock-1", "add stock Steel Bolts quantity 10 unit bx in Shelf A");
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Cardboard Box", "Shelf A");

        // 6. Rename preserves identity and does not rewrite Stock Entries or alter Equivalent Stock.
        var stockBefore = await StockRowsAsync(factory, inventoryId);
        var renamedUnit = await CompleteAsync(factory, owner, "ref-rename-1", "rename unit boxes to Carton");
        Assert.Equal(boxUnitId, SingleChange(renamedUnit, "reference_changes").GetProperty("referenceId").GetString());
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Carton", "Shelf A");
        Assert.Equal(stockBefore, await StockRowsAsync(factory, inventoryId));

        var renamedLocation = await CompleteAsync(factory, owner, "ref-rename-2", "rename location Shelf A to Aisle 3");
        Assert.Equal("rename_location", SingleChange(renamedLocation, "reference_changes").GetProperty("operation").GetString());
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Carton", "Aisle 3");
        Assert.Equal(stockBefore, await StockRowsAsync(factory, inventoryId));

        // A rename changes only the canonical name, so the name it used to answer to stops resolving
        // while every alias it still carries goes on resolving to the very same identity.
        var goneAlias = await OutcomeAsync(factory, owner, "ref-alias-3", "add alias kartons to unit Cardboard Box");
        Assert.Equal("not_found", goneAlias.GetProperty("category").GetString());
        Assert.Equal("reference_not_found", goneAlias.GetProperty("code").GetString());
        await CompleteAsync(factory, owner, "ref-alias-4", "add alias kartons to unit bx");

        // 7. Retire is refused while Stock references it - and refused before anyone is asked to confirm.
        var blockedUnit = await OutcomeAsync(factory, owner, "ref-retire-1", "retire unit Carton");
        Assert.Equal("conflict", blockedUnit.GetProperty("category").GetString());
        Assert.Equal("reference_in_use", blockedUnit.GetProperty("code").GetString());

        var blockedLocation = await OutcomeAsync(factory, owner, "ref-retire-2", "retire location Aisle 3");
        Assert.Equal("reference_in_use", blockedLocation.GetProperty("code").GetString());

        // 8. Once the Stock is gone, Retire needs the Owner's explicit confirmation.
        // Setting to zero and forgetting are both confirmed mutations, exactly as #32 shipped them.
        var clearing = await OutcomeAsync(factory, owner, "ref-stock-3", "set stock Steel Bolts quantity 0");
        await CompleteAsync(factory, owner, "ref-stock-4", $"confirm {TokenOf(clearing)}");
        var forget = await OutcomeAsync(factory, owner, "ref-stock-5", "forget stock Steel Bolts");
        await CompleteAsync(factory, owner, "ref-stock-6", $"confirm {TokenOf(forget)}");

        var proposedRetire = await OutcomeAsync(factory, owner, "ref-retire-3", "retire unit Carton");
        Assert.Equal("confirmation_required", proposedRetire.GetProperty("category").GetString());
        var proposedChange = SingleChange(proposedRetire, "reference_proposal");
        Assert.Equal("retire_unit", proposedChange.GetProperty("operation").GetString());
        Assert.Equal(boxUnitId, proposedChange.GetProperty("referenceId").GetString());

        // Nothing has happened yet: it still lists and still resolves.
        Assert.Contains("Carton", await UnitNamesAsync(factory, owner, "ref-list-3"));
        Assert.Equal(0, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitRetired)));

        // 9. Rejecting changes nothing at all.
        await CompleteAsync(factory, owner, "ref-reject-1", "reject");
        Assert.Contains("Carton", await UnitNamesAsync(factory, owner, "ref-list-6"));
        Assert.Equal(0, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitRetired)));

        // 10. A confirmed Retire preserves the identity, stops the Unit resolving, and audits once.
        var reproposed = await OutcomeAsync(factory, owner, "ref-retire-4", "retire unit Carton");
        var retired = await CompleteAsync(factory, owner, "ref-confirm-1", $"confirm {TokenOf(reproposed)}");
        Assert.Equal(boxUnitId, SingleChange(retired, "reference_changes").GetProperty("referenceId").GetString());
        Assert.DoesNotContain("Carton", await UnitNamesAsync(factory, owner, "ref-list-7"));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitRetired)));
        Assert.Equal(boxUnitId, (await RetiredUnitIdAsync(factory, inventoryId)).ToString());

        // The token is single use.
        var reused = await OutcomeAsync(factory, owner, "ref-confirm-2", $"confirm {TokenOf(reproposed)}");
        Assert.Equal("proposal_not_found", reused.GetProperty("code").GetString());

        // 11. A retired reference is exactly as unknown as one that never existed - and its terms are
        //     free again, so a fresh Unit may claim them.
        var unknownNow = await OutcomeAsync(factory, owner, "ref-retire-5", "retire unit Carton");
        Assert.Equal("reference_not_found", unknownNow.GetProperty("code").GetString());
        await CompleteAsync(factory, owner, "ref-create-5", "create unit Carton");

        // 12. Unknown references offer bounded deterministic suggestions.
        var suggested = await OutcomeAsync(factory, owner, "ref-suggest-1", "retire unit Cart");
        Assert.Equal("reference_not_found", suggested.GetProperty("code").GetString());
        var suggestionPayload = PayloadOf(suggested, "reference_suggestions");
        Assert.Equal("unit", suggestionPayload.GetProperty("reference").GetString());
        Assert.Equal("Carton", suggestionPayload.GetProperty("suggestions")[0].GetString());
        Assert.True(suggestionPayload.GetProperty("suggestions").GetArrayLength() <= 5);

        // 13. A pending stock proposal that depends on a Location is invalidated when it is retired.
        await CompleteAsync(factory, owner, "ref-create-6", "create location Bay 7");
        await CompleteAsync(factory, owner, "ref-stock-7", "add stock Brass Rivets quantity 4 in Bay 7");
        await CompleteAsync(factory, owner, "ref-stock-8", "move stock Brass Rivets all to unlocated");

        var editor = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Second Editor");
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");
        await editor.SelectInventoryAsync(inventoryId);

        // A multi-change batch is always confirmed, and this one would create Stock at Bay 7 - so it
        // depends on Bay 7 while leaving it empty, which is exactly the case a Retire has to settle.
        var editorProposal = await OutcomeAsync(
            factory,
            editor,
            "ref-editor-1",
            "change stock: add Copper Nails quantity 1 in Bay 7; add Zinc Screws quantity 1 in Bay 7");
        Assert.Equal("confirmation_required", editorProposal.GetProperty("category").GetString());

        var bayRetire = await OutcomeAsync(factory, owner, "ref-retire-6", "retire location Bay 7");
        await CompleteAsync(factory, owner, "ref-confirm-3", $"confirm {TokenOf(bayRetire)}");

        var strandedConfirm = await OutcomeAsync(factory, editor, "ref-editor-2", $"confirm {TokenOf(editorProposal)}");
        Assert.Equal("not_found", strandedConfirm.GetProperty("category").GetString());
        Assert.Equal("proposal_not_found", strandedConfirm.GetProperty("code").GetString());

        // 14. Only the Owner may Retire; an Editor is forbidden and the denial is audited.
        await CompleteAsync(factory, owner, "ref-create-7", "create location Bay 8");
        var editorRetire = await OutcomeAsync(factory, editor, "ref-editor-3", "retire location Bay 8");
        Assert.Equal("forbidden", editorRetire.GetProperty("category").GetString());
        Assert.True(await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.AccessDenied)) > 0);

        // 15. A Viewer may list, and may not create.
        var viewer = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Third Viewer");
        await owner.GrantMembershipAsync(inventoryId, viewer.ParticipantIdentifier, "Viewer");
        await viewer.SelectInventoryAsync(inventoryId);

        var viewerList = await CompleteAsync(factory, viewer, "ref-viewer-1", "list locations");
        Assert.True(PayloadOf(viewerList, "location_list").GetProperty("locations").GetArrayLength() > 0);

        var viewerCreate = await OutcomeAsync(factory, viewer, "ref-viewer-2", "create location Bay 9");
        Assert.Equal("forbidden", viewerCreate.GetProperty("category").GetString());

        // 16. A retry of an accepted Turn returns the recorded Outcome and never re-plans: the very
        //     same identity comes back, exactly one Location exists, and nothing was audited twice.
        var firstCreate = await CompleteAsync(factory, owner, "ref-retry-1", "create location Bay 10");
        var auditsBeforeRetry = await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.LocationCreated));

        var duplicate = await owner.SubmitTurnAsync("ref-retry-1", "create location Bay 10");
        var retried = await duplicate.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            SingleChange(firstCreate, "reference_changes").GetProperty("referenceId").GetString(),
            SingleChange(retried, "reference_changes").GetProperty("referenceId").GetString());
        Assert.Equal(1, await CountLocationsNamedAsync(factory, inventoryId, "bay 10"));
        Assert.Equal(auditsBeforeRetry, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.LocationCreated)));
    }

    // ---- Payload reading ----

    private static JsonElement PayloadOf(JsonElement outcome, string expectedKind)
    {
        var payload = outcome.GetProperty("payload");
        Assert.Equal(expectedKind, payload.GetProperty("kind").GetString());
        return payload;
    }

    private static JsonElement SingleChange(JsonElement outcome, string expectedKind) =>
        Assert.Single(PayloadOf(outcome, expectedKind).GetProperty("changes").EnumerateArray().ToList());

    /// <summary>Reads the one-time token off whichever kind of proposal the Outcome carries.</summary>
    private static string TokenOf(JsonElement outcome)
    {
        var payload = outcome.GetProperty("payload");
        var kind = payload.GetProperty("kind").GetString();
        Assert.True(kind is "stock_proposal" or "reference_proposal", $"Expected a proposal payload but found '{kind}'.");

        var token = payload.GetProperty("token").GetString()!;
        Assert.True(ConfirmationToken.IsWellFormed(token));
        return token;
    }

    // ---- Turn driving ----

    private static async Task<JsonElement> OutcomeAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var turnId = await client.SubmitAcceptedTurnAsync(nativeMessageId, contentText);
        await ProcessPendingAsync(factory);

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
        return await scope.ServiceProvider.GetRequiredService<Application.Turns.TurnProcessingCoordinator>()
            .ProcessPendingAsync(CancellationToken.None);
    }

    // ---- Projection assertions ----

    /// <summary>The active Unit canonical names the catalog list reports, through the conversation itself.</summary>
    private static async Task<IReadOnlyList<string>> UnitNamesAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId)
    {
        var outcome = await CompleteAsync(factory, client, nativeMessageId, "list units");

        return PayloadOf(outcome, "unit_list")
            .GetProperty("units")
            .EnumerateArray()
            .Select(unit => unit.GetProperty("name").GetString()!)
            .ToList();
    }

    private static async Task AssertStockAsync(
        ConversationTestClient client, Guid inventoryId, string name, string quantity, string unit, string? location)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock"));
        var projection = await response.Content.ReadFromJsonAsync<JsonElement>();

        var row = projection.GetProperty("rows").EnumerateArray()
            .SingleOrDefault(candidate => candidate.GetProperty("name").GetString() == name);

        Assert.Equal(JsonValueKind.Object, row.ValueKind);
        Assert.Equal(quantity, row.GetProperty("quantity").GetString());
        Assert.Equal(unit, row.GetProperty("unit").GetString());

        var rowLocation = row.GetProperty("location");
        if (location is null)
        {
            Assert.Equal(JsonValueKind.Null, rowLocation.ValueKind);
        }
        else
        {
            Assert.Equal(location, rowLocation.GetString());
        }
    }

    // ---- Database reads ----

    /// <summary>
    /// Every Stock Entry row, exactly as stored - including its version stamp. Snapshotting this
    /// before a rename and comparing afterwards is what proves a rename rewrites no stock at all.
    /// </summary>
    private static async Task<IReadOnlyList<string>> StockRowsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.StockEntries
            .AsNoTracking()
            .Where(e => e.InventoryId == inventoryId)
            .OrderBy(e => e.Id)
            .Select(e =>
                e.Id.ToString() + "|" + e.Name + "|" + e.NormalizedName + "|" + e.UnitId.ToString()
                + "|" + (e.LocationId == null ? "" : e.LocationId.ToString()) + "|" + e.Quantity.ToString()
                + "|" + (e.Note ?? "") + "|" + e.ConcurrencyStamp.ToString())
            .ToListAsync();
    }

    private static async Task<int> CountAuditsAsync(WebApplicationFactory<Program> factory, Guid inventoryId, string eventType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InventoryAudits.AsNoTracking().CountAsync(a => a.InventoryId == inventoryId && a.EventType == eventType);
    }

    /// <summary>The one retired Unit's identity, proving a confirmed Retire keeps the row and the identity.</summary>
    private static async Task<Guid> RetiredUnitIdAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == inventoryId && u.RetiredAt != null)
            .Select(u => u.Id)
            .SingleAsync();
    }

    private static async Task<int> CountLocationsNamedAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string normalizedName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.Locations
            .AsNoTracking()
            .CountAsync(l => l.InventoryId == inventoryId && l.NormalizedName == normalizedName);
    }
}