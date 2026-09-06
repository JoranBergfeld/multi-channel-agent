using System.Net.WebSockets;

namespace MultiChannelAgent.Infrastructure.Voice;

/// <summary>
/// Internal seam around <see cref="ClientWebSocket"/> so the gateway can be tested
/// with a deterministic fake instead of a real network connection.
/// </summary>
internal interface IVoiceWebSocket : IDisposable
{
    WebSocketState State { get; }
    void SetRequestHeader(string name, string value);
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);
    ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken);
}
