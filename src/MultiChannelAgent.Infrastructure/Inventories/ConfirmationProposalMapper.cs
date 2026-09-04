using System.Text.Json;
using System.Text.Json.Serialization;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// The exact, versioned serialization of a proposal's contents.
///
/// Quantities cross this boundary as invariant decimal text rather than as numbers: a proposal is a
/// promise about exact amounts, and JSON numbers are the one representation that could quietly round
/// one. Every identity crosses as its Guid text. <see cref="SchemaVersion"/> is written so a later
/// shape change can be detected rather than silently mis-read - a row it cannot read is refused, not
/// guessed at.
/// </summary>
internal static class ConfirmationProposalMapper
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record EntryStateDto(
        int Version,
        Guid? StockEntryId,
        string Name,
        string NormalizedName,
        Guid UnitId,
        string UnitCanonicalName,
        Guid? LocationId,
        string? LocationName,
        string? Note,
        string PreviousQuantity,
        string ResultingQuantity,
        bool Retired);

    private sealed record ChangeDto(
        int Order,
        string Kind,
        string Effect,
        EntryStateDto Source,
        EntryStateDto? Destination,
        string TransferredQuantity,
        string? NewName,
        string? NewNormalizedName);

    private sealed record VersionDto(Guid StockEntryId, Guid ConcurrencyStamp);

    private sealed record AbsenceDto(string NormalizedName, Guid UnitId, Guid? LocationId);

    private sealed record ChangesEnvelope(int Version, IReadOnlyList<ChangeDto> Changes);

    public static ConfirmationProposalEntity ToEntity(ConfirmationProposal proposal) => new()
    {
        ProposalId = proposal.Id.Value,
        TokenHash = proposal.TokenHash.Value,
        ParticipantId = proposal.ParticipantId.Value,
        ChannelConversationId = proposal.ChannelConversationId,
        InventoryId = proposal.InventoryId.Value,
        ProposedInTurnId = proposal.ProposedInTurnId.Value,
        Status = nameof(ProposalStatus.Pending),
        ChangesJson = JsonSerializer.Serialize(
            new ChangesEnvelope(SchemaVersion, proposal.Changes.Select(ToDto).ToList()), Options),
        ExpectedVersionsJson = JsonSerializer.Serialize(
            proposal.ExpectedVersions.Select(v => new VersionDto(v.StockEntryId.Value, v.ConcurrencyStamp)).ToList(), Options),
        ExpectedAbsencesJson = JsonSerializer.Serialize(
            proposal.ExpectedAbsences.Select(a => new AbsenceDto(a.NormalizedName, a.UnitId.Value, a.LocationId?.Value)).ToList(), Options),
        CreatedAt = proposal.CreatedAt,
        ExpiresAt = proposal.ExpiresAt,
        ExpiresAtTicks = proposal.ExpiresAt.UtcTicks,
        SettledAt = null,
        SettledAtTicks = null,
    };

    public static ConfirmationProposal ToDomain(ConfirmationProposalEntity entity)
    {
        var envelope = JsonSerializer.Deserialize<ChangesEnvelope>(entity.ChangesJson, Options)
            ?? throw new InvalidOperationException("A stored proposal carried no changes.");

        if (envelope.Version != SchemaVersion)
        {
            // A proposal is only ever ten minutes old, so a shape this process cannot read is a
            // deployment mistake, not a migration case to guess at.
            throw new InvalidOperationException($"A stored proposal uses unsupported schema version {envelope.Version}.");
        }

        var versions = JsonSerializer.Deserialize<List<VersionDto>>(entity.ExpectedVersionsJson, Options) ?? [];
        var absences = JsonSerializer.Deserialize<List<AbsenceDto>>(entity.ExpectedAbsencesJson, Options) ?? [];

        return new ConfirmationProposal
        {
            Id = new ProposalId(entity.ProposalId),
            TokenHash = new ConfirmationTokenHash(entity.TokenHash),
            ParticipantId = new ParticipantId(entity.ParticipantId),
            ChannelConversationId = entity.ChannelConversationId,
            InventoryId = new InventoryId(entity.InventoryId),
            ProposedInTurnId = new TurnId(entity.ProposedInTurnId),
            Kind = ProposalKind.Stock,
            Changes = envelope.Changes.Select(ToDomain).ToList(),
            ExpectedVersions = versions
                .Select(v => new ExpectedEntryVersion(new StockEntryId(v.StockEntryId), v.ConcurrencyStamp))
                .ToList(),
            ExpectedAbsences = absences
                .Select(a => new ExpectedEquivalentStockAbsence(
                    a.NormalizedName, new UnitId(a.UnitId), a.LocationId is { } id ? new LocationId(id) : null))
                .ToList(),
            CreatedAt = entity.CreatedAt,
        };
    }

    private static ChangeDto ToDto(ProposedChange change) => new(
        change.Order,
        StockMutationKinds.ToMachineText(change.Kind),
        change.Effect.ToString(),
        ToDto(change.Source),
        change.Destination is null ? null : ToDto(change.Destination),
        change.TransferredQuantity.ToInvariantText(),
        change.NewName,
        change.NewNormalizedName);

    private static EntryStateDto ToDto(ProposedEntryState state) => new(
        SchemaVersion,
        state.StockEntryId?.Value,
        state.Name,
        state.NormalizedName,
        state.UnitId.Value,
        state.UnitCanonicalName,
        state.LocationId?.Value,
        state.LocationName,
        state.Note,
        state.PreviousQuantity.ToInvariantText(),
        state.ResultingQuantity.ToInvariantText(),
        state.Retired);

    private static ProposedChange ToDomain(ChangeDto dto)
    {
        if (!StockMutationKinds.TryParse(dto.Kind, out var kind)
            || !Enum.TryParse<StockChangeEffectKind>(dto.Effect, ignoreCase: false, out var effect))
        {
            throw new InvalidOperationException("A stored proposal carried an unreadable change kind or effect.");
        }

        return new ProposedChange
        {
            Order = dto.Order,
            Kind = kind,
            Effect = effect,
            Source = ToDomain(dto.Source),
            Destination = dto.Destination is null ? null : ToDomain(dto.Destination),
            TransferredQuantity = ParseQuantity(dto.TransferredQuantity),
            NewName = dto.NewName,
            NewNormalizedName = dto.NewNormalizedName,
        };
    }

    private static ProposedEntryState ToDomain(EntryStateDto dto) => new(
        dto.StockEntryId is { } id ? new StockEntryId(id) : null,
        dto.Name,
        dto.NormalizedName,
        new UnitId(dto.UnitId),
        dto.UnitCanonicalName,
        dto.LocationId is { } locationId ? new LocationId(locationId) : null,
        dto.LocationName,
        dto.Note,
        ParseQuantity(dto.PreviousQuantity),
        ParseQuantity(dto.ResultingQuantity),
        dto.Retired);

    private static Quantity ParseQuantity(string text) => Quantity.TryParseInvariant(text, out var quantity)
        ? quantity
        : throw new InvalidOperationException("A stored proposal carried an unreadable Quantity.");
}
