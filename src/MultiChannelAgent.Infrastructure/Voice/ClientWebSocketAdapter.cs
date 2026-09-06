using System.Net.WebSockets;

namespace MultiChannelAgent.Infrastructure.Voice;

/// <summary>
/// Production adapter that delegates to a real <see cref="ClientWebSocket"/>.
/// </summary>
internal sealed class ClientWebSocketAdapter : IVoiceWebSocket
{
    private readonly ClientWebSocket socket = new();

    public WebSocketState State => socket.State;

    public void SetRequestHeader(string name, string value) =>
        socket.Options.SetRequestHeader(name, value);

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        socket.ConnectAsync(uri, cancellationToken);

    public async Task SendAsync(
        ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken) =>
        await socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer, CancellationToken cancellationToken) =>
        socket.ReceiveAsync(buffer, cancellationToken);

    public Task CloseAsync(
        WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        socket.CloseAsync(closeStatus, statusDescription, cancellationToken);

    public void Dispose() => socket.Dispose();
}
