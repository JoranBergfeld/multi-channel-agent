using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Exercises Owner-driven membership governance (grant/change role, remove, list) and ownership
/// transfer end to end over real HTTP, backed by SQLite (fast, Docker-free) - covering the role
/// matrix, non-disclosure for non-members, forbidden-not-not-found for non-owner members, recipient
/// acceptance never required, self-demotion/self-removal refusal, self-transfer conflict, and Active
/// Inventory being cleared on access loss. The SQL-Server-backed equivalent lives in
/// <see cref="InventoryGovernanceSqlScenarioTests"/>.
/// </summary>
public sealed class InventoryGovernanceHttpTests : IAsyncLifetime
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

    private async Task<(CookieJar Jar, string CsrfToken, string ParticipantId)> SignInAndBootstrapAsync(string displayName, string? participantId = null)
    {
        var jar = new CookieJar();
        var resolvedParticipantId = participantId ?? Guid.NewGuid().ToString();

        var signInRequest = Request(HttpMethod.Post, "/api/test/sign-in");
        signInRequest.Content = JsonContent.Create(new { participantId = resolvedParticipantId, displayName, activeTenantMember = true });
        var signInResponse = await SendAsync(jar, signInRequest);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapResponse = await SendAsync(jar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();

        return (jar, body.GetProperty("csrfToken").GetString()!, resolvedParticipantId);
    }

    private async Task<string> CreateInventoryAsync(CookieJar ownerJar, string ownerCsrf, string name, string clientRequestId)
    {
        var request = Request(HttpMethod.Post, "/api/inventories");
        request.Content = JsonContent.Create(new { name, clientRequestId });
        var response = await SendAsync(ownerJar, request, ownerCsrf);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        return view.GetProperty("id").GetString()!;
    }

    private async Task<HttpResponseMessage> GrantAsync(CookieJar jar, string csrf, string inventoryId, string targetIdentifier, string role)
    {
        var request = Request(HttpMethod.Put, $"/api/inventories/{inventoryId}/members");
        request.Content = JsonContent.Create(new { targetIdentifier, role });
        return await SendAsync(jar, request, csrf);
    }

    [Fact]
    public async Task Owner_can_grant_viewer_and_the_recipient_gets_access_without_any_acceptance_step()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var (recipientJar, recipientCsrf, recipientId) = await SignInAndBootstrapAsync("Recipient Person");
        var grantResponse = await GrantAsync(ownerJar, ownerCsrf, inventoryId, recipientId, "Viewer");
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        // No "accept" call of any kind - the recipient can already select it as their next request.
        var selectResponse = await SendAsync(
            recipientJar, Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), recipientCsrf);
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);
    }

    [Fact]
    public async Task Owner_can_change_an_existing_members_role_from_viewer_to_editor()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (_, _, recipientId) = await SignInAndBootstrapAsync("Recipient Person");
        await GrantAsync(ownerJar, ownerCsrf, inventoryId, recipientId, "Viewer");

        var response = await GrantAsync(ownerJar, ownerCsrf, inventoryId, recipientId, "Editor");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var role = db.Memberships.Single(m => m.ParticipantId == Guid.Parse(recipientId)).Role;
        Assert.Equal(MultiChannelAgent.Domain.Inventories.MembershipRole.Editor, role);
    }

    [Fact]
    public async Task Granting_owner_role_through_the_ordinary_endpoint_is_rejected()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (_, _, recipientId) = await SignInAndBootstrapAsync("Recipient Person");

        var response = await GrantAsync(ownerJar, ownerCsrf, inventoryId, recipientId, "Owner");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_demote_themselves_through_the_grant_endpoint()
    {
        var (ownerJar, ownerCsrf, ownerId) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var response = await GrantAsync(ownerJar, ownerCsrf, inventoryId, ownerId, "Editor");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_remove_themselves()
    {
        var (ownerJar, ownerCsrf, ownerId) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var response = await SendAsync(
            ownerJar, Request(HttpMethod.Delete, $"/api/inventories/{inventoryId}/members/{ownerId}"), ownerCsrf);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_non_member_attempting_to_grant_is_refused_with_a_plain_404()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var (outsiderJar, outsiderCsrf, _) = await SignInAndBootstrapAsync("Outsider Person");
        var (_, _, recipientId) = await SignInAndBootstrapAsync("Recipient Person");

        var response = await GrantAsync(outsiderJar, outsiderCsrf, inventoryId, recipientId, "Viewer");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_non_owner_member_attempting_to_grant_is_forbidden()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var (viewerJar, viewerCsrf, viewerId) = await SignInAndBootstrapAsync("Viewer Person");
        await GrantAsync(ownerJar, ownerCsrf, inventoryId, viewerId, "Viewer");
        var (_, _, recipientId) = await SignInAndBootstrapAsync("Recipient Person");

        var response = await GrantAsync(viewerJar, viewerCsrf, inventoryId, recipientId, "Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Grant_without_a_csrf_token_is_rejected()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var request = Request(HttpMethod.Put, $"/api/inventories/{inventoryId}/members");
        request.Content = JsonContent.Create(new { targetIdentifier = Guid.NewGuid().ToString(), role = "Viewer" });
        var response = await SendAsync(ownerJar, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Owner_can_remove_a_non_owner_member_and_their_access_is_lost_on_the_next_request()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (viewerJar, viewerCsrf, viewerId) = await SignInAndBootstrapAsync("Viewer Person");
        await GrantAsync(ownerJar, ownerCsrf, inventoryId, viewerId, "Viewer");
        var selectResponse = await SendAsync(viewerJar, Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), viewerCsrf);
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        var removeResponse = await SendAsync(
            ownerJar, Request(HttpMethod.Delete, $"/api/inventories/{inventoryId}/members/{viewerId}"), ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, removeResponse.StatusCode);

        // Next request from the removed Participant must observe current authorization, not a stale
        // session role - and their Active Inventory must have been cleared.
        var laterBootstrap = await SendAsync(viewerJar, Request(HttpMethod.Get, "/api/session/bootstrap"));
        var body = await laterBootstrap.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bootstrap").GetProperty("activeInventoryId").ValueKind);

        var laterSelect = await SendAsync(viewerJar, Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), viewerCsrf);
        Assert.Equal(HttpStatusCode.NotFound, laterSelect.StatusCode);
    }

    [Fact]
    public async Task Members_list_is_owner_only_and_never_leaked_to_non_owners()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (viewerJar, viewerCsrf, viewerId) = await SignInAndBootstrapAsync("Viewer Person");
        await GrantAsync(ownerJar, ownerCsrf, inventoryId, viewerId, "Viewer");

        var ownerListResponse = await SendAsync(ownerJar, Request(HttpMethod.Get, $"/api/inventories/{inventoryId}/members"));
        Assert.Equal(HttpStatusCode.OK, ownerListResponse.StatusCode);
        var members = await ownerListResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, members.GetArrayLength());

        var viewerListResponse = await SendAsync(viewerJar, Request(HttpMethod.Get, $"/api/inventories/{inventoryId}/members"));
        Assert.Equal(HttpStatusCode.Forbidden, viewerListResponse.StatusCode);
    }

    [Fact]
    public async Task Granting_a_role_writes_a_semantic_audit_fact_with_a_ninety_day_expiry_and_no_sensitive_content()
    {
        var (ownerJar, ownerCsrf, ownerId) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (_, _, recipientId) = await SignInAndBootstrapAsync("Recipient Person");

        var beforeCall = DateTimeOffset.UtcNow;
        await GrantAsync(ownerJar, ownerCsrf, inventoryId, recipientId, "Viewer");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var fact = db.InventoryAudits.Single(a => a.EventType == "MembershipGranted" && a.InventoryId == Guid.Parse(inventoryId));

        Assert.Equal("Participant", fact.ActorKind);
        Assert.Equal(ownerId, fact.ActorId);
        Assert.Equal(Guid.Parse(recipientId), fact.SubjectParticipantId);
        Assert.True(fact.OccurredAtUtc >= beforeCall);
        Assert.Equal(fact.OccurredAtUtc.AddDays(90), fact.ExpiresAtUtc);
        Assert.DoesNotContain("Warehouse", fact.OutcomeCode, StringComparison.Ordinal);
    }

    // Denied access must still be audited server-side, even though the caller only ever sees a plain
    // non-disclosing 404 - never a distinct signal.
    [Fact]
    public async Task A_denied_grant_attempt_from_a_non_member_is_audited_without_disclosing_anything_to_the_caller()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (outsiderJar, outsiderCsrf, outsiderId) = await SignInAndBootstrapAsync("Outsider Person");
        var (_, _, recipientId) = await SignInAndBootstrapAsync("Recipient Person");

        var response = await GrantAsync(outsiderJar, outsiderCsrf, inventoryId, recipientId, "Viewer");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var fact = db.InventoryAudits.Single(a => a.EventType == "AccessDenied" && a.ActorId == outsiderId);
        Assert.Equal("Denied:NotAMember", fact.OutcomeCode);
        Assert.Equal(Guid.Parse(inventoryId), fact.InventoryId);
    }

    [Fact]
    public async Task Owner_can_transfer_ownership_atomically_and_the_previous_owner_is_demoted_to_editor()
    {
        var (ownerJar, ownerCsrf, ownerId) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (_, _, targetId) = await SignInAndBootstrapAsync("Target Person");

        var transferRequest = Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/transfer-ownership");
        transferRequest.Content = JsonContent.Create(new { targetIdentifier = targetId });
        var response = await SendAsync(ownerJar, transferRequest, ownerCsrf);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.Equal(
            MultiChannelAgent.Domain.Inventories.MembershipRole.Owner,
            db.Memberships.Single(m => m.ParticipantId == Guid.Parse(targetId)).Role);
        Assert.Equal(
            MultiChannelAgent.Domain.Inventories.MembershipRole.Editor,
            db.Memberships.Single(m => m.ParticipantId == Guid.Parse(ownerId)).Role);
        Assert.Single(db.Memberships.Where(
            m => m.InventoryId == Guid.Parse(inventoryId) && m.Role == MultiChannelAgent.Domain.Inventories.MembershipRole.Owner));
    }

    [Fact]
    public async Task Transferring_to_oneself_is_rejected_as_a_conflict()
    {
        var (ownerJar, ownerCsrf, ownerId) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var transferRequest = Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/transfer-ownership");
        transferRequest.Content = JsonContent.Create(new { targetIdentifier = ownerId });
        var response = await SendAsync(ownerJar, transferRequest, ownerCsrf);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_non_owner_member_attempting_to_transfer_ownership_is_forbidden()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");
        var (viewerJar, viewerCsrf, viewerId) = await SignInAndBootstrapAsync("Viewer Person");
        await GrantAsync(ownerJar, ownerCsrf, inventoryId, viewerId, "Viewer");
        var (_, _, targetId) = await SignInAndBootstrapAsync("Target Person");

        var transferRequest = Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/transfer-ownership");
        transferRequest.Content = JsonContent.Create(new { targetIdentifier = targetId });
        var response = await SendAsync(viewerJar, transferRequest, viewerCsrf);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Transferring_to_an_unresolvable_identifier_is_a_validation_error()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var transferRequest = Request(HttpMethod.Post, $"/api/inventories/{inventoryId}/transfer-ownership");
        transferRequest.Content = JsonContent.Create(new { targetIdentifier = Guid.NewGuid().ToString() });
        var response = await SendAsync(ownerJar, transferRequest, ownerCsrf);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Granting/transferring by tenant address (not just object id) exercises the other resolvable
    // identifier form the directory boundary supports.
    [Fact]
    public async Task Granting_by_a_registered_tenant_address_resolves_to_the_correct_participant()
    {
        var (ownerJar, ownerCsrf, _) = await SignInAndBootstrapAsync("Owner Person");
        var inventoryId = await CreateInventoryAsync(ownerJar, ownerCsrf, "Warehouse", "req-1");

        var recipientId = Guid.NewGuid().ToString();
        var registerRequest = Request(HttpMethod.Post, "/api/test/tenant-directory/register");
        registerRequest.Content = JsonContent.Create(new
        {
            participantId = recipientId,
            displayName = "Not Yet Signed In",
            address = "notyet@example.com",
        });
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(registerRequest)).StatusCode);

        var response = await GrantAsync(ownerJar, ownerCsrf, inventoryId, "notyet@example.com", "Viewer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        Assert.Equal(
            MultiChannelAgent.Domain.Inventories.MembershipRole.Viewer,
            db.Memberships.Single(m => m.ParticipantId == Guid.Parse(recipientId)).Role);
    }
}
