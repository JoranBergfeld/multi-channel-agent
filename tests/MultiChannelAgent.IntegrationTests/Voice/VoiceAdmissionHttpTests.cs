using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// Docker-free HTTP-boundary coverage for the <c>/api/voice/*</c> endpoints: admission, heartbeat,
/// and release. Uses <see cref="SqliteWebApplicationFactory"/> with a deterministic
/// <see cref="FakeVoiceLiveGateway"/> injected through test DI override, never an external Azure
/// endpoint. Authentication, CSRF, input validation, response shape, and ownership isolation are
/// all pinned here at the HTTP boundary.
/// </summary>
public sealed class VoiceAdmissionHttpTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _disabledFactory = null!;
    private HttpClient _disabledClient = null!;

    private SqliteWebApplicationFactory _enabledFactory = null!;
    private HttpClient _enabledClient = null!;
    private FakeVoiceLiveGateway _fakeGateway = null!;

    public Task InitializeAsync()
    {
        // Factory with voice DISABLED (default) — for auth, CSRF, and disabled-denial tests.
        _disabledFactory = new SqliteWebApplicationFactory();
        _disabledClient = _disabledFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        // Factory with voice ENABLED — for admission success, heartbeat, and release tests.
        _fakeGateway = new FakeVoiceLiveGateway();
        _enabledFactory = new SqliteWebApplicationFactory(configureTestServices: services =>
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
        _enabledClient = _enabledFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _disabledClient.Dispose();
        await _disabledFactory.DisposeAsync();
        _enabledClient.Dispose();
        await _enabledFactory.DisposeAsync();
    }

    // ── Auth / CSRF ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Admit_without_auth_returns_401()
    {
        var response = await _disabledClient.PostAsJsonAsync("/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Admit_without_csrf_returns_400()
    {
        var (jar, _) = await SignInAndBootstrapAsync(_disabledClient);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/admit")
        {
            Content = JsonContent.Create(new { sdpOffer = "v=0\r\n" }),
        };
        jar.Apply(request);
        // No CSRF header
        var response = await _disabledClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_without_auth_returns_401()
    {
        var response = await _disabledClient.PostAsJsonAsync(
            "/api/voice/heartbeat", new { voiceSessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Release_without_auth_returns_401()
    {
        var response = await _disabledClient.PostAsJsonAsync(
            "/api/voice/release", new { voiceSessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_without_csrf_returns_400()
    {
        var (jar, _) = await SignInAndBootstrapAsync(_disabledClient);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/heartbeat")
        {
            Content = JsonContent.Create(new { voiceSessionId = Guid.NewGuid() }),
        };
        jar.Apply(request);
        var response = await _disabledClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Release_without_csrf_returns_400()
    {
        var (jar, _) = await SignInAndBootstrapAsync(_disabledClient);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/release")
        {
            Content = JsonContent.Create(new { voiceSessionId = Guid.NewGuid() }),
        };
        jar.Apply(request);
        var response = await _disabledClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Disabled denial ──────────────────────────────────────────────────────

    [Fact]
    public async Task Admit_when_disabled_returns_200_with_typed_denial()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_disabledClient);
        var response = await PostWithCsrfAsync(_disabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("admitted").GetBoolean());
        Assert.Equal("VoiceDisabled", body.GetProperty("denialReason").GetString());
        Assert.True(
            !body.TryGetProperty("voiceSessionId", out var vsid) || vsid.ValueKind == JsonValueKind.Null,
            "Denied admission must not contain a voiceSessionId.");
        Assert.True(
            !body.TryGetProperty("sdpAnswer", out var sdp) || sdp.ValueKind == JsonValueKind.Null,
            "Denied admission must not contain an sdpAnswer.");
    }

    // ── Enabled admission success ────────────────────────────────────────────

    [Fact]
    public async Task Admit_when_enabled_returns_200_with_session_id_and_sdp_answer()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("admitted").GetBoolean());
        Assert.NotEqual(Guid.Empty.ToString(), body.GetProperty("voiceSessionId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("sdpAnswer").GetString()));
        Assert.True(
            !body.TryGetProperty("denialReason", out var dr) || dr.ValueKind == JsonValueKind.Null,
            "Successful admission must not contain a denialReason.");
    }

    [Fact]
    public async Task Admit_response_never_contains_controlSessionId_or_azure_urls()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });

        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("controlSessionId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("services.ai.azure.com", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admit_with_blank_sdpOffer_returns_400()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Admit_with_missing_sdpOffer_returns_400()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Heartbeat ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_active_session_returns_lifecycle_state()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/heartbeat", new { voiceSessionId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("renewed").GetBoolean());
        Assert.Equal("active", body.GetProperty("lifecycleState").GetString());
        Assert.True(body.GetProperty("remainingSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task Heartbeat_malformed_id_returns_400()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/heartbeat", new { voiceSessionId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_nonexistent_session_returns_404()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/heartbeat", new { voiceSessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Heartbeat_wrong_participant_returns_404()
    {
        // Participant A admits.
        var (jarA, csrfA) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jarA, csrfA);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        // Participant B heartbeats the same session → 404 (no ownership disclosure).
        var (jarB, csrfB) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jarB, csrfB,
            "/api/voice/heartbeat", new { voiceSessionId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Release ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_active_session_returns_200()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Release_terminates_provider_session()
    {
        var gatewayBefore = _fakeGateway.ActiveSessionCount;
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;
        Assert.True(_fakeGateway.ActiveSessionCount > gatewayBefore);

        await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId });
        Assert.Equal(gatewayBefore, _fakeGateway.ActiveSessionCount);
    }

    [Fact]
    public async Task Release_ends_session_in_store()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId });

        // Heartbeat after release returns 404 — session is ended.
        var heartbeat = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/heartbeat", new { voiceSessionId });
        Assert.Equal(HttpStatusCode.NotFound, heartbeat.StatusCode);
    }

    [Fact]
    public async Task Release_wrong_participant_returns_404()
    {
        var (jarA, csrfA) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jarA, csrfA);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        var (jarB, csrfB) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jarB, csrfB,
            "/api/voice/release", new { voiceSessionId });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Release_nonexistent_session_returns_404()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Release_malformed_id_returns_400()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId = "not-a-guid" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Release_is_idempotent()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jar, csrf);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        var first = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second release is idempotent — still 200 (or 404, both acceptable for ended sessions).
        var second = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId });
        Assert.True(
            second.StatusCode == HttpStatusCode.OK || second.StatusCode == HttpStatusCode.NotFound,
            $"Idempotent release must be 200 or 404, got {second.StatusCode}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(HttpClient client)
    {
        var jar = new CookieJar();
        var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "Voice Test User",
                activeTenantMember = true,
            }),
        };
        jar.Apply(signIn);
        var signInResponse = await client.SendAsync(signIn);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrap = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrap);
        var bootstrapResponse = await client.SendAsync(bootstrap);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (jar, body.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<HttpResponseMessage> PostWithCsrfAsync(
        HttpClient client, CookieJar jar, string csrf, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    private static async Task<JsonElement> AdmitSuccessAsync(
        HttpClient client, CookieJar jar, string csrf)
    {
        var response = await PostWithCsrfAsync(client, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("admitted").GetBoolean(), "Expected successful admission.");
        return body;
    }
}
