using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MultiChannelAgent.Infrastructure.Persistence;

/// <summary>
/// The one read that asks whether a conversation has moved past the generation a Turn was accepted
/// under, written once per provider so the answer cannot quietly become a different question.
///
/// It is a locking read rather than an ordinary one, and that is the whole point of the type. Azure
/// SQL runs with <c>READ_COMMITTED_SNAPSHOT</c> on, so an ordinary read is served from row versions:
/// while a reset holds this pair's binding updated-but-uncommitted, an unhinted read answers with the
/// generation that reset is replacing. That answer is worse than stale - the reset's own settle
/// statement has already run and found nothing, because the Turn stores its proposal after it - so
/// both mechanisms miss, and a confirmable proposal is left in a conversation nobody is in.
/// </summary>
public static class FoundryConversationBindingSupersessionRead
{
    /// <summary>
    /// <c>UPDLOCK</c> makes this read take an update lock on the binding row instead of reading a
    /// version of it, and update locks are held to the end of the transaction. That is exactly the
    /// mutual exclusion the check needs: this read and
    /// <c>SqlConversationRotationStore</c>'s generation bump both want conflicting locks on the same
    /// row, so they are strictly ordered rather than passing through each other. Whichever goes
    /// second sees what the first did.
    ///
    /// <c>HOLDLOCK</c> is deliberately not added. It would additionally range-lock the key when no row
    /// is there, and no caller needs that: the binding for a (Participant, ChannelConversation) is
    /// created when a Turn is accepted, long before that Turn can be processed, so this read never
    /// decides about a row that is still to appear.
    /// </summary>
    private const string SqlServerRead =
        """
        SELECT ParticipantId, ChannelConversationId, FoundryConversationId, Generation, CreatedAt
        FROM FoundryConversationBindings WITH (UPDLOCK)
        WHERE ParticipantId = {0} AND ChannelConversationId = {1}
        """;

    /// <summary>
    /// SQLite has no table hints and needs none: it admits one writer at a time, so a reader inside a
    /// transaction cannot pass through a rotation that is still writing. This is the provider the
    /// fast, Docker-free tests run on; SQL Server is the production one.
    /// </summary>
    private const string SqliteRead =
        """
        SELECT ParticipantId, ChannelConversationId, FoundryConversationId, Generation, CreatedAt
        FROM FoundryConversationBindings
        WHERE ParticipantId = {0} AND ChannelConversationId = {1}
        """;

    /// <summary>
    /// The supersession read for one (Participant, ChannelConversation) on whichever provider is in
    /// use, with both identities carried as parameters rather than spliced into the text - matching
    /// how this codebase already writes its hand-written statements.
    /// </summary>
    public static FormattableString Statement(
        DatabaseFacade database, Guid participantId, string channelConversationId)
    {
        ArgumentNullException.ThrowIfNull(database);

        return FormattableStringFactory.Create(
            database.IsSqlServer() ? SqlServerRead : SqliteRead, participantId, channelConversationId);
    }
}
