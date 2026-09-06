using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace MultiChannelAgent.Infrastructure.Voice;

/// <summary>
/// Thread-safe registry of active Voice Live control sessions. Maps control session IDs to their
/// owned WebSocket connections. Registration only succeeds after successful SDP negotiation;
/// duplicate IDs never overwrite an existing entry.
/// </summary>
internal sealed class GatewayRegistry
{
    private readonly ConcurrentDictionary<string, IVoiceWebSocket> sessions = new();

    /// <summary>
    /// Registers a negotiated session. Returns <see langword="false"/> if the control session ID
    /// is already present (the existing entry is never overwritten or leaked).
    /// </summary>
    public bool TryRegister(string controlSessionId, IVoiceWebSocket socket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlSessionId);
        ArgumentNullException.ThrowIfNull(socket);
        return sessions.TryAdd(controlSessionId, socket);
    }

    /// <summary>
    /// Returns <see langword="true"/> only if the session is registered and the socket is still
    /// in the <see cref="WebSocketState.Open"/> state.
    /// </summary>
    public bool OwnsSession(string controlSessionId) =>
        sessions.TryGetValue(controlSessionId, out var socket) && socket.State == WebSocketState.Open;

    /// <summary>
    /// Atomically removes and returns the socket for the given control session ID, or
    /// <see langword="null"/> if the ID is not registered. Only one concurrent caller can obtain
    /// the socket for a given ID.
    /// </summary>
    public IVoiceWebSocket? TryRemove(string controlSessionId) =>
        sessions.TryRemove(controlSessionId, out var socket) ? socket : null;

    public int Count => sessions.Count;
}
