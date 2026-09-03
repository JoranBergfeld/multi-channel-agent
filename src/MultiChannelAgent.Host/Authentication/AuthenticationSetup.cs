using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MultiChannelAgent.Host.Authentication;

/// <summary>The scheme name used to challenge the configured external identity provider (Entra or its test double).</summary>
public static class ProviderSchemes
{
    public const string Entra = "Entra";
    public const string Test = "Test";
}

/// <summary>
/// Wires the single-tenant Entra session contract: a Secure HttpOnly SameSite session cookie is
/// always the default authenticate/sign-in scheme, and unauthenticated `/api` requests get a plain
/// 401/403 instead of a login-page redirect. Exactly one external challenge provider is selected from
/// configuration - the real Entra OIDC flow, or (only outside Production) a deterministic test double
/// - and a misconfigured or invalid selection fails fast at startup rather than silently falling back.
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddMultiChannelAgentAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var provider = configuration["Authentication:Provider"];
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException(
                "Missing required 'Authentication:Provider' configuration value. Expected 'Entra' or 'Test'.");
        }

        var authenticationBuilder = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "mca_auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = "/auth/sign-in";
                options.AccessDeniedPath = "/auth/sign-in";

                // API callers must never be redirected to an HTML login page: return a plain,
                // non-disclosing status code instead so the BFF contract stays a clean JSON API.
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        if (string.Equals(provider, ProviderSchemes.Test, StringComparison.OrdinalIgnoreCase))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Authentication:Provider=Test is never allowed when the environment is Production.");
            }

            authenticationBuilder.AddScheme<AuthenticationSchemeOptions, TestChallengeAuthenticationHandler>(
                ProviderSchemes.Test, _ => { });

            return services;
        }

        if (string.Equals(provider, ProviderSchemes.Entra, StringComparison.OrdinalIgnoreCase))
        {
            var tenantId = configuration["Authentication:Entra:TenantId"];
            var clientId = configuration["Authentication:Entra:ClientId"];
            var clientSecret = configuration["Authentication:Entra:ClientSecret"];

            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Authentication:Provider=Entra requires 'Authentication:Entra:TenantId', 'ClientId', and " +
                    "'ClientSecret' to all be configured. Refusing to start with an incomplete production identity provider.");
            }

            authenticationBuilder.AddOpenIdConnect(ProviderSchemes.Entra, options =>
            {
                options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";

                // Tokens are only ever persisted inside this encrypted, HttpOnly, Secure cookie
                // ticket - never sent to browser JavaScript, and never handled by any other seam.
                options.SaveTokens = true;

                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");

                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = ParticipantClaims.DisplayName;

                options.Events.OnTokenValidated = context =>
                {
                    var identity = (ClaimsIdentity)context.Principal!.Identity!;

                    // Fail closed: only explicit, trustworthy evidence (see
                    // EntraTenantMembershipEvidence) grants active-tenant-member status. A missing or
                    // conflicting claim still lets the caller authenticate (sign-in succeeds), but the
                    // ActiveTenantMember authorization policy then refuses with a generic,
                    // non-disclosing response instead of ever defaulting to "member".
                    var isActiveTenantMember = EntraTenantMembershipEvidence.IsActiveTenantMember(context.Principal, tenantId);

                    identity.AddClaim(new Claim(
                        ParticipantClaims.ActiveTenantMember,
                        isActiveTenantMember ? "true" : "false"));

                    var objectId = context.Principal.FindFirst("oid")?.Value;
                    if (!string.IsNullOrWhiteSpace(objectId) && identity.FindFirst(ParticipantClaims.ParticipantId) is null)
                    {
                        identity.AddClaim(new Claim(ParticipantClaims.ParticipantId, objectId));
                    }

                    return Task.CompletedTask;
                };
            });

            return services;
        }

        throw new InvalidOperationException(
            $"Unknown Authentication:Provider '{provider}'. Expected 'Entra' or 'Test'.");
    }
}
