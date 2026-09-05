using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage of the one seam that publishes "something in this Inventory changed".
///
/// It is deliberately not a call any endpoint or store makes. It keys off the minimal semantic audit
/// fact every state-changing store already stages in the same save, which is what makes it impossible
/// for a future mutation path - or a future channel - to change Inventory state without publishing:
/// forgetting to publish would mean forgetting to audit, which is a far louder failure. Because the
/// bump runs inside the caller's own transaction, and always last, nothing is ever published before
/// it commits, a rollback takes the version with it, and the version row's lock is held for the
/// shortest possible slice of the transaction. It is not a deadlock-prevention scheme and is not
/// claimed to be one.
///
/// What it is claimed to be is atomic per Inventory: one upsert, never a look followed by an insert,
/// on both the provider production runs on and the one these tests run on. The tests below pin that
/// shape for each provider separately, because the reason it is safe differs between them - SQLite by
/// statement, SQL Server by the lock the statement takes - and neither one is evidence for the other.
/// </summary>
public sealed class InventoryVersionBumpTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Actor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();

    public InventoryVersionBumpTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task A_new_inventory_starts_at_version_zero_without_anyone_asking_for_it()
    {
        using var db = CreateContext();

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task One_audited_change_moves_the_inventory_forward_exactly_one_version()
    {
        using var db = CreateContext();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task Several_audit_facts_committed_together_still_move_it_forward_exactly_once()
    {
        using var db = CreateContext();

        db.InventoryAudits.Add(Audit(AuditEventType.StockAdded));
        db.InventoryAudits.Add(Audit(AuditEventType.StockRemoved));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The signal is "refetch this Inventory", not "here is a change log", so one commit is one
        // version however many facts it recorded.
        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_denied_access_attempt_changes_nothing_and_therefore_publishes_nothing()
    {
        using var db = CreateContext();

        await RecordAuditAsync(db, AuditEventType.AccessDenied);

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_save_that_records_no_audit_fact_publishes_nothing()
    {
        using var db = CreateContext();

        db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
            RetiredAt = null,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_change_that_rolls_back_never_leaves_a_published_version_behind()
    {
        using var db = CreateContext();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RecordAuditAsync(db, AuditEventType.StockAdded);
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();

        // Nothing was published before the commit, because the bump ran inside the very transaction
        // that was thrown away.
        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task Two_inventories_advance_independently()
    {
        var otherInventoryId = Guid.NewGuid();

        using var db = CreateContext();
        db.Inventories.Add(new InventoryEntity
        {
            Id = otherInventoryId,
            Name = "Other Warehouse",
            NormalizedName = "other warehouse",
            CreatedByParticipantId = Actor.Value,
            ClientRequestId = "seed-2",
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
        Assert.Equal(0L, await VersionAsync(db, otherInventoryId));
    }

    [Fact]
    public async Task The_store_reads_every_requested_inventory_and_omits_the_ones_it_has_no_row_for()
    {
        using var db = CreateContext();
        await RecordAuditAsync(db, AuditEventType.StockAdded);

        var versions = await new SqlInventoryVersionStore(db)
            .ReadAsync([_inventoryId, Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(1L, versions[_inventoryId]);
        Assert.Single(versions);
    }

    [Fact]
    public void The_version_row_is_referentially_independent_of_the_inventory_it_names()
    {
        using var db = CreateContext();

        // Asserted, not assumed. An audit fact about an Inventory deliberately carries no foreign key
        // (see InventoryAuditEntityConfiguration), and this row is published from exactly those facts,
        // so a cascading key here would let a state the audit model tolerates fail somebody else's
        // mutating transaction through the fallback insertion below.
        var entityType = db.Model.FindEntityType(typeof(InventoryVersionEntity))!;

        Assert.Empty(entityType.GetForeignKeys());
        Assert.Equal("InventoryVersions", entityType.GetTableName());
    }

    [Fact]
    public async Task An_inventory_that_somehow_has_no_version_row_gets_one_from_its_next_audited_change()
    {
        using var db = CreateContext();

        // Exactly the residue the migration's backfill exists to prevent, forced here so the
        // publication's insert branch is a tested path rather than a hopeful comment. It is reachable
        // at all only because there is no foreign key stopping the row from being established on
        // demand.
        await DeleteVersionRowAsync(db);

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));

        // And the re-established row is an ordinary one from then on: the next change advances it by
        // exactly one, not by one per statement the publication happens to be made of.
        await RecordAuditAsync(db, AuditEventType.StockRemoved);

        Assert.Equal(2L, await VersionAsync(db, _inventoryId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publishing_a_version_is_one_upsert_whether_or_not_the_row_is_already_there(
        bool versionRowMissing)
    {
        var commands = new RecordCommandsAgainstVersionsInterceptor();
        using var db = CreateContext(commands);

        if (versionRowMissing)
        {
            await DeleteVersionRowAsync(db);
        }

        commands.Recorded.Clear();
        await RecordAuditAsync(db, AuditEventType.StockAdded);

        // One statement, not a look followed by a write. Two statements are two chances for another
        // transaction to act between them, and on the missing-row path the second one is an INSERT of
        // a primary key the other transaction may already have taken.
        var publication = Assert.Single(commands.Recorded);
        Assert.Contains("ON CONFLICT", publication, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO UPDATE", publication, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public void Saving_synchronously_publishes_the_very_same_upsert()
    {
        // The synchronous twin exists so the seam cannot be bypassed by saving synchronously. A twin
        // is only worth having if it stays a twin, so it is held to the same statement here rather
        // than trusted to.
        var commands = new RecordCommandsAgainstVersionsInterceptor();
        using var db = CreateContext(commands);

        db.Database.ExecuteSql($"DELETE FROM InventoryVersions WHERE InventoryId = {_inventoryId}");
        db.ChangeTracker.Clear();
        commands.Recorded.Clear();

        db.InventoryAudits.Add(Audit(AuditEventType.StockAdded));
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var publication = Assert.Single(commands.Recorded);
        Assert.Contains("ON CONFLICT", publication, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DO UPDATE", publication, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1L, db.InventoryVersions.AsNoTracking().Single(v => v.InventoryId == _inventoryId).Version);
    }

    [Fact]
    public async Task The_publication_never_observes_a_missing_row_so_nothing_can_take_the_key_in_between()
    {
        // The interleaving this guards, as it reaches production: rolling-deployment residue leaves an
        // Inventory with no version row, two audited writes to it commit concurrently, and a
        // publication built from "look, then insert if absent" has both of them observe the absence and
        // both attempt the insert. One wins; the other dies on the primary key and takes an otherwise
        // valid audited write down with it.
        //
        // Reproduced deterministically rather than by racing threads: this establishes the row at the
        // one instant that matters - immediately after the publication has observed it missing. A
        // publication that never observes absence has no such instant, which is what is asserted below.
        // This says nothing about SQL Server's locking, which SQLite cannot stand in for; it pins the
        // statement shape that makes the locking question answerable at all.
        var competingWriter = new EstablishTheRowTheInstantItIsObservedMissingInterceptor();
        using var db = CreateContext(competingWriter);

        await DeleteVersionRowAsync(db);

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.False(competingWriter.FoundAnOpening);
        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task On_sql_server_one_statement_holds_the_key_while_it_decides_to_insert_or_increment()
    {
        // SQLite's upsert asserted above is a statement shape; it is not evidence about SQL Server,
        // whose duplicate-key race is decided by locking rather than by statement count. That evidence
        // has to be gathered against the SQL Server provider, and it can be gathered without a server:
        // the command is built and captured on its way to a connection that is never opened.
        var captured = new CaptureTheCommandWithoutAServerInterceptor();
        using var db = new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
                .UseSqlServer("Server=none")
                .AddInterceptors(captured)
                .Options);

        await db.Database.ExecuteSqlAsync(InventoryVersionPublication.Statement(db.Database, _inventoryId));

        var publication = Assert.Single(captured.Recorded);

        // HOLDLOCK is the whole point: it makes the MERGE take a range lock on the key it is about to
        // decide about, so a second transaction reaching the same key waits and then sees the row
        // instead of racing it to the insert. Without it, MERGE is check-then-insert with better
        // syntax.
        Assert.Contains("MERGE", publication, StringComparison.Ordinal);
        Assert.Contains("WITH (HOLDLOCK)", publication, StringComparison.Ordinal);
        Assert.Contains("WHEN MATCHED THEN UPDATE", publication, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", publication, StringComparison.Ordinal);

        // The Inventory travels as a parameter, never as text spliced into the statement.
        Assert.Equal(_inventoryId, Assert.Single(captured.ParameterValues));
        Assert.DoesNotContain(_inventoryId.ToString(), publication, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records every command the publication issues against the version table, so a return to
    /// check-then-insert is a failing test rather than a review someone has to catch.
    /// </summary>
    private sealed class RecordCommandsAgainstVersionsInterceptor : DbCommandInterceptor
    {
        public List<string> Recorded { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            if (TargetsVersions(command))
            {
                Recorded.Add(command.CommandText);
            }
        }
    }

    /// <summary>
    /// A competing writer that takes the primary key at the only moment a check-then-insert
    /// publication leaves open: right after that publication has seen no row to update.
    /// </summary>
    private sealed class EstablishTheRowTheInstantItIsObservedMissingInterceptor : DbCommandInterceptor
    {
        public bool FoundAnOpening { get; private set; }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            EstablishRowIfNoneWasFound(command, result);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            EstablishRowIfNoneWasFound(command, result);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void EstablishRowIfNoneWasFound(DbCommand command, int rowsAffected)
        {
            if (rowsAffected != 0 || FoundAnOpening || !TargetsVersions(command))
            {
                return;
            }

            FoundAnOpening = true;

            using var establishRow = command.Connection!.CreateCommand();
            establishRow.Transaction = command.Transaction;
            establishRow.CommandText =
                "INSERT OR IGNORE INTO InventoryVersions (InventoryId, Version) VALUES (@InventoryId, 1)";
            var inventoryId = establishRow.CreateParameter();
            inventoryId.ParameterName = "@InventoryId";

            // Copied from the publication's own parameter so the competing writer stores the key
            // exactly as the provider does, rather than in a representation that would never collide.
            inventoryId.Value = command.Parameters[0].Value!;
            establishRow.Parameters.Add(inventoryId);
            establishRow.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Captures the command EF Core actually builds for a provider without that provider's server
    /// being present: the connection is never opened and the command is never executed, so what is
    /// asserted is the real generated text and its real parameters rather than a copy of them kept in
    /// a test.
    /// </summary>
    private sealed class CaptureTheCommandWithoutAServerInterceptor : DbCommandInterceptor, IDbConnectionInterceptor
    {
        public List<string> Recorded { get; } = [];

        public List<object?> ParameterValues { get; } = [];

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return InterceptionResult<int>.SuppressWithResult(1);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(1));
        }

        public InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        public InterceptionResult ConnectionClosing(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            InterceptionResult.Suppress();

        public ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
            ValueTask.FromResult(InterceptionResult.Suppress());

        private void Record(DbCommand command)
        {
            Recorded.Add(command.CommandText);
            ParameterValues.AddRange(command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value));
        }
    }

    private static bool TargetsVersions(DbCommand command) =>
        command.CommandText.Contains("InventoryVersions", StringComparison.Ordinal);

    private async Task DeleteVersionRowAsync(MultiChannelAgentDbContext db)
    {
        await db.Database.ExecuteSqlAsync($"DELETE FROM InventoryVersions WHERE InventoryId = {_inventoryId}");
        db.ChangeTracker.Clear();
    }

    private MultiChannelAgentDbContext CreateContext(DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString);

        return new MultiChannelAgentDbContext(
            (interceptor is null ? options : options.AddInterceptors(interceptor)).Options);
    }

    private static async Task<long> VersionAsync(MultiChannelAgentDbContext db, Guid inventoryId)
    {
        var row = await db.InventoryVersions.AsNoTracking().FirstOrDefaultAsync(v => v.InventoryId == inventoryId);
        return row?.Version ?? -1L;
    }

    private async Task RecordAuditAsync(MultiChannelAgentDbContext db, AuditEventType eventType)
    {
        db.InventoryAudits.Add(Audit(eventType));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private InventoryAuditEntity Audit(AuditEventType eventType) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType.ToString(),
        ActorKind = AuditActorKind.Participant.ToString(),
        ActorId = Actor.Value.ToString(),
        InventoryId = _inventoryId,
        SubjectParticipantId = null,
        OutcomeCode = "ok",
        OccurredAtUtc = Now,
        OccurredAtUtcTicks = Now.UtcTicks,
        ExpiresAtUtc = Now.AddDays(90),
    };

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = Actor.Value,
            DisplayName = "Version Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = Actor.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
