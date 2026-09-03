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
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    public static string OutcomeCodeFor(StockMutationKind kind, bool createdEntry) => kind switch
    {
        StockMutationKind.Add => createdEntry ? "Add:Created" : "Add:Increased",
        StockMutationKind.Remove => "Remove:Decreased",
        StockMutationKind.Set => "Set:Applied",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };
}
