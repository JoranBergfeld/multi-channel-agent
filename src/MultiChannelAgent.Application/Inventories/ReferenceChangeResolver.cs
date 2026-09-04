using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How one requested administration change turned out when it met current state.</summary>
public enum ReferenceChangeResolutionKind
{
    /// <summary>Decided exactly, and ready to be applied or proposed.</summary>
    Resolved,

    /// <summary>The named term is not an active alias of that Unit.</summary>
    NotFound,

    /// <summary>The named Unit or Location does not exist here, or is retired. Bounded deterministic suggestions accompany it.</summary>
    ReferenceNotFound,

    /// <summary>The change conflicts with current reference data: a taken term, a reserved rule, a no-op, or stock still referencing it.</summary>
    Conflict,

    /// <summary>The change itself could not be understood or was out of bounds.</summary>
    Invalid,
}

/// <summary>
/// One requested administration change, resolved. On success it carries the exactly-decided
/// <see cref="ProposedReferenceChange"/> plus the expected versions and expected term absences it was
/// decided against - everything a proposal needs and everything an executor needs.
/// </summary>
public sealed record ReferenceChangeResolution(
    ReferenceChangeResolutionKind Kind,
    string Code,
    ProposedReferenceChange? Change = null,
    IReadOnlyList<ExpectedReferenceVersion>? ExpectedVersions = null,
    IReadOnlyList<ExpectedTermAbsence>? ExpectedAbsences = null,
    ReferenceKind? UnresolvedReference = null,
    IReadOnlyList<string>? Suggestions = null);

