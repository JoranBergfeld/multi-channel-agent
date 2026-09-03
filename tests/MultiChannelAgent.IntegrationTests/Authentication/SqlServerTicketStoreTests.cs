using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Authentication;
using MultiChannelAgent.Host.Authentication;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// Unit coverage for the <see cref="ITicketStore"/> adapter behind server-side authentication
/// tickets: store returns a short opaque key, retrieve/renew/remove round-trip correctly through a
/// real per-operation DI scope (guarding against the singleton/scoped lifetime bug this store must
/// avoid), and the persisted payload is genuinely protected ciphertext rather than a readable
/// serialized ticket.
/// </summary>
public sealed class SqlServerTicketStoreTests
{
    private static (ITicketStore Store, FakeAuthTicketRepository Repository) CreateStore()
    {
        var repository = new FakeAuthTicketRepository();
        var services = new ServiceCollection();
        // Registered Scoped (not Singleton) to mirror the production DbContext-backed repository's
        // real lifetime; SqlServerTicketStore must create its own scope per operation to resolve it
        // safely from what is itself a Singleton store.
        services.AddSingleton<IAuthTicketRepository>(repository);
        services.AddDataProtection();
        var provider = services.BuildServiceProvider(validateScopes: true);

        var store = new SqlServerTicketStore(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDataProtectionProvider>());

        return (store, repository);
    }

    private static AuthenticationTicket CreateTicket(string participantMarker, DateTimeOffset? expiresUtc = null)
    {
        var identity = new ClaimsIdentity([new Claim("sub", participantMarker)], CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties { ExpiresUtc = expiresUtc };
        return new AuthenticationTicket(new ClaimsPrincipal(identity), properties, CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task Storing_a_ticket_returns_a_short_opaque_key_that_retrieves_the_same_ticket_back()
    {
        var (store, _) = CreateStore();
        var ticket = CreateTicket("participant-1");

        var key = await store.StoreAsync(ticket);

        Assert.True(key.Length < 64, $"Expected a short opaque key; got {key.Length} characters.");

        var retrieved = await store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.Equal("participant-1", retrieved!.Principal.FindFirst("sub")!.Value);
    }

    [Fact]
    public async Task Retrieving_an_unknown_key_returns_null()
    {
        var (store, _) = CreateStore();

        Assert.Null(await store.RetrieveAsync("unknown-key"));
    }

    [Fact]
    public async Task Renewing_replaces_the_stored_ticket_for_the_same_key()
    {
        var (store, _) = CreateStore();
        var key = await store.StoreAsync(CreateTicket("participant-1"));

        await store.RenewAsync(key, CreateTicket("participant-2"));

        var retrieved = await store.RetrieveAsync(key);
        Assert.Equal("participant-2", retrieved!.Principal.FindFirst("sub")!.Value);
    }

    [Fact]
    public async Task Removing_a_key_makes_it_permanently_unretrievable()
    {
        var (store, _) = CreateStore();
        var key = await store.StoreAsync(CreateTicket("participant-1"));

        await store.RemoveAsync(key);

        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task The_persisted_payload_is_protected_ciphertext_not_a_readable_serialized_ticket()
    {
        var (store, repository) = CreateStore();

        var key = await store.StoreAsync(CreateTicket("secret-participant-marker"));

        var persisted = repository.Payloads[key];
        var persistedText = System.Text.Encoding.UTF8.GetString(persisted);
        Assert.DoesNotContain("secret-participant-marker", persistedText, StringComparison.Ordinal);
    }

    private sealed class FakeAuthTicketRepository : IAuthTicketRepository
    {
        public Dictionary<string, byte[]> Payloads { get; } = [];

        public Task SaveAsync(string key, byte[] protectedTicket, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken)
        {
            Payloads[key] = protectedTicket;
            return Task.CompletedTask;
        }

        public Task<byte[]?> FindAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Payloads.TryGetValue(key, out var value) ? value : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Payloads.Remove(key);
            return Task.CompletedTask;
        }
    }
}
