namespace MultiChannelAgent.Host.Security;

/// <summary>
/// Resolves the caller-supplied "returnUrl" query value on `/auth/sign-in` to a safe post-login
/// redirect target. <c>Results.Challenge</c> will happily send <see
/// cref="Microsoft.AspNetCore.Authentication.AuthenticationProperties.RedirectUri"/> to whatever URL
/// it is given once the challenge completes, so trusting an arbitrary caller-supplied value here is
/// an open-redirect vulnerability: an attacker crafts a sign-in link whose <c>returnUrl</c> points at
/// an attacker-controlled site, and a victim who legitimately signs in is then bounced there. Only a
/// same-origin absolute-path URL - one beginning with exactly one <c>/</c>, never <c>//</c> (a
/// protocol-relative URL) and never containing a backslash (which some browsers normalize into a
/// forward slash, turning <c>/\evil.example</c> into an effective protocol-relative URL) - is trusted;
/// anything else falls back to the safe default of <see cref="Default"/>.
/// </summary>
public static class LocalReturnUrl
{
    public const string Default = "/";

    public static string Resolve(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return Default;
        }

        if (returnUrl[0] != '/' || returnUrl.Contains('\\'))
        {
            return Default;
        }

        if (returnUrl.Length > 1 && returnUrl[1] == '/')
        {
            return Default;
        }

        return returnUrl;
    }
}
