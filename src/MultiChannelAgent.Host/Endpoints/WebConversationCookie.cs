using System.Diagnostics.CodeAnalysis;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// A stable web conversation identity per Participant/browser profile, established the first time
/// the authenticated session bootstraps and reused on every later request. This is deliberately just
/// a stable identifier - not a full conversation history seam - so a later ticket can build resumable
/// web conversation continuity on top of it without this ticket needing to implement that history.
///
/// It is also the one piece of a Turn's trusted context that travels through the client, so what
/// comes back is only ever accepted when it is an identifier this application itself issued. Anything
/// else - tampered, corrupted, or absurdly long - is a value no conversation was ever created for, so
/// it is treated exactly as if no cookie had been sent: a fresh conversation is issued and the
/// request carries on, with no distinct response telling a caller their value was rejected.
/// </summary>
public static class WebConversationCookie
{
    public const string Name = "mca_web_conversation";

    public static string EnsureId(HttpContext httpContext)
    {
        if (httpContext.Request.Cookies.TryGetValue(Name, out var existing) && IsIssuedIdentifier(existing))
        {
            return existing;
        }

        var id = NewId();
        httpContext.Response.Cookies.Append(Name, id, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(400),
        });

        return id;
    }

    /// <summary>
    /// Exactly the shape <see cref="NewId"/> mints, checked as a whole rather than by length alone:
    /// an opaque identifier that parses back to the GUID it was issued as. That keeps every value the
    /// application ever carries into a Turn well within what a durable ChannelConversation identifier
    /// accepts, so a hostile cookie can never become an unhandled failure deeper in the workflow.
    /// </summary>
    private static bool IsIssuedIdentifier([NotNullWhen(true)] string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out _);

    private static string NewId() => Guid.NewGuid().ToString("D");
}
