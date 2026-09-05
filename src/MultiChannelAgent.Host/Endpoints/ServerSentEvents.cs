using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// How the per-Turn stream paces itself. Registered as a singleton in <c>Program.cs</c> holding
/// exactly the production numbers below; nothing else in the application reads them.
///
/// It is a settable record rather than three constants for one reason, and it is a test reason worth
/// being honest about: the heartbeat is load-bearing (an ingress that sees no bytes closes the
/// connection), so it has to be asserted, and asserting it at fifteen real seconds would tax every CI
/// run forever. A fake clock cannot help - these values are consumed inside a live HTTP request that
/// a test is concurrently reading bytes from, so there is no safe moment for anyone to advance one.
/// </summary>
public sealed record TurnStreamOptions
{
    /// <summary>How often the stream looks for events it has not sent yet.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the stream may stay silent before it proves it is still alive.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The bounded interactive wait. After this the client reconnects and resumes.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>How the Participant-level invalidation stream paces itself. Same rationale as <see cref="TurnStreamOptions"/>.</summary>
public sealed record InventoryStreamOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The bounded life of one connection. A browser's EventSource reconnects by itself, and a
    /// reconnect costs one snapshot, so bounding this keeps a long-lived tab's connection fresh
    /// without the client having to do anything.
    /// </summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The Server-Sent Events wire format, in one place. Every stream this application serves frames its
/// events here, so the framing rules - and the one rule that matters, that a body is always exactly
/// one line - can never drift between streams.
/// </summary>
public static class ServerSentEvents
{
    public const string ContentType = "text/event-stream";

    /// <summary>The header a browser's EventSource sets by itself when it reconnects a stream it was already reading.</summary>
    public const string LastEventIdHeader = "Last-Event-ID";

    /// <summary>
    /// The query parameter a client uses to resume a stream it is opening fresh. A browser can only
    /// set the header on its <em>own</em> automatic reconnect; a page that reloaded and is
    /// reconnecting deliberately has no way to set a header at all, so it passes its resume point
    /// here instead.
    /// </summary>
    public const string LastEventIdQuery = "lastEventId";

    public static void PrepareResponse(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-cache, no-store";

        // Some reverse proxies buffer responses by default, which would hold every event until the
        // stream ended and defeat the entire point of streaming.
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    /// <summary>
    /// Writes one event and flushes it. <paramref name="data"/> must already be a single line; every
    /// caller in this application produces it with <c>JsonSerializer</c>, which escapes newlines
    /// inside strings and never pretty-prints, so this holds by construction.
    /// </summary>
    public static async Task WriteEventAsync(
        HttpResponse response, long? id, string name, string data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var frame = new StringBuilder();
        if (id is { } issued)
        {
            frame.Append("id: ").Append(issued.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        frame.Append("event: ").Append(name).Append('\n');
        frame.Append("data: ").Append(data).Append("\n\n");

        await response.WriteAsync(frame.ToString(), cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a comment line. Necessary rather than decorative: an ingress that sees no bytes for
    /// long enough closes the connection. A comment carries no identity, so it can never move a
    /// client's resume point.
    /// </summary>
    public static async Task WriteHeartbeatAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        await response.WriteAsync(": heartbeat\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// The resume point this request is asking for, or 0 for "from the beginning". Anything this
    /// application did not issue - unparseable, negative, or simply not one of its identities - is
    /// treated exactly as if none had been sent, and never as an error: a browser's EventSource
    /// cannot read an error body, so refusing would make it reconnect forever with the same bad
    /// value, and replaying a caller's own events discloses nothing they did not already have.
    /// </summary>
    public static long ReadResumePoint(HttpRequest request, Func<long, bool> isIssued)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(isIssued);

        var raw = request.Headers[LastEventIdHeader].FirstOrDefault()
            ?? request.Query[LastEventIdQuery].FirstOrDefault();

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && isIssued(value)
            ? value
            : 0L;
    }
}
