using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises the two authorized reference projection endpoints
/// (<c>GET /api/inventories/{id}/units</c> and <c>.../locations</c>) over real HTTP against the
/// deterministic Test authentication double, backed by SQLite: the Inventory workspace's own
/// refetch path for the catalog administration changes.
/// </summary>
public sealed class ReferenceEndpointsHttpTests : IAsyncLifetime
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

        [Fact]
    public async Task An_authorized_Participant_reads_the_active_Units_and_Locations()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Catalog Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Catalog Warehouse");

        var units = await GetJsonAsync(jar, $"/api/inventories/{inventoryId}/units");
        var locations = await GetJsonAsync(jar, $"/api/inventories/{inventoryId}/locations");

        Assert.Equal(1, units.GetProperty("units").GetArrayLength());
        Assert.Equal("each", units.GetProperty("units")[0].GetProperty("name").GetString());
        Assert.Equal(4, units.GetProperty("units")[0].GetProperty("aliases").GetArrayLength());
        Assert.False(units.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, locations.GetProperty("locations").GetArrayLength());
    }

    [Fact]
    public async Task An_Inventory_the_Participant_may_not_see_is_indistinguishable_from_one_that_does_not_exist()
    {
        var (jar, _) = await SignInAndBootstrapAsync("Stranger");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{Guid.NewGuid()}/units");
        jar.Apply(request);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_page_size_outside_the_bound_is_answered_with_a_problem_naming_it()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Catalog Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Catalog Warehouse");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/locations?pageSize=9999");
        jar.Apply(request);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pageSize", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<JsonElement> GetJsonAsync(CookieJar jar, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        jar.Apply(request);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
