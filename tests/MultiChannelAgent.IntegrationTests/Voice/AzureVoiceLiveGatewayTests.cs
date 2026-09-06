using System.Net.WebSockets;
using System.Text.Json;
using Azure.Core;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

public sealed class AzureVoiceLiveGatewayTests
{
    private const string TestEndpoint = "wss://test-voice.services.ai.azure.com/openai";
    private const string TestModel = "gpt-4o-realtime";
    private const string TestToken = "sentinel-token-value-12345";
    private const string TestSdpOffer = "v=0\r\no=caller 123 456 IN IP4 0.0.0.0\r\n";
    private const string TestSdpAnswer = "v=0\r\no=answer 789 012 IN IP4 0.0.0.0\r\n";
    private const string TestControlSessionId = "sess_abc123def456";

    private static VoiceOptions CreateOptions(
        string endpoint = TestEndpoint, string model = TestModel) => new()
    {
        Enabled = true,
        Endpoint = endpoint,
        Model = model,
        VoiceName = "en-US-Ava:DragonHDLatestNeural",
    };

    private static string SessionCreatedJson(string controlSessionId) =>
        JsonSerializer.Serialize(new { type = "session.created", session = new { id = controlSessionId } });

    private static string SdpCreatedJson(string sdpAnswer) =>
        JsonSerializer.Serialize(new { type = "rtc.call.sdp.created", sdp_answer = sdpAnswer });

    private static FakeVoiceWebSocket CreateSocketWithSuccessfulNegotiation(
        string controlSessionId = TestControlSessionId,
        string sdpAnswer = TestSdpAnswer)
    {
        var socket = new FakeVoiceWebSocket();
        socket.EnqueueReceive(SessionCreatedJson(controlSessionId));
        socket.EnqueueReceive(SdpCreatedJson(sdpAnswer));
        return socket;
    }

    private static AzureVoiceLiveGateway CreateGateway(
        FakeVoiceWebSocket socket,
        GatewayRegistry? registry = null,
        VoiceOptions? options = null,
        TokenCredential? credential = null) =>
        new(
            credential ?? new FakeTokenCredential(TestToken),
            registry ?? new GatewayRegistry(),
            options ?? CreateOptions(),
            () => socket);

    // ── URI construction ──────────────────────────────────────────────────

