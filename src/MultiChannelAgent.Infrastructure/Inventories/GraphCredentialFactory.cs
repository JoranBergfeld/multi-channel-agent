using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// Builds the Azure.Core <see cref="TokenCredential"/> <see cref="GraphTenantMemberDirectory"/> uses
/// to acquire app-only <c>https://graph.microsoft.com/.default</c> tokens. Construction is
/// synchronous and never performs network I/O - actual token acquisition only happens the first time
/// a caller resolves a tenant member, never at startup or for <c>/health/live</c> - but configuration
/// is still validated eagerly here so a misconfigured deployment fails with one clear error message
/// instead of a confusing downstream Graph 401 the first time recovery/membership governance is used.
/// Reuses the same "Authentication:Entra:TenantId"/"ClientId"/"ClientSecret" configuration the Host's
/// authentication setup already requires for interactive sign-in when "Authentication:Provider=Entra",
/// so no additional configuration is normally required; opting into
/// "Authentication:Entra:UseManagedIdentityForGraph=true" instead uses
/// <see cref="DefaultAzureCredential"/> (managed identity when deployed to Azure, falling back
/// through the other locally-available credential sources otherwise) and does not require a client
/// secret at all.
/// </summary>
public static class GraphCredentialFactory
{
    public static TokenCredential Create(IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("Authentication:Entra:UseManagedIdentityForGraph"))
        {
            return new DefaultAzureCredential();
        }

        var tenantId = configuration["Authentication:Entra:TenantId"];
        var clientId = configuration["Authentication:Entra:ClientId"];
        var clientSecret = configuration["Authentication:Entra:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "The Microsoft Graph tenant member directory requires 'Authentication:Entra:TenantId', 'ClientId', " +
                "and 'ClientSecret' to all be configured (or 'Authentication:Entra:UseManagedIdentityForGraph=true' " +
                "to use managed identity instead). Refusing to build an unusable production tenant member directory.");
        }

        return new ClientSecretCredential(tenantId, clientId, clientSecret);
    }
}
