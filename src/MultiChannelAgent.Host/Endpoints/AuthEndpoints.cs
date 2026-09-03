using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>Maps the production sign-in/sign-out routes shared by every authentication provider.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints, string challengeScheme)
    {
        endpoints.MapGet("/auth/sign-in", (string? returnUrl) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl },
                [challengeScheme]));

        endpoints.MapPost("/auth/sign-out", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return challengeScheme == ProviderSchemes.Entra
                ? Results.SignOut(authenticationSchemes: [challengeScheme])
                : Results.Ok();
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}
