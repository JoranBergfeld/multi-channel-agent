using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MultiChannelAgent.Application.Tests.Voice;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// Explicit security regression suite: proves that no Azure credentials, provider-internal
/// identifiers, configuration secrets, stack traces, or ownership details ever reach a client
/// through any voice API response or static asset.
///
/// Conspicuous sentinel strings are seeded into test configuration. Every test asserts those
/// sentinels are absent from all response bodies so a future regression in serialisation,
/// exception handling, or problem-detail mapping is caught at the test boundary before production.
///
/// Task 8 (<see cref="VoiceAdmissionHttpTests"/>) already pins behaviour and has focused no-leak
/// assertions; this file is a dedicated, exhaustive security regression layer on top of that.
/// </summary>
public sealed class VoiceSecurityTests : IAsyncLifetime
{
    // ── Sentinel values seeded into test config ─────────────────────────────────────────────────
    // Conspicuous strings that must never appear in any HTTP response body.

    private const string SentinelEndpointHost = "sentinel-voice-99999.services.ai.azure.com";
    private const string SentinelEndpoint = $"wss://{SentinelEndpointHost}/realtime";
    private const string SentinelModel = "SENTINEL-MODEL-99999";

    /// <summary>
    /// Credential-shaped and provider-internal terms that must never appear in any response body.
    /// Deliberately excludes the bare word "token" to avoid false positives against the legitimate
    /// CSRF <c>csrfToken</c> contract field.
    /// </summary>
    private static readonly IReadOnlyList<string> CredentialDenyList =
    [
        // Seeded sentinel values
        SentinelEndpoint,
        SentinelEndpointHost,
        SentinelModel,
        // Azure / provider infrastructure terms
        "services.ai.azure.com",
        "cognitiveservices.azure.com",
        "wss://",
        // Credential-shaped field names
        "apiKey",
        "api-key",
        "api_key",
        "authorization",
        "bearer",
        "access_token",
        "clientSecret",
        "client_secret",
        "client-secret",
        // Transport-internal identifier never exposed to clients
        "controlSessionId",
    ];

    private SqliteWebApplicationFactory _disabledFactory = null!;
    private HttpClient _disabledClient = null!;
    private SqliteWebApplicationFactory _enabledFactory = null!;
    private HttpClient _enabledClient = null!;

    public Task InitializeAsync()
    {
        _disabledFactory = new SqliteWebApplicationFactory();
        _disabledClient = _disabledFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        _enabledFactory = new SqliteWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IVoiceLiveGateway>();
            services.AddSingleton<IVoiceLiveGateway>(new FakeVoiceLiveGateway());

            services.RemoveAll<VoiceOptions>();
            services.AddSingleton(new VoiceOptions
            {
                Enabled = true,
                Endpoint = SentinelEndpoint,
                Model = SentinelModel,
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

    // ── Test 1: Enabled admission success leaks nothing ──────────────────────────────────────────

    [Fact]
    public async Task Admit_success_response_contains_only_client_contract_fields_and_no_provider_internals()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.True(body.GetProperty("admitted").GetBoolean());

        // Inspect parsed JSON field names — only the declared client contract must appear at root.
        var allowedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "admitted", "voiceSessionId", "sdpAnswer", "denialReason",
        };
        foreach (var prop in body.EnumerateObject())
        {
            Assert.True(
                allowedFields.Contains(prop.Name),
                $"Response contains unexpected field '{prop.Name}' outside the client contract.");
        }

        // Raw text denylist: no credential or provider-internal content anywhere in the body.
        AssertNoCredentialLeak(text, "enabled admit success");
    }

    // ── Test 2: Disabled admission response also leaks nothing ───────────────────────────────────

    [Fact]
    public async Task Admit_disabled_response_leaks_no_provider_internals()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_disabledClient);
        var response = await PostWithCsrfAsync(_disabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.False(body.GetProperty("admitted").GetBoolean());

        var allowedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "admitted", "voiceSessionId", "sdpAnswer", "denialReason",
        };
        foreach (var prop in body.EnumerateObject())
        {
            Assert.True(
                allowedFields.Contains(prop.Name),
                $"Disabled response contains unexpected field '{prop.Name}' outside the client contract.");
        }

