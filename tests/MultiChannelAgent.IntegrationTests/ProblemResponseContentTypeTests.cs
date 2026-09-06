using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Voice;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Proves that every error response written by the <c>UseExceptionHandler</c> pipeline in
/// Program.cs uses <c>Content-Type: application/problem+json</c>, not the default
/// <c>application/json; charset=utf-8</c> that a parameterless <c>WriteAsJsonAsync</c> would emit.
///
/// Two scenarios exercise the handler:
/// <list type="bullet">
/// <item>Malformed request body → ASP.NET Core raises <see cref="BadHttpRequestException"/> (400).
///   The handler preserves the HTTP status from the exception.</item>
/// <item>Unhandled service exception → handler maps any other exception to 500. A
///   <see cref="AlwaysThrowingVoiceSessionStore"/> is injected through test DI; no test-only
///   endpoint is exposed in the production application.</item>
/// </list>
/// </summary>
public sealed class ProblemResponseContentTypeTests : IAsyncLifetime
{
    // Factory for 400 (malformed-body) tests
    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    // Factory for 500 (unhandled-exception) tests — throws on every store call
    private SqliteWebApplicationFactory _throwingFactory = null!;
    private HttpClient _throwingClient = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        _throwingFactory = new SqliteWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IVoiceSessionStore>();
            services.AddScoped<IVoiceSessionStore, AlwaysThrowingVoiceSessionStore>();
        });
        _throwingClient = _throwingFactory.CreateClient(new WebApplicationFactoryClientOptions
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
        _throwingClient.Dispose();
        await _throwingFactory.DisposeAsync();
    }

    // ── 400 / BadHttpRequestException ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Malformed_json_400_response_content_type_is_problem_json()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_client);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/admit")
        {
            Content = new StringContent("not-valid-json{{{", Encoding.UTF8, "application/json"),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(
            response.Content.Headers.ContentType?.MediaType == "application/problem+json",
            $"400 response must use application/problem+json, got: {response.Content.Headers.ContentType}");
    }

    // ── 500 / unhandled exception ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Injects <see cref="AlwaysThrowingVoiceSessionStore"/> so the heartbeat handler throws an
    /// unhandled <see cref="InvalidOperationException"/>, exercising the 500 branch of
    /// <c>UseExceptionHandler</c> in Program.cs.
    /// </summary>
    [Fact]
    public async Task Unhandled_exception_500_response_content_type_is_problem_json()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_throwingClient);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/heartbeat")
        {
            Content = JsonContent.Create(new { voiceSessionId = Guid.NewGuid().ToString() }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _throwingClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.True(
            response.Content.Headers.ContentType?.MediaType == "application/problem+json",
            $"500 response must use application/problem+json, got: {response.Content.Headers.ContentType}");
    }

    [Fact]
    public async Task Unhandled_exception_500_response_body_has_clean_problem_shape_without_exception_details()
    {
        var (jar, csrf) = await SignInAndBootstrapAsync(_throwingClient);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/voice/heartbeat")
        {
            Content = JsonContent.Create(new { voiceSessionId = Guid.NewGuid().ToString() }),
        };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        var response = await _throwingClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // No exception details or stack frames must appear
        Assert.False(
            body.Contains("Exception", StringComparison.OrdinalIgnoreCase),
            $"500 body must not contain exception type names. Body: {Truncate(body)}");
        Assert.False(
            body.Contains(" at ", StringComparison.Ordinal),
            $"500 body must not contain stack trace frames. Body: {Truncate(body)}");

        // Body must carry the clean {type, title, status} problem shape
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("type", out _),
            $"500 body must have 'type'. Body: {Truncate(body)}");
        Assert.True(doc.RootElement.TryGetProperty("title", out _),
            $"500 body must have 'title'. Body: {Truncate(body)}");
        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(HttpClient client)
    {
        var jar = new CookieJar();

        var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName = "ProblemCT Test User",
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

    private static string Truncate(string body) =>
        body.Length > 500 ? string.Concat(body.AsSpan(0, 500), "...") : body;

    /// <summary>
    /// A deterministic test double that throws <see cref="InvalidOperationException"/> on every
    /// <see cref="IVoiceSessionStore"/> method. Injected through <see cref="SqliteWebApplicationFactory"/>
    /// test DI so the heartbeat endpoint produces an unhandled exception without any test-only
    /// endpoint being registered in the production application.
    /// </summary>
    private sealed class AlwaysThrowingVoiceSessionStore : IVoiceSessionStore
    {
        private static InvalidOperationException Fault() =>
            new("Synthetic fault for exception-handler content-type contract test.");

        public Task<VoiceAdmissionResult> TryAdmitAsync(VoiceSession session, int globalCap, CancellationToken cancellationToken)
            => throw Fault();

        public Task<VoiceSession?> FindByIdAsync(VoiceSessionId id, CancellationToken cancellationToken)
            => throw Fault();

        public Task<bool> UpdateAsync(VoiceSession session, VoiceSessionStatus expectedStatus, CancellationToken cancellationToken)
            => throw Fault();

        public Task<IReadOnlyList<VoiceSession>> FindExpiredOrIdleAsync(DateTimeOffset now, CancellationToken cancellationToken)
            => throw Fault();

        public Task<IReadOnlyList<VoiceSession>> FindStaleOwnerSessionsAsync(
            string currentOwnerInstanceId, DateTimeOffset heartbeatCutoff, CancellationToken cancellationToken)
            => throw Fault();
    }
}
