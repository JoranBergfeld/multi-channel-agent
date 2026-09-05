using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Domain.Voice;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Voice;

namespace MultiChannelAgent.IntegrationTests.Voice;

/// <summary>
/// Fast, Docker-free regression coverage for the durable-admission race behind
/// <see cref="SqlVoiceSessionStore"/>: two independent <see cref="MultiChannelAgentDbContext"/>
/// instances — each its own real SQLite connection into one shared-cache in-memory database, mirroring
/// two separate HTTP request scopes hitting one real SQL Server — both attempt to admit a Voice session
/// for the SAME <see cref="ParticipantId"/> concurrently. A real relational engine (not mocks, not the
/// EF Core InMemory provider, neither of which enforces the unique index this race depends on)
/// guarantees exactly one insert wins; <see cref="SqlVoiceSessionStore.TryAdmitAsync"/> must resolve
/// the loser into <see cref="VoiceAdmissionDenialReason.AlreadyActive"/> instead of leaking a raw
/// <see cref="DbUpdateException"/> to callers.
/// </summary>
public sealed class SqlVoiceSessionStoreConcurrencyTests : IDisposable
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChannelConversationId SomeConversation = new("conv-race");
    private const string SomeOwner = "instance-1";
    private const int DefaultCap = 5;

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlVoiceSessionStoreConcurrencyTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private static VoiceSessionDeadlines DefaultDeadlines(DateTimeOffset at) => new(
        ExpiresAt: at + TimeSpan.FromMinutes(30),
        WarningAt: at + TimeSpan.FromMinutes(25),
        IdleExpiresAt: at + TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Two_concurrent_admissions_for_same_participant_converge_on_one_session()
    {
        using var dbA = CreateContext();
        using var dbB = CreateContext();

        var storeA = new SqlVoiceSessionStore(dbA);
        var storeB = new SqlVoiceSessionStore(dbB);

        var sessionA = VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, DefaultDeadlines(Now));
        var sessionB = VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, DefaultDeadlines(Now));

        var taskA = storeA.TryAdmitAsync(sessionA, DefaultCap, CancellationToken.None);
        var taskB = storeB.TryAdmitAsync(sessionB, DefaultCap, CancellationToken.None);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Single(results, r => r.Admitted);
        Assert.Single(results, r => !r.Admitted && r.DenialReason == VoiceAdmissionDenialReason.AlreadyActive);

        // Verify exactly one row
        using var verifyDb = CreateContext();
        var count = await verifyDb.VoiceSessions.AsNoTracking()
            .CountAsync(e => e.ParticipantId == SomeParticipant.Value && e.OccupiesSlot);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Two_concurrent_admissions_for_different_participants_both_succeed()
    {
        var participantA = new ParticipantId(Guid.NewGuid());
        var participantB = new ParticipantId(Guid.NewGuid());

        using var dbA = CreateContext();
        using var dbB = CreateContext();

        var storeA = new SqlVoiceSessionStore(dbA);
        var storeB = new SqlVoiceSessionStore(dbB);

        var sessionA = VoiceSession.Reserve(participantA, SomeConversation, SomeOwner, Now, DefaultDeadlines(Now));
        var sessionB = VoiceSession.Reserve(participantB, SomeConversation, SomeOwner, Now, DefaultDeadlines(Now));

        var resultA = await storeA.TryAdmitAsync(sessionA, DefaultCap, CancellationToken.None);
        var resultB = await storeB.TryAdmitAsync(sessionB, DefaultCap, CancellationToken.None);

        Assert.True(resultA.Admitted);
        Assert.True(resultB.Admitted);
    }

    [Fact]
    public async Task Admission_after_ended_session_succeeds_for_same_participant()
    {
        using var db1 = CreateContext();
        var store1 = new SqlVoiceSessionStore(db1);
        var first = VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now, DefaultDeadlines(Now));
        var r1 = await store1.TryAdmitAsync(first, DefaultCap, CancellationToken.None);
        Assert.True(r1.Admitted);

        first.End(Now + TimeSpan.FromMinutes(1));
        using var db2 = CreateContext();
        var store2 = new SqlVoiceSessionStore(db2);
        await store2.UpdateAsync(first, VoiceSessionStatus.Negotiating, CancellationToken.None);

        var second = VoiceSession.Reserve(SomeParticipant, SomeConversation, SomeOwner, Now + TimeSpan.FromMinutes(2), DefaultDeadlines(Now + TimeSpan.FromMinutes(2)));
        using var db3 = CreateContext();
        var store3 = new SqlVoiceSessionStore(db3);
        var r2 = await store3.TryAdmitAsync(second, DefaultCap, CancellationToken.None);

        Assert.True(r2.Admitted);
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }
}
