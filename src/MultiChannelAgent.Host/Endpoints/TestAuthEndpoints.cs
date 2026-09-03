using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted by the deterministic test-only sign-in endpoint.</summary>
public sealed record TestSignInHttpRequest(
    string ParticipantId,
    string DisplayName,
    bool ActiveTenantMember = true,
    bool IsInventoryRecoveryAdministrator = false,
    int? SimulatedAccessTokenSizeBytes = null);

/// <summary>The wire shape accepted by the deterministic test-only tenant directory registration endpoint.</summary>
public sealed record RegisterTenantMemberHttpRequest(string ParticipantId, string DisplayName, string? Address = null);

/// <summary>
/// The deterministic stand-in for the real Entra authorization-code flow: signs a caller-chosen
/// claims principal directly into the real Cookie authentication scheme, so every downstream
/// behavior (session cookie flags, CSRF, authorization policy, non-disclosing refusal) is exercised
/// through the exact same production code path production sign-in eventually reaches, without a live
/// Microsoft Entra tenant. Only ever mapped when <c>Authentication:Provider=Test</c>, which itself is
/// refused outside Production by <see cref="AuthenticationSetup"/> - so this can never reach a real
/// deployment.
/// </summary>
public static class TestAuthEndpoints
{
    public static IEndpointRouteBuilder MapTestAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/test/sign-in", async (HttpContext httpContext, TestSignInHttpRequest request, ITenantMemberDirectory directory) =>
        {
            var claims = new List<Claim>
            {
                new(ParticipantClaims.ParticipantId, request.ParticipantId),
                new(ParticipantClaims.DisplayName, request.DisplayName),
                new(ParticipantClaims.ActiveTenantMember, request.ActiveTenantMember ? "true" : "false"),
            };

            if (request.IsInventoryRecoveryAdministrator)
            {
                claims.Add(new Claim(ParticipantClaims.AppRole, ParticipantClaims.InventoryRecoveryAdministratorRoleValue));
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            // Only ever populated by tests that need to reproduce the real Entra OIDC path's
            // SaveTokens=true behavior (an access/id/refresh token embedded on the ticket's
            // AuthenticationProperties) without a live token, so cookie-size-sensitive behavior (the
            // server-side ticket store) can be exercised deterministically.
            var properties = new AuthenticationProperties();
            if (request.SimulatedAccessTokenSizeBytes is > 0 and var size)
            {
                properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = new string('a', size) }]);
            }

            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);

            // A signed-in identity is, by construction, an identity the tenant directory can
            // resolve - auto-registering it here means a test can grant/transfer to "an existing app
            // user" purely by their known Participant id, without a separate directory setup step.
            if (request.ActiveTenantMember && directory is TestTenantMemberDirectory testDirectory
                && Guid.TryParse(request.ParticipantId, out var participantGuid))
            {
                testDirectory.Register(new ResolvedTenantMember(new ParticipantId(participantGuid), request.DisplayName));
            }

            return Results.Ok();
        });

        // Registers an identity the tenant directory can resolve without that identity ever signing
        // in itself - needed to test granting/transferring to someone brand new to the application.
        endpoints.MapPost("/api/test/tenant-directory/register", (RegisterTenantMemberHttpRequest request, ITenantMemberDirectory directory) =>
        {
            if (directory is not TestTenantMemberDirectory testDirectory)
            {
                return Results.Problem("The deterministic test tenant directory is not active.", statusCode: StatusCodes.Status500InternalServerError);
            }

            if (!Guid.TryParse(request.ParticipantId, out var participantGuid))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["participantId"] = ["participantId must be a GUID."] });
            }

            var member = new ResolvedTenantMember(new ParticipantId(participantGuid), request.DisplayName);
            testDirectory.Register(member);
            if (!string.IsNullOrWhiteSpace(request.Address))
            {
                testDirectory.Register(request.Address, member);
            }

            return Results.Ok();
        });

        // Simulates a tenant member who has since left/been disabled - the deterministic trigger for
        // orphan-recovery tests, without a real Microsoft Graph call.
        endpoints.MapPost("/api/test/tenant-directory/unregister", (RegisterTenantMemberHttpRequest request, ITenantMemberDirectory directory) =>
        {
            if (directory is not TestTenantMemberDirectory testDirectory)
            {
                return Results.Problem("The deterministic test tenant directory is not active.", statusCode: StatusCodes.Status500InternalServerError);
            }

            if (!Guid.TryParse(request.ParticipantId, out var participantGuid))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["participantId"] = ["participantId must be a GUID."] });
            }

            testDirectory.Unregister(participantGuid);
            return Results.Ok();
        });

        return endpoints;
    }
}

