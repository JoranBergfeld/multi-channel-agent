# Add, Remove, and Set Stock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Editors and Owners Add, Remove, and Set stock conversationally in the web channel, with exact decimal Quantity, Equivalent Stock uniqueness, Note preservation, role authorization, typed non-disclosing outcomes, atomic state-plus-audit writes, retry-safe operation identity, and an Inventory workspace that refreshes afterwards.

**Architecture:** The existing read path (issue #30) already runs `InboundTurn -> TurnProcessingCoordinator -> TurnExecutionContext -> StockToolDispatcher -> deterministic service -> Outcome/Delivery`. This plan extends exactly that spine: three new pure Domain primitives (Quantity arithmetic, a mutation planner, a derived operation identity), one new Application service (`StockMutationService`) behind one new store seam (`IStockMutationStore`), one new SQL adapter (`SqlStockMutationStore`) that writes the Stock Entry change, the semantic audit fact, and the operation ledger row in a single `SaveChangesAsync`, plus three new tool names on the existing dispatcher. The model never supplies identity: Participant, Inventory, Turn, and the derived operation id all come from the trusted `TurnExecutionContext`.

**Tech Stack:** C#/.NET 10, EF Core 10 (SQL Server provider in production, SQLite for Docker-free relational tests), xUnit 2.9, Testcontainers `MsSql` for the SQL-backed application-boundary suite, React 19 + TypeScript + Vite + oxlint for the web client.

---

## Scope and non-goals

In scope (issue #31 acceptance criteria):

1. `add_stock` creates or increases Equivalent Stock using an exact decimal Quantity and preserves a conflicting existing Note.
2. `remove_stock` decreases Quantity and rejects underflow without changing state.
3. `set_stock` applies an exact non-negative Quantity; Set to zero returns `confirmation_required` and mutates nothing.
4. Ambiguous, unknown, forbidden, invalid, and state-changed requests return typed, non-disclosing outcomes.
5. Every completed mutation atomically updates current state and appends a minimal semantic audit fact.
6. A stable operation identity plus optimistic concurrency prevent duplicate effects across retries.
7. The web Inventory projection invalidates and refreshes after a mutation from the conversation path.

Explicitly **out of scope** for this slice (later tickets):

- Executing a confirmation. `set_stock` with zero returns `confirmation_required` and nothing else. No proposal storage, no tokens, no `confirm_inventory_operation`, no expiry - that is issue #32's slice.
- `move_stock`, `rename_stock`, `forget_stock`, multi-change `changes` batches, Unit/Location administration, and Initial Import.
- Editing an existing Stock Entry's Note. A quantity mutation never rewrites a Note.
- Monetary budgets, spend enforcement, chargeback, and billing. The parent spec puts these out of scope entirely; nothing in this plan may add a cost ceiling, quota, or spend check.

---

## File responsibility map

### Domain (`src/MultiChannelAgent.Domain/Inventories/`)

| File | Responsibility |
| --- | --- |
| `Quantity.cs` (modify) | Exact non-negative decimal amount. Gains invariant-text parsing, `Zero`, bounded `TryAdd`/`TrySubtract`, and the storage bounds (18 integer digits, 10 decimal places) that the `decimal(28,10)` column can actually hold. |
| `StockMutation.cs` (create) | `StockMutationKind`, the pure `StockMutationPlan` that turns "current quantity + kind + amount" into a typed decision (create, change, underflow, confirmation-required, invalid, out-of-bounds, target-required), and `StockAuditFacts`, the one mapping from a mutation kind to its audit event type and coarse outcome code. |
| `StockOperationId.cs` (create) | The stable operation identity derived from Turn identity plus tool identity. Same Turn retried => same identity => the recorded effect is re-reported, never re-applied. |
| `AuditFact.cs` (modify) | Adds `StockAdded`, `StockRemoved`, `StockSet` to `AuditEventType`. Nothing else changes; audit facts stay minimal and carry no stock detail. |

### Application (`src/MultiChannelAgent.Application/`)

| File | Responsibility |
| --- | --- |
| `Inventories/StockFindingService.cs` (modify) | `StockNarrowingHints` gains `FromFacets`, so Find and mutation ambiguity derive narrowing identically from one place. |
| `Inventories/IStockMutationStore.cs` (create) | The mutation write seam: `StockMutationCommand` (what to apply, under which operation identity, against which observed Quantity), `RecordedStockMutation` (the durable semantic facts of an applied mutation), and `StockMutationStoreOutcome` (`Applied`, `AlreadyApplied`, `StateChanged`). |
| `Inventories/StockMutationService.cs` (create) | The deterministic authority for one stock mutation: Editor authorization, exact reference resolution, quantity parsing, target matching (reusing the Find primitives), planning, and mapping the store's answer to a typed semantic result. |
| `Inventories/StockToolDispatcher.cs` (modify) | Executes `add_stock`/`remove_stock`/`set_stock` under trusted context, derives the operation identity from `TurnExecutionContext.TurnId` plus the tool name, and shapes the `stock_mutation` payload and semantic summaries. |
| `Turns/ConversationalClauses.cs` (modify) | Adds `quantity` and `note` to the bounded clause grammar. |
| `Turns/ScriptedModelBoundary.cs` (modify) | Recognizes `add stock`/`remove stock`/`set stock` and proposes the matching tool call. One shared reference-command parser now serves find and all three mutations. |

### Infrastructure (`src/MultiChannelAgent.Infrastructure/`)

| File | Responsibility |
| --- | --- |
| `Persistence/Entities/StockEntryEntity.cs` (modify) | Gains `ConcurrencyStamp`, the provider-neutral optimistic concurrency guard (same pattern as `MembershipEntity`). |
| `Persistence/Entities/StockOperationEntity.cs` (create) | The durable operation ledger row: one row per applied operation identity, carrying the recorded semantic facts a retry must re-report. |
| `Persistence/Configurations/StockEntryEntityConfiguration.cs` (modify) | Marks `ConcurrencyStamp` a concurrency token. |
| `Persistence/Configurations/StockOperationEntityConfiguration.cs` (create) | Maps `StockOperations`: operation identity as the key, `decimal(28,10)` quantities matching `StockEntries`, bounded strings, and an `AppliedAt` index for a later sweep. |
| `Persistence/MultiChannelAgentDbContext.cs` (modify) | Exposes `StockOperations`. |
| `Persistence/Migrations/*_AddStockMutationLedger.cs` (generated) | Adds the `ConcurrencyStamp` column and the `StockOperations` table. |
| `Inventories/SqlStockMutationStore.cs` (create) | The single atomic write: idempotency lookup, optimistic check, Stock Entry create/update, audit fact, ledger row - one `SaveChangesAsync`, one transaction. |
| `ServiceCollectionExtensions.cs` (modify) | Registers `IStockMutationStore` and `StockMutationService`. |

### Web (`src/web/src/`)

| File | Responsibility |
| --- | --- |
| `turnsApi.ts` (modify) | Adds the `stock_mutation` payload types to the discriminated `TurnOutcomePayload` union. |
| `TurnTracer.tsx` (modify) | Renders a mutation result and offers the mutation commands as hints. It already calls `onTerminalOutcome`, which is what invalidates the workspace. |

### Tests

| File | Responsibility |
| --- | --- |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/QuantityTests.cs` (modify) | Parsing, bounds, and arithmetic. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs` (create) | Every planner branch, plus the audit-fact mapping. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs` (create) | Determinism and separation of operation identities. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs` (modify) | Gains `Find`, `SetQuantity`, `CreateRow` so a mutation double can act on the same rows the read double serves. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockMutationStore.cs` (create) | In-memory `IStockMutationStore` with the same ledger/optimistic semantics as SQL. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockNarrowingHintsTests.cs` (create) | The shared narrowing derivation. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockMutationServiceTests.cs` (create) | Applied paths and every refusal path. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs` (modify) | Mutation tool dispatch, trusted-context enforcement, payload shape. |
| `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs` (modify) | The mutation grammar. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockMutationStoreTests.cs` (create) | Relational proof of atomicity, idempotency, optimistic concurrency, and the Equivalent Stock create race. |
| `tests/MultiChannelAgent.IntegrationTests/StockMutationScenario.cs` (create) | The shared end-to-end conversational mutation scenario. |
| `tests/MultiChannelAgent.IntegrationTests/StockMutationSqliteTests.cs` (create) | Docker-free twin of that scenario. |
| `tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs` (modify) | Runs the same scenario against Testcontainers SQL Server with production migrations. |
| `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs` (modify) | Exposes the signed-in `ParticipantIdentifier` and gains `GrantMembershipAsync`, so a scenario can create a Viewer. |

---

## Task 1: Exact decimal Quantity parsing, bounds, and arithmetic

**Files:**
- Modify: `src/MultiChannelAgent.Domain/Inventories/Quantity.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/QuantityTests.cs`

Why this comes first: every mutation argument arrives as untrusted text and every result is written to a `decimal(28,10)` column. A quantity that parses but cannot be stored must be refused here as a domain rule, not discovered as a SQL truncation error.

- [ ] **Step 1: Write the failing tests**

Append these to `tests/MultiChannelAgent.Domain.Tests/Inventories/QuantityTests.cs`, inside the existing `QuantityTests` class:

```csharp
    [Theory]
    [InlineData("0", "0")]
    [InlineData("12", "12")]
    [InlineData("12.5", "12.5")]
    [InlineData("  12.50  ", "12.5")]
    [InlineData("0.0000000001", "0.0000000001")]
    public void Invariant_decimal_text_parses_to_that_exact_amount(string text, string expected)
    {
        Assert.True(Quantity.TryParseInvariant(text, out var quantity));
        Assert.Equal(expected, quantity.ToInvariantText());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1,5")]
    [InlineData("1 000")]
    [InlineData("1e3")]
    [InlineData("-1")]
    [InlineData("0.00000000001")]
    [InlineData("1000000000000000000")]
    public void Text_that_is_not_a_storable_non_negative_decimal_does_not_parse(string? text)
    {
        Assert.False(Quantity.TryParseInvariant(text, out var quantity));
        Assert.Equal("0", quantity.ToInvariantText());
    }

    [Fact]
    public void Adding_two_amounts_keeps_every_decimal_digit()
    {
        Assert.True(Quantity.Create(12.5m).TryAdd(Quantity.Create(2.25m), out var sum));

        Assert.Equal("14.75", sum.ToInvariantText());
    }

    [Fact]
    public void Adding_beyond_the_storable_range_is_refused_rather_than_silently_wrapped()
    {
        var nearLimit = Quantity.Create(999_999_999_999_999_999m);

        Assert.False(nearLimit.TryAdd(Quantity.Create(1m), out _));
    }

    [Fact]
    public void Subtracting_within_the_amount_on_hand_keeps_every_decimal_digit()
    {
        Assert.True(Quantity.Create(14.75m).TrySubtract(Quantity.Create(4.75m), out var result));

        Assert.Equal("10", result.ToInvariantText());
    }

    [Fact]
    public void Subtracting_more_than_the_amount_on_hand_is_refused_and_never_goes_negative()
    {
        Assert.False(Quantity.Create(3m).TrySubtract(Quantity.Create(3.0000000001m), out var result));

        Assert.Equal("0", result.ToInvariantText());
    }

    [Fact]
    public void Zero_is_an_amount_that_is_not_on_hand()
    {
        Assert.Equal("0", Quantity.Zero.ToInvariantText());
        Assert.False(Quantity.Zero.IsOnHand);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~QuantityTests"`
Expected: FAIL to build with `CS0117: 'Quantity' does not contain a definition for 'TryParseInvariant'` (and the same for `TryAdd`, `TrySubtract`, `Zero`).

- [ ] **Step 3: Implement the parsing, bounds, and arithmetic**

Replace the whole body of `src/MultiChannelAgent.Domain/Inventories/Quantity.cs` with:

```csharp
using System.Globalization;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A non-negative decimal amount paired with a Unit. The exact <see cref="Value"/> is preserved
/// (never rounded or converted) - only the Unit a Stock Entry references determines what the amount
/// means; different Units are never automatically converted between each other.
///
/// <see cref="ToInvariantText"/> is the one way an amount is ever rendered outside this domain, and
/// <see cref="TryParseInvariant"/> is the one way untrusted text ever becomes one, so what a
/// Participant, a channel, or a tool argument sees depends only on the amount itself.
/// </summary>
public readonly record struct Quantity
{
    /// <summary>
    /// The most digits an amount may carry before the decimal point, and the most after it. These are
    /// the domain's own limits, chosen to match what the authoritative column
    /// (<c>decimal(28,10)</c>) can hold exactly, so an amount that could not be stored is refused as a
    /// domain rule rather than discovered as a truncation or overflow at the database.
    /// </summary>
    public const int MaxIntegerDigits = 18;

    /// <summary>The most digits an amount may carry after the decimal point. See <see cref="MaxIntegerDigits"/>.</summary>
    public const int MaxScale = 10;

    private const decimal IntegerDigitLimit = 1_000_000_000_000_000_000m;

    public decimal Value { get; }

    private Quantity(decimal value) => Value = value;

    /// <summary>The amount that is not on hand at all. Every Set to this amount is a deliberate, confirmed act.</summary>
    public static Quantity Zero { get; } = new(0m);

    /// <summary>On-hand Stock is exactly Stock Entries whose Quantity is greater than zero.</summary>
    public bool IsOnHand => Value > 0m;

    public static Quantity Create(decimal value)
    {
        if (value < 0m)
        {
            throw new ArgumentException("Quantity must not be negative.", nameof(value));
        }

        if (!IsStorable(value))
        {
            throw new ArgumentException(
                $"Quantity must have at most {MaxIntegerDigits} digits before the decimal point and {MaxScale} after it.",
                nameof(value));
        }

        return new Quantity(value);
    }

    /// <summary>
    /// Reads the one text form this domain ever exchanges an amount in: plain, culture-invariant
    /// decimal notation. Grouping separators, locale decimal commas, and scientific notation are all
    /// refused rather than guessed at, because each of them means different amounts to different
    /// readers - and so is anything negative or larger than the amount can be stored exactly.
    /// </summary>
    public static bool TryParseInvariant(string? text, out Quantity quantity)
    {
        quantity = Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        const NumberStyles PlainDecimal = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
        if (!decimal.TryParse(text.Trim(), PlainDecimal, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (value < 0m || !IsStorable(value))
        {
            return false;
        }

        quantity = new Quantity(value);
        return true;
    }

    /// <summary>
    /// Increases this amount, refusing rather than wrapping or rounding when the sum could no longer
    /// be stored exactly. <paramref name="result"/> is <see cref="Zero"/> when it refuses, so a caller
    /// that ignores the return value cannot silently write a wrong amount.
    /// </summary>
    public bool TryAdd(Quantity addend, out Quantity result)
    {
        result = Zero;

        var sum = Value + addend.Value;
        if (!IsStorable(sum))
        {
            return false;
        }

        result = new Quantity(sum);
        return true;
    }

    /// <summary>
    /// Decreases this amount, refusing when the subtrahend exceeds it - Quantity is never negative, so
    /// an over-large Remove is a refusal rather than a negative amount.
    /// </summary>
    public bool TrySubtract(Quantity subtrahend, out Quantity result)
    {
        result = Zero;

        if (subtrahend.Value > Value)
        {
            return false;
        }

        var difference = Value - subtrahend.Value;
        if (!IsStorable(difference))
        {
            return false;
        }

        result = new Quantity(difference);
        return true;
    }

    /// <summary>
    /// The canonical, culture-invariant decimal text for this amount: exact, in plain decimal
    /// notation, and independent of the scale it happens to be carried at.
    ///
    /// A .NET decimal remembers its scale, and a database hands one back at its column's scale - SQL
    /// Server returns a decimal(28,10) as 12.0000000000 where SQLite returns 12 - so rendering the
    /// raw value would make the same amount read differently depending on where it was stored, and
    /// any caller comparing that text would disagree with itself across providers. Dividing by one at
    /// full precision drops only the trailing zeros, never a significant digit and never the value,
    /// and (unlike a general "G" format) never switches to scientific notation for small amounts,
    /// which is not decimal text anyone can read back or parse.
    /// </summary>
    public string ToInvariantText() => Normalized(Value).ToString(CultureInfo.InvariantCulture);

    public override string ToString() => ToInvariantText();

    /// <summary>
    /// True when the amount fits the exact decimal shape this domain guarantees. Trailing zeros are
    /// dropped first, so an amount that is only incidentally carried at a wide scale (as a database
    /// hands one back) is judged by the digits it actually has.
    /// </summary>
    private static bool IsStorable(decimal value)
    {
        var normalized = Normalized(value);
        return ScaleOf(normalized) <= MaxScale && Math.Abs(decimal.Truncate(normalized)) < IntegerDigitLimit;
    }

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static decimal Normalized(decimal value) => value / 1.000000000000000000000000000000000m;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~QuantityTests"`
Expected: PASS, all `QuantityTests` green (the 13 pre-existing tests plus the new ones).

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/Quantity.cs tests/MultiChannelAgent.Domain.Tests/Inventories/QuantityTests.cs
git commit -m "feat(inventories): parse and combine exact decimal Quantities for #31"
```

---

## Task 2: Pure stock mutation planning and its audit event types

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/StockMutation.cs`
- Modify: `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockMutationPlanTests
{
    [Fact]
    public void Adding_to_nothing_creates_an_entry_at_the_requested_amount()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Add, currentQuantity: null, Quantity.Create(12.5m));

        Assert.Equal(StockMutationPlanKind.CreateEntry, plan.Kind);
        Assert.Equal("12.5", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Adding_to_existing_stock_increases_it_exactly()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Add, Quantity.Create(12.5m), Quantity.Create(2.25m));

        Assert.Equal(StockMutationPlanKind.ChangeQuantity, plan.Kind);
        Assert.Equal("14.75", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Adding_a_zero_amount_is_not_an_Add_at_all()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Add, Quantity.Create(5m), Quantity.Zero);

        Assert.Equal(StockMutationPlanKind.InvalidAmount, plan.Kind);
    }

    [Fact]
    public void Adding_past_the_storable_range_is_refused_rather_than_stored_wrong()
    {
        var plan = StockMutationPlan.For(
            StockMutationKind.Add, Quantity.Create(999_999_999_999_999_999m), Quantity.Create(1m));

        Assert.Equal(StockMutationPlanKind.OutOfBounds, plan.Kind);
    }

    [Fact]
    public void Removing_within_the_amount_on_hand_decreases_it_exactly()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, Quantity.Create(14.75m), Quantity.Create(4.75m));

        Assert.Equal(StockMutationPlanKind.ChangeQuantity, plan.Kind);
        Assert.Equal("10", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Removing_more_than_the_amount_on_hand_is_an_underflow_that_changes_nothing()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, Quantity.Create(3m), Quantity.Create(4m));

        Assert.Equal(StockMutationPlanKind.Underflow, plan.Kind);
    }

    [Fact]
    public void Removing_a_zero_amount_is_not_a_Remove_at_all()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, Quantity.Create(3m), Quantity.Zero);

        Assert.Equal(StockMutationPlanKind.InvalidAmount, plan.Kind);
    }

    [Fact]
    public void Removing_from_nothing_needs_a_target_that_exists()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Remove, currentQuantity: null, Quantity.Create(1m));

        Assert.Equal(StockMutationPlanKind.TargetRequired, plan.Kind);
    }

    [Fact]
    public void Setting_replaces_the_amount_exactly()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Set, Quantity.Create(3m), Quantity.Create(7.125m));

        Assert.Equal(StockMutationPlanKind.ChangeQuantity, plan.Kind);
        Assert.Equal("7.125", plan.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Setting_to_zero_needs_explicit_confirmation_and_plans_no_change()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Set, Quantity.Create(7m), Quantity.Zero);

        Assert.Equal(StockMutationPlanKind.ConfirmationRequired, plan.Kind);
    }

    [Fact]
    public void Setting_something_that_does_not_exist_needs_a_target_that_exists()
    {
        var plan = StockMutationPlan.For(StockMutationKind.Set, currentQuantity: null, Quantity.Create(7m));

        Assert.Equal(StockMutationPlanKind.TargetRequired, plan.Kind);
    }

    [Theory]
    [InlineData(StockMutationKind.Add, true, AuditEventType.StockAdded, "Add:Created")]
    [InlineData(StockMutationKind.Add, false, AuditEventType.StockAdded, "Add:Increased")]
    [InlineData(StockMutationKind.Remove, false, AuditEventType.StockRemoved, "Remove:Decreased")]
    [InlineData(StockMutationKind.Set, false, AuditEventType.StockSet, "Set:Applied")]
    public void Every_applied_mutation_has_one_minimal_audit_fact_shape(
        StockMutationKind kind, bool createdEntry, AuditEventType expectedEventType, string expectedOutcomeCode)
    {
        Assert.Equal(expectedEventType, StockAuditFacts.EventTypeFor(kind));
        Assert.Equal(expectedOutcomeCode, StockAuditFacts.OutcomeCodeFor(kind, createdEntry));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockMutationPlanTests"`
Expected: FAIL to build with `CS0246: The type or namespace name 'StockMutationPlan' could not be found`.

- [ ] **Step 3: Add the three new audit event types**

In `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`, replace the `AuditEventType` enum with:

```csharp
public enum AuditEventType
{
    MembershipGranted,
    RoleChanged,
    MembershipRemoved,
    OwnershipTransferred,
    OrphanOwnershipRecovered,
    AccessDenied,

    /// <summary>Stock was created or increased. The fact records that it happened, never what or how much.</summary>
    StockAdded,

    /// <summary>Stock was decreased.</summary>
    StockRemoved,

    /// <summary>Stock was set to an exact amount.</summary>
    StockSet,
}
```

- [ ] **Step 4: Write the planner**

Create `src/MultiChannelAgent.Domain/Inventories/StockMutation.cs`:

```csharp
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
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockMutationPlanTests"`
Expected: PASS, 15 tests.

- [ ] **Step 6: Run the whole Domain suite to prove nothing regressed**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj`
Expected: PASS, no failures.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/StockMutation.cs src/MultiChannelAgent.Domain/Inventories/AuditFact.cs tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs
git commit -m "feat(inventories): decide Add, Remove, and Set outcomes as pure domain rules for #31"
```

---

## Task 3: Stable operation identity for a mutation

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/StockOperationId.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockOperationIdTests
{
    private static readonly TurnId SomeTurn = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TurnId AnotherTurn = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void The_same_turn_and_tool_always_derive_the_same_operation_identity()
    {
        var first = StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0);
        var second = StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0);

        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first.Value);
    }

    [Fact]
    public void A_different_turn_derives_a_different_operation_identity()
    {
        Assert.NotEqual(
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0),
            StockOperationId.Derive(AnotherTurn, "add_stock", sequence: 0));
    }

    [Fact]
    public void A_different_tool_in_the_same_turn_derives_a_different_operation_identity()
    {
        Assert.NotEqual(
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0),
            StockOperationId.Derive(SomeTurn, "remove_stock", sequence: 0));
    }

    [Fact]
    public void A_later_call_of_the_same_tool_in_the_same_turn_derives_a_different_operation_identity()
    {
        Assert.NotEqual(
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0),
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 1));
    }

    [Fact]
    public void The_derivation_is_stable_across_processes_not_merely_within_one()
    {
        // A hard-coded expectation: if the derivation ever changes, a Turn retried by a NEWER build
        // would derive a different identity and could apply its effect a second time. That is exactly
        // the failure this identity exists to prevent, so it is pinned here deliberately.
        Assert.Equal(
            "3e2a1f27-6c1c-6a3a-3c1e-38d8f4b0a2c4",
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0).Value.ToString());
    }
}
```

Note for the implementer: the pinned value in the last test is a placeholder that will not match. Run the test once, read the actual derived value out of the xUnit failure message, and paste that exact value into the test. That is the intended workflow for pinning a hash - do not change the implementation to match a made-up value.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockOperationIdTests"`
Expected: FAIL to build with `CS0246: The type or namespace name 'StockOperationId' could not be found`.

