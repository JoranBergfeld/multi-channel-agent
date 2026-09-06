using System.Net.WebSockets;
using System.Text;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// Deterministic fake <see cref="IVoiceWebSocket"/> for gateway tests. Scripted receive entries
/// simulate server responses; sent messages and connection details are captured for assertion.
/// </summary>
internal sealed class FakeVoiceWebSocket : IVoiceWebSocket
{
    private readonly record struct ReceiveEntry(byte[] Data, WebSocketMessageType MessageType, bool EndOfMessage);

    private readonly Queue<ReceiveEntry> receiveQueue = new();
    private readonly List<string> sentMessages = [];
    private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
    private WebSocketState state = WebSocketState.None;

    public Uri? ConnectedUri { get; private set; }
    public IReadOnlyList<string> SentMessages => sentMessages;
    public IReadOnlyDictionary<string, string> Headers => headers;
    public WebSocketState State => state;
    public bool IsDisposed { get; private set; }
    public int CloseCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }
    public Exception? ConnectFailure { get; set; }
    public Exception? CloseFailure { get; set; }

    /// <summary>Enqueue a complete JSON message as a single WebSocket frame.</summary>
    public void EnqueueReceive(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        receiveQueue.Enqueue(new ReceiveEntry(bytes, WebSocketMessageType.Text, true));
    }

    /// <summary>Enqueue a JSON message split across multiple WebSocket frames of <paramref name="chunkSize"/> bytes.</summary>
    public void EnqueueFragmented(string json, int chunkSize)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var i = 0; i < bytes.Length; i += chunkSize)
        {
            var end = Math.Min(i + chunkSize, bytes.Length);
            var chunk = bytes[i..end];
            var isLast = end >= bytes.Length;
            receiveQueue.Enqueue(new ReceiveEntry(chunk, WebSocketMessageType.Text, isLast));
        }
    }

    /// <summary>Enqueue a WebSocket close frame.</summary>
    public void EnqueueClose() =>
        receiveQueue.Enqueue(new ReceiveEntry([], WebSocketMessageType.Close, true));

    public void SetRequestHeader(string name, string value) => headers[name] = value;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectFailure is not null) throw ConnectFailure;
        ConnectedUri = uri;
        state = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public Task SendAsync(
        ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sentMessages.Add(Encoding.UTF8.GetString(buffer.Span));
        return Task.CompletedTask;
    }

    public ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receiveQueue.Count == 0)
            throw new WebSocketException("No more scripted receive entries.");

        var entry = receiveQueue.Dequeue();
        entry.Data.CopyTo(buffer);
        return ValueTask.FromResult(
            new ValueWebSocketReceiveResult(entry.Data.Length, entry.MessageType, entry.EndOfMessage));
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseCallCount++;
        state = WebSocketState.Closed;
        if (CloseFailure is not null) throw CloseFailure;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        DisposeCallCount++;
        IsDisposed = true;
        state = WebSocketState.Closed;
    }
}
