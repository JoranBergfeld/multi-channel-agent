using System.Globalization;
using System.Text;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>One decoded Server-Sent Event: its issued identity (absent on a stream that issues none), its name, and its body.</summary>
public sealed record ServerSentEvent(long? Id, string Name, string Data);

/// <summary>
/// Decodes a live <c>text/event-stream</c> response the way a browser's EventSource does: field by
/// field, dispatching on the blank line that terminates a record, and skipping comment lines. Tests
/// need this because <c>HttpClient</c> gives them bytes, not events, and because asserting on raw
/// bytes would couple every test to the exact framing instead of to the events themselves.
///
/// It is deliberately <b>stateful</b>, and owns one reader over the response body for its whole life.
/// A stateless "read N events out of this response" helper cannot be written correctly:
/// <see cref="StreamReader"/> reads ahead into its own buffer, so bytes already pulled off the socket
/// but not yet returned are thrown away when it is disposed - and a second call on the same response
/// would either read from a body the first call had already disposed, or silently skip whatever the
/// first call had buffered. One reader, opened once, read repeatedly, disposed at the end, is the only
/// shape in which a test can read some events, do something to the system, and then read the rest.
///
/// Being stateful is not enough on its own, and the second half of the contract is what makes the
/// first half true: <b>every</b> byte is consumed through the one <c>PumpAsync</c> state machine, and
/// a completed event is <b>queued</b> rather than returned directly. Both consequences matter.
/// The half-parsed record left behind when a read stops mid-event survives into the next call,
/// because the fields it has already seen are instance state rather than locals. And an event that
/// arrives while a caller is waiting for a heartbeat is queued for the next <see cref="ReadAsync"/>
/// instead of being decoded and dropped - which is what a separate line-skipping heartbeat loop would
/// do, silently, in exactly the tests written to prove nothing is lost.
///
/// The caller keeps owning the <see cref="HttpResponseMessage"/>; this owns only what it created.
/// </summary>
public sealed class ServerSentEventReader : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;

    /// <summary>Events fully decoded but not yet handed to a caller - including any decoded while waiting for a heartbeat.</summary>
    private readonly Queue<ServerSentEvent> _decoded = new();

    // The record currently being decoded. Instance state, not locals, because one SSE record can
    // straddle two calls: a read that has satisfied its count mid-record, or a heartbeat wait that
    // stops between an event's "id:" line and its terminating blank line.
    private readonly StringBuilder _data = new();
    private long? _id;
    private string? _name;
    private bool _ended;

    private ServerSentEventReader(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, Encoding.UTF8);
    }

    /// <summary>How many comment lines - the keep-alive heartbeats - this reader has passed over so far.</summary>
    public int HeartbeatCount { get; private set; }

    /// <summary>Opens a reader over a live streaming response.</summary>
    public static async Task<ServerSentEventReader> OpenAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        return new ServerSentEventReader(await response.Content.ReadAsStreamAsync(cancellationToken));
    }

    /// <summary>
    /// Reads events until <paramref name="count"/> have arrived or the server ends the stream. Never
    /// blocks forever: <paramref name="cancellationToken"/> is the test's own timeout. A second call
    /// continues exactly where the previous one stopped, and anything decoded during a
    /// <see cref="WaitForHeartbeatsAsync"/> is returned here rather than lost.
    /// </summary>
    public async Task<IReadOnlyList<ServerSentEvent>> ReadAsync(int count, CancellationToken cancellationToken)
    {
        var events = new List<ServerSentEvent>();

        while (events.Count < count)
        {
            if (_decoded.Count > 0)
            {
                events.Add(_decoded.Dequeue());
                continue;
            }

            if (!await PumpAsync(cancellationToken))
            {
                break;
            }
        }

        return events;
    }

    /// <summary>
    /// Reads until at least <paramref name="count"/> comment lines have been passed over, or the
    /// stream ends. A comment carries no identity and no body, so it is invisible to
    /// <see cref="ReadAsync"/>; this is how a test asserts the keep-alive an ingress depends on is
    /// actually being written. It consumes the stream through the same state machine
    /// <see cref="ReadAsync"/> does, so any event that arrives while it is waiting is decoded and
    /// queued for the next <see cref="ReadAsync"/> rather than discarded.
    /// </summary>
    public async Task WaitForHeartbeatsAsync(int count, CancellationToken cancellationToken)
    {
        while (HeartbeatCount < count)
        {
            if (!await PumpAsync(cancellationToken))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Consumes exactly one line and applies it to the decode state, queueing an event when the line
    /// terminates one. Returns false once the server has ended the stream, and never reads past that.
    /// This is the single place bytes are interpreted, which is what stops the two public waits from
    /// disagreeing about what a byte meant.
    /// </summary>
    private async Task<bool> PumpAsync(CancellationToken cancellationToken)
    {
        if (_ended)
        {
            return false;
        }

        var line = await _reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            _ended = true;
            return false;
        }

        if (line.Length == 0)
        {
            if (_name is not null)
            {
                _decoded.Enqueue(new ServerSentEvent(_id, _name, _data.ToString()));
            }

            _id = null;
            _name = null;
            _data.Clear();
            return true;
        }

        if (line.StartsWith(':'))
        {
            HeartbeatCount++;
            return true;
        }

        var separator = line.IndexOf(':');
        var field = separator < 0 ? line : line[..separator];
        var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');

        switch (field)
        {
            case "id":
                _id = long.Parse(value, CultureInfo.InvariantCulture);
                break;
            case "event":
                _name = value;
                break;
            case "data":
                _data.Append(value);
                break;
            default:
                break;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync();
    }
}
