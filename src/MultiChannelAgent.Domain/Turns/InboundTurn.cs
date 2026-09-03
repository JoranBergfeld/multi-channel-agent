using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// The full scope a native message identifier is deduplicated within. A channel's own message
/// identifier is only ever unique inside the scope that issued it - two Participants, or two
/// conversations of one Participant, can legitimately carry the same opaque string - so at-least-once
/// deduplication always keys on this whole triple, never on the bare
/// <see cref="NativeMessageId"/>. Keying on the bare id would silently drop an unrelated Participant's
/// real request and hand them someone else's recorded Outcome.
/// </summary>
public readonly record struct NativeMessageKey(
    ParticipantId ParticipantId, ChannelConversationId ChannelConversationId, string NativeMessageId);

/// <summary>
/// Everything an adapter must supply to have a Turn durably accepted. It is the application-owned
/// contract every channel translates its validated native input into - not a web-specific shape - so
/// a Teams or email adapter added later fills in exactly these fields rather than inventing its own.
/// </summary>
public sealed record InboundTurnDraft
{
    /// <summary>The channel's own stable identifier for this message, unique within its issuing scope.</summary>
    public required string? NativeMessageId { get; init; }

    /// <summary>The application-owned Participant the adapter resolved from its trusted evidence.</summary>
    public required ParticipantId ParticipantId { get; init; }

    /// <summary>The channel's own conversation identifier (for the web, the browser-profile conversation).</summary>
    public required string? ChannelConversationId { get; init; }

    /// <summary>Which channel this arrived on, for example <c>web</c>.</summary>
    public required string? Channel { get; init; }

    /// <summary>The typed authenticated evidence the adapter presents for the Participant.</summary>
    public required ChannelPrincipal Principal { get; init; }

    /// <summary>What that channel can render and carry.</summary>
    public required ChannelCapabilities Capabilities { get; init; }

    /// <summary>The Turn's content, in order, each part carrying its provenance.</summary>
    public required IReadOnlyList<TurnContentPart> ContentParts { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public string? Locale { get; init; }

    public string? TraceId { get; init; }

    /// <summary>
    /// The common case every text-only channel produces today: one part, authored directly by the
    /// authenticated Participant in this Turn.
    /// </summary>
    public static InboundTurnDraft DirectText(
        string? nativeMessageId,
        ParticipantId participantId,
        string? channelConversationId,
        string? channel,
        ChannelPrincipal principal,
        ChannelCapabilities capabilities,
        string? contentText,
        string? locale,
        DateTimeOffset receivedAt,
        string? traceId) => new()
        {
            NativeMessageId = nativeMessageId,
            ParticipantId = participantId,
            ChannelConversationId = channelConversationId,
            Channel = channel,
            Principal = principal,
            Capabilities = capabilities,
            ContentParts = [TurnContentPart.Create(1, ContentProvenance.Direct, contentText)],
            Locale = locale,
            ReceivedAt = receivedAt,
            TraceId = traceId,
        };
}

/// <summary>
/// A normalized, channel-neutral inbound Turn. Every adapter translates validated native input into
/// this shape before it is durably accepted. The <see cref="TurnId"/> is generated once at
/// acceptance; the <see cref="NativeMessageKey"/> is the stable native identity, scoped to its
/// issuing Participant and ChannelConversation, used to detect duplicate delivery.
/// <see cref="ParticipantId"/> and <see cref="ChannelConversationId"/> are the application-owned
/// identities the adapter resolved from trusted context (authenticated claims and the channel's own
/// conversation identifier) - never accepted as untrusted caller input.
///
/// <see cref="Principal"/>, <see cref="ContentParts"/>, and <see cref="Capabilities"/> complete that
/// contract: who the channel authenticated, what the Turn actually said and where each piece of it
/// came from, and what the channel can do with an answer. Only <see cref="ContentProvenance.Direct"/>
/// content may ever provide operational intent, which is why provenance is carried here rather than
/// inferred later.
/// </summary>
public sealed record InboundTurn
{
    /// <summary>
    /// The authoritative maximum length of a channel's own message identifier, matching the persisted
    /// column so an over-long one is rejected here - as a validation error a caller can act on - long
    /// before it could reach the database as an unhandled failure.
    /// </summary>
    public const int MaxNativeMessageIdLength = 256;

    /// <summary>The authoritative maximum length of a ChannelConversation identifier, for the same reason.</summary>
    public const int MaxChannelConversationIdLength = 256;

