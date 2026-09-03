using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises the Recovery Administrator-only, API-only endpoints end to end over real HTTP, backed by
/// SQLite (fast, Docker-free) - covering the trusted app-role authorization policy (orthogonal to
/// ActiveTenantMember), healthy-Inventory exclusion, non-disclosure between healthy and nonexistent,
/// the recovery admin never becoming a member, and never being able to reach ordinary Inventory
/// endpoints or stock.
/// </summary>
public sealed class InventoryRecoveryHttpTests : IAsyncLifetime
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

    private async Task<(CookieJar Jar, string CsrfToken, string ParticipantId)> SignInAndBootstrapAsync(string displayName)
    {
        var jar = new CookieJar();
        var participantId = Guid.NewGuid().ToString();

        var signInRequest = Request(HttpMethod.Post, "/api/test/sign-in");
        signInRequest.Content = JsonContent.Create(new { participantId, displayName, activeTenantMember = true });
        await SendAsync(jar, signInRequest);

        var bootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();

        return (jar, body.GetProperty("csrfToken").GetString()!, participantId);
    }

    /// <summary>
    /// Signs in as a Recovery Administrator. Deliberately not also an ordinary ActiveTenantMember
    /// unless the caller opts in, matching least privilege: a Recovery Administrator need not be a
    /// Participant at all. Since a pure Recovery Administrator has no session-bootstrap-equivalent
    /// read, the CSRF token is taken from the orphaned-inventories listing response instead - the
    /// natural "read before you act" call before recovering one.
    /// </summary>
    private async Task<(CookieJar Jar, string CsrfToken, string AdminId)> SignInAsRecoveryAdministratorAsync(
        string displayName, bool alsoActiveTenantMember = false)
    {
        var jar = new CookieJar();
        var adminId = Guid.NewGuid().ToString();

        var signInRequest = Request(HttpMethod.Post, "/api/test/sign-in");
        signInRequest.Content = JsonContent.Create(new
        {
            participantId = adminId,
            displayName,
            activeTenantMember = alsoActiveTenantMember,
            isInventoryRecoveryAdministrator = true,
        });
        await SendAsync(jar, signInRequest);

        var listResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/recovery/orphaned-inventories"));
        var body = await listResponse.Content.ReadFromJsonAsync<JsonElement>();

        return (jar, body.GetProperty("csrfToken").GetString()!, adminId);
    }

    private async Task<string> CreateInventoryAsync(CookieJar ownerJar, string ownerCsrf, string name, string clientRequestId)
    {
        var request = Request(HttpMethod.Post, "/api/inventories");
        request.Content = JsonContent.Create(new { name, clientRequestId });
        var response = await SendAsync(ownerJar, request, ownerCsrf);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        return view.GetProperty("id").GetString()!;
    }

    /// <summary>Removes an Owner from the deterministic tenant directory double - simulating them leaving/being disabled, the trigger orphan recovery requires.</summary>
    private async Task MakeOrphanedAsync(string participantId)
    {
        var request = Request(HttpMethod.Post, "/api/test/tenant-directory/unregister");
        request.Content = JsonContent.Create(new { participantId, displayName = "unused" });
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_ordinary_active_tenant_member_without_the_recovery_role_is_forbidden()
    {
        var (jar, _, _) = await SignInAndBootstrapAsync("Ordinary Person");

        var response = await SendAsync(jar, Request(HttpMethod.Get, "/api/recovery/orphaned-inventories"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_a_plain_401()
    {
        var response = await _client.GetAsync("/api/recovery/orphaned-inventories");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_recovery_administrator_can_list_orphaned_inventories_and_a_healthy_one_is_excluded()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Healthy Owner");
        await CreateInventoryAsync(ownerJar, ownerCsrf, "Healthy Warehouse", "req-healthy");

        var (orphanOwnerJar, orphanOwnerCsrf, orphanOwnerId) = await SignInAndBootstrapAsync("Orphaned Owner");
        var orphanedInventoryId = await CreateInventoryAsync(orphanOwnerJar, orphanOwnerCsrf, "Orphaned Warehouse", "req-orphaned");
        await MakeOrphanedAsync(orphanOwnerId);

        var (adminJar, _, _) = await SignInAsRecoveryAdministratorAsync("Recovery Admin");

        var response = await SendAsync(adminJar, Request(HttpMethod.Get, "/api/recovery/orphaned-inventories"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("page").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("inventoryId").GetString())
            .ToList();

        Assert.Contains(orphanedInventoryId, items);
        Assert.All(items, id => Assert.Equal(orphanedInventoryId, id));
    }

    [Fact]
    public async Task Recovering_a_healthy_inventory_is_a_plain_404_indistinguishable_from_nonexistent()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Healthy Owner");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Healthy Warehouse", "req-healthy");
        var (_, _, targetId) = await SignInAndBootstrapAsync("Target Person");

        var (adminJar, adminCsrf, _) = await SignInAsRecoveryAdministratorAsync("Recovery Admin");

        var recoverRequest = Request(HttpMethod.Post, $"/api/recovery/inventories/{inventoryId}/recover");
        recoverRequest.Content = JsonContent.Create(new { targetIdentifier = targetId });
        var healthyResponse = await SendAsync(adminJar, recoverRequest, adminCsrf);

        var nonexistentRequest = Request(HttpMethod.Post, $"/api/recovery/inventories/{Guid.NewGuid()}/recover");
        nonexistentRequest.Content = JsonContent.Create(new { targetIdentifier = targetId });
        var nonexistentResponse = await SendAsync(adminJar, nonexistentRequest, adminCsrf);

        Assert.Equal(HttpStatusCode.NotFound, healthyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nonexistentResponse.StatusCode);
    }

    [Fact]
    public async Task Recovering_an_orphaned_inventory_transfers_ownership_and_the_admin_never_becomes_a_member()
    {
        var (orphanOwnerJar, orphanOwnerCsrf, orphanOwnerId) = await SignInAndBootstrapAsync("Orphaned Owner");
        var inventoryId = await CreateInventoryAsync(orphanOwnerJar, orphanOwnerCsrf, "Orphaned Warehouse", "req-orphaned");
        await MakeOrphanedAsync(orphanOwnerId);

        var (targetJar, _, targetId) = await SignInAndBootstrapAsync("Target Person");
        var (adminJar, adminCsrf, adminActorId) = await SignInAsRecoveryAdministratorAsync("Recovery Admin");

        var recoverRequest = Request(HttpMethod.Post, $"/api/recovery/inventories/{inventoryId}/recover");
        recoverRequest.Content = JsonContent.Create(new { targetIdentifier = targetId });
        var response = await SendAsync(adminJar, recoverRequest, adminCsrf);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.Equal(
            MultiChannelAgent.Domain.Inventories.MembershipRole.Owner,
            db.Memberships.Single(m => m.ParticipantId == Guid.Parse(targetId)).Role);
        Assert.Equal(
            MultiChannelAgent.Domain.Inventories.MembershipRole.Editor,
            db.Memberships.Single(m => m.ParticipantId == Guid.Parse(orphanOwnerId)).Role);
        // The recovery administrator's own identity never appears as a Membership row on this or any Inventory.
        Assert.DoesNotContain(db.Memberships, m => m.ParticipantId == Guid.Parse(adminActorId));

        // The target's next request observes the new Owner role.
        var targetBootstrap = await SendAsync(targetJar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var targetBody = await targetBootstrap.Content.ReadFromJsonAsync<JsonElement>();
        var owned = targetBody.GetProperty("bootstrap").GetProperty("inventories").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == inventoryId);
        Assert.Equal("Owner", owned.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Recovering_with_an_unresolvable_target_is_a_validation_error()
    {
        var (orphanOwnerJar, orphanOwnerCsrf, orphanOwnerId) = await SignInAndBootstrapAsync("Orphaned Owner");
        var inventoryId = await CreateInventoryAsync(orphanOwnerJar, orphanOwnerCsrf, "Orphaned Warehouse", "req-orphaned");
        await MakeOrphanedAsync(orphanOwnerId);

        var (adminJar, adminCsrf, _) = await SignInAsRecoveryAdministratorAsync("Recovery Admin");

        var recoverRequest = Request(HttpMethod.Post, $"/api/recovery/inventories/{inventoryId}/recover");
        recoverRequest.Content = JsonContent.Create(new { targetIdentifier = Guid.NewGuid().ToString() });
        var response = await SendAsync(adminJar, recoverRequest, adminCsrf);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Recover_without_a_csrf_token_is_rejected()
    {
        var (orphanOwnerJar, orphanOwnerCsrf, orphanOwnerId) = await SignInAndBootstrapAsync("Orphaned Owner");
        var inventoryId = await CreateInventoryAsync(orphanOwnerJar, orphanOwnerCsrf, "Orphaned Warehouse", "req-orphaned");
        await MakeOrphanedAsync(orphanOwnerId);
        var (_, _, targetId) = await SignInAndBootstrapAsync("Target Person");

        var (adminJar, _, _) = await SignInAsRecoveryAdministratorAsync("Recovery Admin");

        var recoverRequest = Request(HttpMethod.Post, $"/api/recovery/inventories/{inventoryId}/recover");
        recoverRequest.Content = JsonContent.Create(new { targetIdentifier = targetId });
        var response = await SendAsync(adminJar, recoverRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A Recovery Administrator is deliberately least-privilege: unless they also happen to be an
    // ordinary active tenant member, they cannot reach ordinary Inventory endpoints (and therefore
    // never any stock) at all.
    [Fact]
    public async Task A_recovery_administrator_who_is_not_also_an_active_tenant_member_cannot_reach_ordinary_inventory_endpoints()
    {
        var (adminJar, _, _) = await SignInAsRecoveryAdministratorAsync("Recovery Admin");

        var response = await SendAsync(adminJar, Request(HttpMethod.Get, "/api/inventories"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
