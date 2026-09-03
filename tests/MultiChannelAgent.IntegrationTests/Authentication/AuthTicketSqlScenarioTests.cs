using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// The SQL-Server-backed equivalent of <see cref="AuthTicketCookieSizeHttpTests"/>: proves the
/// server-side ticket store's core behavior (opaque short cookie, no chunking, durable server-side
/// persistence, sign-out cleanup) holds against an ephemeral SQL Server container with production EF
/// Core migrations applied - not just SQLite.
/// </summary>
public sealed class AuthTicketSqlScenarioTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Session_carrying_a_large_simulated_access_token_stays_opaque_and_durable_against_real_sql_server()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed ticket store scenario.");

        var client = Factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        var jar = new CookieJar();
        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "Owner Person",
                activeTenantMember = true,
                simulatedAccessTokenSizeBytes = 4000,
            }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var setCookieHeaders = signInResponse.Headers.GetValues("Set-Cookie").ToList();
        Assert.DoesNotContain(setCookieHeaders, h => h.StartsWith("mca_authC", StringComparison.Ordinal));

        var authCookieHeader = setCookieHeaders.Single(h => h.StartsWith("mca_auth=", StringComparison.Ordinal));
        var authCookieValue = authCookieHeader.Split(';', 2)[0]["mca_auth=".Length..];
        Assert.True(authCookieValue.Length < 500, $"Expected a short opaque cookie; got {authCookieValue.Length} characters.");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            var persistedTicket = await db.AuthTickets.SingleAsync();
            Assert.True(persistedTicket.ProtectedTicket.Length > 4000);
        }

        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrapRequest);
        var bootstrapResponse = await client.SendAsync(bootstrapRequest);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var csrfToken = body.GetProperty("csrfToken").GetString()!;

        var signOutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/sign-out");
        jar.Apply(signOutRequest);
        signOutRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        await client.SendAsync(signOutRequest);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            Assert.Equal(0, await db.AuthTickets.CountAsync());
        }
    }
}
