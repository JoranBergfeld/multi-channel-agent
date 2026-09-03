using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises the authorized Stock projection endpoint (<c>GET /api/inventories/{id}/stock</c>) over
/// real HTTP against the deterministic Test authentication double, backed by SQLite (fast,
/// Docker-free): the Inventory workspace's own refetch path, independent of the conversational Turn
/// flow.
/// </summary>
public sealed class StockEndpointsHttpTests : IAsyncLifetime
{
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

    private async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(string displayName)
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId = Guid.NewGuid().ToString(), displayName, activeTenantMember = true }),
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
        return (jar, body.GetProperty("csrfToken").GetString()!);
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

    private async Task SeedStockEntryAsync(Guid inventoryId, string name, decimal quantity, Guid? unitId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var resolvedUnitId = unitId ?? db.Units.Single(u => u.InventoryId == inventoryId && u.IsReserved).Id;

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = resolvedUnitId,
            LocationId = null,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds a second Inventory-owned Unit with its own canonical term, so a Unit filter has something to narrow between.</summary>
    private async Task<Guid> SeedUnitAsync(Guid inventoryId, string canonicalName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unitId = Guid.NewGuid();

        db.Units.Add(new UnitEntity
        {
            Id = unitId,
            InventoryId = inventoryId,
            CanonicalName = canonicalName,
            NormalizedCanonicalName = canonicalName.ToLowerInvariant(),
            IsReserved = false,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            Term = canonicalName,
            NormalizedTerm = canonicalName.ToLowerInvariant(),
            IsCanonical = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return unitId;
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
    public async Task An_authorized_participant_receives_on_hand_stock_by_default()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);
        await SeedStockEntryAsync(inventoryId, "Nuts", 0m);

        var response = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal("Bolts", row.GetProperty("name").GetString());
    }

    [Fact]
    public async Task IncludeZero_query_parameter_surfaces_zero_quantity_rows()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);
        await SeedStockEntryAsync(inventoryId, "Nuts", 0m);

        var response = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?includeZero=true"));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task An_unauthorized_participant_gets_a_plain_404_never_disclosing_the_inventory_exists()
    {
        var (ownerJar, ownerCsrf) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Private Warehouse");

        var (strangerJar, _) = await SignInAndBootstrapAsync("Stranger Person");
        var response = await SendAsync(strangerJar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_access_is_a_plain_401()
    {
        var response = await _client.GetAsync($"/api/inventories/{Guid.NewGuid()}/stock");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    // The workspace projection is the same authorized read the conversation performs, so it must
    // offer the same bounds: narrowing to an exact Unit is one of them, and a Unit is named the way
    // its Inventory names it - canonical name or active alias, or its opaque identifier.
    [Fact]
    public async Task A_unit_query_parameter_narrows_to_that_exact_unit()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        var boxUnitId = await SeedUnitAsync(inventoryId, "box");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);
        await SeedStockEntryAsync(inventoryId, "Bolts", 7m, boxUnitId);

        var byCanonicalName = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?unit=box"));
        var byOpaqueId = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?unit={boxUnitId}"));
        var byReservedAlias = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?unit=pcs"));

        Assert.Equal(HttpStatusCode.OK, byCanonicalName.StatusCode);
        var named = await byCanonicalName.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("box", Assert.Single(named.GetProperty("rows").EnumerateArray()).GetProperty("unit").GetString());

        var byId = await byOpaqueId.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("box", Assert.Single(byId.GetProperty("rows").EnumerateArray()).GetProperty("unit").GetString());

        // `pcs` is a reserved alias of every Inventory's `each` Unit.
        var byAlias = await byReservedAlias.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("each", Assert.Single(byAlias.GetProperty("rows").EnumerateArray()).GetProperty("unit").GetString());
    }

    // An unknown reference is never created implicitly and never silently ignored - which would
    // answer a wider question than was asked - and the problem names the parameter at fault.
    [Fact]
    public async Task An_unknown_unit_is_reported_against_the_unit_parameter()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);

        var response = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?unit=crates"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unit", (await ValidationErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task An_unknown_location_is_reported_against_the_location_parameter()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);

        var response = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?locationId={Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("locationId", (await ValidationErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task A_page_size_bounds_the_page_and_its_cursor_resumes_the_same_request()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Apple Bolts", 1m);
        await SeedStockEntryAsync(inventoryId, "Copper Wire", 1m);

        var firstPageResponse = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?pageSize=1"));
        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        var firstPage = await firstPageResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Apple Bolts", Assert.Single(firstPage.GetProperty("rows").EnumerateArray()).GetProperty("name").GetString());
        Assert.True(firstPage.GetProperty("hasMore").GetBoolean());
        var cursor = firstPage.GetProperty("nextCursor").GetString()!;

        var secondPageResponse = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?pageSize=1&cursor={Uri.EscapeDataString(cursor)}"));
        var secondPage = await secondPageResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Copper Wire", Assert.Single(secondPage.GetProperty("rows").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(secondPage.GetProperty("hasMore").GetBoolean());
    }

    // A cursor only ever continues the request that issued it, so reusing one under a different page
    // size is refused rather than resuming a position that means something else.
    [Fact]
    public async Task A_cursor_reused_under_a_different_page_size_is_refused()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Apple Bolts", 1m);
        await SeedStockEntryAsync(inventoryId, "Copper Wire", 1m);
        await SeedStockEntryAsync(inventoryId, "Zebra Bolts", 1m);

        var firstPage = await (await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?pageSize=1")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var cursor = firstPage.GetProperty("nextCursor").GetString()!;

        var response = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?pageSize=2&cursor={Uri.EscapeDataString(cursor)}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("cursor", (await ValidationErrorsAsync(response)).Keys);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task A_page_size_outside_its_bounds_is_reported_against_the_page_size_parameter(string pageSize)
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);

        var response = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?pageSize={pageSize}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pageSize", (await ValidationErrorsAsync(response)).Keys);
    }

    [Fact]
    public async Task An_unlocated_query_parameter_returns_only_stock_kept_nowhere_in_particular()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Warehouse");
        await SeedStockEntryAsync(inventoryId, "Bolts", 5m);

        var response = await SendAsync(jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock?unlocated=true"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Null(Assert.Single(body.GetProperty("rows").EnumerateArray()).GetProperty("location").GetString());
    }
}
