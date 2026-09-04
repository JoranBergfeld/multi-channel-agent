namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Whether a change could be decided at all, and if not, why not.</summary>
public enum StockChangePlanOutcome
{
    /// <summary>The change was decided; <see cref="StockChangePlan.Effect"/> says exactly what it does.</summary>
    Planned,

    /// <summary>The change acts on Stock that exists, and nothing here does.</summary>
    TargetRequired,

    /// <summary>An amount was required and what was given is not a positive amount.</summary>
    InvalidAmount,

    /// <summary>A resulting amount could not be stored exactly (see <see cref="Quantity.MaxIntegerDigits"/>).</summary>
    OutOfBounds,

    /// <summary>More was asked for than is on hand. Quantity is never negative, so nothing changes.</summary>
    Underflow,

    /// <summary>The change would leave Inventory exactly as it is, so it is a semantic no-op rather than work.</summary>
    NoChange,

    /// <summary>Forget removes an empty record; Stock still on hand must be Removed or Set first.</summary>
    ForgetRequiresZeroQuantity,
}

/// <summary>
/// The pure decision one change amounts to, given only current state: the Quantity on hand, what (if
/// anything) is already at the destination or under the new name, and what was asked for. It reads
/// and writes nothing - authorization, matching, reference resolution, proposals, and persistence all
/// live outside it - so the arithmetic and the risk rules can be reasoned about, and tested, on their
/// own.
///
/// The quantity kinds deliberately delegate to <see cref="StockMutationPlan"/> rather than restating
/// its arithmetic: there is exactly one implementation of Add, Remove, and Set in this domain, and it
/// is the one issue #31 shipped and proved.
/// </summary>
public sealed record StockChangePlan
{
    public required StockChangePlanOutcome Outcome { get; init; }

    /// <summary>Meaningful only when <see cref="Outcome"/> is <see cref="StockChangePlanOutcome.Planned"/>.</summary>
    public StockChangeEffectKind Effect { get; init; }

    /// <summary>What the source Stock Entry carries once applied; <see cref="Quantity.Zero"/> when it is retired or nothing was planned.</summary>
    public Quantity SourceResultingQuantity { get; init; } = Quantity.Zero;

    /// <summary>What the destination Stock Entry carries once applied; <see cref="Quantity.Zero"/> when there is no destination.</summary>
    public Quantity DestinationResultingQuantity { get; init; } = Quantity.Zero;

    /// <summary>How much actually moves from source to destination; <see cref="Quantity.Zero"/> when nothing does.</summary>
    public Quantity TransferredQuantity { get; init; } = Quantity.Zero;

    public bool RetiresSource => Outcome == StockChangePlanOutcome.Planned && StockAuditFacts.RetiresSource(Effect);

    public bool RequiresConfirmation => Outcome == StockChangePlanOutcome.Planned && StockAuditFacts.RequiresConfirmation(Effect);

    /// <summary>Plans an Add, Remove, or Set. <paramref name="currentQuantity"/> is null when no Equivalent Stock exists.</summary>
    public static StockChangePlan ForQuantity(StockMutationKind kind, Quantity? currentQuantity, Quantity amount)
    {
        if (!StockMutationKinds.IsQuantityChange(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Add, Remove, and Set state an amount of their own.");
        }

        var plan = StockMutationPlan.For(kind, currentQuantity, amount);

        return plan.Kind switch
        {
            StockMutationPlanKind.CreateEntry => Planned(StockChangeEffectKind.Created, plan.ResultingQuantity),
            StockMutationPlanKind.ChangeQuantity => Planned(QuantityEffectFor(kind), plan.ResultingQuantity),

            // #31 answered a Set to zero with a bare refusal because it had nowhere to put a proposal.
            // It is a fully decided change; what it needs is confirmation, which this ticket can give it.
            StockMutationPlanKind.ConfirmationRequired => Planned(StockChangeEffectKind.QuantityCleared, Quantity.Zero),
            StockMutationPlanKind.Underflow => Refused(StockChangePlanOutcome.Underflow),
            StockMutationPlanKind.InvalidAmount => Refused(StockChangePlanOutcome.InvalidAmount),
            StockMutationPlanKind.OutOfBounds => Refused(StockChangePlanOutcome.OutOfBounds),
            StockMutationPlanKind.TargetRequired => Refused(StockChangePlanOutcome.TargetRequired),
            _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Kind, "Unhandled stock mutation plan kind."),
        };
    }