- [ ] **Step 3: Implement the derivation**

Create `src/MultiChannelAgent.Domain/Inventories/StockOperationId.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stable identity of one attempted Inventory mutation. It is <em>derived</em> - never generated -
/// from identities the application already trusts: the durably accepted Turn, the tool being executed,
/// and that tool call's position within the Turn. Two consequences follow, and they are the whole
/// point of the type:
///
/// <list type="bullet">
/// <item>Retrying the same Turn derives the same identity, so a store that has already recorded that
/// identity can re-report the effect instead of applying a second one.</item>
/// <item>Nothing a model proposes contributes to it, so a hostile or buggy proposal can neither
/// collide with another operation's identity nor mint a fresh one to bypass the ledger.</item>
/// </list>
///
/// The derivation is a plain hash rather than a random value precisely so it survives a process
/// restart, a redeployment, and a different replica picking the Turn up.
/// </summary>
public readonly record struct StockOperationId(Guid Value)
{
    public static StockOperationId Derive(TurnId turnId, string toolName, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var material = $"{turnId.Value:D}|{toolName}|{sequence}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return new StockOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString();
}
```

- [ ] **Step 4: Run the test, read the real derived value, and pin it**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockOperationIdTests"`
Expected: 4 PASS, 1 FAIL - `The_derivation_is_stable_across_processes_not_merely_within_one` reports `Assert.Equal() Failure` with the actual Guid. Copy that actual value into the test's expected string.

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockOperationIdTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/StockOperationId.cs tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs
git commit -m "feat(inventories): derive a stable identity for one stock operation for #31"
```

---

## Task 4: One shared derivation for ambiguity narrowing

**Files:**
- Modify: `src/MultiChannelAgent.Application/Inventories/StockFindingService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockNarrowingHintsTests.cs`

Why: an ambiguous mutation must offer exactly the narrowing an ambiguous Find offers. The rule currently lives inside `StockFindingService.NarrowingHintsAsync`, where a second caller cannot reach it.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/StockNarrowingHintsTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockNarrowingHintsTests
{
    [Fact]
    public void Units_are_offered_only_when_the_matches_actually_differ_by_Unit()
    {
        var oneUnit = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A", "Shelf B"], false));
        var twoUnits = StockNarrowingHints.FromFacets(new StockMatchFacets(["each", "box"], ["Shelf A"], false));

        Assert.Empty(oneUnit.Units);
        Assert.Equal(["each", "box"], twoUnits.Units);
    }

    [Fact]
    public void Locations_are_offered_only_when_placement_actually_distinguishes_the_matches()
    {
        var onePlace = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A"], false));
        var twoPlaces = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A", "Shelf B"], false));

        Assert.Empty(onePlace.Locations);
        Assert.Equal(["Shelf A", "Shelf B"], twoPlaces.Locations);
    }

    [Fact]
    public void Unlocated_stock_is_offered_only_alongside_placed_stock()
    {
        var onlyUnlocated = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], [], true));
        var mixed = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], ["Shelf A"], true));

        Assert.False(onlyUnlocated.IncludesUnlocated);
        Assert.True(mixed.IncludesUnlocated);
        Assert.Equal(["Shelf A"], mixed.Locations);
    }

    [Fact]
    public void Nothing_that_would_change_the_answer_means_no_hints_at_all()
    {
        var hints = StockNarrowingHints.FromFacets(new StockMatchFacets(["each"], [], false));

        Assert.False(hints.HasAny);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockNarrowingHintsTests"`
Expected: FAIL to build with `CS0117: 'StockNarrowingHints' does not contain a definition for 'FromFacets'`.

- [ ] **Step 3: Move the derivation onto the type it describes**

In `src/MultiChannelAgent.Application/Inventories/StockFindingService.cs`, replace the `StockNarrowingHints` record with:

```csharp
public sealed record StockNarrowingHints(
    IReadOnlyList<string> Units,
    IReadOnlyList<string> Locations,
    bool IncludesUnlocated)
{
    public static readonly StockNarrowingHints None = new([], [], false);

    public bool HasAny => Units.Count > 0 || Locations.Count > 0 || IncludesUnlocated;

    /// <summary>
    /// Offers only narrowing that would actually change the answer: a Unit list when the matches
    /// differ on it, a Location list when placement genuinely distinguishes them, and unlocated Stock
    /// only when some match really is kept nowhere in particular alongside placed ones. Shared by
    /// Find and by an ambiguous mutation so both offer a Participant exactly the same choices.
    /// </summary>
    public static StockNarrowingHints FromFacets(StockMatchFacets facets)
    {
        var units = facets.UnitCanonicalNames.Count > 1 ? facets.UnitCanonicalNames : [];
        var distinguishesByPlacement = facets.LocationNames.Count > 1
            || (facets.LocationNames.Count == 1 && facets.HasUnlocatedMatches);

        return new StockNarrowingHints(
            units,
            distinguishesByPlacement ? facets.LocationNames : [],
            distinguishesByPlacement && facets.HasUnlocatedMatches);
    }
}
```

Then replace `StockFindingService.NarrowingHintsAsync` with:

```csharp
    private async Task<StockNarrowingHints> NarrowingHintsAsync(StockFindQuery query, CancellationToken cancellationToken) =>
        StockNarrowingHints.FromFacets(await stockStore.SummarizeMatchFacetsAsync(query, MaxCandidates, cancellationToken));
```

