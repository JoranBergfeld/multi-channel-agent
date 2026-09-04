using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// Serializes and reads the exact entries a stored import will create.
///
/// Quantity crosses as invariant decimal text and every identity as its Guid text, exactly as the
/// shipped <see cref="ConfirmationProposalMapper"/> does. <see cref="SchemaVersion"/> is written so a
/// later shape change is detected rather than silently mis-read: an import proposal is only ever ten
/// minutes old, so a row this process cannot read is a deployment mistake, not a migration case.
/// </summary>
internal static class ImportProposalMapper
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new();

    private sealed record EntryDto(
        int LineNumber,
        IReadOnlyList<int> SourceLineNumbers,
        string Name,
        string NormalizedName,
        string Quantity,
        Guid UnitId,
        string UnitCanonicalName,
        Guid? LocationId,
        string? LocationName,
        string? Note);

    private sealed record EntriesEnvelope(int Version, IReadOnlyList<EntryDto> Entries);

    public static ImportProposalEntity ToEntity(ImportProposal proposal) => new()
    {
        ProposalId = proposal.Id.Value,
        TokenHash = proposal.TokenHash.Value,
        ParticipantId = proposal.ParticipantId.Value,
        InventoryId = proposal.InventoryId.Value,
        FileDigest = proposal.FileDigest.Value,
        Status = nameof(ImportProposalStatus.Pending),
        EntriesJson = JsonSerializer.Serialize(
            new EntriesEnvelope(SchemaVersion, [.. proposal.Entries.Select(ToDto)]), Options),
        ExpectedStockEntryCount = proposal.EmptyStateVersion.ExpectedStockEntryCount,
        CreatedAt = proposal.CreatedAt,
        ExpiresAt = proposal.ExpiresAt,
        ExpiresAtTicks = proposal.ExpiresAt.UtcTicks,
        SettledAt = null,
        SettledAtTicks = null,
    };

    public static ImportProposal ToDomain(ImportProposalEntity entity)
    {
        var envelope = JsonSerializer.Deserialize<EntriesEnvelope>(entity.EntriesJson, Options)
            ?? throw new InvalidOperationException("A stored import proposal carried no entries.");

        if (envelope.Version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"A stored import proposal uses unsupported schema version {envelope.Version}.");
        }

        if (envelope.Entries is null)
        {
            throw new InvalidOperationException("A stored import proposal carried no entries.");
        }

        if (!FileDigest.TryParse(entity.FileDigest, out var digest))
        {
            throw new InvalidOperationException("A stored import proposal carried an unreadable file digest.");
        }

        return new ImportProposal
        {
            Id = new ImportProposalId(entity.ProposalId),
            TokenHash = new ConfirmationTokenHash(entity.TokenHash),
            ParticipantId = new ParticipantId(entity.ParticipantId),
            InventoryId = new InventoryId(entity.InventoryId),
            FileDigest = digest,
            Entries = [.. envelope.Entries.Select(ToDomain)],
            EmptyStateVersion = new EmptyStateVersion(entity.ExpectedStockEntryCount),
            CreatedAt = entity.CreatedAt,
        };
    }

    private static EntryDto ToDto(ImportEntry entry) => new(
        entry.LineNumber,
        entry.SourceLineNumbers,
        entry.Name,
        entry.NormalizedName,
        entry.Quantity.ToInvariantText(),
        entry.UnitId.Value,
        entry.UnitCanonicalName,
        entry.LocationId?.Value,
        entry.LocationName,
        entry.Note);

    private static ImportEntry ToDomain(EntryDto dto)
    {
        if (!Quantity.TryParseInvariant(dto.Quantity, out var quantity))
        {
            throw new InvalidOperationException("A stored import proposal carried an unreadable Quantity.");
        }

        return new ImportEntry
        {
            LineNumber = dto.LineNumber,
            SourceLineNumbers = dto.SourceLineNumbers,
            Name = dto.Name,
            NormalizedName = dto.NormalizedName,
            Quantity = quantity,
            UnitId = new UnitId(dto.UnitId),
            UnitCanonicalName = dto.UnitCanonicalName,
            LocationId = dto.LocationId is { } locationId ? new LocationId(locationId) : null,
            LocationName = dto.LocationName,
            Note = dto.Note,
        };
    }
}
