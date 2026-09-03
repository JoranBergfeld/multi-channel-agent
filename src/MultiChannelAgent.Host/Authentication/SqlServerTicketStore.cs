using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Authentication;

namespace MultiChannelAgent.Host.Authentication;

/// <summary>
/// Adapts the SQL-backed <see cref="IAuthTicketRepository"/> to ASP.NET Core cookie authentication's
/// <see cref="ITicketStore"/> contract, making authentication tokens server-side: the "mca_auth"
/// browser cookie only ever carries this store's short opaque key, never the claims or any embedded
/// OIDC access/id/refresh token that <c>SaveTokens=true</c> places on the ticket. Registered once as a
/// Singleton (required by <c>CookieAuthenticationOptions.SessionStore</c>), it never resolves the
/// Scoped <see cref="IAuthTicketRepository"/> directly - each operation creates its own short-lived DI
/// scope instead, so this Singleton never captures a Scoped/DbContext-backed dependency for its own
/// lifetime. The serialized ticket is protected at rest with ASP.NET Core Data Protection before it
/// ever reaches the database and is only ever unprotected in memory, on read.
/// </summary>
public sealed class SqlServerTicketStore(IServiceScopeFactory scopeFactory, IDataProtectionProvider dataProtectionProvider) : ITicketStore
{
    private const string DataProtectionPurpose = "MultiChannelAgent.Authentication.SqlServerTicketStore.v1";

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);

    public async Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = Guid.NewGuid().ToString("N");
        await SaveAsync(key, ticket);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => SaveAsync(key, ticket);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthTicketRepository>();
        var protectedPayload = await repository.FindAsync(key, CancellationToken.None);
        if (protectedPayload is null)
        {
            return null;
        }

        try
        {
            var unprotected = _protector.Unprotect(protectedPayload);
            return TicketSerializer.Default.Deserialize(unprotected);
        }
        catch (CryptographicException)
        {
            // Undecryptable/tampered payload (e.g. a stale row from a rotated Data Protection key
            // ring): never treat it as a valid session, and remove it so it cannot be retried
            // indefinitely on every subsequent request with this key.
            await repository.DeleteAsync(key, CancellationToken.None);
            return null;
        }
    }

    public async Task RemoveAsync(string key)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthTicketRepository>();
        await repository.DeleteAsync(key, CancellationToken.None);
    }

    private async Task SaveAsync(string key, AuthenticationTicket ticket)
    {
        var serialized = TicketSerializer.Default.Serialize(ticket)
            ?? throw new InvalidOperationException("TicketSerializer.Default.Serialize returned null for a non-null AuthenticationTicket.");
        var protectedPayload = _protector.Protect(serialized);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthTicketRepository>();
        await repository.SaveAsync(key, protectedPayload, ticket.Properties.ExpiresUtc, CancellationToken.None);
    }
}