Delete the now-duplicated doc comment that sat above the old private method - the rule is documented on `FromFacets`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockNarrowingHintsTests|FullyQualifiedName~StockFindingServiceTests"`
Expected: PASS - the 4 new tests plus every pre-existing `StockFindingServiceTests` test, unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockFindingService.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockNarrowingHintsTests.cs
git commit -m "refactor(inventories): derive narrowing hints in one shared place for #31"
```

---

## Task 5: Applying a mutation - the store seam and the service's applied paths

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/IStockMutationStore.cs`
- Create: `src/MultiChannelAgent.Application/Inventories/StockMutationService.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockMutationStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockMutationServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/StockMutationServiceTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockMutationServiceTests
{
    private static readonly ParticipantId Editor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Viewer = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly UnitId EachUnit = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly UnitId BoxUnit = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    private static readonly LocationId ShelfA = new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    private static readonly LocationId ShelfB = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));
    private static readonly StockOperationId SomeOperation = new(Guid.Parse("99999999-9999-9999-9999-999999999999"));
    private static readonly StockOperationId AnotherOperation = new(Guid.Parse("aaaaaaaa-9999-9999-9999-999999999999"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        StockMutationService Service, InMemoryStockStore StockStore, InMemoryStockMutationStore MutationStore);

    private static Harness CreateHarness()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Editor, MembershipRole.Editor, Now);
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);

        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);

        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        referenceStore.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        referenceStore.AddUnit(SomeInventory, BoxUnit, "box");
        referenceStore.AddLocation(SomeInventory, ShelfA, "Shelf A");
        referenceStore.AddLocation(SomeInventory, ShelfB, "Shelf B");

        var mutationStore = new InMemoryStockMutationStore(stockStore);
        mutationStore.NameUnit(EachUnit, "each");
        mutationStore.NameUnit(BoxUnit, "box");
        mutationStore.NameLocation(ShelfA, "Shelf A");
        mutationStore.NameLocation(ShelfB, "Shelf B");

        return new Harness(
            new StockMutationService(stockStore, mutationStore, referenceStore, authorizationService),
            stockStore,
            mutationStore);
    }

    private static StockEntrySummary Row(
        string name, decimal quantity, string idHex, UnitId? unitId = null, LocationId? locationId = null, string? note = null) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        unitId ?? EachUnit,
        unitId == BoxUnit ? "box" : "each",
        locationId,
        locationId == ShelfA ? "Shelf A" : locationId == ShelfB ? "Shelf B" : null,
        note,
        Quantity.Create(quantity));

    private static Task<StockMutationResult> MutateAsync(
        Harness harness, ParticipantId participantId, StockMutationRequest request, StockOperationId? operationId = null) =>
        harness.Service.MutateAsync(
            participantId, SomeInventory, operationId ?? SomeOperation, request, "conversation-1", Now, CancellationToken.None);

    [Fact]
    public async Task Adding_to_stock_that_does_not_exist_yet_creates_it_at_the_exact_amount()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "12.5",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.True(result.View!.Created);
        Assert.Equal("Steel Bolts", result.View.Name);
        Assert.Equal("each", result.View.Unit);
        Assert.Null(result.View.Location);
        Assert.Equal("0", result.View.PreviousQuantity);
        Assert.Equal("12.5", result.View.Quantity);
    }

    [Fact]
    public async Task Adding_to_existing_Equivalent_Stock_increases_it_rather_than_duplicating_it()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 12.5m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "steel bolts",
            QuantityText = "2.25",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.False(result.View!.Created);
        Assert.Equal("14.75", result.View.Quantity);
        Assert.Single(await harness.StockStore.FindMatchesAsync(
            StockFindQuery.ByName(SomeInventory, "Steel Bolts", null, null), 10, CancellationToken.None));
    }

    [Fact]
    public async Task Adding_never_overwrites_an_existing_Note_and_says_it_kept_it()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 1m, "10000000", note: "Blue box"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            Note = "Red box",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("Blue box", result.View!.Note);
        Assert.True(result.View.NotePreserved);
    }

    [Fact]
    public async Task A_created_entry_keeps_the_Note_the_request_gave_it()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            Note = "Blue box",
        });

        Assert.Equal("Blue box", result.View!.Note);
        Assert.False(result.View.NotePreserved);
    }

    [Fact]
    public async Task An_Add_that_names_a_Unit_and_Location_creates_that_exact_Equivalent_Stock()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "3",
            UnitReference = "box",
            LocationReference = "Shelf A",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.True(result.View!.Created);
        Assert.Equal("box", result.View.Unit);
        Assert.Equal("Shelf A", result.View.Location);
    }

    [Fact]
    public async Task Removing_decreases_the_matched_entry_exactly()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 14.75m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "4.75",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("14.75", result.View!.PreviousQuantity);
        Assert.Equal("10", result.View.Quantity);
    }

    [Fact]
    public async Task Setting_replaces_the_matched_entrys_amount_exactly()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = "Steel Bolts",
            QuantityText = "7.125",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("7.125", result.View!.Quantity);
    }

    [Fact]
    public async Task A_Stock_Entry_can_be_targeted_by_its_opaque_identity()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 5m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = row.Id.ToString(),
            QuantityText = "2",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("2", result.View!.Quantity);
    }

    [Fact]
    public async Task Every_completed_mutation_appends_one_minimal_semantic_audit_fact()
    {
        var harness = CreateHarness();

        await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        var fact = Assert.Single(harness.MutationStore.AuditFacts);
        Assert.Equal(AuditEventType.StockAdded, fact.EventType);
        Assert.Equal("Add:Created", fact.OutcomeCode);
        Assert.Equal(SomeInventory, fact.InventoryId);
        Assert.Equal(Editor.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task Retrying_the_same_operation_re_reports_its_effect_instead_of_applying_it_twice()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "5",
        };

        var first = await MutateAsync(harness, Editor, request);
        var retry = await MutateAsync(harness, Editor, request);

        Assert.Equal("15", first.View!.Quantity);
        Assert.Equal(StockMutationResultKind.Completed, retry.Kind);
        Assert.Equal("15", retry.View!.Quantity);
        Assert.Single(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_genuinely_new_operation_applies_again_rather_than_being_mistaken_for_a_retry()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 10m, "10000000"));
        var request = new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "5",
        };

        await MutateAsync(harness, Editor, request, SomeOperation);
        var second = await MutateAsync(harness, Editor, request, AnotherOperation);

        Assert.Equal("20", second.View!.Quantity);
        Assert.Equal(2, harness.MutationStore.AuditFacts.Count);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockMutationServiceTests"`
Expected: FAIL to build with `CS0246: The type or namespace name 'StockMutationService' could not be found` (and the same for `InMemoryStockMutationStore`).

- [ ] **Step 3: Define the mutation write seam**

Create `src/MultiChannelAgent.Application/Inventories/IStockMutationStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How a mutation command was settled by the store.</summary>
public enum StockMutationStoreOutcome
{
    /// <summary>The mutation was applied, and its state change, audit fact, and ledger row were committed together.</summary>
    Applied,

    /// <summary>This exact operation identity had already been applied; the recorded effect is returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>The target moved under the caller's feet, so nothing was applied. The caller must ask again against current state.</summary>
    StateChanged,
}

/// <summary>
/// The durable semantic facts of one applied mutation - exactly what a retry of the same operation
/// identity must be able to re-report without touching Inventory state again. Deliberately semantic:
/// no row versions, concurrency stamps, audit identities, or SQL detail ever appear here.
/// </summary>
public sealed record RecordedStockMutation(
    StockEntryId StockEntryId,
    string Name,
    string UnitCanonicalName,
    string? LocationName,
    string? Note,
    Quantity PreviousQuantity,
    Quantity ResultingQuantity,
    bool CreatedEntry,
    bool NotePreserved);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="StockMutationStoreOutcome.StateChanged"/>.</summary>
public sealed record StockMutationStoreResult(StockMutationStoreOutcome Outcome, RecordedStockMutation? Recorded);

/// <summary>
/// One fully decided mutation, ready to apply. Everything ambiguous has already been resolved by
/// <see cref="StockMutationService"/>: the target (or the exact Equivalent Stock key to create), the
/// resulting Quantity, and the Quantity the caller observed while deciding.
/// </summary>
public sealed record StockMutationCommand
{
    /// <summary>The derived, retry-stable identity of this operation. The store's idempotency ledger is keyed by it.</summary>
    public required StockOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Editor-or-better Membership authorized this mutation; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    public required StockMutationKind Kind { get; init; }

    /// <summary>The requested amount, recorded for completeness; the store writes <see cref="ResultingQuantity"/>.</summary>
    public required Quantity Amount { get; init; }

    public required Quantity ResultingQuantity { get; init; }

    /// <summary>The existing Stock Entry to change; null exactly when this command creates one.</summary>
    public StockEntryId? StockEntryId { get; init; }

    /// <summary>
    /// The Quantity the caller read while planning. The store refuses rather than applies when the row
    /// no longer carries it, so a plan decided against a state nobody holds any more never lands.
    /// Null exactly when <see cref="StockEntryId"/> is.
    /// </summary>
    public Quantity? ExpectedQuantity { get; init; }

    /// <summary>The display name for a created Stock Entry; null when changing an existing one.</summary>
    public string? NewEntryName { get; init; }

    /// <summary>The resolved Unit for a created Stock Entry; null when changing an existing one.</summary>
    public UnitId? NewEntryUnitId { get; init; }

    /// <summary>The resolved Location for a created Stock Entry; null means unlocated (or that an existing entry is being changed).</summary>
    public LocationId? NewEntryLocationId { get; init; }

    /// <summary>The Note for a created Stock Entry. A quantity mutation never rewrites an existing entry's Note, so this is only ever set on the create path.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// True when the request proposed a Note that was deliberately not applied because the target
    /// Stock Entry already existed. Recorded so the answer can say the existing Note was kept rather
    /// than dropping the proposal silently.
    /// </summary>
    public required bool NotePreserved { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The single write seam for a stock mutation. One call must, in one transaction: refuse if this
/// operation identity was already applied (returning what it did), refuse if the target changed since
/// the caller planned, and otherwise change the Stock Entry, append the minimal semantic audit fact,
/// and record the operation ledger row together. Partial application is never acceptable: a caller
/// that sees <see cref="StockMutationStoreOutcome.StateChanged"/> must be able to rely on nothing
/// having happened at all.
/// </summary>
public interface IStockMutationStore
{
    Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Write the mutation service**

Create `src/MultiChannelAgent.Application/Inventories/StockMutationService.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for one stock mutation.</summary>
public enum StockMutationResultKind
{
    Completed,

    /// <summary>The change is understood and authorized but too consequential to apply unasked (Set to zero).</summary>
    ConfirmationRequired,

    /// <summary>The reference matched several Stock Entries; candidates are offered rather than one being guessed.</summary>
    Ambiguous,

    /// <summary>Nothing matched - or nothing the requester may know exists.</summary>
    NotFound,

    /// <summary>A named Unit or Location does not exist in this Inventory. It is never created implicitly.</summary>
    ReferenceNotFound,

    /// <summary>The requester may see this Inventory but may not mutate it.</summary>
    Forbidden,

    /// <summary>The request conflicts with current Stock (an underflow, or state that changed underneath it).</summary>
    Conflict,

    /// <summary>The request itself could not be understood or was out of bounds.</summary>
    Invalid,
}

/// <summary>
/// One applied mutation as exposed at the application boundary. Both Quantities are exact invariant
/// decimal text - never floating point - so no precision is lost in transit.
/// </summary>
public sealed record StockMutationView(
    string StockEntryId,
    string Name,
    string Unit,
    string? Location,
    string? Note,
    string PreviousQuantity,
    string Quantity,
    bool Created,
    bool NotePreserved);

