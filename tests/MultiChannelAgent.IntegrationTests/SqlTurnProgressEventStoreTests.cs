using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

public sealed class SqlTurnProgressEventStoreTests : IDisposable
{
    private sealed class FailFirstProgressSaveInterceptor : SaveChangesInterceptor
    {
        public const string Marker = "provoked-progress-insert-failure";

        private bool _failed;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_failed
                || eventData.Context is not { } db
                || !db.ChangeTracker.Entries<TurnProgressEventEntity>()
                    .Any(entry => entry.State == EntityState.Added))
            {
                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }

            _failed = true;
            throw new InvalidOperationException(Marker);
        }
    }

    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlTurnProgressEventStoreTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        var now = DateTimeOffset.UtcNow;
        db.Participants.Add(new ParticipantEntity
        {
            Id = SomeParticipant.Value,
            DisplayName = "Some Participant",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Append_and_read_preserve_the_issued_identity_kind_and_times()
    {
        var turnId = SeedTurn("round-trip");
        var occurredAt = new DateTimeOffset(2026, 9, 5, 10, 11, 12, TimeSpan.Zero).AddTicks(3456);
        var marker = Marker(turnId, 41, TurnEventKind.Part, occurredAt, occurredAt.AddMinutes(17));

        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);

        Assert.True(await store.AppendAsync(marker, CancellationToken.None));

        var saved = Assert.Single(await store.ReadAsync(turnId, CancellationToken.None));
        Assert.Equal(marker.TurnId, saved.TurnId);
        Assert.Equal(marker.Sequence, saved.Sequence);
        Assert.Equal(marker.Kind, saved.Kind);
        Assert.Equal(marker.OccurredAt, saved.OccurredAt);
        Assert.Equal(marker.ExpiresAt, saved.ExpiresAt);
    }

    [Fact]
    public async Task Duplicate_issued_identity_returns_false_and_preserves_the_first_marker()
    {
        var turnId = SeedTurn("duplicate");
        var firstTime = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var first = Marker(turnId, 42, TurnEventKind.Processing, firstTime, firstTime.AddMinutes(10));
        var duplicate = Marker(turnId, 42, TurnEventKind.Part, firstTime.AddMinutes(1), firstTime.AddMinutes(20));

        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);

        Assert.True(await store.AppendAsync(first, CancellationToken.None));
        Assert.False(await store.AppendAsync(duplicate, CancellationToken.None));

        Assert.Equal(first, Assert.Single(await store.ReadAsync(turnId, CancellationToken.None)));
    }

    [Fact]
    public async Task A_second_independent_writer_for_the_same_identity_yields_one_success_and_one_row()
    {
        var turnId = SeedTurn("independent-writers");
        var now = new DateTimeOffset(2026, 9, 5, 11, 0, 0, TimeSpan.Zero);
        var marker = Marker(turnId, 43, TurnEventKind.Processing, now, now.AddMinutes(10));

        using var firstDb = CreateContext();
        using var secondDb = CreateContext();
        var firstWriter = new SqlTurnProgressEventStore(firstDb);
        var secondWriter = new SqlTurnProgressEventStore(secondDb);

        var results = await Task.WhenAll(
            firstWriter.AppendAsync(marker, CancellationToken.None),
            secondWriter.AppendAsync(marker with { OccurredAt = now.AddSeconds(1) }, CancellationToken.None));

        Assert.Equal([false, true], results.OrderBy(result => result));

        using var verifyDb = CreateContext();
        var verifyStore = new SqlTurnProgressEventStore(verifyDb);
        Assert.Single(await verifyStore.ReadAsync(turnId, CancellationToken.None));
    }

    [Fact]
    public async Task A_non_DbUpdate_append_failure_does_not_leak_into_a_later_append_on_the_same_context()
    {
        var turnId = SeedTurn("failed-write-isolation");
        var now = new DateTimeOffset(2026, 9, 5, 11, 30, 0, TimeSpan.Zero);
        var failed = Marker(turnId, 44, TurnEventKind.Processing, now, now.AddMinutes(10));
        var later = Marker(turnId, 45, TurnEventKind.Part, now.AddSeconds(1), now.AddMinutes(10));

        using var db = CreateContext(new FailFirstProgressSaveInterceptor());
        var store = new SqlTurnProgressEventStore(db);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AppendAsync(failed, CancellationToken.None));
        Assert.Equal(FailFirstProgressSaveInterceptor.Marker, exception.Message);

        Assert.True(await store.AppendAsync(later, CancellationToken.None));
        Assert.Equal([later], await store.ReadAsync(turnId, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_expired_removes_only_expired_markers_and_keeps_the_inbox_turn()
    {
        var turnId = SeedTurn("retention");
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);
        await store.AppendAsync(Marker(turnId, 51, TurnEventKind.Processing, now.AddMinutes(-2), now), CancellationToken.None);
        var retained = Marker(turnId, 52, TurnEventKind.Part, now.AddMinutes(-1), now.AddTicks(1));
        await store.AppendAsync(retained, CancellationToken.None);

        Assert.Equal(1, await store.DeleteExpiredAsync(now, 10, CancellationToken.None));
        Assert.Equal(retained, Assert.Single(await store.ReadAsync(turnId, CancellationToken.None)));
        Assert.True(await db.InboxEntries.AsNoTracking().AnyAsync(entry => entry.TurnId == turnId.Value));
        Assert.True(await db.InboxContentParts.AsNoTracking().AnyAsync(part => part.TurnId == turnId.Value));
    }

    [Fact]
    public async Task Delete_expired_honors_the_batch_limit_and_leaves_the_remaining_three()
    {
        var turnId = SeedTurn("bounded-retention");
        var now = new DateTimeOffset(2026, 9, 5, 13, 0, 0, TimeSpan.Zero);

        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);
        for (var index = 0; index < 5; index++)
        {
            await store.AppendAsync(
                Marker(turnId, 60 + index, TurnEventKind.Part, now.AddMinutes(-10 + index), now.AddMinutes(-5 + index)),
                CancellationToken.None);
        }

        Assert.Equal(2, await store.DeleteExpiredAsync(now, 2, CancellationToken.None));
        Assert.Equal(3, (await store.ReadAsync(turnId, CancellationToken.None)).Count);
        Assert.Equal(3, await store.DeleteExpiredAsync(now, 10, CancellationToken.None));
        Assert.Empty(await store.ReadAsync(turnId, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_expired_deletes_only_the_selected_exact_pairs_not_their_cross_product()
    {
        var firstTurnId = SeedTurn("cross-product-a");
        var secondTurnId = SeedTurn("cross-product-b");
        var now = new DateTimeOffset(2026, 9, 5, 14, 0, 0, TimeSpan.Zero);
        var oldestFirstPair = Marker(firstTurnId, 71, TurnEventKind.Part, now.AddMinutes(-10), now.AddMinutes(-4));
        var oldestSecondPair = Marker(secondTurnId, 72, TurnEventKind.Part, now.AddMinutes(-9), now.AddMinutes(-3));
        var retainedFirstPair = Marker(firstTurnId, 72, TurnEventKind.Part, now.AddMinutes(-8), now.AddMinutes(-2));
        var retainedSecondPair = Marker(secondTurnId, 71, TurnEventKind.Part, now.AddMinutes(-7), now.AddMinutes(-1));

        using var db = CreateContext();
        var store = new SqlTurnProgressEventStore(db);
        foreach (var marker in new[] { oldestFirstPair, oldestSecondPair, retainedFirstPair, retainedSecondPair })
        {
            await store.AppendAsync(marker, CancellationToken.None);
        }

        Assert.Equal(2, await store.DeleteExpiredAsync(now, 2, CancellationToken.None));

        Assert.Equal([retainedFirstPair], await store.ReadAsync(firstTurnId, CancellationToken.None));
        Assert.Equal([retainedSecondPair], await store.ReadAsync(secondTurnId, CancellationToken.None));
    }

    private MultiChannelAgentDbContext CreateContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString);

        return new MultiChannelAgentDbContext(
            (interceptor is null ? options : options.AddInterceptors(interceptor)).Options);
    }

    private TurnId SeedTurn(string suffix)
    {
        var receivedAt = new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero);
        var turn = TestTurns.Text(
            $"native-{suffix}",
            SomeParticipant,
            $"conversation-{suffix}",
            "hello",
            null,
            receivedAt,
            null);

        using var db = CreateContext();
        db.InboxEntries.Add(new InboxEntryEntity
        {
            TurnId = turn.TurnId.Value,
            NativeMessageId = turn.NativeMessageId,
            ParticipantId = turn.ParticipantId.Value,
            ChannelConversationId = turn.ChannelConversationId.Value,
            ConversationSequence = 1,
            Channel = turn.Channel,
            PrincipalKind = turn.Principal.Kind,
            PrincipalSubject = turn.Principal.Subject,
            PrincipalTenantId = turn.Principal.TenantId,
            Capabilities = turn.Capabilities,
            Locale = turn.Locale,
            TraceId = turn.TraceId,
            WasInterrupted = turn.WasInterrupted,
            ReceivedAt = turn.ReceivedAt,
            ReceivedAtTicks = turn.ReceivedAt.UtcTicks,
            CreatedAt = turn.ReceivedAt,
            Status = InboxEntryStatus.Pending,
        });
        db.InboxContentParts.AddRange(turn.ContentParts.Select(part => new InboxContentPartEntity
        {
            TurnId = turn.TurnId.Value,
            Order = part.Order,
            Provenance = part.Provenance,
            Text = part.Text,
        }));
        db.SaveChanges();

        return turn.TurnId;
    }

    private static TurnProgressEvent Marker(
        TurnId turnId,
        long sequence,
        TurnEventKind kind,
        DateTimeOffset occurredAt,
        DateTimeOffset expiresAt) =>
        new()
        {
            TurnId = turnId,
            Sequence = sequence,
            Kind = kind,
            OccurredAt = occurredAt,
            ExpiresAt = expiresAt,
        };
}
