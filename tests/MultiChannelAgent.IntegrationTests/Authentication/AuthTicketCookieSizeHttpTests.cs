using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// Proves the server-side ticket store fix for the "tokens are not server-side" finding end to end
/// over real HTTP: a session carrying a large simulated OIDC access token (representative of what
/// SaveTokens=true actually embeds in a cookie authentication ticket via
/// <c>AuthenticationProperties.StoreTokens</c>) still produces a small, opaque "mca_auth" cookie -
/// never a raw/chunked cookie whose size scales with ticket content - while the large protected
/// payload is durably persisted server-side, the session remains fully usable through the opaque
/// cookie alone, and sign-out removes the server-side row.
/// </summary>
public sealed class AuthTicketCookieSizeHttpTests : IAsyncLifetime
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

    [Fact]
    public async Task A_session_carrying_a_large_simulated_access_token_still_produces_a_small_opaque_auth_cookie()
    {
        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "Ada Lovelace",
                activeTenantMember = true,
                simulatedAccessTokenSizeBytes = 4000,
            }),
        };

        var signInResponse = await _client.SendAsync(signInRequest);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var setCookieHeaders = signInResponse.Headers.GetValues("Set-Cookie").ToList();

        // A cookie this large would previously be split into "mca_authC1", "mca_authC2", ... chunks
        // by ASP.NET Core's automatic cookie chunking; with the fix, only the single unchunked
        // "mca_auth" cookie is ever set.
        Assert.DoesNotContain(setCookieHeaders, h => h.StartsWith("mca_authC", StringComparison.Ordinal));

        var authCookieHeader = setCookieHeaders.Single(h => h.StartsWith("mca_auth=", StringComparison.Ordinal));
        var authCookieValue = authCookieHeader.Split(';', 2)[0]["mca_auth=".Length..];

        Assert.True(
            authCookieValue.Length < 500,
            $"Expected a short opaque session-reference cookie; got {authCookieValue.Length} characters.");

        // The large protected payload is durably persisted server-side and is materially larger than
        // the opaque cookie value referencing it.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            var persistedTicket = await db.AuthTickets.SingleAsync();
            Assert.True(persistedTicket.ProtectedTicket.Length > 4000);
            Assert.True(persistedTicket.ProtectedTicket.Length > authCookieValue.Length);
        }

        // The session remains fully usable: the tiny opaque cookie alone authenticates the request.
        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        bootstrapRequest.Headers.Add("Cookie", $"mca_auth={authCookieValue}");
        var bootstrapResponse = await _client.SendAsync(bootstrapRequest);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
    }

    [Fact]
    public async Task Signing_out_removes_the_server_side_ticket_row()
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "Ada Lovelace",
                activeTenantMember = true,
            }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await _client.SendAsync(signInRequest);
        jar.Capture(signInResponse);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            Assert.Equal(1, await db.AuthTickets.CountAsync());
        }

        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrapRequest);
        var bootstrapResponse = await _client.SendAsync(bootstrapRequest);
        jar.Capture(bootstrapResponse);
        var bootstrapBody = await bootstrapResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var csrfToken = bootstrapBody.GetProperty("csrfToken").GetString()!;

        var signOutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/sign-out");
        jar.Apply(signOutRequest);
        signOutRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        await _client.SendAsync(signOutRequest);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            Assert.Equal(0, await db.AuthTickets.CountAsync());
        }
    }
}