/// <summary>
/// The semantic result of a mutation request: a typed <see cref="StockMutationResultKind"/>, a machine
/// <see cref="Code"/>, the applied change when there was one, and the candidates when the reference
/// was ambiguous. Never SQL detail, row versions, audit identities, or unauthorized existence.
/// </summary>
public sealed record StockMutationResult(
    StockMutationResultKind Kind,
    StockMutationView? View,
    string Code,
    StockFindView? Candidates = null,
    StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// One mutation request's structured descriptor, as proposed. Every field is untrusted text:
/// <see cref="Reference"/> targets a Stock Entry by opaque identity or exact name,
/// <see cref="QuantityText"/> is invariant decimal text, and the Unit/Location references must resolve
/// exactly. Nothing here is ever pattern-matched or guessed.
/// </summary>
public sealed record StockMutationRequest
{
    public required StockMutationKind Kind { get; init; }

    public string? Reference { get; init; }

    public string? QuantityText { get; init; }

    public string? UnitReference { get; init; }

    public string? LocationReference { get; init; }

    public bool UnlocatedOnly { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// The deterministic authority for one Add, Remove, or Set. It authorizes (Editor or better), resolves
/// exact Unit/Location references, parses the Quantity, resolves the target through the very same
/// deterministic matching Find uses - so a mutation can never act on a reference Find would have called
/// ambiguous - plans the change with <see cref="StockMutationPlan"/>, and hands one fully decided
/// command to <see cref="IStockMutationStore"/>.
///
/// Callers only ever supply an InventoryId already scoped by trusted context, never one taken from an
/// untrusted model-proposed argument, and an unauthorized Inventory stays indistinguishable from one
/// that does not exist.
/// </summary>
public sealed class StockMutationService(
    IStockStore stockStore,
    IStockMutationStore mutationStore,
    IInventoryReferenceStore referenceStore,
    InventoryAuthorizationService authorizationService)
{
    /// <summary>The reserved Unit every Inventory starts with; an Add that names no Unit creates against it.</summary>
    public const string ReservedEachUnitName = "each";

    public async Task<StockMutationResult> MutateAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        StockOperationId operationId,
        StockMutationRequest request,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new StockMutationResult(StockMutationResultKind.NotFound, null, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new StockMutationResult(StockMutationResultKind.Forbidden, null, "forbidden");
        }

        if (!Quantity.TryParseInvariant(request.QuantityText, out var amount))
        {
            return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_quantity");
        }

        var proposedNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (proposedNote is { Length: > StockEntry.MaxNoteLength })
        {
            return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_note");
        }

        UnitId? unitId = null;
        if (!string.IsNullOrWhiteSpace(request.UnitReference))
        {
            unitId = await referenceStore.ResolveUnitAsync(inventoryId, request.UnitReference, cancellationToken);
            if (unitId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }
        }

        LocationId? locationId = null;
        if (!string.IsNullOrWhiteSpace(request.LocationReference))
        {
            locationId = await referenceStore.ResolveLocationAsync(inventoryId, request.LocationReference, cancellationToken);
            if (locationId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Location);
            }
        }

        StockFindQuery query;
        try
        {
            query = Guid.TryParse(request.Reference, out var stockEntryId)
                ? StockFindQuery.ById(inventoryId, new StockEntryId(stockEntryId))
                : StockFindQuery.ByName(inventoryId, request.Reference, unitId, locationId, request.UnlocatedOnly);
        }
        catch (ArgumentException)
        {
            return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_reference");
        }

        var matches = await stockStore.FindMatchesAsync(query, StockFindingService.MaxCandidates + 1, cancellationToken);
        var resolution = StockFindOutcome.FromMatches(matches);

        // A mutation acts on one Match. Several matched, so the request names no single Stock Entry -
        // choosing one would be exactly the guess CONTEXT.md forbids.
        if (resolution.Kind == StockFindOutcomeKind.Ambiguous)
        {
            var facets = await stockStore.SummarizeMatchFacetsAsync(query, StockFindingService.MaxCandidates, cancellationToken);

            return new StockMutationResult(
                StockMutationResultKind.Ambiguous,
                null,
                "ambiguous",
                new StockFindView(
                    resolution.Candidates.Select(StockListingService.ToRowView).ToList(),
                    resolution.HasMoreCandidates,
                    StockNarrowingHints.FromFacets(facets)));
        }

        var target = resolution.Kind == StockFindOutcomeKind.Completed ? resolution.Candidates[0] : null;
        var plan = StockMutationPlan.For(request.Kind, target?.Quantity, amount);

        switch (plan.Kind)
        {
            case StockMutationPlanKind.InvalidAmount:
                return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_quantity");

            case StockMutationPlanKind.OutOfBounds:
                return new StockMutationResult(StockMutationResultKind.Invalid, null, "quantity_out_of_bounds");

            case StockMutationPlanKind.Underflow:
                return new StockMutationResult(StockMutationResultKind.Conflict, null, "insufficient_quantity");

            case StockMutationPlanKind.TargetRequired:
                return new StockMutationResult(StockMutationResultKind.NotFound, null, "not_found");

            case StockMutationPlanKind.ConfirmationRequired:
                return new StockMutationResult(StockMutationResultKind.ConfirmationRequired, null, "confirmation_required");
        }

        var creating = plan.Kind == StockMutationPlanKind.CreateEntry;
        string? newEntryName = null;
        UnitId? newEntryUnitId = null;

        if (creating)
        {
            // An opaque identity that matched nothing names no Stock Entry to create - it names one
            // that is simply not here.
            if (query.NormalizedNameReference is null)
            {
                return new StockMutationResult(StockMutationResultKind.NotFound, null, "not_found");
            }

            newEntryName = request.Reference?.Trim();
            if (newEntryName is null or { Length: 0 } or { Length: > StockEntry.MaxNameLength })
            {
                return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_name");
            }

            // A blank Unit means the reserved `each` every Inventory starts with - never a Unit
            // invented on the Participant's behalf.
            newEntryUnitId = unitId ?? await referenceStore.ResolveUnitAsync(inventoryId, ReservedEachUnitName, cancellationToken);
            if (newEntryUnitId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }
        }

        var stored = await mutationStore.ApplyAsync(
            new StockMutationCommand
            {
                OperationId = operationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                Kind = request.Kind,
                Amount = amount,
                ResultingQuantity = plan.ResultingQuantity,
                StockEntryId = target?.Id,
                ExpectedQuantity = target?.Quantity,
                NewEntryName = newEntryName,
                NewEntryUnitId = newEntryUnitId,
                NewEntryLocationId = creating ? locationId : null,
                Note = creating ? proposedNote : null,

                // An existing Stock Entry's Note is never rewritten by a quantity change, so a proposed
                // Note is deliberately left unapplied - and said out loud rather than dropped silently.
                NotePreserved = proposedNote is not null && !creating,
                Now = now,
            },
            cancellationToken);

        if (stored.Outcome == StockMutationStoreOutcome.StateChanged)
        {
            return new StockMutationResult(StockMutationResultKind.Conflict, null, "state_changed");
        }

        var recorded = stored.Recorded!;

        return new StockMutationResult(
            StockMutationResultKind.Completed,
            new StockMutationView(
                recorded.StockEntryId.ToString(),
                recorded.Name,
                recorded.UnitCanonicalName,
                recorded.LocationName,
                recorded.Note,
                recorded.PreviousQuantity.ToInvariantText(),
                recorded.ResultingQuantity.ToInvariantText(),
                recorded.CreatedEntry,
                recorded.NotePreserved),
            "completed");
    }

    private static StockMutationResult ReferenceNotFound(StockReferenceKind reference) =>
        new(StockMutationResultKind.ReferenceNotFound, null, "reference_not_found", null, reference);
}
```

- [ ] **Step 5: Let the in-memory Stock store be written to**

In `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs`, add these members directly under the existing `Add` method:

```csharp
    /// <summary>The row with this identity in this Inventory, or null when it is not (or no longer) there.</summary>
    public StockEntrySummary? Find(InventoryId inventoryId, StockEntryId id) =>
        _rows.FirstOrDefault(r => r.InventoryId == inventoryId && r.Row.Id == id).Row;

    /// <summary>Replaces one row's Quantity, returning the row as it now stands, or null when it is not there.</summary>
    public StockEntrySummary? SetQuantity(InventoryId inventoryId, StockEntryId id, Quantity quantity)
    {
        var index = _rows.FindIndex(r => r.InventoryId == inventoryId && r.Row.Id == id);
        if (index < 0)
        {
            return null;
        }

        var updated = _rows[index].Row with { Quantity = quantity };
        _rows[index] = (inventoryId, updated);
        return updated;
    }

    /// <summary>Creates a row for one exact Equivalent Stock key, returning it.</summary>
    public StockEntrySummary CreateRow(
        InventoryId inventoryId,
        string name,
        UnitId unitId,
        string unitCanonicalName,
        LocationId? locationId,
        string? locationName,
        string? note,
        Quantity quantity)
    {
        var row = new StockEntrySummary(
            new StockEntryId(Guid.NewGuid()),
            name,
            NameNormalization.Normalize(name),
            unitId,
            unitCanonicalName,
            locationId,
            locationName,
            note,
            quantity);

        _rows.Add((inventoryId, row));
        return row;
    }
```

- [ ] **Step 6: Write the in-memory mutation store**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockMutationStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IStockMutationStore"/> for Application-layer unit tests. It applies the
/// same rules the SQL store must: an operation identity already in the ledger re-reports its recorded
/// effect and applies nothing; a target that no longer carries the Quantity the caller planned against
/// is refused; and every applied mutation appends exactly one audit fact. It writes through the same
/// <see cref="InMemoryStockStore"/> the reads come from, so a test sees one consistent Inventory.
/// </summary>
public sealed class InMemoryStockMutationStore(InMemoryStockStore stockStore) : IStockMutationStore
{
    private readonly Dictionary<StockOperationId, RecordedStockMutation> _ledger = [];
    private readonly Dictionary<UnitId, string> _unitNames = [];
    private readonly Dictionary<LocationId, string> _locationNames = [];

    /// <summary>Every audit fact appended so far, in order, so a test can assert exactly one per applied mutation.</summary>
    public List<AuditFact> AuditFacts { get; } = [];

    /// <summary>Simulates a competing writer having changed the target since the caller planned.</summary>
    public bool ForceStateChanged { get; set; }

    public void NameUnit(UnitId unitId, string canonicalName) => _unitNames[unitId] = canonicalName;

    public void NameLocation(LocationId locationId, string name) => _locationNames[locationId] = name;

    public Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        if (_ledger.TryGetValue(command.OperationId, out var alreadyRecorded))
        {
            return Task.FromResult(new StockMutationStoreResult(StockMutationStoreOutcome.AlreadyApplied, alreadyRecorded));
        }

        if (ForceStateChanged)
        {
            return Task.FromResult(new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null));
        }

        StockEntrySummary row;
        Quantity previousQuantity;

        if (command.StockEntryId is { } targetId)
        {
            var current = stockStore.Find(command.InventoryId, targetId);
            if (current is null || current.Quantity != command.ExpectedQuantity)
            {
                return Task.FromResult(new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null));
            }

            previousQuantity = current.Quantity;
            row = stockStore.SetQuantity(command.InventoryId, targetId, command.ResultingQuantity)!;
        }
        else
        {
            previousQuantity = Quantity.Zero;
            var unitId = command.NewEntryUnitId!.Value;
            row = stockStore.CreateRow(
                command.InventoryId,
                command.NewEntryName!,
                unitId,
                _unitNames.GetValueOrDefault(unitId, "each"),
                command.NewEntryLocationId,
                command.NewEntryLocationId is { } locationId ? _locationNames.GetValueOrDefault(locationId) : null,
                command.Note,
                command.ResultingQuantity);
        }

        var recorded = new RecordedStockMutation(
            row.Id,
            row.Name,
            row.UnitCanonicalName,
            row.LocationName,
            row.Note,
            previousQuantity,
            row.Quantity,
            CreatedEntry: command.StockEntryId is null,
            command.NotePreserved);

        _ledger[command.OperationId] = recorded;
        AuditFacts.Add(AuditFact.Create(
            StockAuditFacts.EventTypeFor(command.Kind),
            AuditActorKind.Participant,
            command.ActorId.ToString(),
            command.InventoryId,
            subjectParticipantId: null,
            StockAuditFacts.OutcomeCodeFor(command.Kind, command.StockEntryId is null),
            command.Now));

        return Task.FromResult(new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded));
    }
}
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockMutationServiceTests"`
Expected: PASS, 11 tests.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IStockMutationStore.cs src/MultiChannelAgent.Application/Inventories/StockMutationService.cs tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockMutationStore.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockMutationServiceTests.cs
git commit -m "feat(inventories): apply Add, Remove, and Set to Equivalent Stock for #31"
```

---

## Task 6: Every refusal a mutation can return, typed and non-disclosing

**Files:**
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockMutationServiceTests.cs` (modify)

No production code changes are expected here: Task 5 already wrote every branch. This task exists because the refusals are the acceptance criterion that actually protects the Inventory, and an untested branch is an unproven one. If any test below fails, fix `StockMutationService` and note what was wrong.

- [ ] **Step 1: Write the failing tests**

Append these to the `StockMutationServiceTests` class:

```csharp
    [Fact]
    public async Task A_Viewer_may_see_the_Inventory_but_may_not_change_its_Stock()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Viewer, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Null(result.View);
        Assert.Equal("5", harness.StockStore.Find(SomeInventory, Row("Steel Bolts", 5m, "10000000").Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task A_non_member_cannot_tell_this_Inventory_apart_from_one_that_does_not_exist()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Stranger, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task An_ambiguous_reference_offers_candidates_and_narrowing_instead_of_guessing()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000", locationId: ShelfA));
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 7m, "20000000", locationId: ShelfB));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Ambiguous, result.Kind);
        Assert.Equal(2, result.Candidates!.Candidates.Count);
        Assert.Equal(["Shelf A", "Shelf B"], result.Candidates.NarrowingHints.Locations);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task Naming_the_Location_makes_an_otherwise_ambiguous_reference_exact()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000", locationId: ShelfA));
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 7m, "20000000", locationId: ShelfB));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "1",
            LocationReference = "Shelf B",
        });

        Assert.Equal(StockMutationResultKind.Completed, result.Kind);
        Assert.Equal("6", result.View!.Quantity);
        Assert.Equal("Shelf B", result.View.Location);
    }

    [Fact]
    public async Task Removing_stock_that_is_not_there_is_simply_not_found()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
    }

    [Fact]
    public async Task Setting_stock_that_is_not_there_never_quietly_creates_it()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = "Steel Bolts",
            QuantityText = "7",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task Removing_more_than_is_on_hand_is_refused_and_changes_nothing()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 3m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Remove,
            Reference = "Steel Bolts",
            QuantityText = "3.0000000001",
        });

        Assert.Equal(StockMutationResultKind.Conflict, result.Kind);
        Assert.Equal("insufficient_quantity", result.Code);
        Assert.Equal("3", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task Setting_stock_to_zero_asks_for_confirmation_and_changes_nothing_yet()
    {
        var harness = CreateHarness();
        var row = Row("Steel Bolts", 7m, "10000000");
        harness.StockStore.Add(SomeInventory, row);

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Set,
            Reference = "Steel Bolts",
            QuantityText = "0",
        });

        Assert.Equal(StockMutationResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal("confirmation_required", result.Code);
        Assert.Null(result.View);
        Assert.Equal("7", harness.StockStore.Find(SomeInventory, row.Id)!.Quantity.ToInvariantText());
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("1,5")]
    [InlineData("-3")]
    public async Task A_Quantity_that_is_not_exact_invariant_decimal_text_is_refused(string? quantityText)
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = quantityText,
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_quantity", result.Code);
    }

    [Fact]
    public async Task Adding_zero_is_not_an_Add_and_is_refused_as_invalid()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "0",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_quantity", result.Code);
    }

    [Fact]
    public async Task A_request_that_names_no_Stock_Entry_at_all_is_refused_as_invalid()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "   ",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_reference", result.Code);
    }

    [Fact]
    public async Task A_Unit_this_Inventory_does_not_have_is_reported_rather_than_created()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            UnitReference = "pallet",
        });

        Assert.Equal(StockMutationResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal("reference_not_found", result.Code);
        Assert.Equal(StockReferenceKind.Unit, result.UnresolvedReference);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_Location_this_Inventory_does_not_have_is_reported_rather_than_created()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            LocationReference = "Loading Bay",
        });

        Assert.Equal(StockMutationResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal(StockReferenceKind.Location, result.UnresolvedReference);
    }

    [Fact]
    public async Task An_opaque_identity_that_matches_nothing_is_not_found_rather_than_an_invitation_to_create()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd").ToString(),
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.NotFound, result.Kind);
        Assert.Empty(harness.MutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_target_that_changed_since_the_request_was_planned_is_refused_without_disclosing_why()
    {
        var harness = CreateHarness();
        harness.StockStore.Add(SomeInventory, Row("Steel Bolts", 5m, "10000000"));
        harness.MutationStore.ForceStateChanged = true;

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Null(result.View);
    }

    [Fact]
    public async Task A_Note_longer_than_a_Note_may_be_is_refused_before_it_reaches_a_column()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = "Steel Bolts",
            QuantityText = "1",
            Note = new string('n', StockEntry.MaxNoteLength + 1),
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_note", result.Code);
    }

    [Fact]
    public async Task A_name_longer_than_a_name_may_be_is_refused_before_it_reaches_a_column()
    {
        var harness = CreateHarness();

        var result = await MutateAsync(harness, Editor, new StockMutationRequest
        {
            Kind = StockMutationKind.Add,
            Reference = new string('n', StockEntry.MaxNameLength + 1),
            QuantityText = "1",
        });

        Assert.Equal(StockMutationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_name", result.Code);
    }
```

- [ ] **Step 2: Run the tests to verify they fail (or reveal a real gap)**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockMutationServiceTests"`
Expected: PASS for all of them if Task 5's service is correct. Any failure is a genuine defect in `StockMutationService` - fix the service, not the test. The most likely genuine failure is `A_name_longer_than_a_name_may_be_is_refused_before_it_reaches_a_column`, because an over-long name is only rejected on the create path; if it fails, confirm the create path is what runs (nothing matches that name) before changing anything.

- [ ] **Step 3: Run the whole Application suite to prove nothing regressed**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS, no failures.

- [ ] **Step 4: Commit**

```bash
git add tests/MultiChannelAgent.Application.Tests/Inventories/StockMutationServiceTests.cs src/MultiChannelAgent.Application/Inventories/StockMutationService.cs
git commit -m "test(inventories): pin every refusal a stock mutation can return for #31"
```

---

## Task 7: Dispatching add_stock, remove_stock, and set_stock under trusted context

**Files:**
- Modify: `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs`

- [ ] **Step 1: Write the failing test**

Append these to `StockToolDispatcherTests`. They need the harness to build a mutation service too, so first replace the existing `CreateDispatcher` helper with:

```csharp
    private static (StockToolDispatcher Dispatcher, InMemoryStockStore StockStore) CreateDispatcher()
    {
        var (dispatcher, stockStore, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Viewer);
        return (dispatcher, stockStore);
    }

    private static (StockToolDispatcher Dispatcher, InMemoryStockStore StockStore, InMemoryStockMutationStore MutationStore)
        CreateDispatcherWithMutations(ParticipantId participantId, MembershipRole role)
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, participantId, role, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        referenceStore.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        var mutationStore = new InMemoryStockMutationStore(stockStore);
        mutationStore.NameUnit(EachUnit, "each");

        var dispatcher = new StockToolDispatcher(
            new StockListingService(stockStore, referenceStore, authorizationService),
            new StockFindingService(stockStore, referenceStore, authorizationService),
            new StockMutationService(stockStore, mutationStore, referenceStore, authorizationService));

        return (dispatcher, stockStore, mutationStore);
    }
```

Then append the new tests:

```csharp
    [Fact]
    public async Task Add_stock_tool_call_returns_a_completed_decision_with_a_typed_mutation_payload()
    {
        var (dispatcher, _, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Steel Bolts", ["quantity"] = "12.5" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"kind\":\"stock_mutation\"", decision.Payload);
        Assert.Contains("\"operation\":\"add\"", decision.Payload);
        Assert.Contains("\"quantity\":\"12.5\"", decision.Payload);
        Assert.Single(decision.Deliveries);
    }

    [Fact]
    public async Task Remove_stock_tool_call_that_underflows_returns_a_conflict_that_changed_nothing()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 3m, "10000000"));
        var proposal = new ToolCallProposal(
            "remove_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "4" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Conflict, decision.Category);
        Assert.Equal("insufficient_quantity", decision.Code);
        Assert.Null(decision.Payload);
        Assert.Empty(mutationStore.AuditFacts);
    }

    [Fact]
    public async Task Set_stock_to_zero_returns_confirmation_required_rather_than_clearing_stock()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 7m, "10000000"));
        var proposal = new ToolCallProposal(
            "set_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "0" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Equal("confirmation_required", decision.Code);
        Assert.Empty(mutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_Viewer_proposing_a_mutation_is_refused_without_the_Inventory_being_touched()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Viewer);
        stockStore.Add(SomeInventory, Row("Bolts", 3m, "10000000"));
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "1" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Forbidden, decision.Category);
        Assert.Empty(mutationStore.AuditFacts);
    }

    [Fact]
    public async Task An_ambiguous_mutation_reference_is_answered_with_the_same_candidate_payload_a_Find_uses()
    {
        var (dispatcher, stockStore, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 3m, "10000000"));
        stockStore.Add(SomeInventory, Row("Bolts", 4m, "20000000") with { LocationId = new LocationId(Guid.NewGuid()), LocationName = "Shelf A" });
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "1" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Ambiguous, decision.Category);
        Assert.Contains("\"kind\":\"stock_find\"", decision.Payload);
    }

    // The same security property the read tools already guarantee: identity comes only from the
    // trusted TurnExecutionContext, so args claiming another Participant or Inventory change nothing.
    [Fact]
    public async Task Malicious_untrusted_mutation_args_claiming_another_participant_or_inventory_are_ignored()
    {
        var (dispatcher, _, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        var proposal = new ToolCallProposal("add_stock", new Dictionary<string, string>
        {
            ["reference"] = "Bolts",
            ["quantity"] = "1",
            ["participantId"] = Stranger.ToString(),
            ["inventoryId"] = Guid.NewGuid().ToString(),
        });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"stockEntryId\"", decision.Payload);
    }

    // Two dispatches of the SAME Turn and tool must derive the same operation identity, so the second
    // re-reports the first's effect rather than adding to stock again.
    [Fact]
    public async Task Dispatching_the_same_Turns_mutation_twice_never_applies_it_twice()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 10m, "10000000"));
        var context = Context(Viewer, SomeInventory);
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "5" });

        var first = await dispatcher.DispatchAsync(proposal, context, Now, CancellationToken.None);
        var retry = await dispatcher.DispatchAsync(proposal, context, Now, CancellationToken.None);

        Assert.Contains("\"quantity\":\"15\"", first.Payload);
        Assert.Contains("\"quantity\":\"15\"", retry.Payload);
        Assert.Single(mutationStore.AuditFacts);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockToolDispatcherTests"`
Expected: FAIL to build with `CS1729: 'StockToolDispatcher' does not contain a constructor that takes 3 arguments`.

- [ ] **Step 3: Extend the dispatcher**

In `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`:

Change the class declaration to:

```csharp
public sealed class StockToolDispatcher(
    StockListingService listingService,
    StockFindingService findingService,
    StockMutationService mutationService) : IToolDispatcher
```

Add these constants beneath `FindStockToolName`:

```csharp
    public const string AddStockToolName = "add_stock";
    public const string RemoveStockToolName = "remove_stock";
    public const string SetStockToolName = "set_stock";
```

Add these three arms to the `proposal.ToolName switch`, immediately after the `FindStockToolName` arm:

```csharp
            AddStockToolName => await DispatchMutationAsync(
                Domain.Inventories.StockMutationKind.Add, proposal, context, inventoryId, now, cancellationToken),
            RemoveStockToolName => await DispatchMutationAsync(
                Domain.Inventories.StockMutationKind.Remove, proposal, context, inventoryId, now, cancellationToken),
            SetStockToolName => await DispatchMutationAsync(
                Domain.Inventories.StockMutationKind.Set, proposal, context, inventoryId, now, cancellationToken),
```

Add this method after `DispatchFindAsync`:

```csharp
    private async Task<ModelDecision> DispatchMutationAsync(
        Domain.Inventories.StockMutationKind kind,
        ToolCallProposal proposal,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var untrustedArgs = proposal.UntrustedArgs;

        // Every value here is untrusted: a name, an amount as text, exact Unit/Location references, a
        // Note. None of them is identity, and none of them can widen what this Turn is allowed to
        // touch - the Inventory and the Participant come from trusted context alone.
        var request = new StockMutationRequest
        {
            Kind = kind,
            Reference = untrustedArgs.GetValueOrDefault("reference"),
            QuantityText = untrustedArgs.GetValueOrDefault("quantity"),
            UnitReference = untrustedArgs.GetValueOrDefault("unit"),
            LocationReference = untrustedArgs.GetValueOrDefault("location"),
            UnlocatedOnly = ParseFlag(untrustedArgs, "unlocated"),
            Note = untrustedArgs.GetValueOrDefault("note"),
        };

        // The operation's identity is derived from the durably accepted Turn and the tool being
        // executed - both trusted, both stable across retries - so replaying this Turn re-reports the
        // recorded effect instead of applying a second one. Nothing the model proposes contributes to
        // it, so a hostile proposal can neither mint a fresh identity nor collide with another's.
        var operationId = Domain.Inventories.StockOperationId.Derive(context.TurnId, proposal.ToolName, sequence: 0);

        var result = await mutationService.MutateAsync(
            context.ParticipantId, inventoryId, operationId, request, context.ChannelConversationId.Value, now, cancellationToken);

        return result.Kind switch
        {
            StockMutationResultKind.Completed => Completed(
                "completed",
                SummarizeMutation(kind, result.View!),
                JsonSerializer.Serialize(
                    new StockMutationPayload(1, "stock_mutation", OperationName(kind), result.View!), PayloadOptions)),
            StockMutationResultKind.ConfirmationRequired => Semantic(
                OutcomeCategory.ConfirmationRequired,
                "confirmation_required",
                "Setting Stock to zero clears it, so it needs your explicit confirmation first."),
            StockMutationResultKind.Ambiguous => Ambiguous(
                "ambiguous",
                SummarizeAmbiguity(result.Candidates!),
                JsonSerializer.Serialize(
                    new StockFindPayload(
                        1,
                        "stock_find",
                        result.Candidates!.Candidates,
                        result.Candidates.HasMoreCandidates,
                        NarrowingHintsPayload.From(result.Candidates.NarrowingHints)),
                    PayloadOptions)),
            StockMutationResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No matching Stock Entry was found."),
            StockMutationResultKind.ReferenceNotFound => Semantic(
                OutcomeCategory.NotFound, "reference_not_found", UnresolvedReferenceSummary(result.UnresolvedReference)),
            StockMutationResultKind.Conflict => Semantic(OutcomeCategory.Conflict, result.Code, ConflictSummary(result.Code)),
            StockMutationResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidMutationSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    /// <summary>The stable machine name for the mutation a payload describes.</summary>
    private static string OperationName(Domain.Inventories.StockMutationKind kind) => kind switch
    {
        Domain.Inventories.StockMutationKind.Add => "add",
        Domain.Inventories.StockMutationKind.Remove => "remove",
        Domain.Inventories.StockMutationKind.Set => "set",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
    };

    /// <summary>
    /// The exact read-back a clear low-risk mutation owes the Participant: what changed, where, and
    /// what it now is. When a proposed Note was deliberately not applied, it says so rather than
    /// letting the Note disappear without comment.
    /// </summary>
    private static string SummarizeMutation(Domain.Inventories.StockMutationKind kind, StockMutationView view)
    {
        var placement = view.Location is null ? "unlocated" : $"in {view.Location}";
        var opening = kind switch
        {
            Domain.Inventories.StockMutationKind.Add => view.Created
                ? $"Created {view.Name} ({placement}) at {view.Quantity} {view.Unit}."
                : $"Added to {view.Name} ({placement}): now {view.Quantity} {view.Unit}.",
            Domain.Inventories.StockMutationKind.Remove => $"Removed from {view.Name} ({placement}): now {view.Quantity} {view.Unit}.",
            Domain.Inventories.StockMutationKind.Set => $"Set {view.Name} ({placement}) to {view.Quantity} {view.Unit}.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled stock mutation kind."),
        };

        return view.NotePreserved ? $"{opening} Its existing Note was kept unchanged." : opening;
    }

    /// <summary>Names the current-state conflict a refused mutation ran into, without disclosing anything else about it.</summary>
    private static string ConflictSummary(string code) => code switch
    {
        "insufficient_quantity" => "That is more than the Quantity on hand, so nothing was changed.",
        "state_changed" => "That Stock changed while this request was being prepared, so nothing was changed. Ask again.",
        _ => "That request conflicts with current Stock, so nothing was changed.",
    };

    /// <summary>Names the bound a rejected mutation violated, rather than only that it was rejected.</summary>
    private static string InvalidMutationSummary(string code) => code switch
    {
        "invalid_quantity" => "State a Quantity as a plain decimal number - positive for Add and Remove.",
        "quantity_out_of_bounds" =>
            $"That Quantity is larger than an Inventory can record ({Domain.Inventories.Quantity.MaxIntegerDigits} digits "
            + $"before the decimal point and {Domain.Inventories.Quantity.MaxScale} after it).",
        "invalid_name" => $"A Stock Entry name must be 1 to {Domain.Inventories.StockEntry.MaxNameLength} characters.",
        "invalid_note" => $"A Note must not exceed {Domain.Inventories.StockEntry.MaxNoteLength} characters.",
        "invalid_reference" => "Name the Stock Entry to change.",
        _ => "That request could not be understood.",
    };
```

Add this payload record beside the existing private payload records at the bottom of the class:

```csharp
    /// <summary>The typed read-back one applied mutation leaves behind, versioned like every other payload.</summary>
    private sealed record StockMutationPayload(int Version, string Kind, string Operation, StockMutationView Entry);
```

Finally, update the class doc comment's first sentence to read:

```csharp
/// Executes list_stock/find_stock/add_stock/remove_stock/set_stock tool calls proposed by the model
/// boundary, always under the trusted
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockToolDispatcherTests"`
Expected: PASS - the pre-existing read tests plus the 7 new mutation tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs
git commit -m "feat(inventories): execute stock mutations under trusted turn context for #31"
```

---

## Task 8: A conversational grammar for Add, Remove, and Set

**Files:**
- Modify: `src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs`

- [ ] **Step 1: Write the failing test**

Append these to the existing `ScriptedModelBoundaryTests` class. They reuse that class's existing
`Turn(string)` helper and its `BoundConversation` field, so no new helper is needed:

```csharp
    [Theory]
    [InlineData("add stock Steel Bolts quantity 12.5", "add_stock")]
    [InlineData("remove stock Steel Bolts quantity 2", "remove_stock")]
    [InlineData("set stock Steel Bolts quantity 7", "set_stock")]
    public async Task A_mutation_command_proposes_its_bounded_tool_call(string content, string expectedToolName)
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn(content), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.ToolCall, proposal.Kind);
        Assert.Equal(expectedToolName, proposal.ToolCall!.ToolName);
        Assert.Equal("Steel Bolts", proposal.ToolCall.UntrustedArgs["reference"]);
    }

    [Fact]
    public async Task A_mutation_command_carries_its_amount_unit_location_and_note_as_untrusted_text()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("add stock Steel Bolts quantity 12.5 unit box in Shelf A note Blue box"),
            BoundConversation,
            CancellationToken.None);

        var args = proposal.ToolCall!.UntrustedArgs;
        Assert.Equal("Steel Bolts", args["reference"]);
        Assert.Equal("12.5", args["quantity"]);
        Assert.Equal("box", args["unit"]);
        Assert.Equal("Shelf A", args["location"]);
        Assert.Equal("Blue box", args["note"]);
    }

    [Fact]
    public async Task A_mutation_command_can_ask_for_stock_kept_nowhere_in_particular()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("remove stock Steel Bolts quantity 1 unlocated"), BoundConversation, CancellationToken.None);

        Assert.Equal("true", proposal.ToolCall!.UntrustedArgs["unlocated"]);
    }

    [Fact]
    public async Task A_mutation_command_naming_nothing_to_change_is_not_recognized_as_a_mutation()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("add stock"), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        Assert.Equal("echoed", proposal.Direct!.Code);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ScriptedModelBoundaryTests"`
