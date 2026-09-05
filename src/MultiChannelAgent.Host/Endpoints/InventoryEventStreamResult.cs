using System.Text.Json;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>One Inventory's identity and current version, as this stream reports it.</summary>
public sealed record InventoryVersionWire(Guid InventoryId, long Version);

/// <summary>The complete current picture, sent the moment a connection opens.</summary>
public sealed record InventorySnapshotWire(IReadOnlyList<InventoryVersionWire> Inventories);

/// <summary>An Inventory this Participant may no longer see, so its projection must stop being shown.</summary>
public sealed record InventoryRevokedWire(Guid InventoryId);

/// <summary>
/// One Participant's Inventory invalidation stream.
///
/// It opens with a complete snapshot of every Inventory the Participant may currently see and the
/// version each is at, then reports only differences for as long as the connection lasts.
///
/// It issues no event identities, and that is a claim about what the events carry rather than a
/// convenience. What a client needs from this stream is a function of current state - the version each
/// authorized Inventory is at right now - not of the event history. A `changed` event says nothing the
/// next snapshot does not say, and a `revoked` event says nothing the next snapshot's absence does not
/// say, so a missed event is a fact learned one snapshot later rather than a fact lost. A
/// `Last-Event-ID` could therefore not improve on reconnecting, while advertising one would promise
/// cursor semantics this handler does not implement and a client would be resuming from a position the
/// server ignores. <c>InventoryEventStreamHttpTests</c> proves the consequence directly: a change made
/// while nothing was connected is in the next connection's snapshot.
///
/// It polls, in a fresh dependency scope each pass, for the same two structural reasons the per-Turn
/// stream does: this application runs as several replicas, so the replica that made a change is
/// routinely not the one holding this connection; and a connection that may last ten minutes must
/// never hold one database context open for its whole life.
///
/// The authorized set is re-read on every pass, not just at the start, so a Membership granted or
/// revoked while the tab is open is reported rather than discovered on the next page load.
/// </summary>
public sealed class InventoryEventStreamResult(ParticipantId participantId) : IResult
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var timeProvider = httpContext.RequestServices.GetRequiredService<TimeProvider>();
        var options = httpContext.RequestServices.GetRequiredService<InventoryStreamOptions>();
        var cancellationToken = httpContext.RequestAborted;

        ServerSentEvents.PrepareResponse(httpContext.Response);

        var deadline = timeProvider.GetUtcNow() + options.MaxDuration;
        var lastWrite = timeProvider.GetUtcNow();

        try
        {
            var known = await ReadAsync(scopeFactory, cancellationToken);

            await WriteAsync(
                httpContext.Response,
                "snapshot",
                new InventorySnapshotWire(
                    known.Select(version => new InventoryVersionWire(version.InventoryId, version.Version)).ToList()),
                cancellationToken);
            lastWrite = timeProvider.GetUtcNow();

            while (timeProvider.GetUtcNow() < deadline)
            {
                await Task.Delay(options.PollInterval, timeProvider, cancellationToken);

                var current = await ReadAsync(scopeFactory, cancellationToken);
                var seen = known.ToDictionary(version => version.InventoryId, version => version.Version);
                var stillAuthorized = current.Select(version => version.InventoryId).ToHashSet();
                var wrote = false;

                // Both loops walk the reader's own stable order, so what a client receives for one
                // pass is fully determined by the two states being compared rather than by the
                // enumeration order of a lookup built along the way.
                foreach (var version in current)
                {
                    if (seen.TryGetValue(version.InventoryId, out var previous) && previous == version.Version)
                    {
                        continue;
                    }

                    await WriteAsync(
                        httpContext.Response,
                        "changed",
                        new InventoryVersionWire(version.InventoryId, version.Version),
                        cancellationToken);
                    wrote = true;
                }

                foreach (var version in known.Where(version => !stillAuthorized.Contains(version.InventoryId)))
                {
                    await WriteAsync(
                        httpContext.Response,
                        "revoked",
                        new InventoryRevokedWire(version.InventoryId),
                        cancellationToken);
                    wrote = true;
                }

                known = current;

                if (wrote)
                {
                    lastWrite = timeProvider.GetUtcNow();
                }
                else if (timeProvider.GetUtcNow() - lastWrite >= options.HeartbeatInterval)
                {
                    await ServerSentEvents.WriteHeartbeatAsync(httpContext.Response, cancellationToken);
                    lastWrite = timeProvider.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The tab closed or the connection dropped - the only thing that cancels this request.
            // This endpoint only ever reads, so there is nothing to undo, and the next connection
            // begins with a complete snapshot anyway.
        }
    }

    private static Task WriteAsync<T>(HttpResponse response, string name, T body, CancellationToken cancellationToken) =>
        ServerSentEvents.WriteEventAsync(
            response, id: null, name, JsonSerializer.Serialize(body, SerializerOptions), cancellationToken);

    private async Task<IReadOnlyList<AuthorizedInventoryVersion>> ReadAsync(
        IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<InventoryInvalidationReader>();

        return await reader.ReadAsync(participantId, cancellationToken);
    }
}
