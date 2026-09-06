using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// Docker-free HTTP-boundary coverage for voice-provenance Turn submission: a Turn submitted with
/// a valid active voice session ID belonging to the same Participant and ChannelConversation gets
/// server-attested <see cref="InputModality.Voice"/> and <see cref="ChannelCapabilities.Voice"/>,
/// while any invalid, malformed, nonexistent, wrong-participant, wrong-conversation, non-active,
/// expired, or idle voice session ID falls back safely to <see cref="InputModality.Text"/> with
/// ordinary web capabilities. The client can never bind InputModality or capabilities directly.
/// </summary>
public sealed class VoiceTurnProvenanceTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private FakeVoiceLiveGateway _fakeGateway = null!;

    public Task InitializeAsync()
    {
        _fakeGateway = new FakeVoiceLiveGateway();
        _factory = new SqliteWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IVoiceLiveGateway>();
            services.AddSingleton<IVoiceLiveGateway>(_fakeGateway);

            services.RemoveAll<VoiceOptions>();
            services.AddSingleton(new VoiceOptions
            {
                Enabled = true,
                Endpoint = "wss://test-voice.services.ai.azure.com/voice",
                Model = "test-model",
            });
        });
        _client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ── Voice modality attestation ───────────────────────────────────────────

    [Fact]
    public async Task Turn_with_valid_active_same_participant_and_conversation_gets_Voice_modality_and_capability()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();
        var admitBody = await AdmitSuccessAsync(jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        var turnResponse = await SubmitTurnWithVoiceAsync(jar, csrf, voiceSessionId);
        Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
        var turnBody = await turnResponse.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = turnBody.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Voice, modality);
        Assert.True(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    // ── Safe fallback to Text ────────────────────────────────────────────────

    [Fact]
    public async Task Turn_with_malformed_voiceSessionId_is_accepted_as_Text()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new
            {
                nativeMessageId = Guid.NewGuid().ToString(),
                contentText = "test",
                voiceSessionId = "not-a-guid",
            }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = body.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    [Fact]
    public async Task Turn_with_nonexistent_voiceSessionId_is_accepted_as_Text()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();
        var turnResponse = await SubmitTurnWithVoiceAsync(jar, csrf, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
        var turnBody = await turnResponse.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = turnBody.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    [Fact]
    public async Task Turn_with_wrong_participant_voiceSessionId_is_accepted_as_Text()
    {
        // Participant A admits a voice session.
        var (jarA, csrfA) = await SignInAndBootstrapAsync();
        var admitBody = await AdmitSuccessAsync(jarA, csrfA);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        // Participant B submits a turn referencing A's session → Text.
        var (jarB, csrfB) = await SignInAndBootstrapAsync();
        var turnResponse = await SubmitTurnWithVoiceAsync(jarB, csrfB, voiceSessionId);
        Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
        var turnBody = await turnResponse.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = turnBody.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    [Fact]
    public async Task Turn_with_wrong_conversation_voiceSessionId_is_accepted_as_Text()
    {
        // Sign in the SAME participant with two separate browser profiles (two cookie jars), each
        // of which gets its own web conversation cookie (ChannelConversationId).
        var participantId = Guid.NewGuid().ToString();

        // Browser profile A — admit the voice session here.
        var jarA = new CookieJar();
        var signInA = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId, displayName = "Same User A", activeTenantMember = true }),
        };
        jarA.Apply(signInA);
        var signInAResponse = await _client.SendAsync(signInA);
        jarA.Capture(signInAResponse);
        var bootstrapA = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jarA.Apply(bootstrapA);
        var bootstrapAResponse = await _client.SendAsync(bootstrapA);
        jarA.Capture(bootstrapAResponse);
        var bodyA = await bootstrapAResponse.Content.ReadFromJsonAsync<JsonElement>();
        var csrfA = bodyA.GetProperty("csrfToken").GetString()!;

        var admitBody = await AdmitSuccessAsync(jarA, csrfA);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        // Browser profile B — same participant, different web conversation cookie.
        var jarB = new CookieJar();
        var signInB = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId, displayName = "Same User B", activeTenantMember = true }),
        };
        jarB.Apply(signInB);
        var signInBResponse = await _client.SendAsync(signInB);
        jarB.Capture(signInBResponse);
        var bootstrapB = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jarB.Apply(bootstrapB);
        var bootstrapBResponse = await _client.SendAsync(bootstrapB);
        jarB.Capture(bootstrapBResponse);
        var bodyB = await bootstrapBResponse.Content.ReadFromJsonAsync<JsonElement>();
        var csrfB = bodyB.GetProperty("csrfToken").GetString()!;

        // Submit turn from profile B referencing the session from profile A → Text (wrong conversation).
        var turnResponse = await SubmitTurnWithVoiceAsync(jarB, csrfB, voiceSessionId);
        Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
        var turnBody = await turnResponse.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = turnBody.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    [Fact]
    public async Task Turn_with_ended_voiceSessionId_is_accepted_as_Text()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();
        var admitBody = await AdmitSuccessAsync(jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        // Release the session first.
        await PostWithCsrfAsync(jar, csrf, "/api/voice/release", new { voiceSessionId });

        var turnResponse = await SubmitTurnWithVoiceAsync(jar, csrf, voiceSessionId);
        Assert.Equal(HttpStatusCode.Accepted, turnResponse.StatusCode);
        var turnBody = await turnResponse.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = turnBody.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    [Fact]
    public async Task Turn_without_voiceSessionId_is_accepted_as_Text()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new
            {
                nativeMessageId = Guid.NewGuid().ToString(),
                contentText = "test",
            }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = body.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    // ── Client cannot bind InputModality or capabilities ────────────────────

    [Fact]
    public async Task Client_cannot_set_InputModality_in_request_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new
            {
                nativeMessageId = Guid.NewGuid().ToString(),
                contentText = "test",
                inputModality = "Voice",
            }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = body.GetProperty("turnId").GetGuid();

        var (modality, _) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
    }

    [Fact]
    public async Task Client_cannot_set_capabilities_in_request_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new
            {
                nativeMessageId = Guid.NewGuid().ToString(),
                contentText = "test",
                capabilities = 31, // All flags
            }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = body.GetProperty("turnId").GetGuid();

        var (_, capabilities) = await ReadPersistedTurnAsync(turnId);
        // Must have only the web channel capabilities, not the client-attested value.
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
        Assert.True(capabilities.HasFlag(ChannelCapabilities.Text));
    }

    [Fact]
    public async Task Voice_looking_nativeMessageId_prefix_alone_remains_Text()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new
            {
                nativeMessageId = $"voice:{Guid.NewGuid()}:item_123",
                contentText = "test",
            }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = body.GetProperty("turnId").GetGuid();

        var (modality, capabilities) = await ReadPersistedTurnAsync(turnId);
        Assert.Equal(InputModality.Text, modality);
        Assert.False(capabilities.HasFlag(ChannelCapabilities.Voice));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync()
    {
        var jar = new CookieJar();
        var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "Voice Turn Test User",
                activeTenantMember = true,
            }),
        };
        jar.Apply(signIn);
        var signInResponse = await _client.SendAsync(signIn);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrap = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrap);
        var bootstrapResponse = await _client.SendAsync(bootstrap);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (jar, body.GetProperty("csrfToken").GetString()!);
    }

    private async Task<HttpResponseMessage> PostWithCsrfAsync(
        CookieJar jar, string csrf, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    private async Task<JsonElement> AdmitSuccessAsync(CookieJar jar, string csrf)
    {
        var response = await PostWithCsrfAsync(jar, csrf, "/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("admitted").GetBoolean(), "Expected successful admission.");
        return body;
    }

    private async Task<HttpResponseMessage> SubmitTurnWithVoiceAsync(
        CookieJar jar, string csrf, string voiceSessionId)
    {
        return await PostWithCsrfAsync(jar, csrf, "/api/turns", new
        {
            nativeMessageId = Guid.NewGuid().ToString(),
            contentText = "add five boxes of gloves",
            voiceSessionId,
        });
    }

    private async Task<(InputModality Modality, ChannelCapabilities Capabilities)> ReadPersistedTurnAsync(Guid turnId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var entity = await db.InboxEntries.FindAsync(turnId);
        Assert.NotNull(entity);
        return (entity!.InputModality, entity.Capabilities);
    }
}