        AssertNoCredentialLeak(text, "disabled admit");
    }

    // ── Test 3: SDP validation errors produce clean problem responses ────────────────────────────

    [Fact]
    public async Task Admit_blank_sdp_returns_400_with_clean_problem_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertCleanErrorResponse(text, "blank sdpOffer");
    }

    [Fact]
    public async Task Admit_missing_sdp_returns_400_with_clean_problem_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertCleanErrorResponse(text, "missing sdpOffer");
    }

    [Fact]
    public async Task Admit_oversized_sdp_response_contains_no_credential_or_internal_leak()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        // ~64 KB of repeated SDP content — stresses any logging or error path without being
        // syntactically blank. Whatever status is returned must be clean.
        var oversized = string.Concat(Enumerable.Repeat("v=0\r\na=sendrecv\r\n", 4096));
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/admit", new { sdpOffer = oversized });

        var text = await response.Content.ReadAsStringAsync();
        AssertNoCredentialLeak(text, "oversized sdpOffer");
        AssertNoInternalLeak(text, "oversized sdpOffer");
    }

    [Fact]
    public async Task Admit_malformed_json_body_returns_4xx_with_clean_problem_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/admit")
        {
            Content = new StringContent("not-json-at-all", Encoding.UTF8, "application/json"),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _enabledClient.SendAsync(request);
        jar.Capture(response);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest
            || response.StatusCode == HttpStatusCode.UnsupportedMediaType,
            $"Malformed body must produce 400 or 415, got {(int)response.StatusCode}.");
        AssertCleanErrorResponse(text, "malformed JSON body");
    }

    // ── Test 4: Heartbeat/release for nonexistent, wrong-owner, or malformed sessions ───────────

    [Fact]
    public async Task Heartbeat_nonexistent_session_returns_404_with_no_internal_leak()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/heartbeat", new { voiceSessionId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertNoCredentialLeak(text, "heartbeat nonexistent");
        AssertNoInternalLeak(text, "heartbeat nonexistent");
    }

    [Fact]
    public async Task Heartbeat_wrong_owner_returns_404_with_no_ownership_disclosure()
    {
        // Participant A admits.
        var (jarA, csrfA) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jarA, csrfA);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        // Participant B sends a heartbeat for A's session — must get a uniform 404 with no detail.
        var (jarB, csrfB) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jarB, csrfB,
            "/api/voice/heartbeat", new { voiceSessionId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertNoCredentialLeak(text, "heartbeat wrong owner");
        // Response must not reveal whether the session exists or who owns it.
        AssertNoOwnershipDisclosure(text, "heartbeat wrong owner");
    }

    [Fact]
    public async Task Heartbeat_malformed_id_returns_400_with_clean_problem_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/heartbeat", new { voiceSessionId = "not-a-guid" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertCleanErrorResponse(text, "heartbeat malformed id");
    }

    [Fact]
    public async Task Release_nonexistent_session_returns_404_with_no_internal_leak()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertNoCredentialLeak(text, "release nonexistent");
        AssertNoInternalLeak(text, "release nonexistent");
    }

    [Fact]
    public async Task Release_wrong_owner_returns_404_with_no_ownership_disclosure()
    {
        var (jarA, csrfA) = await SignInAndBootstrapAsync(_enabledClient);
        var admitBody = await AdmitSuccessAsync(_enabledClient, jarA, csrfA);
        var voiceSessionId = admitBody.GetProperty("voiceSessionId").GetString()!;

        var (jarB, csrfB) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jarB, csrfB,
            "/api/voice/release", new { voiceSessionId });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertNoCredentialLeak(text, "release wrong owner");
        AssertNoOwnershipDisclosure(text, "release wrong owner");
    }

    [Fact]
    public async Task Release_malformed_id_returns_400_with_clean_problem_body()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);
        var response = await PostWithCsrfAsync(_enabledClient, jar, csrf,
            "/api/voice/release", new { voiceSessionId = "not-a-guid" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        AssertCleanErrorResponse(text, "release malformed id");
    }

    // ── Test 5: Static assets and session bootstrap expose no voice credentials ─────────────────

    [Fact]
    public async Task Index_html_does_not_expose_voice_credentials_or_config()
    {
        var response = await _enabledClient.GetAsync("/");
        var text = await response.Content.ReadAsStringAsync();
        AssertNoCredentialLeak(text, "index.html");
        // Config key names themselves must not appear in static assets.
        Assert.False(
            text.Contains("Voice:Endpoint", StringComparison.OrdinalIgnoreCase),
            "[index.html] Must not contain 'Voice:Endpoint' config key.");
    }

    [Fact]
    public async Task Bootstrap_response_does_not_expose_voice_credentials_or_config()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_enabledClient);

        // Issue a fresh bootstrap request using the established session cookies.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(request);
        var response = await _enabledClient.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoCredentialLeak(text, "bootstrap response");
        Assert.False(
            text.Contains("Voice:Endpoint", StringComparison.OrdinalIgnoreCase),
            "[bootstrap] Must not contain 'Voice:Endpoint' config key.");
    }

    // ── Security assertion helpers ───────────────────────────────────────────────────────────────

    /// <summary>Asserts no credential-shaped or provider-internal term appears in <paramref name="body"/>.</summary>
    private static void AssertNoCredentialLeak(string body, string context)
    {
        foreach (var term in CredentialDenyList)
        {
            Assert.False(
                body.Contains(term, StringComparison.OrdinalIgnoreCase),
                $"[{context}] Response must not contain '{term}'. Body (first 500 chars): {Truncate(body)}");
        }
    }

    /// <summary>
    /// Asserts no server-internal diagnostic content appears in <paramref name="body"/>:
    /// no exception type names, stack trace frames, filesystem paths, or config key names.
    /// </summary>
    private static void AssertNoInternalLeak(string body, string context)
    {
        Assert.False(
            body.Contains("Exception", StringComparison.OrdinalIgnoreCase),
            $"[{context}] Response must not contain exception type names. Body: {Truncate(body)}");

        // Stack trace frames always contain " at " with surrounding spaces.
        Assert.False(
            body.Contains(" at ", StringComparison.Ordinal),
            $"[{context}] Response must not contain stack trace frames (' at '). Body: {Truncate(body)}");

        Assert.False(
            body.Contains(":\\", StringComparison.Ordinal) || body.Contains("/home/", StringComparison.Ordinal),
            $"[{context}] Response must not contain filesystem paths. Body: {Truncate(body)}");

        Assert.False(
            body.Contains("Voice:Endpoint", StringComparison.OrdinalIgnoreCase),
            $"[{context}] Response must not contain 'Voice:Endpoint' config key. Body: {Truncate(body)}");
    }

    /// <summary>Applies both <see cref="AssertNoCredentialLeak"/> and <see cref="AssertNoInternalLeak"/>.</summary>
    private static void AssertCleanErrorResponse(string body, string context)
    {
        AssertNoCredentialLeak(body, context);
        AssertNoInternalLeak(body, context);
    }

    /// <summary>
    /// Asserts the response does not disclose session ownership details. A uniform 404 must be
    /// indistinguishable whether the session does not exist or belongs to another participant.
    /// </summary>
    private static void AssertNoOwnershipDisclosure(string body, string context)
    {
        // A 404 body must not hint at the real owner's identity.
        Assert.False(
            body.Contains("participantId", StringComparison.OrdinalIgnoreCase),
            $"[{context}] 404 response must not disclose participantId. Body: {Truncate(body)}");
        Assert.False(
            body.Contains("ownerId", StringComparison.OrdinalIgnoreCase),
            $"[{context}] 404 response must not disclose ownerId. Body: {Truncate(body)}");
    }

    private static string Truncate(string body) =>
        body.Length > 500 ? string.Concat(body.AsSpan(0, 500), "...") : body;

    // ── Request helpers (mirrors VoiceAdmissionHttpTests) ───────────────────────────────────────

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(HttpClient client)
    {
        var jar = new CookieJar();
        var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "Security Test User",
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

    private static async Task<JsonElement> AdmitSuccessAsync(HttpClient client, CookieJar jar, string csrf)
    {
        var response = await PostWithCsrfAsync(client, jar, csrf,
            "/api/voice/admit", new { sdpOffer = "v=0\r\n" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("admitted").GetBoolean(), "Expected successful admission.");
        return body;
    }
}
