using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MultiChannelAgent.Host.Authentication;

/// <summary>
/// A minimal, deterministic stand-in for the "Entra" challenge scheme used only when
/// <c>Authentication:Provider=Test</c>. Real sign-in for tests happens through the dedicated
/// <c>/api/test/sign-in</c> endpoint (which signs directly into the Cookie scheme), so this handler
/// never needs to authenticate a request; it exists only so "/auth/sign-in" has a scheme to name, and
/// its default <see cref="AuthenticationHandler{TOptions}"/> challenge behavior (a plain 401) is
/// exactly what a scripted test client expects instead of a live IdP redirect.
/// </summary>
public sealed class TestChallengeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
