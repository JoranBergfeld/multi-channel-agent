using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.Core;
using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Infrastructure.Voice;

/// <summary>
/// Production <see cref="IVoiceLiveGateway"/> that negotiates WebRTC sessions through the Azure
/// Voice Live WebSocket endpoint. Authenticates with Entra <see cref="TokenCredential"/> only (no
/// API-key mode). The access token is acquired per connection and never stored in the registry,
/// domain, or result. All error messages are sanitized to prevent leaking tokens, endpoints, SDP,
/// or model names.
/// </summary>
internal sealed class AzureVoiceLiveGateway : IVoiceLiveGateway
{
    private const string TokenScope = "https://ai.azure.com/.default";
    private const string ApiVersion = "2026-04-10";
    private const string RealtimePath = "voice-live/realtime/calls";

    private readonly TokenCredential credential;
    private readonly GatewayRegistry registry;
    private readonly VoiceOptions options;
    private readonly Func<IVoiceWebSocket> socketFactory;

    public AzureVoiceLiveGateway(TokenCredential credential, GatewayRegistry registry, VoiceOptions options)
        : this(credential, registry, options, static () => new ClientWebSocketAdapter()) { }

    internal AzureVoiceLiveGateway(
        TokenCredential credential, GatewayRegistry registry, VoiceOptions options,
        Func<IVoiceWebSocket> socketFactory)
    {
        this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
    }

    public async Task<VoiceLiveNegotiationResult> NegotiateAsync(
        VoiceLiveNegotiationRequest request, CancellationToken cancellationToken)
    {
        var socket = socketFactory();
        string? controlSessionId;
        string? sdpAnswer;

        try
        {
            var uri = BuildEndpointUri();
            var token = await AcquireTokenAsync(cancellationToken);

            socket.SetRequestHeader("Authorization", $"Bearer {token}");
            await socket.ConnectAsync(uri, cancellationToken);

            await SendSessionUpdateAsync(socket, cancellationToken);
            await SendSdpCreateAsync(socket, request.SdpOffer, cancellationToken);

            (controlSessionId, sdpAnswer) = await ReceiveUntilSdpCreatedAsync(socket, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch
        {
            socket.Dispose();
            throw new InvalidOperationException("Voice Live session negotiation failed.");
        }

        // Post-negotiation validation — these messages are safe (no external data)
        if (string.IsNullOrWhiteSpace(controlSessionId))
        {
            socket.Dispose();
            throw new InvalidOperationException("Voice Live negotiation did not receive a control session ID.");
        }

        if (string.IsNullOrWhiteSpace(sdpAnswer))
        {
            socket.Dispose();
            throw new InvalidOperationException("Voice Live negotiation did not receive an SDP answer.");
        }

        if (!registry.TryRegister(controlSessionId, socket))
        {
            socket.Dispose();
            throw new InvalidOperationException("Voice Live control session ID is already registered.");
        }

        return new VoiceLiveNegotiationResult(controlSessionId, sdpAnswer);
    }

    public async Task TerminateAsync(string controlSessionId, CancellationToken cancellationToken)
    {
        var socket = registry.TryRemove(controlSessionId);
        if (socket is null)
            return;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session terminated.", cts.Token);
        }
        catch
        {
            // Close may fail due to network issues — entry is already removed from registry.
        }
        finally
        {
            socket.Dispose();
        }
    }

    public bool OwnsSession(string controlSessionId) => registry.OwnsSession(controlSessionId);

    /// <summary>
    /// Builds the Voice Live WebSocket endpoint URI from configured base endpoint and model.
    /// Uses <see cref="UriBuilder"/> to avoid string concatenation and query injection.
    /// </summary>
    internal Uri BuildEndpointUri()
    {
        var baseUri = new Uri(options.Endpoint!);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        var builder = new UriBuilder(baseUri)
        {
            Path = $"{basePath}/{RealtimePath}",
            Query = $"api-version={Uri.EscapeDataString(ApiVersion)}&model={Uri.EscapeDataString(options.Model!)}",
        };

        return builder.Uri;
    }

    private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        var context = new TokenRequestContext([TokenScope]);
        var result = await credential.GetTokenAsync(context, cancellationToken);
        return result.Token;
    }

    private static async Task SendJsonAsync(IVoiceWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private async Task SendSessionUpdateAsync(IVoiceWebSocket socket, CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text", "audio" },
                instructions = "Transcribe only.",
                tools = Array.Empty<object>(),
                voice = new
                {
                    type = "azure-realtime-native",
                    name = options.VoiceName,
                },
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "azure_semantic_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500,
                },
                input_audio_noise_reduction = new { type = "near_field" },
                input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
            },
        };

        await SendJsonAsync(socket, payload, cancellationToken);
    }

    private static async Task SendSdpCreateAsync(IVoiceWebSocket socket, string sdpOffer, CancellationToken cancellationToken)
    {
        var payload = new
        {
            type = "rtc.call.sdp.create",
            sdp_offer = sdpOffer,
            session = new { modalities = new[] { "text", "audio" } },
        };

        await SendJsonAsync(socket, payload, cancellationToken);
    }

    private static async Task SendResponseCancelAsync(IVoiceWebSocket socket, CancellationToken cancellationToken)
    {
        await SendJsonAsync(socket, new { type = "response.cancel" }, cancellationToken);
    }

    private static async Task<(string? controlSessionId, string? sdpAnswer)> ReceiveUntilSdpCreatedAsync(
        IVoiceWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var assembler = new MemoryStream();
        string? controlSessionId = null;

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Voice Live connection closed unexpectedly during negotiation.");

            assembler.Write(buffer, 0, result.Count);

            if (!result.EndOfMessage)
                continue;

            var json = Encoding.UTF8.GetString(assembler.GetBuffer(), 0, (int)assembler.Length);
            assembler.SetLength(0);

            using var doc = JsonDocument.Parse(json);
            var eventType = doc.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            switch (eventType)
            {
                case "session.created":
                    if (doc.RootElement.TryGetProperty("session", out var session)
                        && session.TryGetProperty("id", out var idElement))
                    {
                        controlSessionId = idElement.GetString();
                    }
                    break;

                case "rtc.call.sdp.created":
                    var sdpAnswer = doc.RootElement.TryGetProperty("sdp_answer", out var sdpElement)
                        ? sdpElement.GetString()
                        : null;
                    return (controlSessionId, sdpAnswer);

                case "response.created":
                    await SendResponseCancelAsync(socket, cancellationToken);
                    break;

                case "error":
                    throw new InvalidOperationException(
                        "Voice Live provider returned an error during negotiation.");
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
