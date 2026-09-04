using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// One Turn's finite, resumable event stream.
///
/// It is finite in two ways. It ends as soon as the terminal Outcome has been written, because
/// nothing can follow it. And it ends anyway after <see cref="TurnStreamOptions.MaxDuration"/>,
/// because an interactive wait has to be bounded: a client whose Turn is still running simply
/// reconnects with the identity of the last event it received and carries on exactly where it left
/// off.
///
/// It polls, in a fresh dependency scope each pass, rather than waiting on an in-process signal. Two
/// reasons, both structural: this application runs as several replicas, so the replica processing a
/// Turn is routinely not the one holding its stream; and a five-minute request must never hold one
/// database context open for its whole life.
///
/// It is a read and nothing else. A disconnect therefore cancels it and undoes nothing, which is
/// precisely what lets a Participant reconnect to mutation-capable work without ever resubmitting it.
/// </summary>
public sealed class TurnEventStreamResult(
    TurnId turnId, ParticipantId participantId, long resumePoint, TurnEventPage firstPage) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
        var options = httpContext.RequestServices.GetRequiredService<TurnStreamOptions>();
        var cancellationToken = httpContext.RequestAborted;

        ServerSentEvents.PrepareResponse(httpContext.Response);

        var sent = resumePoint;
        var deadline = timeProvider.GetUtcNow() + options.MaxDuration;
        var lastWrite = timeProvider.GetUtcNow();
        var page = firstPage;

        try
        {
            while (true)
            {
                foreach (var streamEvent in page.Events)
                {
                    await ServerSentEvents.WriteEventAsync(
                        httpContext.Response, streamEvent.Sequence, streamEvent.Name, streamEvent.Data, cancellationToken);
                    sent = streamEvent.Sequence;
                    lastWrite = timeProvider.GetUtcNow();
                }

                if (page.ReachedTerminal || timeProvider.GetUtcNow() >= deadline)
                {
                    return;
                }

                if (timeProvider.GetUtcNow() - lastWrite >= options.HeartbeatInterval)
                {
                    await ServerSentEvents.WriteHeartbeatAsync(httpContext.Response, cancellationToken);
                    lastWrite = timeProvider.GetUtcNow();
                }

                await Task.Delay(options.PollInterval, timeProvider, cancellationToken);

                using var scope = scopeFactory.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<TurnEventReader>();

                // A Turn cannot stop existing or change owner, so a null here is unreachable; treating
                // it as "finished" simply ends the stream rather than looping forever if it ever did.
                page = await reader.ReadAfterAsync(turnId, participantId, sent, cancellationToken)
                    ?? new TurnEventPage([], ReachedTerminal: true);
            }
        }
        catch (OperationCanceledException)
        {
            // The browser navigated away, refreshed, or lost its connection. There is nothing to undo:
            // this endpoint only ever reads, so the Turn is exactly where it was and the Participant
            // can pick it back up by reconnecting.
        }
    }
}
