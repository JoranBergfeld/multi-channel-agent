using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// One signed-in web Participant's browser profile against the real HTTP application boundary: its
/// own cookie jar (so its own web ChannelConversation), its own CSRF token, and the small set of
/// calls a conversational scenario needs. Sharing this between the SQL Server-backed scenario and its
/// Docker-free SQLite twin keeps both proving the identical externally observable behavior.
/// </summary>
public sealed class ConversationTestClient
{
    private readonly HttpClient _client;
    private readonly CookieJar _jar = new();

    private ConversationTestClient(HttpClient client) => _client = client;

    public string CsrfToken { get; private set; } = string.Empty;

    public static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    /// <summary>Signs a fresh Participant in and bootstraps their session, yielding a ready client.</summary>
    public static async Task<ConversationTestClient> SignInAsync(HttpClient client, string displayName)
    {
        var participant = new ConversationTestClient(client);

        var signInResponse = await participant.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId = Guid.NewGuid().ToString(), displayName, activeTenantMember = true }),
        });
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapResponse = await participant.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap"));
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        participant.CsrfToken = body.GetProperty("csrfToken").GetString()!;

        return participant;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool withCsrf = false)
    {
        _jar.Apply(request);
        if (withCsrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", CsrfToken);
        }

        var response = await _client.SendAsync(request);
        _jar.Capture(response);
        return response;
    }

    public async Task<Guid> CreateAndSelectInventoryAsync(string name)
    {
        var createResponse = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
            {
                Content = JsonContent.Create(new { name, clientRequestId = Guid.NewGuid().ToString() }),
            },
            withCsrf: true);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inventoryId = Guid.Parse(created.GetProperty("id").GetString()!);

        var selectResponse = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), withCsrf: true);
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        return inventoryId;
    }

    public async Task<HttpResponseMessage> SubmitTurnAsync(string nativeMessageId, string contentText) =>
        await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId, contentText }) },
            withCsrf: true);

    public async Task<Guid> SubmitAcceptedTurnAsync(string nativeMessageId, string contentText)
    {
        var response = await SubmitTurnAsync(nativeMessageId, contentText);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();
    }

    public async Task<JsonElement?> GetOutcomeAsync(Guid turnId)
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId}/outcome"));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