    /// <summary>The authoritative maximum length of a channel name, for the same reason.</summary>
    public const int MaxChannelLength = 32;

    /// <summary>The authoritative maximum length of a Turn's locale tag, for the same reason.</summary>
    public const int MaxLocaleLength = 32;

    /// <summary>The authoritative maximum length of a Turn's trace identifier, for the same reason.</summary>
    public const int MaxTraceIdLength = 128;

    public required TurnId TurnId { get; init; }

    public required string NativeMessageId { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required ChannelConversationId ChannelConversationId { get; init; }

    public required string Channel { get; init; }

    public required ChannelPrincipal Principal { get; init; }

    public required ChannelCapabilities Capabilities { get; init; }

    public required IReadOnlyList<TurnContentPart> ContentParts { get; init; }

    public string? Locale { get; init; }

    public string? TraceId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>The scope-complete identity duplicate native delivery is detected by.</summary>
    public NativeMessageKey NativeMessageKey => new(ParticipantId, ChannelConversationId, NativeMessageId);

    /// <summary>
    /// The only content that may provide operational intent: what the authenticated Participant
    /// themselves said in this Turn, in order. Quoted, forwarded, attached, retrieved, tool-produced,
    /// and model-derived parts are deliberately excluded - they are data, never instruction.
    /// </summary>
    public string ContentText => string.Join(
        "\n", ContentParts.Where(part => part.Provenance == ContentProvenance.Direct).OrderBy(part => part.Order).Select(part => part.Text));

    public static InboundTurn Create(InboundTurnDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var normalizedNativeMessageId = RequireWithinBounds(
            RequireNonBlank(draft.NativeMessageId, nameof(draft.NativeMessageId)), MaxNativeMessageIdLength, nameof(draft.NativeMessageId));
        var normalizedChannelConversationId = RequireWithinBounds(
            RequireNonBlank(draft.ChannelConversationId, nameof(draft.ChannelConversationId)),
            MaxChannelConversationIdLength,
            nameof(draft.ChannelConversationId));
        var normalizedChannel = RequireWithinBounds(
            RequireNonBlank(draft.Channel, nameof(draft.Channel)), MaxChannelLength, nameof(draft.Channel));
        var contentParts = RequireOrderedContent(draft.ContentParts);

        if (!draft.Capabilities.HasFlag(ChannelCapabilities.Text))
        {
            // Every channel must at least be able to render a textual answer; accepting a Turn whose
            // channel cannot would durably record work nothing could ever deliver.
            throw new ArgumentException("A channel must at least declare text capability.", nameof(draft.Capabilities));
        }

        return new InboundTurn
        {
            TurnId = TurnId.NewId(),
            NativeMessageId = normalizedNativeMessageId,
            ParticipantId = draft.ParticipantId,
            ChannelConversationId = new ChannelConversationId(normalizedChannelConversationId),
            Channel = normalizedChannel,
            Principal = draft.Principal,
            Capabilities = draft.Capabilities,
            ContentParts = contentParts,
            Locale = RequireOptionalWithinBounds(draft.Locale, MaxLocaleLength, nameof(draft.Locale)),
            TraceId = RequireOptionalWithinBounds(draft.TraceId, MaxTraceIdLength, nameof(draft.TraceId)),
            ReceivedAt = draft.ReceivedAt,
        };
    }

    private static IReadOnlyList<TurnContentPart> RequireOrderedContent(IReadOnlyList<TurnContentPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        if (parts.Count == 0)
        {
            throw new ArgumentException("A Turn must carry at least one content part.", nameof(parts));
        }

        if (parts.Select(part => part.Order).Distinct().Count() != parts.Count)
        {
            throw new ArgumentException("Content part order must be unique within a Turn.", nameof(parts));
        }

        if (!parts.Any(part => part.Provenance == ContentProvenance.Direct))
        {
            // A Turn with no direct content carries nothing the Participant themselves said, so there
            // is nothing that could legitimately ask for anything.
            throw new ArgumentException("A Turn must carry at least one direct content part.", nameof(parts));
        }

        return parts.OrderBy(part => part.Order).ToList();
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value.Trim();
    }

    private static string RequireWithinBounds(string value, int maxLength, string parameterName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return value;
    }

    private static string? RequireOptionalWithinBounds(string? value, int maxLength, string parameterName)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null ? null : RequireWithinBounds(normalized, maxLength, parameterName);
    }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
