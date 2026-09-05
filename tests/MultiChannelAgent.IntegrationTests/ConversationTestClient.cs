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
    private readonly CookieJar _jar;

    private ConversationTestClient(HttpClient client, CookieJar jar)
    {
        _client = client;
        _jar = jar;
    }

    public string CsrfToken { get; private set; } = string.Empty;

    /// <summary>The tenant identifier this client signed in as - what an Owner names when granting it a role.</summary>
    public string ParticipantIdentifier { get; private set; } = string.Empty;

    public static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    /// <summary>Signs a fresh Participant in and bootstraps their session, yielding a ready client.</summary>
    public static async Task<ConversationTestClient> SignInAsync(
        HttpClient client,
        string displayName,
        Action<HttpResponseMessage>? inspectBootstrapResponse = null)
    {
        var participant = new ConversationTestClient(client, new CookieJar());

        participant.ParticipantIdentifier = Guid.NewGuid().ToString();

        var signInResponse = await participant.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = participant.ParticipantIdentifier,
                displayName,
                activeTenantMember = true,
            }),
        });
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapResponse = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap"),
            withCsrf: false,
            beforeCookieCapture: inspectBootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        participant.CsrfToken = body.GetProperty("csrfToken").GetString()!;

        return participant;
    }

    /// <summary>
    /// A second browser tab of the same browser profile: the same cookie jar (therefore the same
    /// authenticated session AND the same web ChannelConversation cookie), the same CSRF token, and
    /// the same Participant. This is what makes "one browser-profile conversation shared across tabs"
    /// testable rather than assumed.
    /// </summary>
    public ConversationTestClient OpenAnotherTab() => new ConversationTestClient(_client, _jar).WithIdentityOf(this);

    private ConversationTestClient WithIdentityOf(ConversationTestClient other)
    {
        CsrfToken = other.CsrfToken;
        ParticipantIdentifier = other.ParticipantIdentifier;
        return this;
    }

    /// <summary>Opens this Turn's event stream, optionally resuming after an event this client already has.</summary>
    public async Task<HttpResponseMessage> OpenTurnStreamAsync(
        Guid turnId, long? lastEventId = null, CancellationToken cancellationToken = default)
    {
        var url = lastEventId is { } resumeFrom
            ? $"/api/turns/{turnId}/events?lastEventId={resumeFrom}"
            : $"/api/turns/{turnId}/events";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        _jar.Apply(request);

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Opens this Participant's Inventory invalidation stream.</summary>
    public async Task<HttpResponseMessage> OpenInventoryStreamAsync(CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/inventory-events");
        _jar.Apply(request);

        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>Rotates this conversation's Foundry history and clears its pending confirmation state.</summary>
    public async Task<HttpResponseMessage> StartNewConversationAsync() =>
        await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/conversation/new"), withCsrf: true);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool withCsrf = false) =>
        SendAsync(request, withCsrf, beforeCookieCapture: null);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool withCsrf,
        Action<HttpResponseMessage>? beforeCookieCapture)
    {
        _jar.Apply(request);
        if (withCsrf)
        {
            request.Headers.Add("X-CSRF-TOKEN", CsrfToken);
        }

        var response = await _client.SendAsync(request);
        beforeCookieCapture?.Invoke(response);
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

    /// <summary>Re-reads the session bootstrap, for example to learn this client's own Participant identity.</summary>
    public async Task<JsonElement> GetBootstrapAsync()
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Selects an already-authorized Inventory as this conversation's Active Inventory.</summary>
    public async Task SelectInventoryAsync(Guid inventoryId)
    {
        var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), withCsrf: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Grants another Participant a role in an Inventory this client Owns.</summary>
    public async Task GrantMembershipAsync(Guid inventoryId, string targetIdentifier, string role)
    {
        var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Put, $"/api/inventories/{inventoryId}/members")
            {
                Content = JsonContent.Create(new { targetIdentifier, role }),
            },
            withCsrf: true);

        Assert.True(response.IsSuccessStatusCode, $"Granting {role} failed with {response.StatusCode}.");
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

    /// <summary>Submits a Turn the channel reports as interrupted - a cut-off utterance that may authorize nothing.</summary>
    public async Task<Guid> SubmitInterruptedTurnAsync(string nativeMessageId, string contentText)
    {
        var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/turns")
            {
                Content = JsonContent.Create(new { nativeMessageId, contentText, interrupted = true }),
            },
            withCsrf: true);

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
