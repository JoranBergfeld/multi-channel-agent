namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Which kind of Inventory-owned reference a change administers.</summary>
public enum ReferenceKind
{
    Unit,
    Location,
}

/// <summary>
/// Exactly what one administration change does. There is deliberately no separate "effect" enum
/// beside this one: every kind has exactly one effect, so a second enum whose members mapped
/// one-to-one would be two things to keep in step and no extra expressiveness.
///
/// This vocabulary is simultaneously what a tool argument names, what the ledger records, what the
/// audit fact reports, what decides whether a change must be confirmed, and what decides which role
/// may ask for it - so none of those five can drift apart.
/// </summary>
public enum ReferenceChangeKind
{
    /// <summary>Creates a Unit with a canonical name and an ordered set of initial aliases.</summary>
    CreateUnit,

    /// <summary>Changes a Unit's canonical name. Its identity, its aliases, and every Stock Entry referencing it are untouched.</summary>
    RenameUnit,

    /// <summary>Adds one non-reserved alias to a Unit's terms.</summary>
    AddUnitAlias,

    /// <summary>Removes one non-reserved, non-canonical alias from a Unit's terms.</summary>
    RemoveUnitAlias,

    /// <summary>Withdraws an unused Unit from matching and assignment while keeping its identity.</summary>
    RetireUnit,

    /// <summary>Creates a flat, alias-free Location.</summary>
    CreateLocation,

    /// <summary>Changes a Location's name. Its identity, and every Stock Entry placed there, are untouched.</summary>
    RenameLocation,

    /// <summary>Withdraws an unused Location from matching and assignment while keeping its identity.</summary>
    RetireLocation,
}

/// <summary>
/// The one mapping from an administration change to its machine text, its reference kind, its
/// minimal semantic audit fact, whether it must be confirmed, and the least role that may ask for
/// it. Every one of those answers is defined exactly once, here.
/// </summary>
public static class ReferenceAdministrationFacts
{
    public static string ToMachineText(ReferenceChangeKind kind) => kind switch
    {
        ReferenceChangeKind.CreateUnit => "create_unit",
        ReferenceChangeKind.RenameUnit => "rename_unit",
        ReferenceChangeKind.AddUnitAlias => "add_unit_alias",
        ReferenceChangeKind.RemoveUnitAlias => "remove_unit_alias",
        ReferenceChangeKind.RetireUnit => "retire_unit",
        ReferenceChangeKind.CreateLocation => "create_location",
        ReferenceChangeKind.RenameLocation => "rename_location",
        ReferenceChangeKind.RetireLocation => "retire_location",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled reference change kind."),
    };

    /// <summary>
    /// Reads stored or recorded machine text. Exact and case-sensitive: text spelled differently is
    /// an unreadable record, not a near-miss to be helpfully corrected.
    /// </summary>
    public static bool TryParse(string? text, out ReferenceChangeKind kind)
    {
        switch (text)
        {
            case "create_unit": kind = ReferenceChangeKind.CreateUnit; return true;
            case "rename_unit": kind = ReferenceChangeKind.RenameUnit; return true;
            case "add_unit_alias": kind = ReferenceChangeKind.AddUnitAlias; return true;
            case "remove_unit_alias": kind = ReferenceChangeKind.RemoveUnitAlias; return true;
            case "retire_unit": kind = ReferenceChangeKind.RetireUnit; return true;
            case "create_location": kind = ReferenceChangeKind.CreateLocation; return true;
            case "rename_location": kind = ReferenceChangeKind.RenameLocation; return true;
            case "retire_location": kind = ReferenceChangeKind.RetireLocation; return true;
            default: kind = default; return false;
        }
    }

    public static ReferenceKind ReferenceKindFor(ReferenceChangeKind kind) => kind switch
    {
        ReferenceChangeKind.CreateUnit
            or ReferenceChangeKind.RenameUnit
            or ReferenceChangeKind.AddUnitAlias
            or ReferenceChangeKind.RemoveUnitAlias
            or ReferenceChangeKind.RetireUnit => ReferenceKind.Unit,
        ReferenceChangeKind.CreateLocation
            or ReferenceChangeKind.RenameLocation
            or ReferenceChangeKind.RetireLocation => ReferenceKind.Location,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled reference change kind."),
    };

    /// <summary>
    /// The whole single-change confirmation policy, in one predicate: withdrawing a reference from
    /// the Inventory is the only administration act that cannot simply be done again differently. A
    /// change set additionally confirms whenever it carries more than one change, which is the
    /// caller's rule, not this one.
    /// </summary>
    public static bool RequiresConfirmation(ReferenceChangeKind kind) =>
        kind is ReferenceChangeKind.RetireUnit or ReferenceChangeKind.RetireLocation;

    /// <summary>
    /// The least Membership role that may ask for this change. Editor administers non-destructive
    /// reference data; only the Owner retires.
    /// </summary>
    public static MembershipRole RequiredRole(ReferenceChangeKind kind) =>
        RequiresConfirmation(kind) ? MembershipRole.Owner : MembershipRole.Editor;

    public static AuditEventType EventTypeFor(ReferenceChangeKind kind) => kind switch
    {
        ReferenceChangeKind.CreateUnit => AuditEventType.UnitCreated,
        ReferenceChangeKind.RenameUnit => AuditEventType.UnitRenamed,
        ReferenceChangeKind.AddUnitAlias => AuditEventType.UnitAliasAdded,
        ReferenceChangeKind.RemoveUnitAlias => AuditEventType.UnitAliasRemoved,
        ReferenceChangeKind.RetireUnit => AuditEventType.UnitRetired,
        ReferenceChangeKind.CreateLocation => AuditEventType.LocationCreated,
        ReferenceChangeKind.RenameLocation => AuditEventType.LocationRenamed,
        ReferenceChangeKind.RetireLocation => AuditEventType.LocationRetired,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled reference change kind."),
    };

    /// <summary>
    /// The coarse outcome code one applied change is audited under. Never free text, and never the
    /// name, alias, or identity of the reference it changed.
    /// </summary>
    public static string OutcomeCodeFor(ReferenceChangeKind kind) => kind switch
    {
        ReferenceChangeKind.CreateUnit => "Unit:Created",
        ReferenceChangeKind.RenameUnit => "Unit:Renamed",
        ReferenceChangeKind.AddUnitAlias => "Unit:AliasAdded",
        ReferenceChangeKind.RemoveUnitAlias => "Unit:AliasRemoved",
        ReferenceChangeKind.RetireUnit => "Unit:Retired",
        ReferenceChangeKind.CreateLocation => "Location:Created",
        ReferenceChangeKind.RenameLocation => "Location:Renamed",
        ReferenceChangeKind.RetireLocation => "Location:Retired",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled reference change kind."),
    };
}