    /// <summary>
    /// Plans a Move. <paramref name="requestedAmount"/> is null for "all";
    /// <paramref name="destinationQuantity"/> is null when the destination holds no Equivalent Stock;
    /// <paramref name="destinationIsSamePlacement"/> is true when the destination is where the Stock
    /// already is.
    /// </summary>
    public static StockChangePlan ForMove(
        Quantity sourceQuantity, Quantity? requestedAmount, bool destinationIsSamePlacement, Quantity? destinationQuantity)
    {
        // Asking for an amount and naming a non-amount is a malformed Move, whatever the destination
        // is - so it is judged before anything about placement.
        if (requestedAmount is { } stated && !stated.IsOnHand)
        {
            return Refused(StockChangePlanOutcome.InvalidAmount);
        }

        if (destinationIsSamePlacement)
        {
            return Refused(StockChangePlanOutcome.NoChange);
        }

        // "All" of an empty Stock Entry is a legitimate relocation of an empty record; only an
        // explicitly stated non-positive amount is malformed, and that was refused above.
        var transferred = requestedAmount ?? sourceQuantity;

        if (!sourceQuantity.TrySubtract(transferred, out var remainder))
        {
            return Refused(StockChangePlanOutcome.Underflow);
        }

        var movesEverything = !remainder.IsOnHand;

        if (destinationQuantity is not { } atDestination)
        {
            return movesEverything

                // Nothing equivalent is there, so the Stock Entry itself moves and keeps its identity.
                ? Planned(StockChangeEffectKind.Placed, sourceQuantity, Quantity.Zero, transferred)
                : Planned(StockChangeEffectKind.Split, remainder, transferred, transferred);
        }

        if (!atDestination.TryAdd(transferred, out var merged))
        {
            return Refused(StockChangePlanOutcome.OutOfBounds);
        }

        return movesEverything
            ? Planned(StockChangeEffectKind.Merged, Quantity.Zero, merged, transferred)
            : Planned(StockChangeEffectKind.SplitMerged, remainder, merged, transferred);
    }

    /// <summary>
    /// Plans a Rename. <paramref name="collidingQuantity"/> is null when no Equivalent Stock carries
    /// the new normalized name at this Stock Entry's Unit and Location.
    /// </summary>
    public static StockChangePlan ForRename(
        string currentDisplayName, string newDisplayName, string currentNormalizedName, Quantity sourceQuantity, Quantity? collidingQuantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNormalizedName);

        if (string.Equals(currentDisplayName, newDisplayName, StringComparison.Ordinal))
        {
            return Refused(StockChangePlanOutcome.NoChange);
        }

        // Only case and whitespace are normalized away, so a name whose normalized form is unchanged
        // cannot collide with anything but the Stock Entry itself - the displayed name changes and the
        // identity is untouched.
        if (NameNormalization.Normalize(newDisplayName) == currentNormalizedName)
        {
            return Planned(StockChangeEffectKind.Renamed, sourceQuantity);
        }

        if (collidingQuantity is not { } colliding)
        {
            return Planned(StockChangeEffectKind.Renamed, sourceQuantity);
        }

        if (!colliding.TryAdd(sourceQuantity, out var merged))
        {
            return Refused(StockChangePlanOutcome.OutOfBounds);
        }

        return Planned(StockChangeEffectKind.RenameMerged, Quantity.Zero, merged, sourceQuantity);
    }

    /// <summary>Plans a Forget. Only an empty Stock Entry may be forgotten, so Forget can never stand in for Remove or Set.</summary>
    public static StockChangePlan ForForget(Quantity sourceQuantity) => sourceQuantity.IsOnHand
        ? Refused(StockChangePlanOutcome.ForgetRequiresZeroQuantity)
        : Planned(StockChangeEffectKind.Forgotten, Quantity.Zero);

    private static StockChangeEffectKind QuantityEffectFor(StockMutationKind kind) => kind switch
    {
        StockMutationKind.Add => StockChangeEffectKind.QuantityIncreased,
        StockMutationKind.Remove => StockChangeEffectKind.QuantityDecreased,
        StockMutationKind.Set => StockChangeEffectKind.QuantitySet,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    private static StockChangePlan Planned(
        StockChangeEffectKind effect,
        Quantity sourceResulting,
        Quantity? destinationResulting = null,
        Quantity? transferred = null) => new()
        {
            Outcome = StockChangePlanOutcome.Planned,
            Effect = effect,
            SourceResultingQuantity = sourceResulting,
            DestinationResultingQuantity = destinationResulting ?? Quantity.Zero,
            TransferredQuantity = transferred ?? Quantity.Zero,
        };

    private static StockChangePlan Refused(StockChangePlanOutcome outcome) => new() { Outcome = outcome };
}
