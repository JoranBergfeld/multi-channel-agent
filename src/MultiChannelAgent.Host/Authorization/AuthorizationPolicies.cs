using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Host.Authentication;

namespace MultiChannelAgent.Host.Authorization;

public static class AuthorizationPolicies
{
    public const string ActiveTenantMember = "ActiveTenantMember";

    public const string InventoryRecoveryAdministrator = "InventoryRecoveryAdministrator";

    public static IServiceCollection AddMultiChannelAgentAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(ActiveTenantMember, policy => policy.RequireClaim(ParticipantClaims.ActiveTenantMember, "true"))
            .AddPolicy(InventoryRecoveryAdministrator, policy => policy.RequireClaim(
                ParticipantClaims.AppRole, ParticipantClaims.InventoryRecoveryAdministratorRoleValue));

        return services;
    }
}
