namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>The exact uploaded bytes, retained only for the lifetime of their proposal.</summary>
public sealed class ImportUploadEntity
{
    public Guid ProposalId { get; set; }

    public required byte[] Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
