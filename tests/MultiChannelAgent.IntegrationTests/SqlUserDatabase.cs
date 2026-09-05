using Microsoft.Data.SqlClient;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// An ephemeral user database inside the shared test container, created for one scenario and dropped
/// when it ends.
///
/// It exists because the container's own connection string points at <c>master</c>, and a scenario
/// that needs a database-level option cannot use it: <c>READ_COMMITTED_SNAPSHOT</c> - the setting
/// every Azure SQL database is created with - simply cannot be set on <c>master</c>, and
/// <c>ROLLBACK IMMEDIATE</c> against it would evict every other connection in the container. So a
/// scenario that has to run under production's isolation semantics gets its own database, sets the
/// option there before anything connects, and leaves the shared one alone.
///
/// This is deliberately not the default for SQL-backed tests. They share <c>master</c> in one
/// container on purpose - it is what makes them fast - and nothing else here depends on a
/// database-level option.
/// </summary>
public sealed class SqlUserDatabase : IAsyncDisposable
{
    private readonly string _serverConnectionString;

    private SqlUserDatabase(string serverConnectionString, string name)
    {
        _serverConnectionString = serverConnectionString;
        Name = name;
        ConnectionString = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }
            .ConnectionString;
    }

    /// <summary>The database's own name, as <c>sys.databases</c> records it.</summary>
    public string Name { get; }

    /// <summary>A connection string for this database rather than the container's <c>master</c>.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Creates a uniquely named database on the server <paramref name="serverConnectionString"/>
    /// points at. The name is a fresh GUID, so parallel scenarios in the same container never collide.
    /// </summary>
    public static async Task<SqlUserDatabase> CreateAsync(
        string serverConnectionString, CancellationToken cancellationToken)
    {
        var database = new SqlUserDatabase(serverConnectionString, $"mca_{Guid.NewGuid():N}");
        await database.ExecuteOnServerAsync(
            "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@database); EXEC sp_executesql @sql;",
            cancellationToken);

        return database;
    }

    /// <summary>
    /// Puts this database in the mode an Azure SQL database is created in. Call it before anything
    /// connects - migrations included - so the option is already set for every session that follows
    /// and the eviction that comes with setting it has nothing to evict.
    /// </summary>
    public async Task EnableReadCommittedSnapshotAsync(CancellationToken cancellationToken)
    {
        await ExecuteOnServerAsync(
            "DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + " +
            "N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE'; EXEC sp_executesql @sql;",
            cancellationToken);
    }

    /// <summary>
    /// Whether row-versioned read committed is actually on. A scenario that silently ran without it
    /// would prove nothing, so this is asserted rather than assumed.
    /// </summary>
    public async Task<bool> IsReadCommittedSnapshotOnAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ServerConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @database";
        command.Parameters.AddWithValue("@database", Name);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Drops the database, clearing the pool first so no connection is left holding it. Failures here
    /// are deliberately swallowed: the container this lives in is torn down moments later either way,
    /// and a cleanup error surfacing instead of the assertion that actually failed would explain
    /// nothing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();

        try
        {
            await ExecuteOnServerAsync(
                "DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + " +
                "N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE'; EXEC sp_executesql @sql;",
                CancellationToken.None);

            await ExecuteOnServerAsync(
                "DECLARE @sql nvarchar(max) = N'DROP DATABASE ' + QUOTENAME(@database); EXEC sp_executesql @sql;",
                CancellationToken.None);
        }
        catch (Exception)
        {
            // Deliberately ignored; see the remarks above.
        }
    }

    /// <summary>
    /// The connection string this database was created from, pointed at <c>master</c>. Database-level
    /// statements are issued from there because they cannot run inside the database they act on.
    /// </summary>
    private string ServerConnectionString() =>
        new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" }.ConnectionString;

    /// <summary>
    /// Runs one statement against <c>master</c>. The database name is quoted through
    /// <c>QUOTENAME</c> rather than concatenated, because a database name cannot be a parameter in
    /// DDL and this is the safe way to say so.
    /// </summary>
    private async Task ExecuteOnServerAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ServerConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@database", Name);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