Expected: FAIL - `A_mutation_command_proposes_its_bounded_tool_call` reports `Assert.Equal() Failure: Expected ToolCall, Actual Direct`, because `add stock ...` currently falls through to the echo.

- [ ] **Step 3: Teach the clause grammar about amounts and notes**

In `src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs`, replace the generated regex attribute with:

```csharp
    [GeneratedRegex(@"\b(including zero|unlocated|named|unit|in|page size|after|quantity|note)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClauseScanner { get; }
```

and extend the class doc comment's example line to read:

```csharp
/// <c>list stock including zero in Shelf A page size 5</c> or <c>add stock Steel Bolts quantity 5 in Shelf A</c>.
```

- [ ] **Step 4: Teach the scripted boundary the three mutation commands**

In `src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs`:

Add these constants beside `FindCommand`:

```csharp
    private const string AddStockCommand = "add stock";
    private const string RemoveStockCommand = "remove stock";
    private const string SetStockCommand = "set stock";
```

Replace the block that tries list and find with:

```csharp
        if (TryProposeList(content, out var listProposal))
        {
            return Task.FromResult(listProposal!);
        }

        // Longest command words first: "add stock"/"remove stock"/"set stock" each name a whole
        // command, and "find" must stay last so it never swallows one of them.
        foreach (var (command, toolName) in MutationCommands)
        {
            if (TryProposeReferenceCommand(content, command, toolName, out var mutationProposal))
            {
                return Task.FromResult(mutationProposal!);
            }
        }

        if (TryProposeReferenceCommand(content, FindCommand, "find_stock", out var findProposal))
        {
            return Task.FromResult(findProposal!);
        }
```

Add this table beside the command constants:

```csharp
    /// <summary>The mutation commands this boundary recognizes, each mapped to the bounded tool it proposes.</summary>
    private static readonly (string Command, string ToolName)[] MutationCommands =
    [
        (AddStockCommand, "add_stock"),
        (RemoveStockCommand, "remove_stock"),
        (SetStockCommand, "set_stock"),
    ];
```

Replace the whole `TryProposeFind` method with this generalized one:

```csharp
    /// <summary>
    /// Parses a command shaped <c>&lt;command&gt; &lt;reference&gt; [clauses]</c>: everything before the
    /// first clause keyword is the reference itself, and the clauses that follow narrow or quantify
    /// it. A reference is always required - the command word alone names nothing to act on. Every
    /// value produced here is untrusted text; the dispatcher and the deterministic services resolve
    /// and bound it, and identity never comes from here.
    /// </summary>
    private static bool TryProposeReferenceCommand(string content, string command, string toolName, out ModelProposal? proposal)
    {
        proposal = null;

        if (!StartsWithCommand(content, command, out var remainder) || remainder.Length == 0)
        {
            return false;
        }

        var reference = remainder;
        IReadOnlyDictionary<string, string> clauses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var clauseStart = FindFirstClauseIndex(remainder);
        if (clauseStart >= 0)
        {
            reference = remainder[..clauseStart].Trim();
            if (!ConversationalClauses.TryParse(remainder[clauseStart..], out clauses))
            {
                return false;
            }
        }

        if (reference.Length == 0)
        {
            return false;
        }

        var args = new Dictionary<string, string> { ["reference"] = reference };
        CopyFlag(clauses, "unlocated", args, "unlocated");
        CopyValue(clauses, "unit", args, "unit");
        CopyValue(clauses, "in", args, "location");
        CopyValue(clauses, "quantity", args, "quantity");
        CopyValue(clauses, "note", args, "note");

        proposal = ModelProposal.Tool(toolName, args);
        return true;
    }
```

Replace `FindFirstClauseIndex`'s clause array so amounts and notes also end a reference:

```csharp
        foreach (var clause in (string[])[" unit ", " in ", " unlocated", " quantity ", " note "])
```

and update that method's doc comment to:

```csharp
    /// <summary>
    /// Where a reference stops and its clauses begin. Only a clause keyword standing as its own word
    /// can start them, so a reference that merely contains one of those words (a "unit heater", say)
    /// stays part of the reference.
    /// </summary>
```

Finally, update the class doc comment to say it recognizes five deterministic commands - two reads and three mutations - instead of "exactly two deterministic read commands".

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ScriptedModelBoundaryTests"`
Expected: PASS - the pre-existing list/find grammar tests plus the 6 new mutation grammar tests.

- [ ] **Step 6: Run the whole Application suite to prove the shared parser did not change read behavior**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS, no failures.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs
git commit -m "feat(turns): recognize add, remove, and set stock commands for #31"
```

---

## Task 9: Persistence for the concurrency guard and the operation ledger

**Files:**
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockEntryEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockOperationEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockEntryEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockOperationEntityConfiguration.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Create (generated): `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/<timestamp>_AddStockMutationLedger.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/StockEntryRelationalModelTests.cs` (modify)

- [ ] **Step 1: Write the failing test**

Append to `tests/MultiChannelAgent.IntegrationTests/Inventories/StockEntryRelationalModelTests.cs`, matching that file's existing SQLite harness conventions (it already builds a `MultiChannelAgentDbContext` over an in-memory SQLite database and seeds an Inventory and a Unit):

```csharp
    [Fact]
    public void A_Stock_Entry_carries_a_concurrency_stamp_that_a_writer_must_agree_with()
    {
        using var db = CreateContext();
        var entityType = db.Model.FindEntityType(typeof(StockEntryEntity))!;
        var stamp = entityType.FindProperty(nameof(StockEntryEntity.ConcurrencyStamp))!;

        Assert.True(stamp.IsConcurrencyToken);
    }

    [Fact]
    public void One_operation_identity_can_only_ever_be_recorded_once()
    {
        var (inventoryId, _) = SeedInventoryAndUnit();
        var operationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        using (var db = CreateContext())
        {
            db.StockOperations.Add(NewOperation(operationId, inventoryId));
            db.SaveChanges();
        }

        using var second = CreateContext();
        second.StockOperations.Add(NewOperation(operationId, inventoryId));

        Assert.ThrowsAny<DbUpdateException>(() => second.SaveChanges());
    }

    private static StockOperationEntity NewOperation(Guid operationId, Guid inventoryId) => new()
    {
        OperationId = operationId,
        InventoryId = inventoryId,
        Kind = "Add",
        StockEntryId = Guid.NewGuid(),
        Name = "Steel Bolts",
        UnitCanonicalName = "each",
        LocationName = null,
        Note = null,
        PreviousQuantity = 0m,
        ResultingQuantity = 12.5m,
        CreatedEntry = true,
        NotePreserved = false,
        AppliedAt = DateTimeOffset.UtcNow,
    };
