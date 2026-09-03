using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Docker-free coverage (real relational engine, real HTTP boundary) that every processed Turn of a
/// signed-in web conversation is bound to one durable Foundry conversation generation - the Turn the
/// model answers directly just as much as the one that reaches a tool - and that the binding is
/// stable for that Participant's ChannelConversation rather than minted per Turn.
/// </summary>
public sealed class FoundryConversationBindingScenarioTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Every_processed_turn_shares_one_durable_foundry_conversation_for_its_channel_conversation()
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(httpClient, "Bound Participant");
        await participant.CreateAndSelectInventoryAsync("Bound Warehouse");

        // One Turn the model answers directly (an echo, no tool call) and one that reaches a tool.
        var directTurn = await participant.SubmitAcceptedTurnAsync("native-binding-1", "hello");
        var toolTurn = await participant.SubmitAcceptedTurnAsync("native-binding-2", "list stock");

        await ProcessUntilQuietAsync();

        Assert.Equal("completed", (await participant.GetOutcomeAsync(directTurn))!.Value.GetProperty("status").GetString());
        Assert.Equal("completed", (await participant.GetOutcomeAsync(toolTurn))!.Value.GetProperty("status").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var bindings = await db.FoundryConversationBindings.AsNoTracking().ToListAsync();

        var binding = Assert.Single(bindings);
        Assert.NotEqual(Guid.Empty, binding.FoundryConversationId);
        Assert.Equal(1, binding.Generation);
    }

    // A second signed-in browser profile is its own ChannelConversation, so it never continues
    // someone else's Foundry conversation.
    [Fact]
    public async Task A_separate_channel_conversation_gets_its_own_foundry_conversation()
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var first = await ConversationTestClient.SignInAsync(httpClient, "First Participant");
        var second = await ConversationTestClient.SignInAsync(httpClient, "Second Participant");
        await first.CreateAndSelectInventoryAsync("First Warehouse");
        await second.CreateAndSelectInventoryAsync("Second Warehouse");

        await first.SubmitAcceptedTurnAsync("native-binding-a", "hello");
        await second.SubmitAcceptedTurnAsync("native-binding-b", "hello");

        await ProcessUntilQuietAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var bindings = await db.FoundryConversationBindings.AsNoTracking().ToListAsync();

        Assert.Equal(2, bindings.Count);
        Assert.Equal(2, bindings.Select(b => b.FoundryConversationId).Distinct().Count());
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
