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

    private async Task SeedStockEntryAsync(Guid inventoryId, string name, decimal quantity)
    {
        using var scope = _factory.Services.CreateScope();
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
}
