namespace MultiChannelAgent.Domain.Inventories;

/// <summary>The three canonical single-entry quantity mutations this slice supports.</summary>
public enum StockMutationKind
{
    /// <summary>Increases a Stock Entry's Quantity, creating the equivalent Stock Entry when none exists.</summary>
    Add,

    /// <summary>Decreases a Stock Entry's Quantity. Rejected when the requested amount exceeds the Quantity on hand.</summary>
    Remove,

    /// <summary>Replaces a Stock Entry's Quantity with an exact non-negative value.</summary>
    Set,

    /// <summary>Transfers some or all Quantity to another Location or to the unlocated state, merging Equivalent Stock there.</summary>
    Move,

    /// <summary>Changes a Stock Entry's name, merging Equivalent Stock when the new name collides.</summary>
    Rename,

    /// <summary>Permanently removes a zero-quantity Stock Entry.</summary>
    Forget,
}

/// <summary>
/// The one place a mutation kind becomes text and text becomes a mutation kind. Machine text is what
/// a tool argument, a ledger row, and a payload all use, so it is defined once here rather than being
/// re-spelled at each boundary - a kind that reads "move" in one place and "Move" in another is a bug
/// waiting for a retry to find it.
/// </summary>
public static class StockMutationKinds
{
    /// <summary>True for the kinds that state an amount of their own; Move states a transfer, and Rename and Forget state none.</summary>
    public static bool IsQuantityChange(StockMutationKind kind) =>
        kind is StockMutationKind.Add or StockMutationKind.Remove or StockMutationKind.Set;

