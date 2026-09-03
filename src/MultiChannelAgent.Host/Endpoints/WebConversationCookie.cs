namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// A stable web conversation identity per Participant/browser profile, established the first time
/// the authenticated session bootstraps and reused on every later request. This is deliberately just
/// a stable identifier - not a full conversation history seam - so a later ticket can build resumable
/// web conversation continuity on top of it without this ticket needing to implement that history.
/// </summary>
public static class WebConversationCookie
{
    public const string Name = "mca_web_conversation";

    public static string EnsureId(HttpContext httpContext)
    {
        if (httpContext.Request.Cookies.TryGetValue(Name, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var id = Guid.NewGuid().ToString();
        httpContext.Response.Cookies.Append(Name, id, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(400),
        });

        return id;
    }
}
