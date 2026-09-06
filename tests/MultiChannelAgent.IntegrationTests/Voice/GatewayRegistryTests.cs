using System.Net.WebSockets;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

public sealed class GatewayRegistryTests
{
    private static async Task<FakeVoiceWebSocket> CreateConnectedSocketAsync()
    {
        var socket = new FakeVoiceWebSocket();
        await socket.ConnectAsync(new Uri("wss://localhost"), CancellationToken.None);
        return socket;
    }

    [Fact]
    public async Task Registered_session_is_owned()
    {
        var registry = new GatewayRegistry();
        var socket = await CreateConnectedSocketAsync();

        Assert.True(registry.TryRegister("ctrl-1", socket));
        Assert.True(registry.OwnsSession("ctrl-1"));
    }

    [Fact]
    public void Unknown_session_is_not_owned()
    {
        var registry = new GatewayRegistry();

        Assert.False(registry.OwnsSession("nonexistent"));
    }

    [Fact]
    public async Task Removed_session_is_not_owned()
    {
        var registry = new GatewayRegistry();
        var socket = await CreateConnectedSocketAsync();
        registry.TryRegister("ctrl-1", socket);

        var removed = registry.TryRemove("ctrl-1");

        Assert.NotNull(removed);
        Assert.False(registry.OwnsSession("ctrl-1"));
    }

    [Fact]
    public async Task Duplicate_register_returns_false()
    {
        var registry = new GatewayRegistry();
        var socket1 = await CreateConnectedSocketAsync();
        var socket2 = await CreateConnectedSocketAsync();

        Assert.True(registry.TryRegister("ctrl-1", socket1));
        Assert.False(registry.TryRegister("ctrl-1", socket2));
    }

    [Fact]
    public void TryRemove_unknown_returns_null()
    {
        var registry = new GatewayRegistry();

        Assert.Null(registry.TryRemove("nonexistent"));
    }

    [Fact]
    public async Task Closed_socket_is_not_owned()
    {
        var registry = new GatewayRegistry();
        var socket = await CreateConnectedSocketAsync();
        registry.TryRegister("ctrl-1", socket);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        Assert.False(registry.OwnsSession("ctrl-1"));
    }

    [Fact]
    public async Task Count_tracks_registered_sessions()
    {
        var registry = new GatewayRegistry();
        var socket1 = await CreateConnectedSocketAsync();
        var socket2 = await CreateConnectedSocketAsync();

        registry.TryRegister("ctrl-1", socket1);
        registry.TryRegister("ctrl-2", socket2);

        Assert.Equal(2, registry.Count);

        registry.TryRemove("ctrl-1");
        Assert.Equal(1, registry.Count);
    }
}
