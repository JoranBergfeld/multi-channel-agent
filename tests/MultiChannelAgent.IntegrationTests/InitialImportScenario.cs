using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The whole Initial Import workflow through the real HTTP application boundary: the eligibility a
/// brand new Inventory offers, reference data that import resolves but never creates, a file whose
/// every independent problem is reported at once and stores nothing, the exact normalized preview
/// that merges equivalent rows, a re-validation that supersedes this Participant's own pending
/// import and its file, a cancellation that creates nothing, a confirmation that creates exactly the
/// previewed entries and one audit fact, the discarded raw file and the digest that outlives it, the
/// lost-response retry that re-reports rather than imports twice, and the identical 404 a Viewer and
/// a stranger both get.
///
/// Shared by the SQL Server-backed scenario and its Docker-free SQLite twin so both prove the
/// identical externally observable behavior.
/// </summary>
internal static class InitialImportScenario
{
    /// <summary>The five column names Initial Import reads, exactly as <c>ImportContract</c> names them.</summary>
    private const string Header = "Name,Quantity,Unit,Location,Note";

    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Importing Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Import Warehouse");

        // 1. A brand new Inventory is empty, so import is offered.
        var initialEligibility = await EligibilityAsync(owner, inventoryId);
        Assert.Equal(HttpStatusCode.OK, initialEligibility.Status);
        Assert.True(initialEligibility.Body.GetProperty("eligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initialEligibility.Body.GetProperty("reason").ValueKind);

        // 2. Reference data has to exist first: import resolves, it never creates.
        await CompleteAsync(factory, owner, "imp-unit-1", "create unit Cardboard Box aliases boxes, bx");
        await CompleteAsync(factory, owner, "imp-loc-1", "create location Shelf A");

        // 3. A file with every kind of problem reports all of them at once, and stores nothing. The
        //    four phases are independent - a row's own error means its Unit and Location were never
        //    looked up, and the merge only ever names rows that did resolve - so one upload answers
        //    for every actionable line rather than making a Participant fix the file four times over.
        var broken = await ValidateAsync(owner, inventoryId, string.Join('\n',
        [
            Header,
            ",4,,,",
            "Steel Bolts,nope,,,",
            "Brass Rivets,1,crate,,",
            "Zinc Screws,1,,Bay 9,",
            "Copper Nails,1,,,Blue box",
            "Copper Nails,1,,,Red box",
            string.Empty,
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, broken.Status);
        Assert.Equal("invalid_file", broken.Body.GetProperty("code").GetString());
        Assert.Equal(
            ["missing_name", "invalid_quantity", "unknown_unit", "unknown_location", "conflicting_notes"],
            ErrorCodes(broken.Body));

        // Every problem is reported against the exact line a Participant has to open, in source
        // order, and nothing is reported twice however many phases independently found something.
        Assert.Equal(
            [2, 3, 4, 5, 7],
            broken.Body.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("lineNumber").GetInt32()));
        Assert.Equal(0, broken.Body.GetProperty("omittedErrorCount").GetInt32());
        Assert.Equal(0, await CountPendingImportsAsync(factory, inventoryId));
        Assert.Equal(0, await CountRawUploadsAsync(factory));

        // 4. A file whose only problem is conflicting Notes says exactly that, and nothing else - the
        //    merge diagnostic above was this same rule, reported alongside errors it does not depend on.
        var conflicting = await ValidateAsync(owner, inventoryId, string.Join('\n',
            [Header, "Copper Nails,1,,,Blue box", "Copper Nails,1,,,Red box", string.Empty]));

        Assert.Equal(HttpStatusCode.BadRequest, conflicting.Status);
        Assert.Equal(["conflicting_notes"], ErrorCodes(conflicting.Body));
        Assert.Equal(0, await CountPendingImportsAsync(factory, inventoryId));

        // 5. A valid file previews the exact normalized entries, merging equivalent rows.
        var csv = string.Join('\n',
        [
            Header,
            "Steel Bolts,4,bx,Shelf A,Blue box",
            "Brass Rivets,2.5,,,",
            "STEEL   bolts,6,boxes,shelf a,",
            "Zinc Screws,0,,,",
            string.Empty,
        ]);

        var preview = await ValidateAsync(owner, inventoryId, csv);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Equal(4, preview.Body.GetProperty("sourceRowCount").GetInt32());
        Assert.False(preview.Body.GetProperty("supersededPrevious").GetBoolean());

        var entries = preview.Body.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);

        // Two rows that name the same thing in different words and different case are one Stock
        // Entry: the first row's display text and references survive, the Quantities add up, the one
        // non-blank Note survives, and both source lines are named so the merge is reviewable.
        AssertEntry(entries[0], "Steel Bolts", "10", "Cardboard Box", "Shelf A", "Blue box", [2, 4]);
        AssertEntry(entries[1], "Brass Rivets", "2.5", "each", null, null, [3]);

        // Zero is an amount, not an absence: it previews as its own entry rather than merging away.
        AssertEntry(entries[2], "Zinc Screws", "0", "each", null, null, [5]);

        // 6. Nothing has happened yet: no Stock, and the file is held for exactly this proposal.
        Assert.Equal(0, await CountStockAsync(factory, inventoryId));
        Assert.Equal(1, await CountPendingImportsAsync(factory, inventoryId));
        Assert.Equal(1, await CountRawUploadsAsync(factory));
        Assert.Equal([ProposalId(preview.Body)], await RawUploadOwnersAsync(factory));

        // 7. Validating again replaces this Participant's own pending import and its file.
        var replaced = await ValidateAsync(owner, inventoryId, csv);
        Assert.Equal(HttpStatusCode.OK, replaced.Status);
        Assert.True(replaced.Body.GetProperty("supersededPrevious").GetBoolean());
        Assert.NotEqual(ProposalId(preview.Body), ProposalId(replaced.Body));
        Assert.Equal(1, await CountPendingImportsAsync(factory, inventoryId));

        // The superseded import is settled under its own identity, and the one retained file is the
        // new proposal's - the replaced upload is discarded with the proposal that held it.
        Assert.Equal(
            nameof(ImportProposalStatus.Superseded),
            await ImportStatusAsync(factory, ProposalId(preview.Body)));
        Assert.Equal(1, await CountRawUploadsAsync(factory));
        Assert.Equal([ProposalId(replaced.Body)], await RawUploadOwnersAsync(factory));

        // 8. Cancelling changes nothing at all, and discards the file.
        var rejected = await RejectAsync(owner, inventoryId, ProposalId(replaced.Body), Token(replaced.Body));
        Assert.Equal(HttpStatusCode.OK, rejected.Status);
        Assert.True(rejected.Body.GetProperty("rejected").GetBoolean());
        Assert.Equal(
            nameof(ImportProposalStatus.Rejected),
            await ImportStatusAsync(factory, ProposalId(replaced.Body)));
        Assert.Equal(0, await CountStockAsync(factory, inventoryId));
        Assert.Equal(0, await CountPendingImportsAsync(factory, inventoryId));
        Assert.Equal(0, await CountRawUploadsAsync(factory));
        Assert.True((await EligibilityAsync(owner, inventoryId)).Body.GetProperty("eligible").GetBoolean());

        // 9. A confirmed import creates exactly the entries that were previewed, and one audit fact.
        var confirmed = await ValidateAsync(owner, inventoryId, csv);
        var proposalId = ProposalId(confirmed.Body);
        var token = Token(confirmed.Body);
        var applied = await ConfirmAsync(owner, inventoryId, proposalId, token);

        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Equal(proposalId, ProposalId(applied.Body));
        Assert.Equal(3, applied.Body.GetProperty("createdEntryCount").GetInt32());
        Assert.Equal(3, await CountStockAsync(factory, inventoryId));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.StockImported)));

        // 10. The raw CSV is gone, and only the digest remains in the ledger.
        Assert.Equal(nameof(ImportProposalStatus.Confirmed), await ImportStatusAsync(factory, proposalId));
        Assert.Equal(0, await CountPendingImportsAsync(factory, inventoryId));
        Assert.Equal(0, await CountRawUploadsAsync(factory));
        var digest = confirmed.Body.GetProperty("fileDigest").GetString();
        Assert.Equal(digest, applied.Body.GetProperty("fileDigest").GetString());
        Assert.Equal(digest, await LedgerDigestAsync(factory, inventoryId));

        // 11. The imported Stock is exactly what the preview promised, readable through the ordinary
        //     authorized projection - including the zero-quantity entry, which is on hand nowhere but
        //     is a Stock Entry all the same.
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Cardboard Box", "Shelf A");
        await AssertStockAsync(owner, inventoryId, "Brass Rivets", "2.5", "each", null);
        await AssertStockAsync(owner, inventoryId, "Zinc Screws", "0", "each", null, includeZero: true);

        // 12. A lost-response retry re-reports the recorded result without importing twice, and
        //     import is no longer offered at all.
        var retry = await ConfirmAsync(owner, inventoryId, proposalId, token);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(proposalId, ProposalId(retry.Body));
        Assert.Equal(3, retry.Body.GetProperty("createdEntryCount").GetInt32());
        Assert.Equal(digest, retry.Body.GetProperty("fileDigest").GetString());

        // The answer was re-read, not re-run: the same three Stock Entries and the same single audit.
        Assert.Equal(3, await CountStockAsync(factory, inventoryId));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.StockImported)));

        var afterwards = await EligibilityAsync(owner, inventoryId);
        Assert.Equal(HttpStatusCode.OK, afterwards.Status);
        Assert.False(afterwards.Body.GetProperty("eligible").GetBoolean());
        Assert.Equal("inventory_not_empty", afterwards.Body.GetProperty("reason").GetString());

        var afterImport = await ValidateAsync(owner, inventoryId, csv);
        Assert.Equal(HttpStatusCode.Conflict, afterImport.Status);
        Assert.Equal("inventory_not_empty", afterImport.Body.GetProperty("code").GetString());

        // 13. Retired reference data is unknown to import too.
        var second = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(factory), "Second Owner");
        var emptyInventoryId = await second.CreateAndSelectInventoryAsync("Second Warehouse");
        await CompleteAsync(factory, second, "imp-unit-2", "create unit Pallet");
        var retire = await OutcomeAsync(factory, second, "imp-retire-1", "retire unit Pallet");
        await CompleteAsync(factory, second, "imp-confirm-1", $"confirm {TokenOf(retire)}");

        var retired = await ValidateAsync(second, emptyInventoryId, string.Join('\n',
            [Header, "Steel Bolts,1,Pallet,,", string.Empty]));
        Assert.Equal(HttpStatusCode.BadRequest, retired.Status);
        Assert.Equal(["unknown_unit"], ErrorCodes(retired.Body));

        // 14. A Viewer may not import at all, and is told nothing that distinguishes refusal from absence.
        var viewer = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(factory), "Third Viewer");
        await second.GrantMembershipAsync(emptyInventoryId, viewer.ParticipantIdentifier, "Viewer");

        AssertUndisclosed(await EligibilityAsync(viewer, emptyInventoryId));
        AssertUndisclosed(await ValidateAsync(viewer, emptyInventoryId, csv));

        // 15. A stranger sees exactly the same 404 for an Inventory they are not a member of.
        var stranger = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(factory), "Fourth Stranger");

        AssertUndisclosed(await EligibilityAsync(stranger, inventoryId));
        AssertUndisclosed(await ValidateAsync(stranger, inventoryId, csv));
    }

    // ---- Answer assertions ----

    /// <summary>
    /// A refusal that discloses nothing: the identical bare 404 whether the Inventory is not this
    /// Participant's, not theirs to import into, or not there at all. A body of any kind would be the
    /// difference between those three.
    /// </summary>
    private static void AssertUndisclosed((HttpStatusCode Status, JsonElement Body) response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal(JsonValueKind.Undefined, response.Body.ValueKind);
    }

    /// <summary>One previewed entry, whole: what it will be called, hold, reference, and remember.</summary>
    private static void AssertEntry(
        JsonElement entry, string name, string quantity, string unit, string? location, string? note, int[] sourceLines)
    {
        Assert.Equal(name, entry.GetProperty("name").GetString());
        Assert.Equal(quantity, entry.GetProperty("quantity").GetString());
        Assert.Equal(unit, entry.GetProperty("unitCanonicalName").GetString());
        Assert.Equal(location, entry.GetProperty("locationName").GetString());
        Assert.Equal(note, entry.GetProperty("note").GetString());
        Assert.Equal(sourceLines, entry.GetProperty("sourceLineNumbers").EnumerateArray().Select(line => line.GetInt32()));
    }

    // ---- Import routes ----

    private static async Task<(HttpStatusCode Status, JsonElement Body)> EligibilityAsync(
        ConversationTestClient client, Guid inventoryId) =>
        await ReadAsync(await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import")));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ValidateAsync(
        ConversationTestClient client, Guid inventoryId, string csv)
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return await ReadAsync(await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent { { part, "file", "stock.csv" } },
            },
            withCsrf: true));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ConfirmAsync(
        ConversationTestClient client, Guid inventoryId, Guid proposalId, string token) =>
        await DecideAsync(client, inventoryId, "confirm", proposalId, token);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RejectAsync(
        ConversationTestClient client, Guid inventoryId, Guid proposalId, string token) =>
        await DecideAsync(client, inventoryId, "reject", proposalId, token);

    /// <summary>
    /// Decides exactly the proposal a preview handed back, with exactly the token it issued - the
    /// only pair either route ever accepts.
    /// </summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> DecideAsync(
        ConversationTestClient client, Guid inventoryId, string route, Guid proposalId, string token) =>
        await ReadAsync(await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/{route}")
            {
                Content = JsonContent.Create(new { proposalId, token }),
            },
            withCsrf: true));

    /// <summary>
    /// The status and whatever JSON came with it. A refusal that discloses nothing carries no body at
    /// all, so an absent one is <see cref="JsonValueKind.Undefined"/> rather than a read that throws.
    /// </summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonDocument.Parse(payload).RootElement.Clone());
    }

    // ---- Preview reading ----

    private static Guid ProposalId(JsonElement preview) => preview.GetProperty("proposalId").GetGuid();

    private static string Token(JsonElement preview)
    {
        var token = preview.GetProperty("token").GetString()!;
        Assert.True(ConfirmationToken.IsWellFormed(token));
        return token;
    }

    /// <summary>Every reported problem's machine code, in the order the report listed them.</summary>
    private static IReadOnlyList<string> ErrorCodes(JsonElement problem) =>
    [
        .. problem.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString()!),
    ];

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

    /// <summary>Reads the one-time token off a conversational proposal Outcome.</summary>
    private static string TokenOf(JsonElement outcome)
    {
        var payload = outcome.GetProperty("payload");
        var kind = payload.GetProperty("kind").GetString();
        Assert.True(kind is "stock_proposal" or "reference_proposal", $"Expected a proposal payload but found '{kind}'.");

        var token = payload.GetProperty("token").GetString()!;
        Assert.True(ConfirmationToken.IsWellFormed(token));
        return token;
    }

    // ---- Projection assertions ----

    private static async Task AssertStockAsync(
        ConversationTestClient client,
        Guid inventoryId,
        string name,
        string quantity,
        string unit,
        string? location,
        bool includeZero = false)
    {
        var query = includeZero ? "?includeZero=true" : string.Empty;
        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock{query}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

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

    private static async Task<int> CountStockAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockEntries.AsNoTracking().CountAsync(e => e.InventoryId == inventoryId);
    }

    private static async Task<int> CountPendingImportsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var pending = nameof(ImportProposalStatus.Pending);

        return await db.ImportProposals
            .AsNoTracking()
            .CountAsync(p => p.InventoryId == inventoryId && p.Status == pending);
    }

    /// <summary>
    /// Every retained raw upload, across the whole database. Uploads carry no Inventory of their own -
    /// they are the file one proposal is holding - so counting them all is what proves none is ever
    /// left behind anywhere once its proposal is settled.
    /// </summary>
    private static async Task<int> CountRawUploadsAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.ImportUploads.AsNoTracking().CountAsync();
    }

    /// <summary>
    /// Which proposals are still holding a raw upload. A count alone would let a superseding
    /// validation keep the replaced file and discard the new one and still look right.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> RawUploadOwnersAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.ImportUploads
            .AsNoTracking()
            .OrderBy(upload => upload.ProposalId)
            .Select(upload => upload.ProposalId)
            .ToListAsync();
    }

    /// <summary>How one import proposal was settled, by its own identity.</summary>
    private static async Task<string> ImportStatusAsync(WebApplicationFactory<Program> factory, Guid proposalId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.ImportProposals
            .AsNoTracking()
            .Where(proposal => proposal.ProposalId == proposalId)
            .Select(proposal => proposal.Status)
            .SingleAsync();
    }

    private static async Task<int> CountAuditsAsync(WebApplicationFactory<Program> factory, Guid inventoryId, string eventType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InventoryAudits.AsNoTracking().CountAsync(a => a.InventoryId == inventoryId && a.EventType == eventType);
    }

    /// <summary>The one digest the ledger kept for this Inventory, which is all that outlives the file.</summary>
    private static async Task<string> LedgerDigestAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        return await db.ImportOperations
            .AsNoTracking()
            .Where(operation => operation.InventoryId == inventoryId)
            .Select(operation => operation.FileDigest)
            .SingleAsync();
    }
}
