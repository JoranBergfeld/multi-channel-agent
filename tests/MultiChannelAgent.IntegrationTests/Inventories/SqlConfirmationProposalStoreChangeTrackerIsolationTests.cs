using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free regression coverage for the EF Core <c>ChangeTracker</c> contamination invariant
/// behind <see cref="SqlConfirmationProposalStore"/>, mirroring
/// <see cref="SqlTurnResultStoreChangeTrackerIsolationTests"/> and
/// <see cref="SqlStockChangeSetStoreChangeTrackerIsolationTests"/>.
///
/// Storing a proposal stages an Added row and only then saves and commits. One coordinator scope
/// serves a whole batch of Turns through one DbContext, so a store that fails must leave nothing
/// staged: a leaked Added proposal is not merely clutter, it is a <em>pending</em> proposal - the one
/// row in this schema that a later "confirm" would happily execute - flushed by an unrelated Turn
/// that never proposed anything.
/// </summary>
public sealed class SqlConfirmationProposalStoreChangeTrackerIsolationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private const string Conversation = "web:profile-1";

    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;
    private readonly FailurePoints _failures = new();
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());

    /// <summary>
    /// Test-side switches for the two faults that cannot be provoked by data alone. They are attached
    /// through <see cref="DbContextOptionsBuilder"/> interceptors, so nothing test-only exists in the
    /// production store.
    /// </summary>
    private sealed class FailurePoints
    {
        public bool FailProposalInsert { get; set; }

        public bool FailCommit { get; set; }

        /// <summary>Cancelled as the staged insert reaches the database, not before it was staged.</summary>
        public CancellationTokenSource? CancelOnProposalInsert { get; set; }
    }

    private sealed class ThrowingCommandInterceptor(FailurePoints failures) : DbCommandInterceptor
    {
        public const string Marker = "provoked-insert-failure";

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Guard(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Guard(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Guard(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO \"ConfirmationProposals\"", StringComparison.Ordinal))
            {
                return;
            }

            if (failures.CancelOnProposalInsert is { } cancellation)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            if (failures.FailProposalInsert)
            {
                throw new InvalidOperationException(Marker);
            }
        }
    }

    private sealed class ThrowingTransactionInterceptor(FailurePoints failures) : DbTransactionInterceptor
    {
        public const string Marker = "provoked-commit-failure";

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            failures.FailCommit
                ? throw new InvalidOperationException(Marker)
                : base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public SqlConfirmationProposalStoreChangeTrackerIsolationTests()
    {
        // One open connection keeps the in-memory database alive, and one DbContext is shared by every
        // call below - exactly as one coordinator scope shares one context across a batch of Turns.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new ThrowingCommandInterceptor(_failures), new ThrowingTransactionInterceptor(_failures))
                .Options);

        _db.Database.EnsureCreated();
        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_store_whose_insert_violates_a_unique_index_leaves_no_proposal_staged()
    {
        // A token hash is unique across the whole table, so reusing one is a real insert failure that
        // needs no interception at all.
        var token = ConfirmationToken.Issue();
        SeedSettledProposal(ConfirmationToken.HashOf(token), "web:profile-other");
        var store = new SqlConfirmationProposalStore(_db);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => store.StoreAsync(Proposal(token), Now, CancellationToken.None));

        AssertTrackerWasCleared();
        await AssertNoPendingProposalLeaksAsync();
    }

    [Fact]
    public async Task A_store_whose_insert_faults_leaves_no_proposal_staged_and_reports_the_fault_it_met()
    {
        var store = new SqlConfirmationProposalStore(_db);
        _failures.FailProposalInsert = true;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => store.StoreAsync(Proposal(ConfirmationToken.Issue()), Now, CancellationToken.None));

        // The fault that actually happened is what propagates - not a rollback error raised on top of
        // it, and not a conflict this store never established.
        Assert.Contains(ThrowingCommandInterceptor.Marker, Flatten(thrown), StringComparison.Ordinal);

        _failures.FailProposalInsert = false;
        AssertTrackerWasCleared();
        await AssertNoPendingProposalLeaksAsync();
    }

    [Fact]
    public async Task A_store_whose_commit_fails_leaves_nothing_staged_and_nothing_committed()
    {
        var store = new SqlConfirmationProposalStore(_db);
        _failures.FailCommit = true;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => store.StoreAsync(Proposal(ConfirmationToken.Issue()), Now, CancellationToken.None));

        Assert.Contains(ThrowingTransactionInterceptor.Marker, Flatten(thrown), StringComparison.Ordinal);

        _failures.FailCommit = false;
        AssertTrackerWasCleared();
        await AssertNoPendingProposalLeaksAsync();
    }

    [Fact]
    public async Task A_store_cancelled_after_staging_its_proposal_leaves_nothing_behind()
    {
        var store = new SqlConfirmationProposalStore(_db);
        using var cancellation = new CancellationTokenSource();
        _failures.CancelOnProposalInsert = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.StoreAsync(Proposal(ConfirmationToken.Issue()), Now, cancellation.Token));

        // Cleanup itself must not observe that cancellation, or the row would stay staged precisely
        // when the caller is least able to notice.
        _failures.CancelOnProposalInsert = null;
        AssertTrackerWasCleared();
        await AssertNoPendingProposalLeaksAsync();
    }

    [Fact]
    public async Task Superseding_and_inserting_still_commit_together_and_leave_exactly_one_pending_proposal()
    {
        var store = new SqlConfirmationProposalStore(_db);
        var first = Proposal(ConfirmationToken.Issue());
        var second = Proposal(ConfirmationToken.Issue());

        Assert.False((await store.StoreAsync(first, Now, CancellationToken.None)).SupersededExisting);
        Assert.True((await store.StoreAsync(second, Now, CancellationToken.None)).SupersededExisting);

        Assert.Equal(ProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Equal(second.Id, (await store.FindPendingAsync(_participant, Conversation, CancellationToken.None))!.Id);
        Assert.Equal(
            1,
            await _db.ConfirmationProposals.AsNoTracking().CountAsync(p => p.Status == nameof(ProposalStatus.Pending)));
        AssertNothingIsStaged();
    }

    private static string Flatten(Exception exception)
    {
        var text = exception.ToString();

        return exception is AggregateException aggregate
            ? text + string.Join(" ", aggregate.InnerExceptions.Select(inner => inner.ToString()))
            : text;
    }

    /// <summary>
    /// An abandoned attempt must leave the tracker empty, not merely free of pending writes: a row
    /// left tracked as Unchanged after its transaction rolled back is a phantom this scope would go
    /// on resolving later reads against.
    /// </summary>
    private void AssertTrackerWasCleared() => Assert.Empty(_db.ChangeTracker.Entries());

    private void AssertNothingIsStaged() =>
        Assert.DoesNotContain(_db.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);

    /// <summary>
    /// Proves the failed store left nothing behind for the next Turn in this scope to flush: the very
    /// next unrelated write must not carry a pending proposal into the database with it.
    /// </summary>
    private async Task AssertNoPendingProposalLeaksAsync()
    {
        _db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            Name = "Shelf Z",
            NormalizedName = "shelf z",
            CreatedAt = Now,
        });

        await _db.SaveChangesAsync();

        Assert.DoesNotContain(
            await _db.ConfirmationProposals.AsNoTracking().ToListAsync(), p => p.Status == nameof(ProposalStatus.Pending));
    }

    private ConfirmationProposal Proposal(string token)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(token),
            _participant,
            Conversation,
            new InventoryId(_inventoryId),
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", _unit, "each",
                        null, null, null, Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    private void SeedSettledProposal(ConfirmationTokenHash tokenHash, string channelConversationId)
    {
        _db.ConfirmationProposals.Add(new ConfirmationProposalEntity
        {
            ProposalId = Guid.NewGuid(),
            TokenHash = tokenHash.Value,
            ParticipantId = _participant.Value,
            ChannelConversationId = channelConversationId,
            InventoryId = _inventoryId,
            ProposedInTurnId = Guid.NewGuid(),
            Status = nameof(ProposalStatus.Rejected),
            Kind = nameof(ProposalKind.Stock),
            ChangesJson = "{}",
            ExpectedVersionsJson = "[]",
            ExpectedAbsencesJson = "[]",
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(10),
            ExpiresAtTicks = Now.AddMinutes(10).UtcTicks,
            SettledAt = Now,
            SettledAtTicks = Now.UtcTicks,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void Seed()
    {
        var creatorId = Guid.NewGuid();
        _db.Participants.Add(new ParticipantEntity
        {
            Id = creatorId,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        _db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = creatorId,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }
}
