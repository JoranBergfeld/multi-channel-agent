using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises the signed-in web BFF end to end over real HTTP against the deterministic Test
/// authentication double, backed by SQLite (fast, Docker-free) instead of Testcontainers SQL Server -
/// covering session bootstrap, CSRF, secure cookie flags, non-disclosing authorization, Inventory
/// creation/listing/selection, and onboarding. The SQL-Server-backed equivalent of the core scenario
/// (creation, duplicate requests, selection, non-disclosure) lives in
/// <see cref="InventorySqlScenarioTests"/>.
/// </summary>
public sealed class InventorySessionHttpTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        // Antiforgery's SecurePolicy=Always refuses to operate over a plain-HTTP request (matching
        // the Secure cookie flags this BFF always sets), so the test client must present an https://
        // base address for TestServer to treat every request as HTTPS, exactly like production.
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            // Each test drives its own CookieJar per simulated Participant; the client's own
            // automatic cookie container must stay off, or a later sign-in on the same HttpClient
            // would silently overwrite an earlier Participant's captured session cookie.
            HandleCookies = false,
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    private static HttpRequestMessage Request(HttpMethod method, string url) => new(method, url);

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

    private async Task<(CookieJar Jar, string CsrfToken, JsonElement Bootstrap)> SignInAndBootstrapAsync(
        string participantId, string displayName, bool active = true)
    {
        var jar = new CookieJar();

        var signInRequest = Request(HttpMethod.Post, "/api/test/sign-in");
        signInRequest.Content = JsonContent.Create(new { participantId, displayName, activeTenantMember = active });
        var signInResponse = await SendAsync(jar, signInRequest);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        var csrfToken = body.GetProperty("csrfToken").GetString()!;
        var bootstrap = body.GetProperty("bootstrap");

        return (jar, csrfToken, bootstrap);
    }

    [Fact]
    public async Task Bootstrap_without_authentication_is_a_plain_401()
    {
        var response = await _client.GetAsync("/api/session/bootstrap");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // An authenticated but inactive/non-member identity must get a generic non-disclosing refusal,
    // not a distinct "you exist but are inactive" signal.
    [Fact]
    public async Task Bootstrap_for_an_inactive_participant_is_a_plain_403()
    {
        var jar = new CookieJar();
        var signInRequest = Request(HttpMethod.Post, "/api/test/sign-in");
        signInRequest.Content = JsonContent.Create(new
        {
            participantId = Guid.NewGuid().ToString(),
            displayName = "Inactive Person",
            activeTenantMember = false,
        });
        await SendAsync(jar, signInRequest);

        var response = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_sets_a_secure_httponly_samesite_auth_cookie_and_web_conversation_cookie()
    {
        var jar = new CookieJar();
        var signInRequest = Request(HttpMethod.Post, "/api/test/sign-in");
        signInRequest.Content = JsonContent.Create(new
        {
            participantId = Guid.NewGuid().ToString(),
            displayName = "Ada Lovelace",
            activeTenantMember = true,
        });
        var signInResponse = await SendAsync(jar, signInRequest);

        var authCookieHeader = signInResponse.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("mca_auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", authCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", authCookieHeader, StringComparison.OrdinalIgnoreCase);

        var bootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var webConversationCookieHeader = bootstrapResponse.Headers
            .GetValues("Set-Cookie")
            .Single(h => h.StartsWith("mca_web_conversation=", StringComparison.Ordinal));
        Assert.Contains("httponly", webConversationCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", webConversationCookieHeader, StringComparison.OrdinalIgnoreCase);

        var csrfCookieHeader = bootstrapResponse.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("mca_csrf=", StringComparison.Ordinal));
        Assert.Contains("httponly", csrfCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", csrfCookieHeader, StringComparison.OrdinalIgnoreCase);
    }

    // Tokens must never appear in any API response body/DTO - only the encrypted auth cookie carries
    // session state, and it is opaque to the client.
    [Fact]
    public async Task Bootstrap_response_never_exposes_tokens_or_the_raw_auth_cookie_value()
    {
        var (_, _, bootstrap) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        var raw = bootstrap.GetRawText();
        Assert.DoesNotContain("access_token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("id_token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_participant_with_no_memberships_is_signaled_for_onboarding()
    {
        var (_, _, bootstrap) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "New Participant");

        Assert.True(bootstrap.GetProperty("needsOnboarding").GetBoolean());
        Assert.Empty(bootstrap.GetProperty("inventories").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, bootstrap.GetProperty("activeInventoryId").ValueKind);
    }

    [Fact]
    public async Task Creating_an_inventory_without_a_csrf_token_is_rejected()
    {
        var (jar, _, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        var createRequest = Request(HttpMethod.Post, "/api/inventories");
        createRequest.Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" });
        var response = await SendAsync(jar, createRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Creating_an_inventory_with_a_valid_csrf_token_makes_the_requester_owner()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        var createRequest = Request(HttpMethod.Post, "/api/inventories");
        createRequest.Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" });
        var response = await SendAsync(jar, createRequest, csrfToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Warehouse", view.GetProperty("name").GetString());
        Assert.Equal("Owner", view.GetProperty("role").GetString());
        Assert.Equal("Ada Lovelace", view.GetProperty("ownerDisplayName").GetString());
        Assert.Equal(8, view.GetProperty("shortId").GetString()!.Length);
    }

    // Every new Inventory must carry the reserved `each` Unit and its fixed aliases, created
    // atomically with the Inventory and its Owner Membership - verified directly against the
    // database, since no HTTP endpoint exposes Units in this ticket.
    [Fact]
    public async Task Creating_an_inventory_atomically_creates_the_reserved_each_unit_with_fixed_aliases()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        var createRequest = Request(HttpMethod.Post, "/api/inventories");
        createRequest.Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" });
        var response = await SendAsync(jar, createRequest, csrfToken);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        var inventoryId = Guid.Parse(view.GetProperty("id").GetString()!);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unit = db.Units.Single(u => u.InventoryId == inventoryId);
        Assert.Equal("each", unit.CanonicalName);
        Assert.True(unit.IsReserved);

        var terms = db.UnitTerms.Where(t => t.UnitId == unit.Id).Select(t => t.Term).OrderBy(t => t).ToList();
        Assert.Equal(["each", "pc", "pcs", "piece", "pieces"], terms);
    }

    [Fact]
    public async Task Resubmitting_the_same_client_request_id_returns_the_original_inventory()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        async Task<JsonElement> CreateAsync()
        {
            var request = Request(HttpMethod.Post, "/api/inventories");
            request.Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" });
            var response = await SendAsync(jar, request, csrfToken);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var first = await CreateAsync();
        var second = await CreateAsync();

        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.Single(db.Inventories);
    }

    [Fact]
    public async Task Listing_shows_only_the_inventories_the_participant_is_authorized_for()
    {
        var (jarA, csrfA, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Alice");
        var createA = Request(HttpMethod.Post, "/api/inventories");
        createA.Content = JsonContent.Create(new { name = "Alice's Warehouse", clientRequestId = "req-a" });
        await SendAsync(jarA, createA, csrfA);

        var (jarB, csrfB, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Bob");
        var createB = Request(HttpMethod.Post, "/api/inventories");
        createB.Content = JsonContent.Create(new { name = "Bob's Warehouse", clientRequestId = "req-b" });
        await SendAsync(jarB, createB, csrfB);

        var listResponseA = await SendAsync(jarA, Request(HttpMethod.Get, "/api/inventories"));
        var listA = await listResponseA.Content.ReadFromJsonAsync<JsonElement>();
        var namesA = listA.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();

        Assert.Contains("Alice's Warehouse", namesA);
        Assert.DoesNotContain("Bob's Warehouse", namesA);
    }

    // Selecting an Inventory the Participant is not authorized for must return a plain 404 - never a
    // distinct signal - and must never itself grant Membership.
    [Fact]
    public async Task Selecting_an_unauthorized_inventory_is_a_plain_404_and_grants_no_access()
    {
        var (jarOwner, csrfOwner, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Owner Person");
        var create = Request(HttpMethod.Post, "/api/inventories");
        create.Content = JsonContent.Create(new { name = "Private Warehouse", clientRequestId = "req-1" });
        var createResponse = await SendAsync(jarOwner, create, csrfOwner);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inventoryId = created.GetProperty("id").GetString();

        var (jarOutsider, csrfOutsider, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Outsider Person");
        var selectRequest = Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/select");
        var selectResponse = await SendAsync(jarOutsider, selectRequest, csrfOutsider);

        Assert.Equal(HttpStatusCode.NotFound, selectResponse.StatusCode);

        var listResponse = await SendAsync(jarOutsider, Request(HttpMethod.Get, "/api/inventories"));
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(list.EnumerateArray());
    }

    // Selecting an Inventory ID that does not exist at all must be indistinguishable from selecting
    // one that exists but is unauthorized - both a plain 404.
    [Fact]
    public async Task Selecting_a_nonexistent_inventory_id_is_also_a_plain_404()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        var selectRequest = Request(HttpMethod.Post, $"/api/inventories/{Guid.NewGuid()}/select");
        var response = await SendAsync(jar, selectRequest, csrfToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // With exactly one accessible Inventory, bootstrap must auto-select it for the current
    // conversation so ordinary requests require no explicit setup step.
    [Fact]
    public async Task Bootstrap_auto_selects_the_single_accessible_inventory()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");
        var create = Request(HttpMethod.Post, "/api/inventories");
        create.Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" });
        var createResponse = await SendAsync(jar, create, csrfToken);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        var bootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        var bootstrap = body.GetProperty("bootstrap");

        Assert.Equal(created.GetProperty("id").GetString(), bootstrap.GetProperty("activeInventoryId").GetString());
    }

    // With multiple accessible Inventories, the agent must never guess: no auto-selection happens
    // until the Participant explicitly selects one.
    [Fact]
    public async Task Multiple_accessible_inventories_require_explicit_selection()
    {
        var (jar, csrfToken, _) = await SignInAndBootstrapAsync(Guid.NewGuid().ToString(), "Ada Lovelace");

        var createOne = Request(HttpMethod.Post, "/api/inventories");
        createOne.Content = JsonContent.Create(new { name = "Warehouse A", clientRequestId = "req-1" });
        await SendAsync(jar, createOne, csrfToken);

        var createTwo = Request(HttpMethod.Post, "/api/inventories");
        createTwo.Content = JsonContent.Create(new { name = "Warehouse B", clientRequestId = "req-2" });
        var createTwoResponse = await SendAsync(jar, createTwo, csrfToken);
        var second = await createTwoResponse.Content.ReadFromJsonAsync<JsonElement>();

        var bootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bootstrap").GetProperty("activeInventoryId").ValueKind);

        var selectResponse = await SendAsync(
            jar, Request(HttpMethod.Post, $"/api/inventories/{second.GetProperty("id").GetString()}/select"), csrfToken);
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        var laterBootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var laterBody = await laterBootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            second.GetProperty("id").GetString(),
            laterBody.GetProperty("bootstrap").GetProperty("activeInventoryId").GetString());
    }
}
