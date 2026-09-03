using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Docker-free end-to-end coverage of the conversational read bounds: a signed-in Participant can ask
/// for a bounded page, resume it with the cursor the previous answer gave them, narrow by an exact
/// Unit or Location, ask for unlocated Stock explicitly, and be told plainly when a named reference
/// does not exist - all through the real HTTP boundary, the scripted proposal, the trusted dispatcher,
/// the deterministic services, and a real relational engine.
/// </summary>
public sealed class ConversationalStockFilterTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_bounded_page_can_be_resumed_conversationally_with_the_cursor_it_returned()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Paging Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Paging Warehouse");
        SeedStock(inventoryId, "Apple Bolts", 1m);
        SeedStock(inventoryId, "Copper Wire", 1m);
        SeedStock(inventoryId, "Zebra Bolts", 1m);

        var firstPage = await AnswerAsync(participant, "page-1", "list stock page size 1");
        Assert.Equal("completed", firstPage.GetProperty("status").GetString());
        var firstRows = firstPage.GetProperty("payload").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Apple Bolts", Assert.Single(firstRows).GetProperty("name").GetString());
        Assert.True(firstPage.GetProperty("payload").GetProperty("hasMore").GetBoolean());

        var cursor = firstPage.GetProperty("payload").GetProperty("nextCursor").GetString()!;
        var secondPage = await AnswerAsync(participant, "page-2", $"list stock page size 1 after {cursor}");

        var secondRows = secondPage.GetProperty("payload").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Copper Wire", Assert.Single(secondRows).GetProperty("name").GetString());
    }

    // A cursor only answers the question it was issued for: reused against a different page size it
    // is refused, rather than resuming a position that means something else.
    [Fact]
    public async Task A_cursor_reused_for_a_differently_shaped_request_is_refused()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Cursor Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Cursor Warehouse");
        SeedStock(inventoryId, "Apple Bolts", 1m);
        SeedStock(inventoryId, "Copper Wire", 1m);
        SeedStock(inventoryId, "Zebra Bolts", 1m);

        var firstPage = await AnswerAsync(participant, "cursor-1", "list stock page size 1");
        var cursor = firstPage.GetProperty("payload").GetProperty("nextCursor").GetString()!;

        var mismatched = await AnswerAsync(participant, "cursor-2", $"list stock page size 2 after {cursor}");

        Assert.Equal("completed", mismatched.GetProperty("status").GetString());
        Assert.Equal("invalid", mismatched.GetProperty("category").GetString());
        Assert.Equal("invalid_query", mismatched.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stock_can_be_narrowed_to_an_exact_location_and_to_unlocated_stock()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Filtering Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Filtering Warehouse");
        var shelfA = SeedLocation(inventoryId, "Shelf A");
        SeedStock(inventoryId, "Placed Bolts", 1m, shelfA);
        SeedStock(inventoryId, "Loose Bolts", 1m);

        var placed = await AnswerAsync(participant, "filter-1", "list stock in Shelf A");
        var loose = await AnswerAsync(participant, "filter-2", "list stock unlocated");

        Assert.Equal(
            "Placed Bolts",
            Assert.Single(placed.GetProperty("payload").GetProperty("rows").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(
            "Loose Bolts",
            Assert.Single(loose.GetProperty("payload").GetProperty("rows").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Stock_can_be_narrowed_by_a_units_active_alias()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Unit Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Unit Warehouse");
        SeedStock(inventoryId, "Loose Bolts", 1m);

        // "pcs" is a reserved alias of the every-Inventory `each` Unit.
        var byAlias = await AnswerAsync(participant, "unit-1", "list stock unit pcs");

        Assert.Equal(
            "Loose Bolts",
            Assert.Single(byAlias.GetProperty("payload").GetProperty("rows").EnumerateArray()).GetProperty("name").GetString());
    }

    // An unknown Unit or Location is never created implicitly and never silently ignored: the answer
    // says plainly that the reference does not exist, as a completed answer rather than a failure.
    [Fact]
    public async Task An_unknown_location_is_answered_as_reference_not_found()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Unknown Reference Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Unknown Reference Warehouse");
        SeedStock(inventoryId, "Loose Bolts", 1m);

        var answer = await AnswerAsync(participant, "unknown-1", "list stock in Shelf Z");

        Assert.Equal("completed", answer.GetProperty("status").GetString());
        Assert.Equal("not_found", answer.GetProperty("category").GetString());
        Assert.Equal("reference_not_found", answer.GetProperty("code").GetString());
    }

    private async Task<JsonElement> AnswerAsync(ConversationTestClient participant, string nativeMessageId, string content)
    {
        var turnId = await participant.SubmitAcceptedTurnAsync(nativeMessageId, content);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>().ProcessPendingAsync(CancellationToken.None);
        }

        return (await participant.GetOutcomeAsync(turnId))!.Value;
    }

    private Guid SeedLocation(Guid inventoryId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var locationId = Guid.NewGuid();
        db.Locations.Add(new LocationEntity
        {
            Id = locationId,
            InventoryId = inventoryId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return locationId;
    }

    private void SeedStock(Guid inventoryId, string name, decimal quantity, Guid? locationId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unit = db.Units.Single(u => u.InventoryId == inventoryId);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unit.Id,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }
    // A Participant with no Inventory selected for this conversation must be able to converse at all -
    // that is precisely when the agent has to tell them to select one. The guidance is a completed
    // answer carrying an actionable code, not a failure.
    [Fact]
    public async Task Reading_stock_with_no_active_inventory_answers_with_reachable_guidance()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Unselected Participant");

        var answer = await AnswerAsync(participant, "no-inventory-1", "list stock");

        Assert.Equal("completed", answer.GetProperty("status").GetString());
        Assert.Equal("invalid", answer.GetProperty("category").GetString());
        Assert.Equal("no_active_inventory", answer.GetProperty("code").GetString());
        Assert.Contains("Select an Inventory", answer.GetProperty("summary").GetString());

        // And it is delivered, so the Participant actually sees it.
        Assert.Single(answer.GetProperty("deliveries").EnumerateArray());
    }
    // Reading is a Viewer capability, and Editor and Owner both include it. Proving it end to end for
    // an Editor - a distinct signed-in Participant, granted only Editor on someone else's Inventory -
    // keeps a stricter-than-intended authorization check from silently locking Editors out of reads.
    [Fact]
    public async Task An_editor_of_another_participants_inventory_can_read_stock_conversationally()
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Owning Participant");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Shared Warehouse");
        SeedStock(inventoryId, "Shared Bolts", 4m);

        var editor = await ConversationTestClient.SignInAsync(httpClient, "Editing Participant");
        GrantEditorMembership(inventoryId, await ParticipantIdOfAsync(editor));
        await editor.SelectInventoryAsync(inventoryId);

        var listed = await AnswerAsync(editor, "editor-1", "list stock");
        var found = await AnswerAsync(editor, "editor-2", "find Shared Bolts");

        Assert.Equal("completed", listed.GetProperty("status").GetString());
        Assert.Equal(
            "Shared Bolts",
            Assert.Single(listed.GetProperty("payload").GetProperty("rows").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("completed", found.GetProperty("category").GetString());
    }

    private static async Task<Guid> ParticipantIdOfAsync(ConversationTestClient participant)
    {
        var bootstrap = await participant.GetBootstrapAsync();
        return Guid.Parse(bootstrap.GetProperty("bootstrap").GetProperty("participantId").GetString()!);
    }

    private void GrantEditorMembership(Guid inventoryId, Guid participantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = inventoryId,
            ParticipantId = participantId,
            Role = MembershipRole.Editor,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }
}