```

`SeedInventoryAndUnit()` and `CreateContext()` are the helpers that class already has; `Microsoft.EntityFrameworkCore` and the entity namespace are already imported there.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~StockEntryRelationalModelTests"`
Expected: FAIL to build with `CS0246: The type or namespace name 'StockOperationEntity' could not be found`.

- [ ] **Step 3: Add the concurrency stamp**

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockEntryEntity.cs`, add this property after `CreatedAt`:

```csharp
    /// <summary>
    /// Optimistic concurrency guard, regenerated on every Quantity change: a plain compared column -
    /// not a provider-specific rowversion type - so the same concurrency check works identically
    /// against SQLite (fast tests) and SQL Server (production). A mutation reads this row, decides,
    /// then writes conditioned on the value it read; a concurrent writer changes it first, so the
    /// loser's save fails with
    /// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> instead of both
    /// silently succeeding and one amount being lost.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockEntryEntityConfiguration.cs`, add this line directly beneath the `Quantity` precision line:

```csharp
        builder.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();
```

- [ ] **Step 4: Add the operation ledger row and its mapping**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockOperationEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger row for one applied Inventory mutation, keyed by the operation identity derived
/// from the Turn and tool that produced it. Its whole purpose is retry safety: a replayed Turn finds
/// its own row here and re-reports exactly what it did, so the same Add can never be applied twice by
/// a redelivery, a restart, or a competing replica.
///
/// It stores the semantic facts an answer needs and nothing more - no prompts, no raw payloads, no
/// concurrency stamps, and no audit identity.
/// </summary>
public sealed class StockOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid InventoryId { get; set; }

    /// <summary>The mutation kind as text ("Add", "Remove", "Set"), so the ledger reads as itself without a lookup.</summary>
    public required string Kind { get; set; }

    public Guid StockEntryId { get; set; }

    public required string Name { get; set; }

    public required string UnitCanonicalName { get; set; }

    public string? LocationName { get; set; }

    public string? Note { get; set; }

    public decimal PreviousQuantity { get; set; }

    public decimal ResultingQuantity { get; set; }

    public bool CreatedEntry { get; set; }

    public bool NotePreserved { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockOperationEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class StockOperationEntityConfiguration : IEntityTypeConfiguration<StockOperationEntity>
{
    /// <summary>Matches <c>UnitEntityConfiguration</c>'s canonical name length, since this column stores a copy of one.</summary>
    private const int UnitCanonicalNameLength = 100;

    public void Configure(EntityTypeBuilder<StockOperationEntity> builder)
    {
        builder.ToTable("StockOperations");

        // The operation identity IS the key, so recording the same operation twice is impossible by
        // construction rather than by convention: the second insert cannot land at all.
        builder.HasKey(e => e.OperationId);

        builder.Property(e => e.Kind).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(StockEntry.MaxNameLength).IsRequired();
        builder.Property(e => e.UnitCanonicalName).HasMaxLength(UnitCanonicalNameLength).IsRequired();
        builder.Property(e => e.LocationName).HasMaxLength(Location.MaxNameLength);
        builder.Property(e => e.Note).HasMaxLength(StockEntry.MaxNoteLength);

        // The same precision and scale StockEntries uses, so a recorded amount is byte-for-byte the
        // amount that was written and a retry re-reports it exactly.
        builder.Property(e => e.PreviousQuantity).HasPrecision(28, 10);
        builder.Property(e => e.ResultingQuantity).HasPrecision(28, 10);

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Supports a later retention sweep over old ledger rows. It is not exercised by this ticket,
        // matching how InventoryAuditEntityConfiguration indexes its own expiry up front.
        builder.HasIndex(e => e.AppliedAt);
    }
}
```

In `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`, add this DbSet directly after `StockEntries`:

```csharp
    public DbSet<StockOperationEntity> StockOperations => Set<StockOperationEntity>();
```

- [ ] **Step 5: Generate the migration**

```bash
dotnet tool install --global dotnet-ef --version 10.0.11 || true
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddStockMutationLedger \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --output-dir Persistence/Migrations
```

Expected: a new `<timestamp>_AddStockMutationLedger.cs`, its `.Designer.cs`, and an updated `MultiChannelAgentDbContextModelSnapshot.cs`.

Read the generated `Up` and confirm it does exactly two things:

1. `AddColumn<Guid>(name: "ConcurrencyStamp", table: "StockEntries", type: "uniqueidentifier", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000"))`. Leave that default exactly as generated. A shared initial value is harmless: the stamp is compared per row and regenerated on every write, so two different rows sharing a starting value can never make one writer's update match another row.
2. `CreateTable(name: "StockOperations", ...)` with `OperationId` as the primary key, the `IX_StockOperations_AppliedAt` index, and the `FK_StockOperations_Inventories_InventoryId` foreign key.

Do not hand-edit the generated file for anything else. In particular do not swap the default for `NEWID()`: the SQLite-backed tests build this same model with `EnsureCreated`, and `NEWID()` is not SQLite SQL.

A note on SQL Server behavior worth knowing while reviewing the generated script: `OperationId` is a derived (hash-shaped, effectively random) Guid, so the clustered primary key on `StockOperations` will see page splits as rows arrive. That is acceptable here because every access is a single-row point lookup by that exact key and the table is append-only - there is no range scan to keep contiguous. The Equivalent Stock filtered unique indexes on `StockEntries` are unchanged by this migration, and they remain what serializes two concurrent creates of the same Equivalent Stock.

- [ ] **Step 6: Verify the migration script generates**

Run:
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations script \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --idempotent \
  --output ./migrations-check.sql
```
Expected: succeeds, and `./migrations-check.sql` contains `CREATE TABLE [StockOperations]` and `ALTER TABLE [StockEntries] ADD [ConcurrencyStamp]`.

Then remove the scratch file: `rm ./migrations-check.sql`

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~StockEntryRelationalModelTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Persistence tests/MultiChannelAgent.IntegrationTests/Inventories/StockEntryRelationalModelTests.cs
git commit -m "feat(infrastructure): record stock operations and guard entries with a stamp for #31"
```

---

## Task 10: The atomic SQL mutation store

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockMutationStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockMutationStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockMutationStoreTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free proof against a real relational engine of the four properties a stock mutation
/// must have: the Stock Entry change, its audit fact, and its ledger row commit together or not at
/// all; the same operation identity never applies twice; a target that moved since the caller planned
/// is refused rather than overwritten; and two concurrent creates of the same Equivalent Stock cannot
/// both land.
/// </summary>
public sealed class SqlStockMutationStoreTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();
    private readonly ParticipantId _actorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public SqlStockMutationStoreTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Creating_stock_writes_the_entry_its_audit_fact_and_its_ledger_row_together()
    {
        using var db = CreateContext();

        var result = await new SqlStockMutationStore(db).ApplyAsync(CreateCommand(), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.Applied, result.Outcome);
        Assert.True(result.Recorded!.CreatedEntry);
        Assert.Equal("12.5", result.Recorded.ResultingQuantity.ToInvariantText());
        Assert.Equal("each", result.Recorded.UnitCanonicalName);

        using var reader = CreateContext();
        var entry = Assert.Single(reader.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventoryId));
        Assert.Equal(12.5m, entry.Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        var fact = Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
        Assert.Equal("Add:Created", fact.OutcomeCode);
        Assert.Null(fact.SubjectParticipantId);
        Assert.Equal(_actorId.ToString(), fact.ActorId);
    }

    [Fact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_and_changes_nothing()
    {
        var command = CreateCommand();

        using (var db = CreateContext())
        {
            await new SqlStockMutationStore(db).ApplyAsync(command, CancellationToken.None);
        }

        using var retryContext = CreateContext();
        var retry = await new SqlStockMutationStore(retryContext).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.AlreadyApplied, retry.Outcome);
        Assert.Equal("12.5", retry.Recorded!.ResultingQuantity.ToInvariantText());

        using var reader = CreateContext();
        Assert.Equal(12.5m, Assert.Single(reader.StockEntries.AsNoTracking()).Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    [Fact]
    public async Task A_target_whose_Quantity_changed_since_the_caller_planned_is_refused_outright()
    {
        var entryId = SeedStock("Steel Bolts", 10m);

        // A competing writer commits first.
        using (var competitor = CreateContext())
        {
            var row = competitor.StockEntries.Single(e => e.Id == entryId);
            row.Quantity = 4m;
            row.ConcurrencyStamp = Guid.NewGuid();
            await competitor.SaveChangesAsync();
        }

        using var db = CreateContext();
        var result = await new SqlStockMutationStore(db).ApplyAsync(
            UpdateCommand(entryId, expected: 10m, resulting: 15m), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.StateChanged, result.Outcome);
        Assert.Null(result.Recorded);

        using var reader = CreateContext();
        Assert.Equal(4m, reader.StockEntries.AsNoTracking().Single(e => e.Id == entryId).Quantity);
        Assert.Empty(reader.StockOperations.AsNoTracking());
        Assert.Empty(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    [Fact]
    public async Task Two_creates_of_the_same_Equivalent_Stock_cannot_both_land()
    {
        using (var first = CreateContext())
        {
            await new SqlStockMutationStore(first).ApplyAsync(CreateCommand(), CancellationToken.None);
        }

        using var second = CreateContext();
        var result = await new SqlStockMutationStore(second).ApplyAsync(
            CreateCommand(operationId: new StockOperationId(Guid.NewGuid())), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.StateChanged, result.Outcome);

        using var reader = CreateContext();
        Assert.Single(reader.StockEntries.AsNoTracking().Where(e => e.NormalizedName == "steel bolts"));
    }

    private StockMutationCommand CreateCommand(StockOperationId? operationId = null) => new()
    {
        OperationId = operationId ?? new StockOperationId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
        InventoryId = new InventoryId(_inventoryId),
        ActorId = _actorId,
        Kind = StockMutationKind.Add,
        Amount = Quantity.Create(12.5m),
        ResultingQuantity = Quantity.Create(12.5m),
        NewEntryName = "Steel Bolts",
        NewEntryUnitId = new UnitId(_unitId),
        NewEntryLocationId = null,
        Note = null,
        NotePreserved = false,
        Now = Now,
    };

    private StockMutationCommand UpdateCommand(Guid entryId, decimal expected, decimal resulting) => new()
    {
        OperationId = new StockOperationId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd")),
        InventoryId = new InventoryId(_inventoryId),
        ActorId = _actorId,
        Kind = StockMutationKind.Add,
        Amount = Quantity.Create(resulting - expected),
        ResultingQuantity = Quantity.Create(resulting),
        StockEntryId = new StockEntryId(entryId),
        ExpectedQuantity = Quantity.Create(expected),
        NotePreserved = false,
        Now = Now,
    };

    private Guid SeedStock(string name, decimal quantity)
    {
        using var db = CreateContext();
        var id = Guid.NewGuid();
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = id,
            InventoryId = _inventoryId,
            UnitId = _unitId,
            LocationId = null,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = Now,
        });
        db.SaveChanges();
        return id;
    }

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = _actorId.Value,
            DisplayName = "Editor Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = _actorId.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            CreatedAt = Now,
        });
        db.SaveChanges();
    }

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockMutationStoreTests"`
Expected: FAIL to build with `CS0246: The type or namespace name 'SqlStockMutationStore' could not be found`.

- [ ] **Step 3: Write the SQL mutation store**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockMutationStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockMutationStore"/>. The Stock Entry change, its minimal semantic
/// audit fact, and its operation ledger row are staged against one
/// <see cref="MultiChannelAgentDbContext"/> and committed by a single
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call, which the provider executes as
/// one transaction: current state and the audit of it can never disagree, and a mutation can never be
/// applied without its ledger row - the very row that stops a retry applying it again.
///
/// The ledger row commits with the state change rather than after it deliberately. The terminal
/// Outcome, the Delivery, and inbox completion are written later, by
/// <see cref="Application.Turns.ITurnResultStore"/>, in their own single atomic write; if the process
/// dies in between, the Turn is reprocessed, derives the same
/// <see cref="StockOperationId"/>, finds this ledger row, and re-reports the effect instead of
/// applying a second one. That is what makes the two writes safe to be two.
/// </summary>
public sealed class SqlStockMutationStore(MultiChannelAgentDbContext db) : IStockMutationStore
{
    public async Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        var alreadyApplied = await db.StockOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == command.OperationId.Value, cancellationToken);

        if (alreadyApplied is not null)
        {
            return new StockMutationStoreResult(StockMutationStoreOutcome.AlreadyApplied, ToRecorded(alreadyApplied));
        }

        return command.StockEntryId is { } targetId
            ? await ChangeAsync(command, targetId, cancellationToken)
            : await CreateAsync(command, cancellationToken);
    }

    private async Task<StockMutationStoreResult> ChangeAsync(
        StockMutationCommand command, StockEntryId targetId, CancellationToken cancellationToken)
    {
        var entry = await db.StockEntries.FirstOrDefaultAsync(
            e => e.Id == targetId.Value && e.InventoryId == command.InventoryId.Value, cancellationToken);

        // The caller decided this change against a Quantity it read a moment ago. If the row is gone,
        // or no longer carries that Quantity, a competing writer got there first: the decision is
        // stale, so it is refused rather than applied on top of a state nobody chose.
        if (entry is null || Quantity.Create(entry.Quantity) != command.ExpectedQuantity)
        {
            db.ChangeTracker.Clear();
            return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
        }

        var unitCanonicalName = await UnitCanonicalNameAsync(entry.UnitId, cancellationToken);
        var locationName = await LocationNameAsync(entry.LocationId, cancellationToken);

        entry.Quantity = command.ResultingQuantity.Value;
        entry.ConcurrencyStamp = Guid.NewGuid();

        var recorded = new RecordedStockMutation(
            new StockEntryId(entry.Id),
            entry.Name,
            unitCanonicalName,
            locationName,
            entry.Note,
            command.ExpectedQuantity!.Value,
            command.ResultingQuantity,
            CreatedEntry: false,
            command.NotePreserved);

        StageLedgerAndAudit(command, recorded, createdEntry: false);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A competing writer committed against this same row between the read above and this
            // save. Nothing here was persisted, so the ledger row and the audit fact staged alongside
            // it are discarded together with the change.
            db.ChangeTracker.Clear();
            return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
        }

        return new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded);
    }

    private async Task<StockMutationStoreResult> CreateAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        // The domain factory validates and normalizes the name and Note, so persistence never sees a
        // value the domain would have refused.
        var entry = StockEntry.Create(
            command.InventoryId,
            command.NewEntryUnitId!.Value,
            command.NewEntryLocationId,
            command.NewEntryName,
            command.Note,
            command.ResultingQuantity,
            command.Now);

        var unitCanonicalName = await UnitCanonicalNameAsync(entry.UnitId.Value, cancellationToken);
        var locationName = await LocationNameAsync(entry.LocationId?.Value, cancellationToken);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = entry.Id.Value,
            InventoryId = entry.InventoryId.Value,
            UnitId = entry.UnitId.Value,
            LocationId = entry.LocationId?.Value,
            Name = entry.Name,
            NormalizedName = entry.NormalizedName,
            Note = entry.Note,
            Quantity = entry.Quantity.Value,
            CreatedAt = entry.CreatedAt,
        });

        var recorded = new RecordedStockMutation(
            entry.Id,
            entry.Name,
            unitCanonicalName,
            locationName,
            entry.Note,
            Quantity.Zero,
            entry.Quantity,
            CreatedEntry: true,
            command.NotePreserved);

        StageLedgerAndAudit(command, recorded, createdEntry: true);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Equivalent Stock is unique in the database, so a competing writer that created this very
            // Stock Entry first makes this insert fail. Classify that as the state having changed only
            // when the equivalent row genuinely now exists; any other failure is a real fault and must
            // keep propagating rather than being reported as a routine conflict.
            db.ChangeTracker.Clear();

            if (await EquivalentExistsAsync(command.InventoryId, entry, cancellationToken))
            {
                return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
            }

            throw;
        }

        return new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded);
    }

    private void StageLedgerAndAudit(StockMutationCommand command, RecordedStockMutation recorded, bool createdEntry)
    {
        db.StockOperations.Add(new StockOperationEntity
        {
            OperationId = command.OperationId.Value,
            InventoryId = command.InventoryId.Value,
            Kind = command.Kind.ToString(),
            StockEntryId = recorded.StockEntryId.Value,
            Name = recorded.Name,
            UnitCanonicalName = recorded.UnitCanonicalName,
            LocationName = recorded.LocationName,
            Note = recorded.Note,
            PreviousQuantity = recorded.PreviousQuantity.Value,
            ResultingQuantity = recorded.ResultingQuantity.Value,
            CreatedEntry = recorded.CreatedEntry,
            NotePreserved = recorded.NotePreserved,
            AppliedAt = command.Now,
        });

        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
            StockAuditFacts.EventTypeFor(command.Kind),
            AuditActorKind.Participant,
            command.ActorId.ToString(),
            command.InventoryId,
            subjectParticipantId: null,
            StockAuditFacts.OutcomeCodeFor(command.Kind, createdEntry),
            command.Now)));
    }

    /// <summary>
    /// Whether the exact Equivalent Stock this create aimed at now exists. Unlocated Stock is the
    /// absence of a Location, so it is asked for as such rather than compared to a null parameter,
    /// which relational NULL semantics would never match.
    /// </summary>
    private async Task<bool> EquivalentExistsAsync(InventoryId inventoryId, StockEntry entry, CancellationToken cancellationToken)
    {
        var rows = db.StockEntries.AsNoTracking().Where(e =>
            e.InventoryId == inventoryId.Value
            && e.NormalizedName == entry.NormalizedName
            && e.UnitId == entry.UnitId.Value);

        rows = entry.LocationId is { } locationId
            ? rows.Where(e => e.LocationId == locationId.Value)
            : rows.Where(e => e.LocationId == null);

        return await rows.AnyAsync(cancellationToken);
    }

    private async Task<string> UnitCanonicalNameAsync(Guid unitId, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking().Where(u => u.Id == unitId).Select(u => u.CanonicalName).FirstAsync(cancellationToken);

    private async Task<string?> LocationNameAsync(Guid? locationId, CancellationToken cancellationToken) =>
        locationId is { } id
            ? await db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => l.Name).FirstAsync(cancellationToken)
            : null;

    private static RecordedStockMutation ToRecorded(StockOperationEntity entity) => new(
        new StockEntryId(entity.StockEntryId),
        entity.Name,
        entity.UnitCanonicalName,
        entity.LocationName,
        entity.Note,
        Quantity.Create(entity.PreviousQuantity),
        Quantity.Create(entity.ResultingQuantity),
        entity.CreatedEntry,
        entity.NotePreserved);
}
```

- [ ] **Step 4: Register the store and the service**

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add this line directly after the `IStockStore` registration:

```csharp
        services.AddScoped<IStockMutationStore, SqlStockMutationStore>();