/// <summary>
/// Turns one untrusted <see cref="ReferenceChangeRequest"/> into one exactly-decided
/// <see cref="ProposedReferenceChange"/>, or into one typed refusal.
///
/// It resolves references through the very same exact, active-only
/// <see cref="IInventoryReferenceStore"/> every stock tool uses, so a reference means the same thing
/// everywhere; it decides nothing itself, delegating every rule to
/// <see cref="ReferenceChangePlan"/>; and it reads the version of every existing reference it
/// touches, so what it decides can be pinned to the state it decided against.
///
/// It authorizes nothing and writes nothing: callers reach it only after
/// <see cref="InventoryAuthorizationService"/> has authorized them for this Inventory, and only with
/// an InventoryId from trusted context.
/// </summary>
public sealed class ReferenceChangeResolver(IReferenceCatalogStore catalogStore, IInventoryReferenceStore referenceStore)
{
    public async Task<ReferenceChangeResolution> ResolveAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Kind switch
        {
            ReferenceChangeKind.CreateUnit => await ResolveCreateUnitAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.RenameUnit => await ResolveRenameUnitAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.AddUnitAlias => await ResolveAddAliasAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.RemoveUnitAlias => await ResolveRemoveAliasAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.RetireUnit => await ResolveRetireUnitAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.CreateLocation => await ResolveCreateLocationAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.RenameLocation => await ResolveRenameLocationAsync(inventoryId, request, cancellationToken),
            ReferenceChangeKind.RetireLocation => await ResolveRetireLocationAsync(inventoryId, request, cancellationToken),
            _ => Invalid("invalid_changes"),
        };
    }

    private async Task<ReferenceChangeResolution> ResolveCreateUnitAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var activeTerms = await catalogStore.ReadActiveUnitTermsAsync(inventoryId, excluding: null, cancellationToken);
        var plan = ReferenceChangePlan.ForCreateUnit(request.Name, request.Aliases, activeTerms);

        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        // The identity is minted here, at proposal time, so confirming creates exactly the Unit that
        // was reviewed rather than a fresh one nobody saw.
        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.CreateUnit,
            Target = new ProposedReferenceState(
                ReferenceKind.Unit, Guid.NewGuid(), plan.DisplayName, plan.NormalizedName, Reserved: false),
            Terms = plan.Terms,
        };

        return Resolved(
            change,
            [],
            plan.Terms.Select(term => new ExpectedTermAbsence(ReferenceKind.Unit, term.NormalizedTerm)).ToList());
    }

    private async Task<ReferenceChangeResolution> ResolveRenameUnitAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var found = await FindUnitAsync(inventoryId, request.Reference, cancellationToken);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var unit = found.Unit!;

        // A rename must not collide with any other active term, including this Unit's own aliases -
        // promoting an alias to canonical would be a reference merge, and merging is out of scope.
        // Only the Unit's own canonical term is excluded, so renaming it in display form only stays
        // a display change.
        var otherTerms = await catalogStore.ReadActiveUnitTermsAsync(inventoryId, unit.Id, cancellationToken);
        var plan = ReferenceChangePlan.ForRenameUnit(
            unit.IsReserved, unit.CanonicalName, unit.NormalizedCanonicalName, request.NewName, otherTerms);

        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.RenameUnit,
            Target = Target(unit),
            NewName = plan.DisplayName,
            NewNormalizedName = plan.NormalizedName,
        };

        // A display-only rename claims nothing: the normalized term it would occupy is the one it
        // already holds.
        var absences = plan.NormalizedName == unit.NormalizedCanonicalName
            ? new List<ExpectedTermAbsence>()
            : [new ExpectedTermAbsence(ReferenceKind.Unit, plan.NormalizedName)];

        return Resolved(change, [Version(unit)], absences);
    }

    private async Task<ReferenceChangeResolution> ResolveAddAliasAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var found = await FindUnitAsync(inventoryId, request.Reference, cancellationToken);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var unit = found.Unit!;

        // The whole namespace, including this Unit's own terms: a term the Unit already answers to is
        // caught first, as a no-op, so passing them in cannot mis-report one as another Unit's.
        var activeTerms = await catalogStore.ReadActiveUnitTermsAsync(inventoryId, excluding: null, cancellationToken);
        var plan = ReferenceChangePlan.ForAddUnitAlias(request.Alias, unit.Terms, activeTerms);

        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.AddUnitAlias,
            Target = Target(unit),
            Term = plan.Term,
        };

        return Resolved(change, [Version(unit)], [new ExpectedTermAbsence(ReferenceKind.Unit, plan.Term!.NormalizedTerm)]);
    }

    private async Task<ReferenceChangeResolution> ResolveRemoveAliasAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var found = await FindUnitAsync(inventoryId, request.Reference, cancellationToken);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var unit = found.Unit!;
        var plan = ReferenceChangePlan.ForRemoveUnitAlias(request.Alias, unit.Terms);

        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.RemoveUnitAlias,
            Target = Target(unit),
            Term = plan.Term,
        };

        return Resolved(change, [Version(unit)], []);
    }

    private async Task<ReferenceChangeResolution> ResolveRetireUnitAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var found = await FindUnitAsync(inventoryId, request.Reference, cancellationToken);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var unit = found.Unit!;
        var references = await catalogStore.CountStockReferencesAsync(
            inventoryId, ReferenceKind.Unit, unit.Id.Value, cancellationToken);

        var plan = ReferenceChangePlan.ForRetireUnit(unit.IsReserved, references);
        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.RetireUnit,
            Target = Target(unit),
        };

        return Resolved(change, [Version(unit)], []);
    }

    private async Task<ReferenceChangeResolution> ResolveCreateLocationAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var activeNames = await catalogStore.ReadActiveLocationNamesAsync(inventoryId, excluding: null, cancellationToken);
        var plan = ReferenceChangePlan.ForCreateLocation(request.Name, activeNames);

        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.CreateLocation,
            Target = new ProposedReferenceState(
                ReferenceKind.Location, Guid.NewGuid(), plan.DisplayName, plan.NormalizedName, Reserved: false),
        };

        return Resolved(change, [], [new ExpectedTermAbsence(ReferenceKind.Location, plan.NormalizedName)]);
    }

    private async Task<ReferenceChangeResolution> ResolveRenameLocationAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var found = await FindLocationAsync(inventoryId, request.Reference, cancellationToken);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var location = found.Location!;
        var otherNames = await catalogStore.ReadActiveLocationNamesAsync(inventoryId, location.Id, cancellationToken);
        var plan = ReferenceChangePlan.ForRenameLocation(
            location.Name, location.NormalizedName, request.NewName, otherNames);

        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.RenameLocation,
            Target = Target(location),
            NewName = plan.DisplayName,
            NewNormalizedName = plan.NormalizedName,
        };

        var absences = plan.NormalizedName == location.NormalizedName
            ? new List<ExpectedTermAbsence>()
            : [new ExpectedTermAbsence(ReferenceKind.Location, plan.NormalizedName)];

        return Resolved(change, [Version(location)], absences);
    }

    private async Task<ReferenceChangeResolution> ResolveRetireLocationAsync(
        InventoryId inventoryId, ReferenceChangeRequest request, CancellationToken cancellationToken)
    {
        var found = await FindLocationAsync(inventoryId, request.Reference, cancellationToken);
        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var location = found.Location!;
        var references = await catalogStore.CountStockReferencesAsync(
            inventoryId, ReferenceKind.Location, location.Id.Value, cancellationToken);

        var plan = ReferenceChangePlan.ForRetireLocation(references);
        if (plan.Outcome != ReferenceChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedReferenceChange
        {
            Order = request.Order,
            Kind = ReferenceChangeKind.RetireLocation,
            Target = Target(location),
        };

        return Resolved(change, [Version(location)], []);
    }

    private async Task<(UnitCatalogRecord? Unit, ReferenceChangeResolution? Refusal)> FindUnitAsync(
        InventoryId inventoryId, string? reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, Invalid("invalid_reference"));
        }

        var unitId = await referenceStore.ResolveUnitAsync(inventoryId, reference, cancellationToken);
        if (unitId is null)
        {
            return (null, await ReferenceNotFoundAsync(inventoryId, ReferenceKind.Unit, reference, cancellationToken));
        }

        var unit = await catalogStore.FindUnitAsync(inventoryId, unitId.Value, cancellationToken);

        // Resolution and the catalog read are two statements, so a Unit retired between them is
        // simply gone - which is the same answer as never having existed.
        return unit is null
            ? (null, await ReferenceNotFoundAsync(inventoryId, ReferenceKind.Unit, reference, cancellationToken))
            : (unit, null);
    }

    private async Task<(LocationCatalogRecord? Location, ReferenceChangeResolution? Refusal)> FindLocationAsync(
        InventoryId inventoryId, string? reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return (null, Invalid("invalid_reference"));
        }

        var locationId = await referenceStore.ResolveLocationAsync(inventoryId, reference, cancellationToken);
        if (locationId is null)
        {
            return (null, await ReferenceNotFoundAsync(inventoryId, ReferenceKind.Location, reference, cancellationToken));
        }

        var location = await catalogStore.FindLocationAsync(inventoryId, locationId.Value, cancellationToken);

        return location is null
            ? (null, await ReferenceNotFoundAsync(inventoryId, ReferenceKind.Location, reference, cancellationToken))
            : (location, null);
    }

    private async Task<ReferenceChangeResolution> ReferenceNotFoundAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken) =>
        new(
            ReferenceChangeResolutionKind.ReferenceNotFound,
            "reference_not_found",
            UnresolvedReference: kind,
            Suggestions: await catalogStore.SuggestAsync(inventoryId, kind, reference, cancellationToken));

    private static ProposedReferenceState Target(UnitCatalogRecord unit) =>
        new(ReferenceKind.Unit, unit.Id.Value, unit.CanonicalName, unit.NormalizedCanonicalName, unit.IsReserved);

    private static ProposedReferenceState Target(LocationCatalogRecord location) =>
        new(ReferenceKind.Location, location.Id.Value, location.Name, location.NormalizedName, Reserved: false);

    private static ExpectedReferenceVersion Version(UnitCatalogRecord unit) =>
        new(ReferenceKind.Unit, unit.Id.Value, unit.ConcurrencyStamp);

    private static ExpectedReferenceVersion Version(LocationCatalogRecord location) =>
        new(ReferenceKind.Location, location.Id.Value, location.ConcurrencyStamp);

    private static ReferenceChangeResolution Resolved(
        ProposedReferenceChange change,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences) =>
        new(ReferenceChangeResolutionKind.Resolved, "resolved", change, versions, absences);

    private static ReferenceChangeResolution Invalid(string code) => new(ReferenceChangeResolutionKind.Invalid, code);

    /// <summary>The one mapping from a refused plan to the typed status and machine code it is answered with.</summary>
    private static ReferenceChangeResolution Refused(ReferenceChangePlanOutcome outcome) => outcome switch
    {
        ReferenceChangePlanOutcome.InvalidName => new(ReferenceChangeResolutionKind.Invalid, "invalid_name"),
        ReferenceChangePlanOutcome.TermInUse => new(ReferenceChangeResolutionKind.Conflict, "term_in_use"),
        ReferenceChangePlanOutcome.NameInUse => new(ReferenceChangeResolutionKind.Conflict, "name_in_use"),
        ReferenceChangePlanOutcome.NoChange => new(ReferenceChangeResolutionKind.Conflict, "no_change"),
        ReferenceChangePlanOutcome.ReservedUnit => new(ReferenceChangeResolutionKind.Conflict, "reserved_unit"),
        ReferenceChangePlanOutcome.ReservedTerm => new(ReferenceChangeResolutionKind.Conflict, "reserved_term"),
        ReferenceChangePlanOutcome.CanonicalTerm => new(ReferenceChangeResolutionKind.Conflict, "canonical_term"),
        ReferenceChangePlanOutcome.AliasNotFound => new(ReferenceChangeResolutionKind.NotFound, "alias_not_found"),
        ReferenceChangePlanOutcome.ReferenceInUse => new(ReferenceChangeResolutionKind.Conflict, "reference_in_use"),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled reference change plan outcome."),
    };
}
