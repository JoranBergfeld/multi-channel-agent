using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MultiChannelAgent.Host.Authentication;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted by the deterministic test-only sign-in endpoint.</summary>
public sealed record TestSignInHttpRequest(string ParticipantId, string DisplayName, bool ActiveTenantMember = true);

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
        endpoints.MapPost("/api/test/sign-in", async (HttpContext httpContext, TestSignInHttpRequest request) =>
        {
            var claims = new List<Claim>
            {
                new(ParticipantClaims.ParticipantId, request.ParticipantId),
                new(ParticipantClaims.DisplayName, request.DisplayName),
                new(ParticipantClaims.ActiveTenantMember, request.ActiveTenantMember ? "true" : "false"),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Ok();
        });

        return endpoints;
    }
}