    public static string ToMachineText(StockMutationKind kind) => kind switch
    {
        StockMutationKind.Add => "add",
        StockMutationKind.Remove => "remove",
        StockMutationKind.Set => "set",
        StockMutationKind.Move => "move",
        StockMutationKind.Rename => "rename",
        StockMutationKind.Forget => "forget",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    /// <summary>
    /// Reads untrusted machine text. Exact and case-sensitive: a batch element naming "Add" is a
    /// malformed proposal, not a near-miss to be helpfully corrected.
    /// </summary>
    public static bool TryParse(string? text, out StockMutationKind kind)
    {
        switch (text)
        {
            case "add": kind = StockMutationKind.Add; return true;
            case "remove": kind = StockMutationKind.Remove; return true;
            case "set": kind = StockMutationKind.Set; return true;
            case "move": kind = StockMutationKind.Move; return true;
            case "rename": kind = StockMutationKind.Rename; return true;
            case "forget": kind = StockMutationKind.Forget; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// Exactly what one resolved change does to Inventory state. This is the vocabulary the whole
/// confirmation policy is expressed over (see <see cref="StockAuditFacts.RequiresConfirmation"/>) and
/// the vocabulary the atomic executor switches on, so "what will happen" is decided once, while
/// planning, and never re-derived from a kind plus a pile of nullable fields.
/// </summary>
public enum StockChangeEffectKind
{
    /// <summary>No Equivalent Stock existed, so the Stock Entry is created.</summary>
    Created,

    /// <summary>An existing Stock Entry's Quantity increases.</summary>
    QuantityIncreased,

    /// <summary>An existing Stock Entry's Quantity decreases.</summary>
    QuantityDecreased,

    /// <summary>An existing Stock Entry's Quantity is replaced by an exact positive amount.</summary>
    QuantitySet,

    /// <summary>An existing Stock Entry's Quantity is replaced by zero. Its identity survives; its stock does not.</summary>
    QuantityCleared,

    /// <summary>All of a Stock Entry moves to a placement holding no Equivalent Stock, so the entry itself is relocated.</summary>
    Placed,

    /// <summary>Part of a Stock Entry moves to a placement holding no Equivalent Stock, creating one there.</summary>
    Split,

    /// <summary>Part of a Stock Entry moves into existing Equivalent Stock at the destination.</summary>
    SplitMerged,

    /// <summary>All of a Stock Entry moves into existing Equivalent Stock at the destination; the source is retired.</summary>
    Merged,

    /// <summary>A Stock Entry's name changes and no Equivalent Stock collides, so its identity is preserved.</summary>
    Renamed,

    /// <summary>A Stock Entry's new name collides with Equivalent Stock; the collision survives and the source is retired.</summary>
    RenameMerged,

    /// <summary>A zero-quantity Stock Entry is permanently removed.</summary>
    Forgotten,
}

/// <summary>What a planned mutation turns out to be, before anything has been written.</summary>
public enum StockMutationPlanKind
{
    /// <summary>No Equivalent Stock exists yet, so an Add creates the Stock Entry.</summary>
    CreateEntry,

    /// <summary>An existing Stock Entry's Quantity becomes <see cref="StockMutationPlan.ResultingQuantity"/>.</summary>
    ChangeQuantity,

    /// <summary>Clearing Stock is deliberate: a Set to zero is planned but never applied without explicit confirmation.</summary>
    ConfirmationRequired,

    /// <summary>The Remove exceeds the Quantity on hand. Quantity is never negative, so nothing changes.</summary>
    Underflow,

    /// <summary>Add and Remove need a positive amount; anything else is not that mutation at all.</summary>
    InvalidAmount,

    /// <summary>The resulting amount could not be stored exactly (see <see cref="Quantity.MaxIntegerDigits"/>).</summary>
    OutOfBounds,

    /// <summary>Remove and Set act on stock that already exists; there is nothing here to act on.</summary>
    TargetRequired,
}

/// <summary>
/// The pure decision one stock mutation amounts to, given only the Quantity currently on hand (null
/// when no Equivalent Stock exists) and the requested amount. It reads and writes nothing: every
/// authorization, matching, reference resolution, and persistence concern lives outside it, so the
/// arithmetic and the risk rules can be reasoned about - and tested - on their own.
/// </summary>
public sealed record StockMutationPlan
{
    public required StockMutationPlanKind Kind { get; init; }

    /// <summary>
    /// The Quantity the Stock Entry will carry once applied. Meaningful only for
    /// <see cref="StockMutationPlanKind.CreateEntry"/> and <see cref="StockMutationPlanKind.ChangeQuantity"/>;
    /// <see cref="Quantity.Zero"/> for every kind that changes nothing.
    /// </summary>
    public Quantity ResultingQuantity { get; init; } = Quantity.Zero;

    public static StockMutationPlan For(StockMutationKind kind, Quantity? currentQuantity, Quantity amount) => kind switch
    {
        StockMutationKind.Add => PlanAdd(currentQuantity, amount),
        StockMutationKind.Remove => PlanRemove(currentQuantity, amount),
        StockMutationKind.Set => PlanSet(currentQuantity, amount),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    private static StockMutationPlan PlanAdd(Quantity? currentQuantity, Quantity amount)
    {
        if (!amount.IsOnHand)
        {
            return new StockMutationPlan { Kind = StockMutationPlanKind.InvalidAmount };
        }

        if (currentQuantity is not { } current)
        {
            return new StockMutationPlan { Kind = StockMutationPlanKind.CreateEntry, ResultingQuantity = amount };
        }

        return current.TryAdd(amount, out var increased)
            ? new StockMutationPlan { Kind = StockMutationPlanKind.ChangeQuantity, ResultingQuantity = increased }
            : new StockMutationPlan { Kind = StockMutationPlanKind.OutOfBounds };
    }

    private static StockMutationPlan PlanRemove(Quantity? currentQuantity, Quantity amount)
    {
        if (!amount.IsOnHand)
        {
            return new StockMutationPlan { Kind = StockMutationPlanKind.InvalidAmount };
        }

        if (currentQuantity is not { } current)
        {
            return new StockMutationPlan { Kind = StockMutationPlanKind.TargetRequired };
        }

        return current.TrySubtract(amount, out var decreased)
            ? new StockMutationPlan { Kind = StockMutationPlanKind.ChangeQuantity, ResultingQuantity = decreased }
            : new StockMutationPlan { Kind = StockMutationPlanKind.Underflow };
    }

    private static StockMutationPlan PlanSet(Quantity? currentQuantity, Quantity amount)
    {
        // Setting an amount replaces one that already exists. There is no Stock Entry here to replace,
        // and inventing one would make Set a second, silent way to create stock.
        if (currentQuantity is null)
        {
            return new StockMutationPlan { Kind = StockMutationPlanKind.TargetRequired };
        }

        // Clearing stock is the deliberate act CONTEXT.md and the spec both single out, so it is
        // planned and then handed back for confirmation rather than applied.
        if (!amount.IsOnHand)
        {
            return new StockMutationPlan { Kind = StockMutationPlanKind.ConfirmationRequired };
        }

        return new StockMutationPlan { Kind = StockMutationPlanKind.ChangeQuantity, ResultingQuantity = amount };
    }
}

/// <summary>
/// The one mapping from a completed mutation to the minimal semantic audit fact it appends. Shared by
/// every store that applies a mutation, so the durable audit vocabulary is defined in exactly one
/// place. Deliberately carries no name, Quantity, Note, or Stock Entry identity: the audit records
/// that an Editor changed stock in an Inventory, never what the stock was.
/// </summary>
public static class StockAuditFacts
{
    public static AuditEventType EventTypeFor(StockMutationKind kind) => kind switch
    {
        StockMutationKind.Add => AuditEventType.StockAdded,
        StockMutationKind.Remove => AuditEventType.StockRemoved,
        StockMutationKind.Set => AuditEventType.StockSet,
        StockMutationKind.Move => AuditEventType.StockMoved,
        StockMutationKind.Rename => AuditEventType.StockRenamed,
        StockMutationKind.Forget => AuditEventType.StockForgotten,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    public static string OutcomeCodeFor(StockMutationKind kind, bool createdEntry) => kind switch
    {
        StockMutationKind.Add => createdEntry ? "Add:Created" : "Add:Increased",
        StockMutationKind.Remove => "Remove:Decreased",
        StockMutationKind.Set => "Set:Applied",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    /// <summary>The coarse outcome code one resolved effect is audited under. Never free text, and never stock detail.</summary>
    public static string OutcomeCodeFor(StockChangeEffectKind effect) => effect switch
    {
        StockChangeEffectKind.Created => "Add:Created",
        StockChangeEffectKind.QuantityIncreased => "Add:Increased",
        StockChangeEffectKind.QuantityDecreased => "Remove:Decreased",
        StockChangeEffectKind.QuantitySet => "Set:Applied",
        StockChangeEffectKind.QuantityCleared => "Set:Cleared",
        StockChangeEffectKind.Placed => "Move:Placed",
        StockChangeEffectKind.Split => "Move:Split",
        StockChangeEffectKind.SplitMerged => "Move:SplitMerged",
        StockChangeEffectKind.Merged => "Move:Merged",
        StockChangeEffectKind.Renamed => "Rename:Renamed",
        StockChangeEffectKind.RenameMerged => "Rename:Merged",
        StockChangeEffectKind.Forgotten => "Forget:Forgotten",
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unhandled stock change effect."),
    };

    /// <summary>
    /// True when the effect ends a Stock Entry's identity - by merging it away, or by forgetting it.
    /// This is what a proposal must report as the retired source, and what makes the change
    /// irreversible enough to be worth confirming.
    /// </summary>
    public static bool RetiresSource(StockChangeEffectKind effect) =>
        effect is StockChangeEffectKind.Merged or StockChangeEffectKind.RenameMerged or StockChangeEffectKind.Forgotten;

    /// <summary>
    /// The whole single-change confirmation policy, in one predicate: an effect that ends an
    /// identity, or one that clears stock outright. A change set additionally confirms whenever it
    /// carries more than one change, which is the caller's rule, not this one.
    /// </summary>
    public static bool RequiresConfirmation(StockChangeEffectKind effect) =>
        RetiresSource(effect) || effect is StockChangeEffectKind.QuantityCleared;
}
