namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One recorded administration change. Semantic facts only: what was done, to which identity, under
/// which name - never a version, an audit identity, or SQL detail.
/// </summary>
public sealed class ReferenceEffectEntity
{
    public Guid Id { get; set; }

    public Guid OperationId { get; set; }

    /// <summary>1-based position within the change set, so a replay re-reports it in the order the Participant reviewed.</summary>
    public int Order { get; set; }

    /// <summary>The <c>ReferenceChangeKind</c> as machine text (for example <c>retire_unit</c>).</summary>
    public required string Kind { get; set; }

    /// <summary>The <c>ReferenceKind</c> as text.</summary>
    public required string ReferenceKind { get; set; }

    public Guid ReferenceId { get; set; }

    /// <summary>The reference's display name at the moment the change was applied.</summary>
    public required string Name { get; set; }

    /// <summary>The exact new display name a rename applied, or null.</summary>
    public string? NewName { get; set; }

    /// <summary>The single alias an alias add established or an alias removal ended, or null.</summary>
    public string? Alias { get; set; }

    /// <summary>The initial aliases a Unit creation established, as a JSON array of strings; null for every other kind.</summary>
    public string? AliasesJson { get; set; }
}
