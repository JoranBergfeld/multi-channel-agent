using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises the signed-in Initial Import workflow (<c>/api/inventories/{id}/import</c> and its
/// validate, confirm, and reject routes) over real HTTP against the deterministic Test
/// authentication double, backed by SQLite (fast, Docker-free): the only way a Participant ever
/// reaches Initial Import, since it is a browser workflow rather than a conversational tool.
/// </summary>
public sealed class ImportEndpointsHttpTests : IAsyncLifetime
{
    private const string Header = "Name,Quantity,Unit,Location,Note";

    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    private async Task<(CookieJar Jar, string CsrfToken, string ParticipantId)> SignInAndBootstrapAsync(string displayName)
    {
        var jar = new CookieJar();
        var participantId = Guid.NewGuid().ToString();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId, displayName, activeTenantMember = true }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await _client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrapRequest);
        var bootstrapResponse = await _client.SendAsync(bootstrapRequest);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (jar, body.GetProperty("csrfToken").GetString()!, participantId);
    }

    private async Task<HttpResponseMessage> SendAsync(CookieJar jar, HttpRequestMessage request, string? csrfToken = null)
    {
        jar.Apply(request);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        var response = await _client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    private async Task<Guid> CreateInventoryAsync(CookieJar jar, string csrfToken, string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name, clientRequestId = Guid.NewGuid().ToString() }),
        };
        var response = await SendAsync(jar, request, csrfToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private static MultipartFormDataContent CsvContent(string csv, string fileName = "stock.csv")
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    private async Task<HttpResponseMessage> ValidateAsync(CookieJar jar, string csrfToken, Guid inventoryId, string csv) =>
        await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = CsvContent(csv),
            },
            csrfToken);

    /// <summary>Confirms exactly the proposal a preview handed back, with exactly the token it issued.</summary>
    private async Task<HttpResponseMessage> ConfirmAsync(CookieJar jar, string csrfToken, Guid inventoryId, JsonElement preview) =>
        await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/confirm")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = preview.GetProperty("proposalId").GetGuid(),
                    token = preview.GetProperty("token").GetString(),
                }),
            },
            csrfToken);

    /// <summary>Puts a second Participant on the Inventory in the role Initial Import is being asked about.</summary>
    private async Task GrantAsync(CookieJar ownerJar, string ownerCsrfToken, Guid inventoryId, string participantId, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/inventories/{inventoryId}/members")
        {
            Content = JsonContent.Create(new { targetIdentifier = participantId, role }),
        };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(ownerJar, request, ownerCsrfToken)).StatusCode);
    }

    private static async Task<IReadOnlyDictionary<string, string[]>> ValidationErrorsAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors").EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.EnumerateArray().Select(message => message.GetString()!).ToArray());
    }

    [Fact]
    public async Task An_Editor_sees_that_import_is_available_for_an_empty_Inventory()
    {
        var (ownerJar, ownerCsrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrfToken, "Import Warehouse");
        var (editorJar, _, editorId) = await SignInAndBootstrapAsync("Import Editor");
        await GrantAsync(ownerJar, ownerCsrfToken, inventoryId, editorId, "Editor");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import");
        var response = await SendAsync(editorJar, request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("eligible").GetBoolean());
    }

    // Initial Import is an Owner's and an Editor's workflow. A Viewer is a member, but a refusal that
    // said so would disclose the Inventory to somebody who may not import into it at all.
    [Fact]
    public async Task A_Viewer_is_refused_exactly_as_a_stranger_is()
    {
        var (ownerJar, ownerCsrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrfToken, "Import Warehouse");
        var (viewerJar, viewerCsrfToken, viewerId) = await SignInAndBootstrapAsync("Import Viewer");
        await GrantAsync(ownerJar, ownerCsrfToken, inventoryId, viewerId, "Viewer");

        var eligibility = await SendAsync(
            viewerJar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import"));
        var validate = await ValidateAsync(viewerJar, viewerCsrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(HttpStatusCode.NotFound, eligibility.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, validate.StatusCode);
    }

    [Fact]
    public async Task A_valid_file_previews_the_exact_entries_and_hands_back_a_one_time_token()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\nSteel Bolts,6,,,\n");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.GetProperty("sourceRowCount").GetInt32());
        var entry = Assert.Single(body.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal("Steel Bolts", entry.GetProperty("name").GetString());
        Assert.Equal("10", entry.GetProperty("quantity").GetString());
        Assert.Equal(64, body.GetProperty("fileDigest").GetString()!.Length);
        Assert.NotEqual(Guid.Empty, body.GetProperty("proposalId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task A_file_with_errors_is_a_validation_problem_naming_every_line()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\n,4,,,\nSteel Bolts,nope,,,\n");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_file", body.GetProperty("code").GetString());
        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(["missing_name", "invalid_quantity"], errors.Select(e => e.GetProperty("code").GetString()));
        Assert.Equal([2, 3], errors.Select(e => e.GetProperty("lineNumber").GetInt32()));
        Assert.Equal(0, body.GetProperty("omittedErrorCount").GetInt32());
    }

    // What this proves is the endpoint's own bound, not the server's: the part is small enough that
    // the request is accepted and read, and it is the file's length - not the body's - that refuses
    // it, before a byte of it is copied into the import. The server's own bound is the subject of
    // ImportUploadLimitsHttpTests.
    [Fact]
    public async Task A_file_part_longer_than_the_bound_is_refused_on_its_length_rather_than_read()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var oversized = $"{Header}\n" + new string('a', ImportContract.MaxUploadBytes);

        var response = await ValidateAsync(jar, csrfToken, inventoryId, oversized);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("file_too_large", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_request_without_a_file_part_names_the_part_it_wanted()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent(),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mutating_request_without_the_CSRF_token_is_refused()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
        {
            Content = CsvContent($"{Header}\nSteel Bolts,4,,,\n"),
        };
        var response = await SendAsync(jar, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Antiforgery reads the form itself when a request carries no token header, so on this route it -
    // not the endpoint - is the first thing to touch the body, and it has to honor the same bound. A
    // maximum-sized upload whose token is a form field is previewed exactly as one with a header is:
    // the read antiforgery performs holds the file in memory too, rather than failing on the denied
    // buffering directory and reporting it as one more CSRF refusal.
    [Fact]
    public async Task A_maximum_sized_upload_that_carries_its_token_in_the_form_is_previewed_all_the_same()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var (csv, rowCount) = MaximumSizedValidCsv();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent
                {
                    { file, "file", "stock.csv" },
                    { new StringContent(csrfToken), "__RequestVerificationToken" },
                },
            });
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rowCount, JsonDocument.Parse(payload).RootElement.GetProperty("sourceRowCount").GetInt32());
    }

    [Fact]
    public async Task An_Inventory_the_Participant_may_not_see_is_indistinguishable_from_one_that_does_not_exist()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Stranger");

        var eligibility = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{Guid.NewGuid()}/import"));
        var validate = await ValidateAsync(jar, csrfToken, Guid.NewGuid(), $"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(HttpStatusCode.NotFound, eligibility.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, validate.StatusCode);
    }

    [Fact]
    public async Task A_confirmed_import_creates_the_entries_the_preview_showed()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await ConfirmAsync(jar, csrfToken, inventoryId, preview);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("createdEntryCount").GetInt32());

        // The Inventory is no longer empty, so import is no longer offered.
        var eligibility = await (await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal("inventory_not_empty", eligibility.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Confirming_an_unknown_token_is_a_plain_not_found()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/confirm")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = Guid.NewGuid(),
                    token = ConfirmationToken.Issue(),
                }),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancelling_settles_the_import_and_creates_nothing()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(
            jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/reject")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = preview.GetProperty("proposalId").GetGuid(),
                    token = (string?)null,
                }),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rejected").GetBoolean());

        // Nothing was created, so the Inventory is still empty and import is still offered.
        var eligibility = await (await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(eligibility.GetProperty("eligible").GetBoolean());
    }

    [Fact]
    public async Task Initial_import_is_never_offered_to_a_caller_who_is_not_signed_in()
    {
        var eligibility = await _client.GetAsync($"/api/inventories/{Guid.NewGuid()}/import");
        var confirm = await _client.PostAsJsonAsync(
            $"/api/inventories/{Guid.NewGuid()}/import/confirm",
            new { proposalId = Guid.NewGuid(), token = ConfirmationToken.Issue() });

        Assert.Equal(HttpStatusCode.Unauthorized, eligibility.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, confirm.StatusCode);
    }

    // The bound is exact: a file of precisely the maximum size is read and answered on its contents,
    // never refused for its size. This one is answered for its two-mebibyte Name, which proves it
    // reached validation rather than the size gate.
    [Fact]
    public async Task A_file_of_exactly_the_maximum_size_is_read_rather_than_refused()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var row = new string('a', ImportContract.MaxUploadBytes - Header.Length - ",1,,,".Length - 2) + ",1,,,";
        var exactlyAtTheBound = $"{Header}\n{row}\n";
        Assert.Equal(ImportContract.MaxUploadBytes, Encoding.UTF8.GetByteCount(exactlyAtTheBound));

        var response = await ValidateAsync(jar, csrfToken, inventoryId, exactlyAtTheBound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_file", body.GetProperty("code").GetString());
        var error = Assert.Single(body.GetProperty("errors").EnumerateArray().ToList());
        Assert.Equal("name_too_long", error.GetProperty("code").GetString());
    }

    // The raw upload lives in memory for the length of its request and in SQL while its proposal is
    // pending, and nowhere else - so a two-mebibyte file must be held whole in memory rather than
    // spooled to a temp file, which is what ASP.NET Core's 64 KiB default buffering threshold would do
    // with all but the smallest import. UploadSpillGuard denies the buffering directory to every test
    // in this assembly, so a maximum-sized file that previews at all is a file that never reached disk;
    // the digest then says the bytes that were validated and stored are exactly the bytes sent.
    [Fact]
    public async Task A_maximum_sized_file_is_previewed_whole_without_ever_being_written_to_disk()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var (csv, rowCount) = MaximumSizedValidCsv();
        var bytes = Encoding.UTF8.GetBytes(csv);
        Assert.Equal(ImportContract.MaxUploadBytes, bytes.Length);

        var response = await ValidateAsync(jar, csrfToken, inventoryId, csv);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(payload).RootElement;
        Assert.Equal(rowCount, body.GetProperty("sourceRowCount").GetInt32());
        Assert.Equal(rowCount, body.GetProperty("entries").GetArrayLength());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            body.GetProperty("fileDigest").GetString());
    }

    /// <summary>
    /// A valid file of exactly <see cref="ImportContract.MaxUploadBytes"/> bytes and the number of rows
    /// it carries: distinct names so nothing merges away, Notes padded to the shipped bound so the two
    /// mebibytes are ordinary content rather than one absurd field.
    /// </summary>
    private static (string Csv, int RowCount) MaximumSizedValidCsv()
    {
        const int framing = 15; // "Item 0000,1,,," and the newline that ends the record.
        var wholeRow = framing + StockEntry.MaxNoteLength;
        var records = ImportContract.MaxUploadBytes - (Header.Length + 1);
        var wholeRows = records / wholeRow;
        var lastRow = records % wholeRow;

        var builder = new StringBuilder($"{Header}\n");
        for (var row = 0; row < wholeRows; row++)
        {
            builder.Append(Row(row, StockEntry.MaxNoteLength));
        }

        if (lastRow > 0)
        {
            builder.Append(Row(wholeRows, lastRow - framing));
        }

        return (builder.ToString(), wholeRows + (lastRow > 0 ? 1 : 0));

        static string Row(int index, int noteLength) => $"Item {index:D4},1,,,{new string('n', noteLength)}\n";
    }

    [Fact]
    public async Task A_body_that_is_not_a_multipart_upload_at_all_names_the_part_it_wanted()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new StringContent($"{Header}\nSteel Bolts,4,,,\n", Encoding.UTF8, "text/csv"),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", (await ValidationErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task A_part_under_another_name_is_not_the_file_part()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes($"{Header}\nSteel Bolts,4,,,\n"));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent { { part, "upload", "stock.csv" } },
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", (await ValidationErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task An_empty_file_part_is_no_file_at_all()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await ValidateAsync(jar, csrfToken, inventoryId, string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", (await ValidationErrorsAsync(response)).Keys);
    }

    // A lost response must never cost a Participant their import: confirming the same proposal again
    // re-reports exactly what the first confirmation did, rather than answering that it is gone.
    [Fact]
    public async Task Confirming_the_same_import_again_re_reports_what_it_did()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var first = await (await ConfirmAsync(jar, csrfToken, inventoryId, preview)).Content.ReadFromJsonAsync<JsonElement>();
        var retry = await ConfirmAsync(jar, csrfToken, inventoryId, preview);
        var second = await retry.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(first.GetProperty("proposalId").GetString(), second.GetProperty("proposalId").GetString());
        Assert.Equal(first.GetProperty("createdEntryCount").GetInt32(), second.GetProperty("createdEntryCount").GetInt32());
        Assert.Equal(first.GetProperty("fileDigest").GetString(), second.GetProperty("fileDigest").GetString());
    }

    [Fact]
    public async Task A_token_that_does_not_match_the_proposal_is_refused_and_the_proposal_survives()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var mismatched = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/confirm")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = preview.GetProperty("proposalId").GetGuid(),
                    token = ConfirmationToken.Issue(),
                }),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        var problem = await mismatched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("proposal_token_mismatch", problem.GetProperty("code").GetString());

        // A typo must not destroy reviewed work, so the right token still confirms it.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(jar, csrfToken, inventoryId, preview)).StatusCode);
    }

    [Fact]
    public async Task An_Inventory_that_already_holds_Stock_has_nothing_initial_to_import()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(jar, csrfToken, inventoryId, preview)).StatusCode);

        var response = await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nCopper Wire,2,,,\n");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("inventory_not_empty", problem.GetProperty("code").GetString());
    }
}