```

and this line directly after the `StockFindingService` registration:

```csharp
        services.AddScoped<StockMutationService>();
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockMutationStoreTests"`
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Inventories/SqlStockMutationStore.cs src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockMutationStoreTests.cs
git commit -m "feat(infrastructure): apply a stock mutation and its audit in one transaction for #31"
```

---

## Task 11: Concurrent writers cannot both apply

**Files:**
- Modify: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockMutationStoreTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `SqlStockMutationStoreTests`:

```csharp
    // The window the concurrency stamp exists to close: two callers both read the same Quantity, both
    // decide, and both try to save. Exactly one may win; the loser must change nothing.
    [Fact]
    public async Task Two_callers_that_both_read_the_same_Quantity_cannot_both_apply()
    {
        var entryId = SeedStock("Steel Bolts", 10m);

        using var firstContext = CreateContext();
        using var secondContext = CreateContext();

        // Both load the row (and so both hold the same concurrency stamp) before either saves.
        _ = await firstContext.StockEntries.FirstAsync(e => e.Id == entryId);
        _ = await secondContext.StockEntries.FirstAsync(e => e.Id == entryId);

        var first = await new SqlStockMutationStore(firstContext).ApplyAsync(
            UpdateCommand(entryId, expected: 10m, resulting: 15m), CancellationToken.None);

        var second = await new SqlStockMutationStore(secondContext).ApplyAsync(
            SecondUpdateCommand(entryId, expected: 10m, resulting: 12m), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.Applied, first.Outcome);
        Assert.Equal(StockMutationStoreOutcome.StateChanged, second.Outcome);

        using var reader = CreateContext();
        Assert.Equal(15m, reader.StockEntries.AsNoTracking().Single(e => e.Id == entryId).Quantity);
        Assert.Single(reader.StockOperations.AsNoTracking());
        Assert.Single(reader.InventoryAudits.AsNoTracking().Where(a => a.EventType == "StockAdded"));
    }

    private StockMutationCommand SecondUpdateCommand(Guid entryId, decimal expected, decimal resulting) =>
        UpdateCommand(entryId, expected, resulting) with
        {
            OperationId = new StockOperationId(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")),
        };
```

- [ ] **Step 2: Run the test to verify it fails or passes for the right reason**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockMutationStoreTests"`
Expected: PASS, 5 tests. The second caller is refused because its own already-loaded, now-stale row makes the conditioned `UPDATE` affect zero rows, which EF Core reports as `DbUpdateConcurrencyException`.

If this test instead fails with `Assert.Equal() Failure: Expected StateChanged, Actual Applied`, the concurrency token is not configured - go back to Task 9 Step 3 and confirm `IsConcurrencyToken()` is present, then rerun.

- [ ] **Step 3: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockMutationStoreTests.cs
git commit -m "test(infrastructure): prove two concurrent writers cannot both apply for #31"
```

---

## Task 12: Showing a mutation in the web conversation and refreshing the workspace

**Files:**
- Modify: `src/web/src/turnsApi.ts`
- Modify: `src/web/src/TurnTracer.tsx`

The refresh itself already works: `TurnTracer` calls `onTerminalOutcome` for every terminal Outcome, and `App` bumps `stockRefetchToken`, which is `StockWorkspace`'s refetch trigger. What is missing is that a mutation Outcome's payload has no type and renders as nothing, so a Participant sees a bare summary and cannot tell what the workspace is about to show.

- [ ] **Step 1: Add the mutation payload types**

In `src/web/src/turnsApi.ts`, add these directly above `export type TurnOutcomePayload`:

```ts
/** One Stock Entry as it stands after a mutation. Quantities are exact decimal text, never numbers. */
export interface StockMutationEntryView {
  stockEntryId: string;
  name: string;
  unit: string;
  location: string | null;
  note: string | null;
  previousQuantity: string;
  quantity: string;
  created: boolean;
  /** True when a proposed Note was deliberately not applied because the Stock Entry already existed. */
  notePreserved: boolean;
}

export interface StockMutationPayload {
  version: number;
  kind: 'stock_mutation';
  operation: 'add' | 'remove' | 'set';
  entry: StockMutationEntryView;
}
```

and change the union to:

```ts
export type TurnOutcomePayload = StockListPayload | StockFindPayload | StockMutationPayload;
```

- [ ] **Step 2: Render the mutation result**

In `src/web/src/TurnTracer.tsx`, add this component directly beneath `NarrowingHints`:

```tsx
function StockMutationResult({ payload }: { payload: StockMutationPayload }) {
  const { entry } = payload;

  return (
    <>
      <h3>{entry.created ? 'Created' : 'Updated'}</h3>
      <dl>
        <dt>Stock Entry</dt>
        <dd>{entry.name}</dd>
        <dt>Unit</dt>
        <dd>{entry.unit}</dd>
        <dt>Location</dt>
        <dd>{entry.location ?? 'Unlocated'}</dd>
        <dt>Quantity</dt>
        <dd>
          {entry.previousQuantity} → {entry.quantity}
        </dd>
        {entry.note !== null && (
          <>
            <dt>Note</dt>
            <dd>{entry.note}</dd>
          </>
        )}
      </dl>
      {entry.notePreserved && <p>The existing Note was kept unchanged.</p>}
    </>
  );
}
```

Add `StockMutationPayload` to the import from `./turnsApi`:

```tsx
import {
  getTurnOutcome,
  submitTurn,
  type StockMutationPayload,
  type StockNarrowingHints,
  type StockRowView,
  type TurnOutcomeView,
} from './turnsApi';
```

Add this render branch directly after the `stock_find` branch:

```tsx
          {outcome.payload?.kind === 'stock_mutation' && <StockMutationResult payload={outcome.payload} />}
```

Replace the hint paragraph with one that names the mutations too:

```tsx
      <p>
        Read: <code>list stock</code>, <code>list stock including zero</code>, <code>list stock in &lt;location&gt;</code>,{' '}
        <code>list stock unit &lt;unit&gt;</code>, <code>list stock unlocated</code>,{' '}
        <code>list stock page size &lt;n&gt;</code>, or <code>find &lt;name&gt;</code>.
      </p>
      <p>
        Change: <code>add stock &lt;name&gt; quantity &lt;n&gt;</code>,{' '}
        <code>remove stock &lt;name&gt; quantity &lt;n&gt;</code>, or <code>set stock &lt;name&gt; quantity &lt;n&gt;</code>.
        Add <code>unit &lt;unit&gt;</code>, <code>in &lt;location&gt;</code>, <code>unlocated</code>, or{' '}
        <code>note &lt;text&gt;</code> to any of them.
      </p>
```

Update the component doc comment's last sentence to mention that mutations are included:

```tsx
 * Submits a Turn to the application boundary and renders its recorded terminal Outcome, including
 * the typed semantic List/Find/mutation payload when the Outcome carries one. Every terminal Outcome
 * also signals the parent, which is what invalidates and refetches the authoritative Inventory
 * workspace - so a mutation made in the conversation is visible in the workspace immediately.
 * Participant/ChannelConversation identity is always derived server-side; this component never
 * supplies either.
```

- [ ] **Step 3: Verify the client type-checks, builds, and lints**

Run: `npm --prefix src/web run build`
Expected: `tsc -b` succeeds and `vite build` writes `dist/`. A missing case in the payload union would fail here.

Run: `npm --prefix src/web run lint`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add src/web/src/turnsApi.ts src/web/src/TurnTracer.tsx
git commit -m "feat(web): show a stock mutation and refresh the workspace after it for #31"
```

---

## Task 13: The end-to-end conversational mutation scenario

**Files:**
- Modify: `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/StockMutationScenario.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/StockMutationSqliteTests.cs`
- Modify: `tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs`

This is the highest-value correctness seam the parent spec names: a normalized Turn submitted to the production channel-neutral workflow, asserted on durable external effects only.

- [ ] **Step 1: Let a scenario create a Viewer**

In `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs`, add this property beside `CsrfToken`:

```csharp
    /// <summary>The tenant identifier this client signed in as - what an Owner names when granting it a role.</summary>
    public string ParticipantIdentifier { get; private set; } = string.Empty;
```

In `SignInAsync`, replace the sign-in request block so the generated identifier is kept rather than discarded:

```csharp
        participant.ParticipantIdentifier = Guid.NewGuid().ToString();

        var signInResponse = await participant.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = participant.ParticipantIdentifier,
                displayName,
                activeTenantMember = true,
            }),
        });
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);
```

Then add this method after `SelectInventoryAsync`:

```csharp
    /// <summary>Grants another Participant a role in an Inventory this client Owns.</summary>
    public async Task GrantMembershipAsync(Guid inventoryId, string targetIdentifier, string role)
    {
        var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Put, $"/api/inventories/{inventoryId}/members")
            {
                Content = JsonContent.Create(new { targetIdentifier, role }),
            },
            withCsrf: true);

        Assert.True(response.IsSuccessStatusCode, $"Granting {role} failed with {response.StatusCode}.");
    }
```

- [ ] **Step 2: Write the failing scenario**

Create `tests/MultiChannelAgent.IntegrationTests/StockMutationScenario.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// One signed-in web Participant Adding, Removing, and Setting Stock conversationally through the real
/// HTTP application boundary: exact decimal Quantity, Equivalent Stock rather than duplicates, an
/// existing Note kept, underflow refused, Set-to-zero held for confirmation, every typed refusal, one
/// audit fact per completed mutation, retries that cannot double an effect, and the authoritative
/// workspace projection agreeing after each change. Shared by the SQL Server-backed scenario and its
/// Docker-free SQLite twin so both prove the identical externally observable behavior.
/// </summary>
internal static class StockMutationScenario
{
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Mutating Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Mutation Warehouse");

        // Add creates Equivalent Stock at the exact decimal amount, with the Note it was given.
        var created = await CompleteAsync(factory, owner, "native-add-1", "add stock Steel Bolts quantity 12.5 note Blue box");
        var createdEntry = MutationEntry(created, "add");
        Assert.True(createdEntry.GetProperty("created").GetBoolean());
        Assert.Equal("Steel Bolts", createdEntry.GetProperty("name").GetString());
        Assert.Equal("each", createdEntry.GetProperty("unit").GetString());
        Assert.Null(createdEntry.GetProperty("location").GetString());
        Assert.Equal("Blue box", createdEntry.GetProperty("note").GetString());
        Assert.Equal("0", createdEntry.GetProperty("previousQuantity").GetString());
        Assert.Equal("12.5", createdEntry.GetProperty("quantity").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "12.5");

        // Add again increases the SAME Stock Entry and keeps its conflicting Note rather than
        // overwriting it - and says so.
        var increased = await CompleteAsync(factory, owner, "native-add-2", "add stock steel bolts quantity 2.25 note Red box");
        var increasedEntry = MutationEntry(increased, "add");
        Assert.False(increasedEntry.GetProperty("created").GetBoolean());
        Assert.Equal(createdEntry.GetProperty("stockEntryId").GetString(), increasedEntry.GetProperty("stockEntryId").GetString());
        Assert.Equal("14.75", increasedEntry.GetProperty("quantity").GetString());
        Assert.Equal("Blue box", increasedEntry.GetProperty("note").GetString());
        Assert.True(increasedEntry.GetProperty("notePreserved").GetBoolean());
        Assert.Equal(1, await CountStockEntriesAsync(factory, inventoryId));

        // Remove beyond the Quantity on hand is refused, and changes nothing.
        var underflow = await OutcomeAsync(factory, owner, "native-remove-1", "remove stock Steel Bolts quantity 20");
        Assert.Equal("conflict", underflow.GetProperty("category").GetString());
        Assert.Equal("insufficient_quantity", underflow.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "14.75");

        // Remove within it decreases exactly.
        var removed = await CompleteAsync(factory, owner, "native-remove-2", "remove stock Steel Bolts quantity 4.75");
        Assert.Equal("10", MutationEntry(removed, "remove").GetProperty("quantity").GetString());

        // Set replaces exactly.
        var set = await CompleteAsync(factory, owner, "native-set-1", "set stock Steel Bolts quantity 7.125");
        Assert.Equal("7.125", MutationEntry(set, "set").GetProperty("quantity").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // Set to zero clears stock, so it is held for explicit confirmation and applies nothing.
        var confirmation = await OutcomeAsync(factory, owner, "native-set-zero", "set stock Steel Bolts quantity 0");
        Assert.Equal("confirmation_required", confirmation.GetProperty("category").GetString());
        Assert.Equal("confirmation_required", confirmation.GetProperty("code").GetString());
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // A Quantity that is not exact invariant decimal text is refused as invalid.
        var invalid = await OutcomeAsync(factory, owner, "native-invalid-1", "add stock Steel Bolts quantity lots");
        Assert.Equal("invalid", invalid.GetProperty("category").GetString());
        Assert.Equal("invalid_quantity", invalid.GetProperty("code").GetString());

        // Stock that is not there is simply not found.
        var missing = await OutcomeAsync(factory, owner, "native-missing-1", "remove stock Brass Rivets quantity 1");
        Assert.Equal("not_found", missing.GetProperty("category").GetString());

        // A Location this Inventory does not have is reported, never created implicitly.
        var unknownReference = await OutcomeAsync(factory, owner, "native-unknown-1", "add stock Steel Bolts quantity 1 in Loading Bay");
        Assert.Equal("not_found", unknownReference.GetProperty("category").GetString());
        Assert.Equal("reference_not_found", unknownReference.GetProperty("code").GetString());

        // Four completed mutations so far (create, increase, remove, set), and exactly one minimal
        // audit fact and one ledger row each. The refused ones left nothing behind at all.
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));
        Assert.Equal(4, await CountStockOperationsAsync(factory, inventoryId));

        // Retrying the very same native message never applies a second effect: the recorded Outcome
        // comes straight back, no Turn is reprocessed, and Stock is untouched.
        var duplicate = await owner.SubmitTurnAsync("native-add-2", "add stock steel bolts quantity 2.25 note Red box");
        var duplicateBody = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("14.75", MutationEntry(duplicateBody, "add").GetProperty("quantity").GetString());
        Assert.Equal(0, await ProcessPendingAsync(factory));
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));
        Assert.Equal(4, await CountStockOperationsAsync(factory, inventoryId));
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // A Viewer may see this Inventory but may not change it, and the refusal touches nothing.
        var viewer = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Watching Viewer");
        await owner.GrantMembershipAsync(inventoryId, viewer.ParticipantIdentifier, "Viewer");
        await viewer.SelectInventoryAsync(inventoryId);

        var forbidden = await OutcomeAsync(factory, viewer, "native-viewer-1", "add stock Steel Bolts quantity 1");
        Assert.Equal("forbidden", forbidden.GetProperty("category").GetString());
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));
        await AssertProjectionAsync(owner, inventoryId, "Steel Bolts", "7.125");

        // An ambiguous reference offers candidates rather than guessing which Stock Entry was meant.
        await SeedLocatedStockAsync(factory, inventoryId, "Steel Bolts", 3m, "Shelf A");
        var ambiguous = await OutcomeAsync(factory, owner, "native-ambiguous-1", "add stock Steel Bolts quantity 1");
        Assert.Equal("ambiguous", ambiguous.GetProperty("category").GetString());
        Assert.Equal("stock_find", ambiguous.GetProperty("payload").GetProperty("kind").GetString());
        Assert.Equal(2, ambiguous.GetProperty("payload").GetProperty("candidates").EnumerateArray().Count());
        Assert.Equal(4, await CountStockAuditsAsync(factory, inventoryId));

        // Naming the Location makes it exact again.
        var narrowed = await CompleteAsync(factory, owner, "native-narrowed-1", "add stock Steel Bolts quantity 1 in Shelf A");
        var narrowedEntry = MutationEntry(narrowed, "add");
        Assert.Equal("Shelf A", narrowedEntry.GetProperty("location").GetString());
        Assert.Equal("4", narrowedEntry.GetProperty("quantity").GetString());
    }

    /// <summary>Submits one Turn, drives processing deterministically, and returns its recorded terminal Outcome.</summary>
    private static async Task<JsonElement> OutcomeAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var turnId = await client.SubmitAcceptedTurnAsync(nativeMessageId, contentText);
        Assert.Equal(1, await ProcessPendingAsync(factory));

        var outcome = await client.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);
        return outcome!.Value;
    }

    /// <summary>The same, asserting the Turn completed rather than merely reaching some terminal Outcome.</summary>
    private static async Task<JsonElement> CompleteAsync(
        WebApplicationFactory<Program> factory, ConversationTestClient client, string nativeMessageId, string contentText)
    {
        var outcome = await OutcomeAsync(factory, client, nativeMessageId, contentText);
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal("completed", outcome.GetProperty("category").GetString());
        return outcome;
    }

    private static JsonElement MutationEntry(JsonElement outcome, string expectedOperation)
    {
        var payload = outcome.GetProperty("payload");
        Assert.Equal("stock_mutation", payload.GetProperty("kind").GetString());
        Assert.Equal(expectedOperation, payload.GetProperty("operation").GetString());
        return payload.GetProperty("entry");
    }

    /// <summary>
    /// Asserts the authoritative workspace projection - the very endpoint the Inventory panel refetches
    /// once a terminal Outcome arrives - already reports what the conversation just changed.
    /// </summary>
    private static async Task AssertProjectionAsync(
        ConversationTestClient client, Guid inventoryId, string name, string expectedQuantity)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock"));
        var projection = await response.Content.ReadFromJsonAsync<JsonElement>();

        var row = projection.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == name && r.GetProperty("location").ValueKind == JsonValueKind.Null);

        Assert.Equal(expectedQuantity, row.GetProperty("quantity").GetString());
    }

    private static async Task<int> ProcessPendingAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>()
            .ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<int> CountStockEntriesAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockEntries.AsNoTracking().CountAsync(e => e.InventoryId == inventoryId);
    }

    private static async Task<int> CountStockAuditsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.InventoryAudits.AsNoTracking().CountAsync(a =>
            a.InventoryId == inventoryId
            && (a.EventType == "StockAdded" || a.EventType == "StockRemoved" || a.EventType == "StockSet"));
    }

    private static async Task<int> CountStockOperationsAsync(WebApplicationFactory<Program> factory, Guid inventoryId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.StockOperations.AsNoTracking().CountAsync(o => o.InventoryId == inventoryId);
    }

    /// <summary>
    /// Seeds a second Stock Entry with the same name in a real Location, so the next reference to that
    /// name genuinely matches more than one Stock Entry.
    /// </summary>
    private static async Task SeedLocatedStockAsync(
        WebApplicationFactory<Program> factory, Guid inventoryId, string name, decimal quantity, string locationName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unitId = db.Units.AsNoTracking().Single(u => u.InventoryId == inventoryId).Id;
        var locationId = Guid.NewGuid();

        db.Locations.Add(new LocationEntity
        {
            Id = locationId,
            InventoryId = inventoryId,
            Name = locationName,
            NormalizedName = NameNormalization.Normalize(locationName),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 3: Run the scenario through the Docker-free twin to verify it fails**

Create `tests/MultiChannelAgent.IntegrationTests/StockMutationSqliteTests.cs`:

```csharp
namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The conversational stock mutation scenario against SQLite: the same externally observable behavior
/// as <see cref="StockConversationScenarioTests"/> proves against SQL Server, with no Docker needed.
/// </summary>
public sealed class StockMutationSqliteTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Adding_removing_and_setting_stock_through_a_web_conversation_behaves_exactly_as_specified() =>
        await StockMutationScenario.RunAsync(_factory!);
}
```

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~StockMutationSqliteTests"`
Expected: FAIL on the first assertion the pipeline does not yet satisfy. If every earlier task landed correctly it may instead PASS on the first run - that is a legitimate outcome for an integration scenario written after its units, and nothing needs to be manufactured to make it go red. Never weaken an assertion to make this test pass.

