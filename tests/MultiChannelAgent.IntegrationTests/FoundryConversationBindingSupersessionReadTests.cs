using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The shape of the one read that has to serialize against a conversation reset, asserted per
/// provider without a database.
///
/// A supersession check that answers from a row version while a rotation is mid-flight reports a
/// conversation as current when it is already being replaced, and the Turn's proposal is then settled
/// by nobody. On Azure SQL - where READ_COMMITTED_SNAPSHOT is on by default - that is exactly what an
/// unhinted read does, and nothing about the resulting statement looks wrong. So the hint that makes
/// this read take a lock instead is asserted here, where a regression is visible immediately, as well
/// as end to end in <see cref="SqlSupersessionReadSerializationTests"/> behind Docker.
/// </summary>
public sealed class FoundryConversationBindingSupersessionReadTests
{
    private static readonly Guid SomeParticipant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string SomeConversation = "web:profile-1";

    private static MultiChannelAgentDbContext SqlServerContext() => new(
        new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlServer("Server=localhost;Database=shape-only;Trusted_Connection=True")
            .Options);

    private static MultiChannelAgentDbContext SqliteContext() => new(
        new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options);

    [Fact]
    public void On_SQL_Server_the_read_takes_an_update_lock_so_it_serializes_with_a_rotation()
    {
        using var db = SqlServerContext();

        var statement = FoundryConversationBindingSupersessionRead.Statement(
            db.Database, SomeParticipant, SomeConversation);

        Assert.Contains("WITH (UPDLOCK)", statement.Format, StringComparison.Ordinal);
        Assert.Contains("FoundryConversationBindings", statement.Format, StringComparison.Ordinal);
    }

    // SQLite has no table hints, and needs none: one writer at a time is the whole storage engine.
    [Fact]
    public void On_SQLite_the_read_carries_no_table_hint()
    {
        using var db = SqliteContext();

        var statement = FoundryConversationBindingSupersessionRead.Statement(
            db.Database, SomeParticipant, SomeConversation);

        Assert.DoesNotContain("UPDLOCK", statement.Format, StringComparison.Ordinal);
        Assert.Contains("FoundryConversationBindings", statement.Format, StringComparison.Ordinal);
    }

    // The identities are carried as parameters, never spliced into the text - the same rule every
    // other hand-written statement in this codebase follows.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_identities_are_parameters_rather_than_text(bool sqlServer)
    {
        using var db = sqlServer ? SqlServerContext() : SqliteContext();

        var statement = FoundryConversationBindingSupersessionRead.Statement(
            db.Database, SomeParticipant, SomeConversation);

        Assert.DoesNotContain(SomeParticipant.ToString(), statement.Format, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SomeConversation, statement.Format, StringComparison.Ordinal);
        Assert.Equal([SomeParticipant, SomeConversation], statement.GetArguments());
    }
}
