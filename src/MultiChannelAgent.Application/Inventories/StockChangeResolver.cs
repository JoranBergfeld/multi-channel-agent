using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How one requested change turned out when it met current state.</summary>
public enum StockChangeResolutionKind
{
    /// <summary>Decided exactly, and ready to be applied or proposed.</summary>
    Resolved,

    /// <summary>The reference matched several Stock Entries; candidates are offered rather than one being guessed.</summary>
    Ambiguous,

    /// <summary>Nothing matched - or nothing the requester may know exists.</summary>
    NotFound,

    /// <summary>A named Unit or Location does not exist in this Inventory. It is never created implicitly.</summary>
    ReferenceNotFound,

    /// <summary>The change conflicts with current Stock: an underflow, a no-op, or Forget on Stock still on hand.</summary>
    Conflict,

    /// <summary>The change itself could not be understood or was out of bounds.</summary>
    Invalid,
}

/// <summary>
/// One requested change, resolved. On success it carries the exactly-decided
/// <see cref="ProposedChange"/> plus the expected versions and (when it creates Stock) the expected
/// absence it was decided against - everything a proposal needs and everything an executor needs.
/// </summary>
public sealed record StockChangeResolution(
    StockChangeResolutionKind Kind,
    string Code,
    ProposedChange? Change = null,
    IReadOnlyList<ExpectedEntryVersion>? ExpectedVersions = null,
    ExpectedEquivalentStockAbsence? ExpectedAbsence = null,
    StockFindView? Candidates = null,
    StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// Turns one untrusted <see cref="StockChangeRequest"/> into one exactly-decided
/// <see cref="ProposedChange"/>, or into one typed refusal.
///
/// It resolves targets through the very same deterministic matching Find uses, so a change can never
/// act on a reference Find would have called ambiguous; it resolves Unit and Location references
/// exactly, never creating one; it plans with <see cref="StockChangePlan"/>, so the arithmetic and
/// the risk rules stay pure; and it reads the versions of every existing row it touches, so what it
/// decides can be pinned to the state it decided against.
///
/// It authorizes nothing and writes nothing: callers reach it only after
/// <see cref="InventoryAuthorizationService"/> has authorized them for this Inventory, and only with
/// an InventoryId from trusted context.
/// </summary>
public sealed class StockChangeResolver(IStockStore stockStore, IInventoryReferenceStore referenceStore)
{
    /// <summary>The reserved Unit every Inventory starts with; a change that names no Unit creates against it.</summary>
    public const string ReservedEachUnitName = "each";

    public async Task<StockChangeResolution> ResolveAsync(
        InventoryId inventoryId, StockChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Exact Unit/Location narrowing for the target.
        UnitId? unitId = null;
        if (request.UnitReference is { } unitReference)
        {
            unitId = await referenceStore.ResolveUnitAsync(inventoryId, unitReference, cancellationToken);
            if (unitId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }
        }

        LocationId? locationId = null;
        if (request.LocationReference is { } locationReference)
        {
            locationId = await referenceStore.ResolveLocationAsync(inventoryId, locationReference, cancellationToken);
            if (locationId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Location);
            }
        }

        // 2. The target, resolved exactly as Find would.
        StockFindQuery query;
        try
        {
            query = Guid.TryParse(request.Reference, out var stockEntryId)
                ? StockFindQuery.ById(inventoryId, new StockEntryId(stockEntryId))
                : StockFindQuery.ByName(inventoryId, request.Reference, unitId, locationId, request.UnlocatedOnly);
        }
        catch (ArgumentException)
        {
            return Invalid("invalid_reference");
        }

        var matches = await stockStore.FindMatchesAsync(query, StockFindingService.MaxCandidates + 1, cancellationToken);
        var outcome = StockFindOutcome.FromMatches(matches);

        if (outcome.Kind == StockFindOutcomeKind.Ambiguous)
        {
            var facets = await stockStore.SummarizeMatchFacetsAsync(query, StockFindingService.MaxCandidates, cancellationToken);

            return new StockChangeResolution(
                StockChangeResolutionKind.Ambiguous,
                "ambiguous",
                Candidates: new StockFindView(
                    outcome.Candidates.Select(StockListingService.ToRowView).ToList(),
                    outcome.HasMoreCandidates,
                    StockNarrowingHints.FromFacets(facets)));
        }

        var target = outcome.Kind == StockFindOutcomeKind.Completed ? outcome.Candidates[0] : null;

        return request.Kind switch
        {
            StockMutationKind.Add or StockMutationKind.Remove or StockMutationKind.Set =>
                await ResolveQuantityAsync(inventoryId, request, query, target, unitId, locationId, cancellationToken),
            StockMutationKind.Move => await ResolveMoveAsync(inventoryId, request, target, cancellationToken),
            StockMutationKind.Rename => await ResolveRenameAsync(inventoryId, request, target, cancellationToken),
            StockMutationKind.Forget => await ResolveForgetAsync(inventoryId, request, target, cancellationToken),
            _ => Invalid("invalid_change"),
        };
    }

    private async Task<StockChangeResolution> ResolveQuantityAsync(
        InventoryId inventoryId,
        StockChangeRequest request,
        StockFindQuery query,
        StockEntrySummary? target,
        UnitId? unitId,
        LocationId? locationId,
        CancellationToken cancellationToken)
    {
        if (!Quantity.TryParseInvariant(request.QuantityText, out var amount))
        {
            return Invalid("invalid_quantity");
        }

        var proposedNote = Trimmed(request.Note);
        if (proposedNote is { Length: > StockEntry.MaxNoteLength })
        {
            return Invalid("invalid_note");
        }

        var plan = StockChangePlan.ForQuantity(request.Kind, target?.Quantity, amount);
        if (plan.Outcome != StockChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        if (plan.Effect == StockChangeEffectKind.Created)
        {
            // An opaque identity that matched nothing names no Stock Entry to create - it names one
            // that is simply not here.
            if (query.NormalizedNameReference is null)
            {
                return NotFound();
            }

            var name = Trimmed(request.Reference);
            if (name is null or { Length: > StockEntry.MaxNameLength })
            {
                return Invalid("invalid_name");
            }

            // A blank Unit means the reserved `each` every Inventory starts with - never a Unit
            // invented on the Participant's behalf.
            var newUnitId = unitId ?? await referenceStore.ResolveUnitAsync(inventoryId, ReservedEachUnitName, cancellationToken);
            if (newUnitId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }

            // The authoritative names, not the text the request used: a proposal that showed an alias
            // (or a Location spelled differently) would not be showing what the Inventory holds.
            var unitCanonicalName = await referenceStore.FindUnitCanonicalNameAsync(inventoryId, newUnitId.Value, cancellationToken);
            if (unitCanonicalName is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }

            string? newLocationName = null;
            if (locationId is { } placement)
            {
                newLocationName = await referenceStore.FindLocationNameAsync(inventoryId, placement, cancellationToken);
                if (newLocationName is null)
                {
                    return ReferenceNotFound(StockReferenceKind.Location);
                }
            }

            var normalizedName = NameNormalization.Normalize(name);
            var change = new ProposedChange
            {
                Order = request.Order,
                Kind = request.Kind,
                Effect = StockChangeEffectKind.Created,
                Source = new ProposedEntryState(
                    StockEntryId: null,
                    name,
                    normalizedName,
                    newUnitId.Value,
                    unitCanonicalName,
                    locationId,
                    newLocationName,
                    proposedNote,
                    Quantity.Zero,
                    plan.SourceResultingQuantity,
                    Retired: false),
            };

            return new StockChangeResolution(
                StockChangeResolutionKind.Resolved,
                "resolved",
                change,
                [],
                new ExpectedEquivalentStockAbsence(normalizedName, newUnitId.Value, locationId));
        }

        // A quantity change never rewrites an existing Stock Entry's Note, so the target's own Note
        // is carried through untouched and the proposed one is deliberately not applied.
        var source = StateOf(target!, plan.SourceResultingQuantity, retired: false);
        var resolved = new ProposedChange
        {
            Order = request.Order,
            Kind = request.Kind,
            Effect = plan.Effect,
            Source = source,
        };

        return await VersionedAsync(inventoryId, resolved, [target!.Id], cancellationToken);
    }

    private async Task<StockChangeResolution> ResolveMoveAsync(
        InventoryId inventoryId, StockChangeRequest request, StockEntrySummary? target, CancellationToken cancellationToken)
    {
        if (request.DestinationUnlocated == (request.DestinationLocationReference is not null))
        {
            return Invalid("invalid_destination");
        }

        if (request.MoveAll == (request.QuantityText is not null))
        {
            return Invalid("invalid_quantity");
        }

        Quantity? requestedAmount = null;
        if (!request.MoveAll)
        {
            if (!Quantity.TryParseInvariant(request.QuantityText, out var stated))
            {
                return Invalid("invalid_quantity");
            }

            requestedAmount = stated;
        }

        LocationId? destinationLocationId = null;
        string? destinationLocationName = null;
        if (request.DestinationLocationReference is { } destinationReference)
        {
            destinationLocationId = await referenceStore.ResolveLocationAsync(inventoryId, destinationReference, cancellationToken);
            if (destinationLocationId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Location);
            }

            destinationLocationName = await referenceStore.FindLocationNameAsync(
                inventoryId, destinationLocationId.Value, cancellationToken);

            if (destinationLocationName is null)
            {
                return ReferenceNotFound(StockReferenceKind.Location);
            }
        }

        if (target is null)
        {
            return NotFound();
        }

        var samePlacement = target.LocationId == destinationLocationId;

        // Equivalent Stock at the destination: the same normalized name and Unit, at the destination
        // placement. Resolved through the same matching everything else uses, so "what is already
        // there" is never a guess.
        var destination = samePlacement
            ? null
            : await FindEquivalentAsync(inventoryId, target.NormalizedName, target.UnitId, destinationLocationId, cancellationToken);

        var plan = StockChangePlan.ForMove(target.Quantity, requestedAmount, samePlacement, destination?.Quantity);
        if (plan.Outcome != StockChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var source = StateOf(target, plan.SourceResultingQuantity, plan.RetiresSource);
        var destinationState = plan.Effect switch
        {
            // The Stock Entry itself relocates, so the "destination" is that same entry, at the new
            // placement, carrying the same amount.
            StockChangeEffectKind.Placed => source with { LocationId = destinationLocationId, LocationName = destinationLocationName },

            // A new Stock Entry is created at the destination. It inherits the source's Note, because
            // a split must not lose the distinction the Note was recording.
            StockChangeEffectKind.Split => new ProposedEntryState(
                StockEntryId: null,
                target.Name,
                target.NormalizedName,
                target.UnitId,
                target.UnitCanonicalName,
                destinationLocationId,
                destinationLocationName,
                target.Note,
                Quantity.Zero,
                plan.DestinationResultingQuantity,
                Retired: false),
            _ => StateOf(destination!, plan.DestinationResultingQuantity, retired: false),
        };

        var change = new ProposedChange
        {
            Order = request.Order,
            Kind = StockMutationKind.Move,
            Effect = plan.Effect,
            Source = source,
            Destination = destinationState,
            TransferredQuantity = plan.TransferredQuantity,
        };

        // Split creates a Stock Entry at the destination, and Placed relocates this one into it. Both
        // land on a placement that must still hold no Equivalent Stock, so both pin it - otherwise a
        // competing writer who fills it turns a clean conflict into a uniqueness violation mid-write.
        if (plan.Effect is StockChangeEffectKind.Split or StockChangeEffectKind.Placed)
        {
            var versions = await ReadVersionsAsync(inventoryId, [target.Id], cancellationToken);
            if (versions is null)
            {
                return Conflict("state_changed");
            }

            return new StockChangeResolution(
                StockChangeResolutionKind.Resolved,
                "resolved",
                change,
                versions,
                new ExpectedEquivalentStockAbsence(target.NormalizedName, target.UnitId, destinationLocationId));
        }

        var touched = destination is null ? new[] { target.Id } : [target.Id, destination.Id];

        return await VersionedAsync(inventoryId, change, touched, cancellationToken);
    }

    private async Task<StockChangeResolution> ResolveRenameAsync(
        InventoryId inventoryId, StockChangeRequest request, StockEntrySummary? target, CancellationToken cancellationToken)
    {
        var newName = Trimmed(request.NewName);
        if (newName is null or { Length: > StockEntry.MaxNameLength })
        {
            return Invalid("invalid_name");
        }

        if (target is null)
        {
            return NotFound();
        }

        var newNormalizedName = NameNormalization.Normalize(newName);

        // A collision is Equivalent Stock: the new normalized name at this entry's own Unit and
        // Location. A different placement is a different Stock Entry, and renaming into it is not a
        // collision at all.
        var colliding = newNormalizedName == target.NormalizedName
            ? null
            : await FindEquivalentAsync(inventoryId, newNormalizedName, target.UnitId, target.LocationId, cancellationToken);

        var plan = StockChangePlan.ForRename(target.Name, newName, target.NormalizedName, target.Quantity, colliding?.Quantity);
        if (plan.Outcome != StockChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedChange
        {
            Order = request.Order,
            Kind = StockMutationKind.Rename,
            Effect = plan.Effect,
            Source = StateOf(target, plan.SourceResultingQuantity, plan.RetiresSource),
            Destination = colliding is null ? null : StateOf(colliding, plan.DestinationResultingQuantity, retired: false),
            TransferredQuantity = plan.TransferredQuantity,
            NewName = newName,
            NewNormalizedName = newNormalizedName,
        };

        if (colliding is null)
        {
            // The entry keeps its identity and moves to a name that must still be free at this Unit
            // and Location, so that key is pinned for exactly the same reason a Placed Move pins its
            // destination.
            var versions = await ReadVersionsAsync(inventoryId, [target.Id], cancellationToken);

            return versions is null
                ? Conflict("state_changed")
                : new StockChangeResolution(
                    StockChangeResolutionKind.Resolved,
                    "resolved",
                    change,
                    versions,
                    new ExpectedEquivalentStockAbsence(newNormalizedName, target.UnitId, target.LocationId));
        }

        return await VersionedAsync(inventoryId, change, [target.Id, colliding.Id], cancellationToken);
    }

    private async Task<StockChangeResolution> ResolveForgetAsync(
        InventoryId inventoryId, StockChangeRequest request, StockEntrySummary? target, CancellationToken cancellationToken)
    {
        if (target is null)
        {
            return NotFound();
        }

        var plan = StockChangePlan.ForForget(target.Quantity);
        if (plan.Outcome != StockChangePlanOutcome.Planned)
        {
            return Refused(plan.Outcome);
        }

        var change = new ProposedChange
        {
            Order = request.Order,
            Kind = StockMutationKind.Forget,
            Effect = StockChangeEffectKind.Forgotten,
            Source = StateOf(target, Quantity.Zero, retired: true),
        };

        return await VersionedAsync(inventoryId, change, [target.Id], cancellationToken);
    }

    private async Task<StockEntrySummary?> FindEquivalentAsync(
        InventoryId inventoryId, string normalizedName, UnitId unitId, LocationId? locationId, CancellationToken cancellationToken)
    {
        var query = StockFindQuery.ByName(inventoryId, normalizedName, unitId, locationId, unlocatedOnly: locationId is null);

        // Equivalent Stock is unique, so at most one row can match. Asking for two makes an
        // impossible second row loud rather than silently ignored.
        var matches = await stockStore.FindMatchesAsync(query, 2, cancellationToken);

        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<StockChangeResolution> VersionedAsync(
        InventoryId inventoryId, ProposedChange change, IReadOnlyList<StockEntryId> touched, CancellationToken cancellationToken)
    {
        var versions = await ReadVersionsAsync(inventoryId, touched, cancellationToken);

        return versions is null
            ? Conflict("state_changed")
            : new StockChangeResolution(StockChangeResolutionKind.Resolved, "resolved", change, versions);
    }

    /// <summary>
    /// The versions of every row this change will write to, or null when one of them has already
    /// vanished - in which case the decision was made against a state nobody holds any more, and
    /// proposing it would be proposing something that can never commit.
    /// </summary>
    private async Task<IReadOnlyList<ExpectedEntryVersion>?> ReadVersionsAsync(
        InventoryId inventoryId, IReadOnlyList<StockEntryId> touched, CancellationToken cancellationToken)
    {
        var versions = await stockStore.ReadVersionsAsync(inventoryId, touched, cancellationToken);

        return versions.Count == touched.Distinct().Count()
            ? versions.Select(version => new ExpectedEntryVersion(version.StockEntryId, version.ConcurrencyStamp)).ToList()
            : null;
    }

    private static ProposedEntryState StateOf(StockEntrySummary row, Quantity resultingQuantity, bool retired) => new(
        row.Id,
        row.Name,
        row.NormalizedName,
        row.UnitId,
        row.UnitCanonicalName,
        row.LocationId,
        row.LocationName,
        row.Note,
        row.Quantity,
        resultingQuantity,
        retired);

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static StockChangeResolution Refused(StockChangePlanOutcome outcome) => outcome switch
    {
        StockChangePlanOutcome.TargetRequired => NotFound(),
        StockChangePlanOutcome.InvalidAmount => Invalid("invalid_quantity"),
        StockChangePlanOutcome.OutOfBounds => Invalid("quantity_out_of_bounds"),
        StockChangePlanOutcome.Underflow => Conflict("insufficient_quantity"),
        StockChangePlanOutcome.NoChange => Conflict("no_change"),
        StockChangePlanOutcome.ForgetRequiresZeroQuantity => Conflict("forget_requires_zero_quantity"),
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled stock change plan outcome."),
    };

    private static StockChangeResolution NotFound() => new(StockChangeResolutionKind.NotFound, "not_found");

    private static StockChangeResolution Invalid(string code) => new(StockChangeResolutionKind.Invalid, code);

    private static StockChangeResolution Conflict(string code) => new(StockChangeResolutionKind.Conflict, code);

    private static StockChangeResolution ReferenceNotFound(StockReferenceKind reference) =>
        new(StockChangeResolutionKind.ReferenceNotFound, "reference_not_found", UnresolvedReference: reference);
}