    [Fact]
    public void Uri_uses_configured_primary_host_and_appends_realtime_path()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket, options: CreateOptions("wss://myresource.services.ai.azure.com"));

        var uri = gateway.BuildEndpointUri();

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("myresource.services.ai.azure.com", uri.Host);
        Assert.Contains("/voice-live/realtime/calls", uri.AbsolutePath);
        Assert.Contains("api-version=2026-04-10", uri.Query);
        Assert.Contains("model=gpt-4o-realtime", uri.Query);
    }

    [Fact]
    public void Uri_uses_configured_legacy_host()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket, options: CreateOptions("wss://legacy.cognitiveservices.azure.com"));

        var uri = gateway.BuildEndpointUri();

        Assert.Equal("legacy.cognitiveservices.azure.com", uri.Host);
        Assert.Contains("/voice-live/realtime/calls", uri.AbsolutePath);
    }

    [Fact]
    public void Uri_url_encodes_model_with_special_characters()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket, options: CreateOptions(model: "gpt-4o realtime/v2"));

        var uri = gateway.BuildEndpointUri();

        // Must not contain raw spaces or slashes in query value
        Assert.DoesNotContain("model=gpt-4o realtime/v2", uri.Query);
        Assert.Contains("model=gpt-4o", uri.Query); // URL-encoded form present
    }

    [Fact]
    public void Uri_preserves_existing_endpoint_path()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket, options: CreateOptions("wss://myresource.services.ai.azure.com/openai/deployments"));

        var uri = gateway.BuildEndpointUri();

        Assert.StartsWith("/openai/deployments/voice-live/realtime/calls", uri.AbsolutePath);
    }

    // ── Auth ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Token_is_acquired_with_exactly_ai_azure_scope_and_request_cancellation_token()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var credential = new FakeTokenCredential(TestToken);
        var gateway = CreateGateway(socket, credential: credential);
        using var cts = new CancellationTokenSource();

        await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), cts.Token);

        Assert.NotNull(credential.LastRequestContext);
        Assert.Equal(["https://ai.azure.com/.default"], credential.LastRequestContext.Value.Scopes);
        Assert.Equal(cts.Token, credential.LastCancellationToken);
    }

    [Fact]
    public async Task Authorization_bearer_header_is_set_on_websocket()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket);

        await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        Assert.True(socket.Headers.ContainsKey("Authorization"));
        var authHeader = socket.Headers["Authorization"];
        // Regression: must not be placeholder, empty, or API-key form
        Assert.NotEqual("******", authHeader);
        Assert.False(string.IsNullOrWhiteSpace(authHeader));
        Assert.DoesNotMatch(@"^\*+$", authHeader);
        Assert.DoesNotMatch(@"(?i)^api[-_]?key", authHeader);
        // Must be standard HTTP Bearer scheme with the test token
        Assert.Equal($"Bearer {TestToken}", authHeader);
    }

    // ── Protocol messages ─────────────────────────────────────────────────

    [Fact]
    public async Task Session_update_has_tools_instructions_transcription_vad_noise_echo()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket);

        await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        // First sent message is session.update
        Assert.True(socket.SentMessages.Count >= 2);
        using var doc = JsonDocument.Parse(socket.SentMessages[0]);
        var root = doc.RootElement;

        Assert.Equal("session.update", root.GetProperty("type").GetString());

        var session = root.GetProperty("session");
        Assert.Equal("Transcribe only.", session.GetProperty("instructions").GetString());
        Assert.Equal(0, session.GetProperty("tools").GetArrayLength());
        Assert.Equal("whisper-1", session.GetProperty("input_audio_transcription").GetProperty("model").GetString());
        Assert.Equal("azure_semantic_vad", session.GetProperty("turn_detection").GetProperty("type").GetString());
        Assert.True(session.TryGetProperty("input_audio_noise_reduction", out _));
        Assert.True(session.TryGetProperty("input_audio_echo_cancellation", out _));

        // Voice name from options
        Assert.Equal("en-US-Ava:DragonHDLatestNeural", session.GetProperty("voice").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Sdp_create_contains_offer_from_request()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket);

        await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        // Second sent message is rtc.call.sdp.create
        Assert.True(socket.SentMessages.Count >= 2);
        using var doc = JsonDocument.Parse(socket.SentMessages[1]);
        var root = doc.RootElement;

        Assert.Equal("rtc.call.sdp.create", root.GetProperty("type").GetString());
        Assert.Equal(TestSdpOffer, root.GetProperty("sdp_offer").GetString());
        Assert.True(root.GetProperty("session").TryGetProperty("modalities", out _));
    }

    // ── Receive handling ──────────────────────────────────────────────────

    [Fact]
    public async Task Fragmented_receive_assembles_complete_sdp_created_message()
    {
        var socket = new FakeVoiceWebSocket();
        socket.EnqueueReceive(SessionCreatedJson(TestControlSessionId));

        // Fragment the SDP created message across multiple frames
        socket.EnqueueFragmented(SdpCreatedJson(TestSdpAnswer), chunkSize: 20);

        var gateway = CreateGateway(socket);
        var result = await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        Assert.Equal(TestSdpAnswer, result.SdpAnswer);
        Assert.Equal(TestControlSessionId, result.ControlSessionId);
    }

    [Fact]
    public async Task Response_created_triggers_response_cancel_before_sdp_answer()
    {
        var socket = new FakeVoiceWebSocket();
        socket.EnqueueReceive(SessionCreatedJson(TestControlSessionId));
        socket.EnqueueReceive("""{"type":"response.created","response":{"id":"resp_unsolicited"}}""");
        socket.EnqueueReceive(SdpCreatedJson(TestSdpAnswer));

        var gateway = CreateGateway(socket);
        await gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        // session.update (0), sdp.create (1), response.cancel (2)
        Assert.True(socket.SentMessages.Count >= 3);
        using var doc = JsonDocument.Parse(socket.SentMessages[2]);
        Assert.Equal("response.cancel", doc.RootElement.GetProperty("type").GetString());
    }

    // ── Successful negotiation ────────────────────────────────────────────

    [Fact]
    public async Task Successful_negotiation_returns_control_session_id_and_sdp_answer()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket);

        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        Assert.Equal(TestControlSessionId, result.ControlSessionId);
        Assert.Equal(TestSdpAnswer, result.SdpAnswer);
    }

    [Fact]
    public async Task Successful_negotiation_registers_session_in_registry()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var registry = new GatewayRegistry();
        var gateway = CreateGateway(socket, registry: registry);

        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        Assert.True(gateway.OwnsSession(result.ControlSessionId));
        Assert.Equal(1, registry.Count);
    }

    // ── Terminate ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Terminate_closes_socket_and_removes_from_registry()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var registry = new GatewayRegistry();
        var gateway = CreateGateway(socket, registry: registry);
        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        await gateway.TerminateAsync(result.ControlSessionId, CancellationToken.None);

        Assert.False(gateway.OwnsSession(result.ControlSessionId));
        Assert.Equal(1, socket.CloseCallCount);
        Assert.True(socket.IsDisposed);
    }

    [Fact]
    public async Task Terminate_unknown_id_is_noop()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var gateway = CreateGateway(socket);

        // Should not throw
        await gateway.TerminateAsync("unknown-session-id", CancellationToken.None);
    }

    [Fact]
    public async Task Concurrent_terminate_exactly_one_closes_socket()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        var registry = new GatewayRegistry();
        var gateway = CreateGateway(socket, registry: registry);
        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => gateway.TerminateAsync(result.ControlSessionId, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(1, socket.CloseCallCount);
        Assert.Equal(1, socket.DisposeCallCount);
    }

    // ── Error handling ────────────────────────────────────────────────────

    [Fact]
    public async Task Negotiation_connect_failure_disposes_socket_without_registering()
    {
        var socket = new FakeVoiceWebSocket
        {
            ConnectFailure = new WebSocketException("Connection refused"),
        };
        var registry = new GatewayRegistry();
        var gateway = CreateGateway(socket, registry: registry);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None));

        Assert.True(socket.IsDisposed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Cancellation_disposes_socket_and_preserves_cancellation()
    {
        var socket = new FakeVoiceWebSocket();
        // No receive entries queued — ReceiveAsync will throw OperationCanceledException via token
        socket.EnqueueReceive(SessionCreatedJson(TestControlSessionId));
        // The next receive will check cancellation

        var registry = new GatewayRegistry();
        var gateway = CreateGateway(socket, registry: registry);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Cancellation should propagate
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), cts.Token));

        Assert.True(socket.IsDisposed);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Provider_error_event_throws_sanitized_exception()
    {
        var socket = new FakeVoiceWebSocket();
        socket.EnqueueReceive(SessionCreatedJson(TestControlSessionId));
        socket.EnqueueReceive("""{"type":"error","error":{"type":"invalid_request","message":"Raw Azure error with wss://secret.services.ai.azure.com/path?model=secret"}}""");

        var gateway = CreateGateway(socket);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None));

        // Must not leak the raw error message
        Assert.DoesNotContain("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wss://", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(socket.IsDisposed);
    }

    [Fact]
    public async Task Unexpected_close_during_negotiation_throws_sanitized_exception()
    {
        var socket = new FakeVoiceWebSocket();
        socket.EnqueueReceive(SessionCreatedJson(TestControlSessionId));
        socket.EnqueueClose();

        var gateway = CreateGateway(socket);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None));

        Assert.DoesNotContain(TestToken, ex.Message);
        Assert.True(socket.IsDisposed);
    }

    // ── Duplicate ID ──────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_control_session_id_throws_and_disposes_new_socket()
    {
        var registry = new GatewayRegistry();

        // First negotiation succeeds
        var socket1 = CreateSocketWithSuccessfulNegotiation();
        var gateway1 = CreateGateway(socket1, registry: registry);
        await gateway1.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        // Second negotiation receives the same control session ID
        var socket2 = CreateSocketWithSuccessfulNegotiation();
        var gateway2 = CreateGateway(socket2, registry: registry);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway2.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None));

        // Original remains registered; new socket disposed
        Assert.True(registry.OwnsSession(TestControlSessionId));
        Assert.True(socket2.IsDisposed);
        Assert.False(socket1.IsDisposed);
    }

    // ── Socket close failure ──────────────────────────────────────────────

    [Fact]
    public async Task Terminate_socket_close_failure_still_removes_and_disposes()
    {
        var socket = CreateSocketWithSuccessfulNegotiation();
        socket.CloseFailure = new WebSocketException("Network error during close");
        var registry = new GatewayRegistry();
        var gateway = CreateGateway(socket, registry: registry);
        var result = await gateway.NegotiateAsync(
            new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None);

        // Should not throw — close failure is absorbed
        await gateway.TerminateAsync(result.ControlSessionId, CancellationToken.None);

        Assert.False(gateway.OwnsSession(result.ControlSessionId));
        Assert.True(socket.IsDisposed);
        Assert.Equal(0, registry.Count);
    }

    // ── Secret redaction ──────────────────────────────────────────────────

    [Fact]
    public async Task Negotiation_error_does_not_contain_token_endpoint_sdp_or_model()
    {
        var sensitiveEndpoint = "wss://sentinel-host.services.ai.azure.com/secret-path";
        var sensitiveModel = "sentinel-model-deployment";
        var sensitiveToken = "sentinel-bearer-token-99999";
        var sensitiveSdp = "v=0\r\no=sentinel-sdp-secret\r\n";

        var socket = new FakeVoiceWebSocket
        {
            ConnectFailure = new HttpRequestException(
                $"POST {sensitiveEndpoint}?model={sensitiveModel} failed with Authorization: Bearer {sensitiveToken} body: {sensitiveSdp}"),
        };

        var options = CreateOptions(sensitiveEndpoint, sensitiveModel);
        var gateway = CreateGateway(socket, credential: new FakeTokenCredential(sensitiveToken), options: options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(sensitiveSdp), CancellationToken.None));

        string[] sentinels = [sensitiveToken, sensitiveEndpoint, sensitiveModel, sensitiveSdp, "sentinel-host", "sentinel-bearer", "sentinel-sdp", "sentinel-model"];
        AssertNoSecretsInExceptionTree(ex, sentinels);
    }

    [Fact]
    public async Task Recursive_redaction_excludes_sentinels_from_inner_exception_and_data()
    {
        var sensitiveToken = "recursive-sentinel-token-77777";
        var innerException = new InvalidOperationException(
            $"Inner error with token {sensitiveToken}",
            new Exception($"Deep inner with {sensitiveToken}"));
        innerException.Data["debug"] = $"Data contains {sensitiveToken}";

        var socket = new FakeVoiceWebSocket
        {
            ConnectFailure = innerException,
        };

        var gateway = CreateGateway(socket, credential: new FakeTokenCredential(sensitiveToken));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None));

        AssertNoSecretsInExceptionTree(ex, [sensitiveToken, "recursive-sentinel"]);
    }

    // ── Disabled gateway ──────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_gateway_negotiate_throws_InvalidOperationException()
    {
        var disabled = new DisabledVoiceLiveGateway();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => disabled.NegotiateAsync(new VoiceLiveNegotiationRequest(TestSdpOffer), CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_gateway_terminate_is_noop()
    {
        var disabled = new DisabledVoiceLiveGateway();

        // Must not throw
        await disabled.TerminateAsync("any-session-id", CancellationToken.None);
    }

    [Fact]
    public void Disabled_gateway_owns_session_returns_false()
    {
        var disabled = new DisabledVoiceLiveGateway();

        Assert.False(disabled.OwnsSession("any-session-id"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void AssertNoSecretsInExceptionTree(Exception ex, string[] sentinels)
    {
        var current = ex;
        while (current is not null)
        {
            foreach (var sentinel in sentinels)
            {
                Assert.DoesNotContain(sentinel, current.Message, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var key in current.Data.Keys)
            {
                var value = current.Data[key]?.ToString() ?? string.Empty;
                foreach (var sentinel in sentinels)
                {
                    Assert.DoesNotContain(sentinel, value, StringComparison.OrdinalIgnoreCase);
                }
            }

            current = current.InnerException;
        }
    }

    /// <summary>Simple fake <see cref="TokenCredential"/> that returns a fixed token and captures the scope.</summary>
    private sealed class FakeTokenCredential(string token) : TokenCredential
    {
        public TokenRequestContext? LastRequestContext { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastRequestContext = requestContext;
            LastCancellationToken = cancellationToken;
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            LastRequestContext = requestContext;
            LastCancellationToken = cancellationToken;
            return new(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}