- [ ] **Step 4: Fix whatever the scenario exposes, then run it again**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~StockMutationSqliteTests"`
Expected: PASS, 1 test.

Most likely fixes, in order of likelihood:
1. The scripted grammar splits a reference differently than expected - check `find stock`-style clause splitting for `note` and `quantity` (Task 8).
2. `StockMutationService` is not registered in DI (Task 10 Step 4).
3. Granting the Viewer role returns a non-success status - read `InventoryGovernanceEndpoints`'s `PUT /members` handler and correct the request body in `GrantMembershipAsync` rather than changing the endpoint.

- [ ] **Step 5: Run the same scenario against SQL Server with production migrations**

In `tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs`, append this test to the class:

```csharp
    // Every stock mutation acceptance criterion for #31, end to end against real SQL Server with
    // production migrations applied. StockMutationSqliteTests proves the identical behavior
    // Docker-free.
    [SkippableFact]
    public async Task Adding_removing_and_setting_stock_through_a_web_conversation_behaves_exactly_as_specified()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed Stock mutation scenario.");

        await StockMutationScenario.RunAsync(Factory!);
    }
```

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~StockConversationScenarioTests"`
Expected: PASS. This takes several minutes: it pulls and starts the SQL Server image and applies every production migration.

If Docker is genuinely unavailable in this environment, run without the environment variable and confirm the SQL-backed tests report as skipped rather than failed, then state plainly in the commit that SQL Server coverage was not executed locally and CI will gate it.

- [ ] **Step 6: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs tests/MultiChannelAgent.IntegrationTests/StockMutationScenario.cs tests/MultiChannelAgent.IntegrationTests/StockMutationSqliteTests.cs tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs
git commit -m "test(integration): mutate stock through a web conversation end to end for #31"
```

---

## Task 14: Whole-suite verification

**Files:** none created; this task fixes whatever it finds.

- [ ] **Step 1: Build exactly as CI does**

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings. `TreatWarningsAsErrors` is on, so any warning is a failure.

- [ ] **Step 2: Run every backend test**

Run: `dotnet test --configuration Release`
Expected: PASS across Domain, Application, Architecture, and Integration. SQL-backed scenarios skip when Docker is unavailable; every SQLite twin must pass regardless.

- [ ] **Step 3: Confirm the migration script still generates**

Run:
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations script \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --idempotent \
  --output ./migrations-check.sql
rm ./migrations-check.sql
```
Expected: succeeds.

- [ ] **Step 4: Build and lint the web client**

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed.

- [ ] **Step 5: Confirm the architecture boundaries still hold**

Run: `dotnet test tests/MultiChannelAgent.ArchitectureTests/MultiChannelAgent.ArchitectureTests.csproj`
Expected: PASS. `StockOperationId` lives in Domain and references `MultiChannelAgent.Domain.Turns.TurnId`, which is the same assembly and therefore fine; `StockMutationService` and `IStockMutationStore` live in Application and must reference nothing from Infrastructure.

- [ ] **Step 6: Commit any fixes**

```bash
git add -A
git commit -m "fix(inventories): settle the whole suite for conversational stock mutations for #31"
```

If nothing needed fixing, skip this commit rather than creating an empty one.

---

## Acceptance criteria coverage

| Acceptance criterion | Where it is implemented | Where it is proven |
| --- | --- | --- |
| `add_stock` creates or increases Equivalent Stock using exact decimal Quantity | Tasks 1, 2, 5, 10 | `QuantityTests`, `StockMutationPlanTests`, `StockMutationServiceTests` (`Adding_to_nothing...`, `Adding_to_existing_Equivalent_Stock...`), `SqlStockMutationStoreTests`, `StockMutationScenario` |
| `add_stock` preserves conflicting existing Notes | Task 5 (`NotePreserved`), Task 10 (create-only Note write) | `StockMutationServiceTests.Adding_never_overwrites_an_existing_Note...`, `StockMutationScenario` |
| `remove_stock` decreases Quantity | Tasks 2, 5 | `StockMutationPlanTests`, `StockMutationServiceTests.Removing_decreases...`, `StockMutationScenario` |
| `remove_stock` rejects underflow without changing state | Task 2 (`Underflow`), Task 5 (`insufficient_quantity`) | `StockMutationPlanTests`, `StockMutationServiceTests.Removing_more_than_is_on_hand...`, `StockToolDispatcherTests`, `StockMutationScenario` |
| `set_stock` applies an exact non-negative Quantity | Tasks 2, 5 | `StockMutationPlanTests`, `StockMutationServiceTests.Setting_replaces...`, `StockMutationScenario` |
| Set to zero returns `confirmation_required` | Task 2 (`ConfirmationRequired`), Task 7 (category mapping) | `StockMutationServiceTests.Setting_stock_to_zero...`, `StockToolDispatcherTests.Set_stock_to_zero...`, `StockMutationScenario` |
| Ambiguous requests return a typed non-disclosing outcome | Task 5 (Find-based resolution), Task 4 (shared narrowing) | `StockMutationServiceTests.An_ambiguous_reference...`, `StockToolDispatcherTests`, `StockMutationScenario` |
| Unknown requests return a typed non-disclosing outcome | Task 5 (`not_found`, `reference_not_found`) | `StockMutationServiceTests` (`Removing_stock_that_is_not_there...`, `A_Unit_this_Inventory_does_not_have...`), `StockMutationScenario` |
| Forbidden requests return a typed non-disclosing outcome | Task 5 (Editor authorization through `InventoryAuthorizationService`) | `StockMutationServiceTests` (`A_Viewer_may_see...`, `A_non_member_cannot_tell...`), `StockMutationScenario` |
| Invalid requests return a typed non-disclosing outcome | Tasks 1, 5 (`invalid_quantity`, `invalid_name`, `invalid_note`, `invalid_reference`, `quantity_out_of_bounds`) | `StockMutationServiceTests` invalid theories, `StockMutationScenario` |
| State-changed requests return a typed non-disclosing outcome | Task 5 (`state_changed`), Tasks 9-11 (stamp + expected Quantity) | `StockMutationServiceTests.A_target_that_changed...`, `SqlStockMutationStoreTests` (`A_target_whose_Quantity_changed...`, `Two_callers_that_both_read...`) |
| Every completed mutation atomically updates state and appends minimal semantic audit facts | Task 2 (`StockAuditFacts`), Task 10 (one `SaveChangesAsync`) | `SqlStockMutationStoreTests.Creating_stock_writes_the_entry_its_audit_fact_and_its_ledger_row_together`, `StockMutationScenario` audit counts |
| Stable operation identity prevents duplicate effects across retries | Task 3, Task 7 (derivation), Tasks 9-10 (ledger) | `StockOperationIdTests`, `StockToolDispatcherTests.Dispatching_the_same_Turns_mutation_twice...`, `SqlStockMutationStoreTests.Applying_the_same_operation_identity_again...`, `StockMutationScenario` duplicate submission |
| Optimistic concurrency prevents duplicate effects | Task 9 (`ConcurrencyStamp`), Task 10 (expected Quantity + `DbUpdateConcurrencyException`) | `SqlStockMutationStoreTests.Two_callers_that_both_read_the_same_Quantity_cannot_both_apply` |
| The web Inventory projection invalidates and refreshes after a conversational mutation | Task 12 (payload rendering; `onTerminalOutcome` already bumps `stockRefetchToken`) | `StockMutationScenario.AssertProjectionAsync` after every applied change; `npm run build` type-checks the payload union |

## Deliberate design decisions worth knowing

- **Two writes, not one.** The Stock Entry change, its audit fact, and its ledger row commit in one transaction (Task 10). The Outcome, the Delivery, and inbox completion commit in a second one (the existing `ITurnResultStore`). Merging them would mean the dispatcher returning a staged, uncommitted unit of work up through the coordinator, which the current architecture does not model. The ledger row is what makes two writes safe: a crash between them leaves the Turn pending, and reprocessing derives the same operation identity, finds the ledger row, and re-reports rather than re-applies.
- **Add resolves its target by matching, then falls back to creating.** A name that matches exactly one existing Stock Entry increases that entry (even if it is placed somewhere and the request named no Location), matching user stories 25-26. A name that matches none creates at the exact Equivalence key the request named, defaulting to the reserved `each` Unit and to unlocated. A name that matches several is ambiguous. This is the only rule that satisfies both "creates or increases Equivalent Stock" and "never guesses".
- **A quantity mutation never writes a Note onto an existing Stock Entry.** `NotePreserved` reports that a proposed Note was deliberately not applied. Editing a Note belongs to a later slice, and silently overwriting one is exactly what user story 27 forbids.
- **Underflow is a `conflict`, not an `invalid`.** The request was well-formed and authorized; it conflicts with current state. `invalid` is reserved for requests that could not be understood or were out of bounds.
