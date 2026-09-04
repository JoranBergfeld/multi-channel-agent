# Confirmed Stock Mutations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the stock mutation language for issue #32: `move_stock` transfers a positive Quantity or all of it to a Location or to the unlocated state and merges Equivalent Stock deterministically; `rename_stock` preserves identity unless a confirmed collision merges entries and reports the survivor and the retired source; `forget_stock` requires confirmation and succeeds only for a zero-quantity Stock Entry; every multi-change batch, Set to zero, Forget, and merge-retiring Move or Rename produces an exact server-stored confirmation proposal; one pending proposal per Participant and ChannelConversation is stored with a ten-minute single-use token and expected versions; direct explicit confirmation executes that stored proposal atomically while rejection, replacement, access loss, Inventory switch, expiry, interruption, and conflict invalidate it; and a retried Turn returns the recorded outcome and never replans after a mutation has completed.

**Architecture:** The spine shipped by #30/#31 is unchanged - `InboundTurn -> TurnAcceptanceService -> TurnProcessingCoordinator -> TurnExecutionContextFactory -> StockToolDispatcher -> deterministic Application service -> Outcome/Delivery`. This plan extends exactly that spine and adds nothing parallel to it. Four pure Domain additions carry the new rules: three more `StockMutationKind` members with their effect vocabulary, one pure `StockChangePlan` that decides every change (quantity, Move, Rename, Forget) from current state alone, an opaque `ConfirmationToken` that is only ever *stored hashed*, and an immutable `ConfirmationProposal` bound to Participant, ChannelConversation, Inventory, Turn, exact effects, and expected versions. Two new Application store seams carry the state: `IConfirmationProposalStore` (one pending proposal per Participant + ChannelConversation, enforced by a filtered unique index) and `IStockChangeSetStore` (one atomic writer for one *or many* resolved changes, which consumes the proposal, applies every effect, appends one minimal audit fact per change, and writes one ledger header plus one ledger effect row per change - all in a single transaction). Three new Application services compose them: `StockChangeResolver` turns untrusted change requests into exactly-decided `ProposedChange`s against current state, `StockChangeSetService` applies a lone low-risk change immediately or stores a proposal, and `StockConfirmationService` executes or rejects a stored proposal under *trusted, direct-content* confirmation evidence the model never supplies. Identity never comes from the model: Participant, ChannelConversation, Inventory, Turn, confirmation evidence, and the execution operation identity all come from `TurnExecutionContext` and from the stored proposal.

**Tech Stack:** C#/.NET 10, EF Core 10 (SQL Server provider in production, SQLite for Docker-free relational tests), xUnit 2.9, `Microsoft.Extensions.TimeProvider.Testing` for deterministic expiry, Testcontainers `MsSql` for the SQL-backed application-boundary suite, React 19 + TypeScript + Vite + oxlint for the web client.

---

## Scope and non-goals

In scope (issue #32 acceptance criteria, verbatim):

1. `move_stock` transfers a positive Quantity or all to a Location or unlocated state and merges Equivalent Stock deterministically.
2. `rename_stock` preserves identity unless a confirmed collision merges entries and reports the survivor and retired source.
3. `forget_stock` requires confirmation and succeeds only for a zero-quantity Stock Entry.
4. Every multi-change batch, Set to zero, Forget, and merge-retiring Move or Rename produces an exact confirmation proposal.
5. One pending proposal per Participant + ChannelConversation stored with a ten-minute single-use token and expected versions.
6. Direct explicit confirmation executes the stored proposal atomically; rejection, replacement, access loss, Inventory switch, expiry, interruption, and conflict invalidate it.
7. Retries return recorded outcomes and never replan after mutation completes.

Preserved from #30/#31 and never regressed by this plan: trusted context injection, non-disclosing refusals, per-ChannelConversation FIFO, semantic Outcome separated from Delivery, exact invariant-decimal Quantity, the operation ledger replay rule, minimal semantic audits, optimistic concurrency, and web projection refresh after any change.

Explicitly **out of scope** for this slice:

- Issue #33 reference administration: `create_units`, `rename_units`, `add_unit_aliases`, `remove_unit_aliases`, `retire_units`, `list_units`, `create_locations`, `rename_locations`, `retire_locations`, `list_locations`. This plan never creates, renames, or retires a Unit or a Location. An unknown Unit or Location stays `reference_not_found`. Retire-driven proposal invalidation is #33's to add on top of the `ProposalStatus` vocabulary this plan establishes.
- Initial Import (issue #35) and its own proposal shape. This plan's proposal store is deliberately general enough to hold one later (Participant + ChannelConversation keyed, opaque payload), but nothing here parses CSV.
- Monetary budgets, spend thresholds, chargeback, cost ceilings, and quota purchase. The parent spec puts these entirely out of scope; **no task in this plan may add a cost check, spend ceiling, or budget policy of any kind.**
- Voice transport, WebRTC, Voice Live admission, and speech recognition. This plan models *interruption* as a channel-contract flag on the Turn so the later voice adapter has something to set, and it does not implement voice.
- Teams and email adapters, Foundry-backed model integration, and multi-tool-call agent runs.

---

## File responsibility map

### Domain (`src/MultiChannelAgent.Domain/`)

| File | Responsibility |
| --- | --- |
| `Inventories/StockMutation.cs` (modify) | `StockMutationKind` gains `Move`, `Rename`, `Forget`. New `StockMutationKinds` helper (machine text, parsing, "is this a quantity change"). New `StockChangeEffectKind` - the twelve exact effects one change can have. `StockAuditFacts` gains the audit event types for the three new kinds, one outcome code per effect, and the two predicates that define the whole confirmation policy: `RetiresSource` and `RequiresConfirmation`. `StockMutationPlan` is untouched. |
| `Inventories/StockChangePlan.cs` (create) | The single pure planner for every change: quantity changes delegate to the existing `StockMutationPlan`, and Move/Rename/Forget are decided here from current state alone. Reads and writes nothing. |
| `Inventories/AuditFact.cs` (modify) | `AuditEventType` gains `StockMoved`, `StockRenamed`, `StockForgotten`. |
| `Inventories/ConfirmationToken.cs` (create) | The opaque single-use secret: issue 32 cryptographically random bytes as 43 base64url characters, hash to 64 lowercase hex characters, compare in fixed time. The plaintext is returned to the Participant exactly once and **never stored**. |
| `Inventories/ConfirmationProposal.cs` (create) | `ProposalId`, `ProposalStatus`, `ProposedEntryState`, `ProposedChange`, `ExpectedEntryVersion`, `ExpectedEquivalentStockAbsence`, and the immutable `ConfirmationProposal` aggregate with its ten-minute lifetime, its binding predicate, and its derived execution operation identity. |
| `Inventories/StockOperationId.cs` (modify) | Gains `DeriveForProposal(ProposalId)`: the execution identity of a confirmed proposal, derived from the proposal rather than from the confirming Turn, so the ledger key is stable even if the same proposal were confirmed from a re-driven Turn. |
| `Turns/InboundTurn.cs` (modify) | `InboundTurnDraft` and `InboundTurn` gain `WasInterrupted`. An interrupted Turn's direct content may never authorize a mutation or a confirmation. |

### Application (`src/MultiChannelAgent.Application/`)

| File | Responsibility |
| --- | --- |
| `Turns/DirectConfirmationEvidence.cs` (create) | `DirectConfirmationEvidence` and the deterministic reader that derives it from a Turn's **direct** content only. Quoted, forwarded, attached, retrieved, tool-produced, and model-derived text can never confirm, because `InboundTurn.ContentText` already excludes them. An interrupted Turn always reads `None`. |
| `Turns/TurnExecutionContext.cs` (modify) | The trusted context gains `Confirmation` and `WasInterrupted`, assembled by the factory from the Turn itself - never from a model proposal. |
| `Turns/SubmitTurnRequest.cs` (modify) | Adapters may declare a Turn interrupted at acceptance. |
| `Turns/TurnAcceptanceService.cs` (modify) | Carries `WasInterrupted` into the durable `InboundTurnDraft`. |
| `Turns/ConversationalClauses.cs` (modify) | The bounded clause grammar gains `to`, `to unlocated`, and `all`. |
| `Turns/ScriptedModelBoundary.cs` (modify) | Recognizes `move stock`, `rename stock`, `forget stock`, `change stock` (a batch), `confirm`, and `reject`, and proposes the matching bounded tool call. It still parses only direct content and still supplies no identity. |
| `Inventories/IStockStore.cs` (modify) | Gains `ReadVersionsAsync` and `StockEntryVersion`: the current optimistic-concurrency version of named Stock Entries, so a proposal can record *expected versions* without leaking a concurrency stamp into the display projection. |
| `Inventories/IConfirmationProposalStore.cs` (create) | The pending-proposal seam: find the one pending proposal for a Participant + ChannelConversation, store a new one (superseding any pending one atomically), settle one to a terminal status, invalidate the pending one, expire pending ones past their lifetime, and delete settled ones past retention. |
| `Inventories/IStockChangeSetStore.cs` (create) | The one atomic writer for one *or many* changes: `StockChangeSetCommand`, `RecordedEntryState`, `RecordedStockChangeEffect`, `RecordedStockChangeSet`, `StockChangeSetStoreOutcome`. It consumes the proposal, applies every effect, appends the audits, and writes the ledger - or changes nothing at all. |
| `Inventories/StockChangeSetParser.cs` (create) | `StockChangeRequest` plus the strict parser for the untrusted `changes` JSON array a batch tool call carries: bounded count, known kinds, known properties only, string values only. |
| `Inventories/StockChangeResolver.cs` (create) | Turns one untrusted `StockChangeRequest` into either one exactly-decided `ProposedChange` (with its expected versions and expected absence) or one typed refusal, resolved against current state through the very same deterministic matching Find uses. |
| `Inventories/StockChangeSetService.cs` (create) | Authorizes Editor, answers a replay from the ledger before re-planning anything, resolves every change, and then either applies a lone low-risk change immediately or stores an exact proposal and hands back its one-time token. |
| `Inventories/StockConfirmationService.cs` (create) | Executes or rejects the stored proposal. Requires trusted direct-content evidence, verifies the token in fixed time, enforces expiry and binding, executes atomically, and maps every failure to a generic non-disclosing code. |
| `Inventories/ConfirmationProposalLifecycle.cs` (create) | The one place every non-confirmation invalidator lives: an interrupted Turn, an Active Inventory that changed or was lost, and the replacement a new proposal performs. |
| `Inventories/ConfirmationProposalCleanupCoordinator.cs` (create) | Leased, bounded sweep: expire pending proposals past their lifetime, then delete settled ones past retention. |
| `Inventories/InventorySelectionService.cs` (modify) | An explicit Inventory switch, and access loss detected while reading the Active Inventory, invalidate the pending proposal. |
| `Inventories/StockToolDispatcher.cs` (modify) | Executes `move_stock`, `rename_stock`, `forget_stock`, `apply_stock_changes`, `confirm_inventory_operation`, and `reject_inventory_operation` under trusted context, and shapes the `stock_proposal` and `stock_changes` payloads. |
| `Turns/TurnProcessingCoordinator.cs` (modify) | Reconciles the pending proposal against the freshly assembled trusted context before the model is asked anything. |

### Infrastructure (`src/MultiChannelAgent.Infrastructure/`)

| File | Responsibility |
| --- | --- |
| `Persistence/Entities/InboxEntryEntity.cs` (modify) | Gains `WasInterrupted`. |
| `Persistence/Entities/ConfirmationProposalEntity.cs` (create) | The durable pending/settled proposal row: token hash, binding, status, exact serialized contents, expiry. |
| `Persistence/Entities/StockChangeSetOperationEntity.cs` (create) | The change-set ledger header: operation identity, Inventory, confirming Turn, optional proposal, applied-at. |
| `Persistence/Entities/StockChangeSetEffectEntity.cs` (create) | One recorded effect per change, carrying only the semantic facts a retry must re-report. |
| `Persistence/Configurations/*` (create/modify) | Column bounds; the **filtered unique index that makes "one pending proposal per Participant and ChannelConversation" a database guarantee**; the unique token-hash index; the unique `(InventoryId, ConfirmedByTurnId)` replay index; expiry indexes. |
| `Persistence/MultiChannelAgentDbContext.cs` (modify) | Exposes the three new sets. |
| `Persistence/Migrations/*_AddTurnInterruption.cs` (generated) | `InboxEntries.WasInterrupted` with a `false` default (that default is the backfill). |
| `Persistence/Migrations/*_AddConfirmationProposals.cs` (generated) | The `ConfirmationProposals` table and its indexes. |
| `Persistence/Migrations/*_AddStockChangeSetLedger.cs` (generated) | The `StockChangeSetOperations` and `StockChangeSetEffects` tables and their indexes. |
| `Inventories/ConfirmationProposalMapper.cs` (create) | The exact, versioned JSON serialization of a proposal's contents and expected versions. Quantities are invariant decimal text so no precision can be lost between proposing and executing. |
| `Inventories/SqlConfirmationProposalStore.cs` (create) | The SQL adapter: atomic supersede-then-insert, pending lookup, guarded settle, bounded expiry and retention sweeps. |
| `Inventories/SqlStockChangeSetStore.cs` (create) | The atomic executor: guarded proposal consumption, a deterministically ordered lock-and-verify pass over every touched row, an ordered effect pass, the ledger, the audits - one transaction, or nothing. |
| `Inventories/SqlStockStore.cs` (modify) | Implements `ReadVersionsAsync`. |
| `Turns/SqlInboxStore.cs` (modify) | Persists and rehydrates `WasInterrupted`. |
| `ServiceCollectionExtensions.cs` (modify) | Registers the two new stores and the five new services/coordinators. |

### Host (`src/MultiChannelAgent.Host/`)

| File | Responsibility |
| --- | --- |
| `Endpoints/TurnEndpoints.cs` (modify) | The submission DTO accepts an optional `interrupted` flag. |
| `Workers/ConfirmationProposalCleanupWorker.cs` (create) | Periodically drives the proposal sweep. |
| `Program.cs` (modify) | Registers that worker. |

### Web (`src/web/src/`)

| File | Responsibility |
| --- | --- |
| `turnsApi.ts` (modify) | Adds `StockProposalPayload` and `StockChangeSetPayload` to the discriminated `TurnOutcomePayload` union. |
| `TurnTracer.tsx` (modify) | Renders an exact proposal (every change, the survivor and the retired source, the expiry) with confirm/reject commands, and renders an executed change set. Terminal Outcomes already refresh the workspace. |

### Tests

| File | Responsibility |
| --- | --- |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/StockChangePlanTests.cs` (create) | Every planner branch for every kind. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationTokenTests.cs` (create) | Issuance, shape, hashing, fixed-time comparison, and that a hash never reveals its token. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs` (create) | Construction invariants, ten-minute expiry, binding, and derived execution identity. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs` (modify) | The proposal-derived identity is stable and cannot collide with a Turn-derived one. |
| `tests/MultiChannelAgent.Domain.Tests/InboundTurnTests.cs` (modify) | Interruption survives normalization. |
| `tests/MultiChannelAgent.Application.Tests/Turns/DirectConfirmationEvidenceTests.cs` (create) | Only direct current-turn content confirms; quoted text and interrupted Turns never do. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs` (create) | In-memory twin of the SQL proposal semantics. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockChangeSetStore.cs` (create) | In-memory twin of the atomic change-set semantics. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs` (modify) | Gains versions, relocation, renaming, and deletion so the doubles can execute every effect. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetParserTests.cs` (create) | The untrusted batch JSON contract. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeResolverTests.cs` (create) | Every resolution and every refusal. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetServiceTests.cs` (create) | Immediate application, proposal creation, replacement, replay. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockConfirmationServiceTests.cs` (create) | Confirmation, rejection, evidence, token, expiry, single use, conflict, replay. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalLifecycleTests.cs` (create) | Interruption, Inventory switch, and access loss invalidation. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs` (modify) | The six new tool dispatches and their payloads. |
| `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs` (modify) | The new grammar. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs` (create) | One pending proposal per Participant + ChannelConversation, enforced relationally; supersede; guarded settle; sweeps. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockChangeSetStoreTests.cs` (create) | Atomicity, rollback on a version conflict, single-use under a double confirm, merge, forget, and replay. |
| `tests/MultiChannelAgent.IntegrationTests/ConfirmedStockMutationScenario.cs` (create) | The shared end-to-end conversational scenario. |
| `tests/MultiChannelAgent.IntegrationTests/ConfirmedStockMutationSqliteTests.cs` (create) | Docker-free twin. |
| `tests/MultiChannelAgent.IntegrationTests/ConfirmationExpirySqliteTests.cs` (create) | The ten-minute expiry, end to end, on controlled time. |
| `tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs` (modify) | Runs the scenario against Testcontainers SQL Server with production migrations. |
| `tests/MultiChannelAgent.IntegrationTests/SqliteWebApplicationFactory.cs` (modify) | Accepts an optional `TimeProvider` so expiry is testable without sleeping. |
| `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs` (modify) | Gains `SubmitInterruptedTurnAsync`. |

---

## Domain and state-machine decisions

These are settled here so no task has to re-decide them.

### 1. One effect vocabulary decides confirmation

Every change resolves to exactly one `StockChangeEffectKind`. Two pure predicates over that enum define the whole policy:

- `StockAuditFacts.RetiresSource(effect)` is true for `Merged`, `RenameMerged`, and `Forgotten`.
- `StockAuditFacts.RequiresConfirmation(effect)` is true for `QuantityCleared` (Set to zero), `Merged`, `RenameMerged`, and `Forgotten`.

A change set requires confirmation when **it has more than one change, or any change requires confirmation**. There is no second place where "is this risky" is decided, so the answer can never drift between the tool layer, the service, and the store.

### 2. The exact contents of a proposal, per effect

`ProposedChange` carries a `Source` state and an optional `Destination` state. Which fields matter is fixed by the effect, and `SqlStockChangeSetStore` executes exactly this table:

| Effect | `Source` | `Destination` | The executor writes |
| --- | --- | --- | --- |
| `Created` | `StockEntryId` null; the equivalence key to create (name, Unit, Location) and optional Note; `Previous` = 0, `Resulting` = amount | null | INSERT the source |
| `QuantityIncreased` / `QuantityDecreased` / `QuantitySet` / `QuantityCleared` | the existing entry; `Resulting` = new amount | null | UPDATE `Quantity` |
| `Placed` | the existing entry; `Resulting` = `Previous` | same `StockEntryId`, destination `LocationId`/`LocationName`, `Previous` = `Resulting` = the source amount | UPDATE `LocationId` |
| `Split` | the existing entry; `Resulting` = remainder | `StockEntryId` null, destination `LocationId`, `Previous` = 0, `Resulting` = transferred | UPDATE source `Quantity`, INSERT destination |
| `SplitMerged` | the existing entry; `Resulting` = remainder | the existing destination entry; `Resulting` = its amount + transferred | UPDATE both `Quantity` values |
| `Merged` | the existing entry; `Resulting` = 0; `Retired` = true | the existing destination entry; `Resulting` = its amount + transferred | UPDATE destination `Quantity`, DELETE source |
| `Renamed` | the existing entry | null (`NewName`/`NewNormalizedName` carry the change) | UPDATE `Name` and `NormalizedName` |
| `RenameMerged` | the existing entry; `Resulting` = 0; `Retired` = true | the existing colliding entry; `Resulting` = its amount + transferred | UPDATE destination `Quantity`, DELETE source |
| `Forgotten` | the existing entry; `Previous` = `Resulting` = 0; `Retired` = true | null | DELETE source |

The proposal therefore needs no re-resolution at confirmation time: it already carries every identity, name, Unit, Location, Note, and exact amount the executor writes. Confirmation **never** replays model arguments and **never** recomputes against newer state.

### 3. Expected versions and expected absences

- Every *existing* Stock Entry a proposal touches (source and destination) contributes one `ExpectedEntryVersion(StockEntryId, ConcurrencyStamp)`, read at proposal time through `IStockStore.ReadVersionsAsync`.
- Every entry a proposal *creates* (`Created`, and `Split`'s destination) contributes one `ExpectedEquivalentStockAbsence(NormalizedName, UnitId, LocationId?)`.
- The stamp - not the Quantity - is the version. Quantity equality alone would let an unrelated Move-out-and-back-in look unchanged; the stamp is regenerated on **every** write to the row.

### 4. The token is never stored in a reusable form

`ConfirmationToken.Issue()` returns 43 base64url characters from 32 cryptographically random bytes. Only `ConfirmationToken.HashOf(token)` - 64 lowercase hex characters of SHA-256 - is persisted, in a unique-indexed column. The plaintext exists exactly once, in the `confirmation_required` answer the Participant sees. A database reader can therefore not confirm anything: they can only see that a proposal exists. Verification is `CryptographicOperations.FixedTimeEquals` over the two hashes.

Lookup is by `(ParticipantId, ChannelConversationId)` and never by token, so a token belonging to a different Participant or conversation cannot even be looked up, let alone matched. That is what makes non-disclosure structural rather than a code path someone has to remember.

### 5. Proposal state machine

```
                         (a newer proposal is stored)            -> Superseded
                         (an explicit direct rejection)           -> Rejected
                         (past CreatedAt + 10 minutes)            -> Expired
Pending ---------------> (the Active Inventory changed)           -> InventorySwitched
                         (Membership was lost)                    -> AccessLost
                         (an interrupted Turn arrived)            -> Interrupted
                         (execution met a version conflict)       -> Conflicted
                         (direct explicit confirmation executed)  -> Confirmed
```

Every arrow is terminal: nothing ever returns to `Pending`. `Confirmed` is set **inside the same transaction that applies the changes**, guarded by `WHERE Status = 'Pending'`, so two concurrent confirmations cannot both execute - the loser updates zero rows, rolls the whole transaction back, and is answered `conflict`.

A settled row is retained for 24 hours and then deleted by the sweep, so a confirmation arriving moments after a rejection can still be told the truth rather than "unknown proposal".

### 6. Stable execution identity and replay

- The confirmed execution's operation identity is `StockOperationId.DeriveForProposal(proposal.Id)` - derived from the proposal, not from the confirming Turn.
- The ledger header additionally carries `ConfirmedByTurnId`, unique per `(InventoryId, ConfirmedByTurnId)`.
- `StockChangeSetService` and `StockConfirmationService` therefore both begin - after authorization, before anything else - with `FindRecordedByTurnAsync(inventoryId, turnId)`. A Turn re-driven after a crash between the mutation transaction and the Outcome transaction is answered from that ledger and **never re-planned and never re-applied**, exactly as #31 established for single quantity mutations.
- This holds even though the proposal has by then been consumed, because the replay lookup does not need the proposal at all.
- Today `TurnProcessingCoordinator` dispatches exactly one tool call per Turn, which is why `(InventoryId, ConfirmedByTurnId)` is unique. When multi-tool-call runs arrive, that index must gain the tool-call sequence; a comment on the configuration says so.

### 7. Two ledgers, disjoint by construction

`#31`'s `StockOperations` ledger stays exactly as it is and keeps serving immediate single Add/Remove/Set. Everything this ticket adds - immediate single Move/Rename, and every confirmed execution - is recorded in the new `StockChangeSetOperations`/`StockChangeSetEffects` ledger. The two can never disagree about one operation because their identities are derived from different material (`"{turn}|{tool}|{seq}"` versus `"proposal|{id}"`, and a Turn-derived identity for an immediate change set is scoped by the tool name, which differs). Rewriting the shipped, proven single-mutation path to share the batch writer would be churn with no behavioral gain, and would put a working acceptance criterion at risk.

### 8. Deterministic ordering, locking, and rollback on SQL Server

`SqlStockChangeSetStore.ApplyAsync` runs one explicit transaction and does this, in this order:

1. **Consume the proposal** (when there is one) with a single guarded `ExecuteUpdateAsync ... WHERE ProposalId = @id AND Status = 'Pending'`. Anything other than exactly one row affected rolls back and answers `Conflict`.
2. **Lock and verify** every touched Stock Entry, ordered by `StockEntryId.ToString("D")` compared ordinally, one guarded `ExecuteUpdateAsync ... WHERE Id = @id AND InventoryId = @inv AND ConcurrencyStamp = @expected` per row that only sets a fresh stamp. This single statement acquires the row's exclusive lock *and* verifies the expected version. Anything other than exactly one row affected rolls back and answers `Conflict`.
3. **Apply the effects** in `ProposedChange.Order`, per the table in decision 2. Because step 2 already holds every row's exclusive lock in one globally agreed order, this pass can neither deadlock against another batch nor lose a write.
4. **Append** one `AuditFact` per change, the ledger header, and one ledger effect row per change, and `SaveChangesAsync` (which also flushes the staged inserts).
5. **Commit.**

Any failure at any step rolls the whole transaction back, so a failed batch changes nothing at all. A unique-index violation from an insert is classified as `Conflict` only after checking whether this very operation identity converged in the ledger (another replica applying its twin); anything else propagates as the real fault it is.

### 9. Non-disclosure

Every confirmation failure answers one of a small closed set of generic codes - `proposal_not_found`, `proposal_expired`, `proposal_token_mismatch`, `confirmation_evidence_missing`, `rejection_evidence_missing`, `state_changed` - with a summary that names no Stock Entry, no other Participant, no Inventory, and no SQL detail. A proposal belonging to another Participant, another ChannelConversation, or another Inventory is `proposal_not_found`, identical to one that never existed.

### 10. The parser/tool contract never carries identity

The model proposes tool names and a flat dictionary of untrusted string arguments; that is unchanged. A batch is carried as one untrusted `changes` argument holding a JSON array of objects whose property values are all strings, parsed by `StockChangeSetParser` with a closed property set, a closed kind set, and a hard bound of `ConfirmationProposal.MaxChanges` (25) elements. `confirm_inventory_operation` and `reject_inventory_operation` accept only a `token`. Neither ever accepts a Participant, Inventory, conversation, Turn, proposal id, or version - those come from `TurnExecutionContext` and from the stored proposal. A model that invents a `confirm_inventory_operation` call without the Participant having said anything affirmative in **direct** content is answered `confirmation_evidence_missing`, and the pending proposal is left untouched.

---

## Task 1: Name Move, Rename, and Forget, and give every change one effect

**Files:**
- Modify: `src/MultiChannelAgent.Domain/Inventories/StockMutation.cs`
- Modify: `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs`

Why this comes first: every later type names a kind and an effect. Fixing that vocabulary - and the two predicates that decide confirmation - before anything reads it means the confirmation policy is decided in exactly one place.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs`, inside the existing class:

```csharp
    [Theory]
    [InlineData(StockMutationKind.Add, "add")]
    [InlineData(StockMutationKind.Remove, "remove")]
    [InlineData(StockMutationKind.Set, "set")]
    [InlineData(StockMutationKind.Move, "move")]
    [InlineData(StockMutationKind.Rename, "rename")]
    [InlineData(StockMutationKind.Forget, "forget")]
    public void Every_mutation_kind_has_stable_machine_text_that_round_trips(StockMutationKind kind, string expected)
    {
        Assert.Equal(expected, StockMutationKinds.ToMachineText(kind));
        Assert.True(StockMutationKinds.TryParse(expected, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delete")]
    [InlineData("Add ")]
    public void Text_that_is_not_a_mutation_kind_does_not_parse(string? text)
    {
        Assert.False(StockMutationKinds.TryParse(text, out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(StockMutationKind.Add, true)]
    [InlineData(StockMutationKind.Remove, true)]
    [InlineData(StockMutationKind.Set, true)]
    [InlineData(StockMutationKind.Move, false)]
    [InlineData(StockMutationKind.Rename, false)]
    [InlineData(StockMutationKind.Forget, false)]
    public void Only_Add_Remove_and_Set_state_an_amount_of_their_own(StockMutationKind kind, bool expected) =>
        Assert.Equal(expected, StockMutationKinds.IsQuantityChange(kind));

    [Theory]
    [InlineData(StockChangeEffectKind.Merged)]
    [InlineData(StockChangeEffectKind.RenameMerged)]
    [InlineData(StockChangeEffectKind.Forgotten)]
    public void An_effect_that_ends_a_Stock_Entrys_identity_retires_its_source(StockChangeEffectKind effect)
    {
        Assert.True(StockAuditFacts.RetiresSource(effect));
        Assert.True(StockAuditFacts.RequiresConfirmation(effect));
    }

    [Theory]
    [InlineData(StockChangeEffectKind.Created)]
    [InlineData(StockChangeEffectKind.QuantityIncreased)]
    [InlineData(StockChangeEffectKind.QuantityDecreased)]
    [InlineData(StockChangeEffectKind.QuantitySet)]
    [InlineData(StockChangeEffectKind.Placed)]
    [InlineData(StockChangeEffectKind.Split)]
    [InlineData(StockChangeEffectKind.SplitMerged)]
    [InlineData(StockChangeEffectKind.Renamed)]
    public void An_effect_that_keeps_every_identity_needs_no_confirmation(StockChangeEffectKind effect)
    {
        Assert.False(StockAuditFacts.RetiresSource(effect));
        Assert.False(StockAuditFacts.RequiresConfirmation(effect));
    }

    [Fact]
    public void Clearing_Stock_keeps_its_identity_but_is_still_deliberate()
    {
        Assert.False(StockAuditFacts.RetiresSource(StockChangeEffectKind.QuantityCleared));
        Assert.True(StockAuditFacts.RequiresConfirmation(StockChangeEffectKind.QuantityCleared));
    }

    [Theory]
    [InlineData(StockMutationKind.Move, AuditEventType.StockMoved)]
    [InlineData(StockMutationKind.Rename, AuditEventType.StockRenamed)]
    [InlineData(StockMutationKind.Forget, AuditEventType.StockForgotten)]
    public void Every_new_mutation_kind_appends_its_own_audit_event_type(StockMutationKind kind, AuditEventType expected) =>
        Assert.Equal(expected, StockAuditFacts.EventTypeFor(kind));

    [Theory]
    [InlineData(StockChangeEffectKind.Created, "Add:Created")]
    [InlineData(StockChangeEffectKind.QuantityIncreased, "Add:Increased")]
    [InlineData(StockChangeEffectKind.QuantityDecreased, "Remove:Decreased")]
    [InlineData(StockChangeEffectKind.QuantitySet, "Set:Applied")]
    [InlineData(StockChangeEffectKind.QuantityCleared, "Set:Cleared")]
    [InlineData(StockChangeEffectKind.Placed, "Move:Placed")]
    [InlineData(StockChangeEffectKind.Split, "Move:Split")]
    [InlineData(StockChangeEffectKind.SplitMerged, "Move:SplitMerged")]
    [InlineData(StockChangeEffectKind.Merged, "Move:Merged")]
    [InlineData(StockChangeEffectKind.Renamed, "Rename:Renamed")]
    [InlineData(StockChangeEffectKind.RenameMerged, "Rename:Merged")]
    [InlineData(StockChangeEffectKind.Forgotten, "Forget:Forgotten")]
    public void Every_effect_has_a_coarse_audit_outcome_code(StockChangeEffectKind effect, string expected)
    {
        var code = StockAuditFacts.OutcomeCodeFor(effect);

        Assert.Equal(expected, code);

        // The audit column bounds this at 64 characters, and an audit fact must never carry detail
        // beyond a coarse code, so an over-long one is a design error rather than a truncation.
        Assert.True(code.Length <= 64);
    }
```

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockMutationPlanTests"`
Expected: FAIL to compile - `StockMutationKind.Move`, `StockMutationKinds`, `StockChangeEffectKind`, `AuditEventType.StockMoved`, `StockAuditFacts.RetiresSource`, `StockAuditFacts.RequiresConfirmation`, and `StockAuditFacts.OutcomeCodeFor(StockChangeEffectKind)` do not exist.

- [ ] **Step 2: Extend the audit event vocabulary**

In `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`, inside `AuditEventType`, after the `StockSet` member:

```csharp
    /// <summary>Stock was moved between placements, possibly merging into Equivalent Stock there.</summary>
    StockMoved,

    /// <summary>A Stock Entry was renamed, possibly merging into Equivalent Stock under the new name.</summary>
    StockRenamed,

    /// <summary>A zero-quantity Stock Entry was permanently removed after explicit confirmation.</summary>
    StockForgotten,
```

- [ ] **Step 3: Extend the mutation vocabulary**

In `src/MultiChannelAgent.Domain/Inventories/StockMutation.cs`, extend `StockMutationKind` with the three new members (leave `Add`, `Remove`, `Set` exactly where they are so no persisted `Kind` text changes meaning):

```csharp
    /// <summary>Transfers some or all Quantity to another Location or to the unlocated state, merging Equivalent Stock there.</summary>
    Move,

    /// <summary>Changes a Stock Entry's name, merging Equivalent Stock when the new name collides.</summary>
    Rename,

    /// <summary>Permanently removes a zero-quantity Stock Entry.</summary>
    Forget,
```

Add, in the same file after `StockMutationKind`:

```csharp
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
```

- [ ] **Step 4: Extend the audit-fact mapping and state the confirmation policy once**

In the same file, replace the body of `StockAuditFacts` so it reads exactly:

```csharp
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
```

- [ ] **Step 5: Verify**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockMutationPlanTests"`
Expected: PASS. The pre-existing `StockMutationPlan` tests must still pass unchanged - `StockMutationPlan.For` is untouched, and it still throws for a kind it does not plan.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/StockMutation.cs src/MultiChannelAgent.Domain/Inventories/AuditFact.cs tests/MultiChannelAgent.Domain.Tests/Inventories/StockMutationPlanTests.cs
git commit -m "feat(inventories): name Move, Rename, Forget and every change effect for #32"
```

---

## Task 2: Plan every change as a pure domain rule

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/StockChangePlan.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/StockChangePlanTests.cs`

Why: the arithmetic and the risk rules for Move, Rename, and Forget must be decidable from current state alone, with no store, no authorization, and no persistence in sight - exactly as `StockMutationPlan` already is for the quantity kinds. Quantity planning is *delegated* to `StockMutationPlan` rather than restated, so there stays exactly one implementation of Add/Remove/Set arithmetic.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/StockChangePlanTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public sealed class StockChangePlanTests
{
    private static Quantity Q(string text)
    {
        Assert.True(Quantity.TryParseInvariant(text, out var quantity));
        return quantity;
    }

    [Fact]
    public void Adding_to_nothing_plans_to_create_the_Stock_Entry()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Add, currentQuantity: null, Q("5"));

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Created, plan.Effect);
        Assert.Equal("5", plan.SourceResultingQuantity.ToInvariantText());
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Adding_to_existing_stock_plans_to_increase_it()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Add, Q("12.5"), Q("2.25"));

        Assert.Equal(StockChangeEffectKind.QuantityIncreased, plan.Effect);
        Assert.Equal("14.75", plan.SourceResultingQuantity.ToInvariantText());
    }

    [Fact]
    public void Removing_more_than_is_on_hand_is_an_underflow_and_plans_nothing()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Remove, Q("3"), Q("4"));

        Assert.Equal(StockChangePlanOutcome.Underflow, plan.Outcome);
    }

    [Fact]
    public void Setting_to_zero_plans_to_clear_and_needs_confirmation()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Set, Q("7"), Quantity.Zero);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.QuantityCleared, plan.Effect);
        Assert.Equal("0", plan.SourceResultingQuantity.ToInvariantText());
        Assert.True(plan.RequiresConfirmation);
        Assert.False(plan.RetiresSource);
    }

    [Fact]
    public void Setting_stock_that_is_not_there_needs_a_target_rather_than_inventing_one()
    {
        var plan = StockChangePlan.ForQuantity(StockMutationKind.Set, currentQuantity: null, Q("4"));

        Assert.Equal(StockChangePlanOutcome.TargetRequired, plan.Outcome);
    }

    [Fact]
    public void Moving_all_of_it_somewhere_empty_relocates_the_Stock_Entry_itself()
    {
        var plan = StockChangePlan.ForMove(Q("10"), requestedAmount: null, destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Placed, plan.Effect);
        Assert.Equal("10", plan.TransferredQuantity.ToInvariantText());
        Assert.Equal("10", plan.SourceResultingQuantity.ToInvariantText());
        Assert.False(plan.RetiresSource);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_all_of_it_into_Equivalent_Stock_merges_and_retires_the_source()
    {
        var plan = StockChangePlan.ForMove(Q("10"), requestedAmount: null, destinationIsSamePlacement: false, Q("4"));

        Assert.Equal(StockChangeEffectKind.Merged, plan.Effect);
        Assert.Equal("10", plan.TransferredQuantity.ToInvariantText());
        Assert.Equal("0", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("14", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.True(plan.RetiresSource);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_part_of_it_somewhere_empty_splits_without_retiring_anything()
    {
        var plan = StockChangePlan.ForMove(Q("10"), Q("3"), destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangeEffectKind.Split, plan.Effect);
        Assert.Equal("7", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("3", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_part_of_it_into_Equivalent_Stock_merges_without_retiring_the_source()
    {
        var plan = StockChangePlan.ForMove(Q("10"), Q("3"), destinationIsSamePlacement: false, Q("2"));

        Assert.Equal(StockChangeEffectKind.SplitMerged, plan.Effect);
        Assert.Equal("7", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("5", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Moving_more_than_is_on_hand_is_an_underflow()
    {
        var plan = StockChangePlan.ForMove(Q("2"), Q("3"), destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.Underflow, plan.Outcome);
    }

    [Fact]
    public void Moving_a_non_positive_amount_is_not_a_Move_at_all()
    {
        var plan = StockChangePlan.ForMove(Q("2"), Quantity.Zero, destinationIsSamePlacement: false, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.InvalidAmount, plan.Outcome);
    }

    [Fact]
    public void Moving_stock_to_where_it_already_is_changes_nothing()
    {
        var plan = StockChangePlan.ForMove(Q("2"), requestedAmount: null, destinationIsSamePlacement: true, destinationQuantity: null);

        Assert.Equal(StockChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void A_merge_that_could_not_be_stored_exactly_is_refused_rather_than_rounded()
    {
        var nearLimit = Quantity.Create(999_999_999_999_999_999m);

        var plan = StockChangePlan.ForMove(nearLimit, requestedAmount: null, destinationIsSamePlacement: false, nearLimit);

        Assert.Equal(StockChangePlanOutcome.OutOfBounds, plan.Outcome);
    }

    [Fact]
    public void Renaming_without_a_collision_preserves_the_Stock_Entrys_identity()
    {
        var plan = StockChangePlan.ForRename("Steel Bolts", "Brass Rivets", "steel bolts", Q("4"), collidingQuantity: null);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Renamed, plan.Effect);
        Assert.Equal("4", plan.SourceResultingQuantity.ToInvariantText());
        Assert.False(plan.RetiresSource);
        Assert.False(plan.RequiresConfirmation);
    }

    [Fact]
    public void Renaming_only_the_capitalisation_still_changes_the_displayed_name()
    {
        // The normalized name is unchanged, so no Equivalent Stock can possibly collide with it, and
        // the entry keeps its identity - but the Participant did ask for a different displayed name.
        var plan = StockChangePlan.ForRename("steel bolts", "Steel Bolts", "steel bolts", Q("4"), collidingQuantity: null);

        Assert.Equal(StockChangeEffectKind.Renamed, plan.Effect);
    }

    [Fact]
    public void Renaming_a_Stock_Entry_to_the_name_it_already_displays_changes_nothing()
    {
        var plan = StockChangePlan.ForRename("Steel Bolts", "Steel Bolts", "steel bolts", Q("4"), collidingQuantity: null);

        Assert.Equal(StockChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Renaming_into_a_collision_merges_and_retires_the_source()
    {
        var plan = StockChangePlan.ForRename("Steel Bolts", "Brass Rivets", "steel bolts", Q("4"), Q("6"));

        Assert.Equal(StockChangeEffectKind.RenameMerged, plan.Effect);
        Assert.Equal("4", plan.TransferredQuantity.ToInvariantText());
        Assert.Equal("0", plan.SourceResultingQuantity.ToInvariantText());
        Assert.Equal("10", plan.DestinationResultingQuantity.ToInvariantText());
        Assert.True(plan.RetiresSource);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Forgetting_an_empty_Stock_Entry_is_planned_and_needs_confirmation()
    {
        var plan = StockChangePlan.ForForget(Quantity.Zero);

        Assert.Equal(StockChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal(StockChangeEffectKind.Forgotten, plan.Effect);
        Assert.True(plan.RetiresSource);
        Assert.True(plan.RequiresConfirmation);
    }

    [Fact]
    public void Forgetting_Stock_that_is_still_on_hand_is_refused_so_it_cannot_bypass_Remove()
    {
        var plan = StockChangePlan.ForForget(Q("0.0000000001"));

        Assert.Equal(StockChangePlanOutcome.ForgetRequiresZeroQuantity, plan.Outcome);
    }

    [Fact]
    public void A_plan_that_decided_nothing_never_claims_to_retire_or_to_need_confirmation()
    {
        var plan = StockChangePlan.ForForget(Q("1"));

        Assert.False(plan.RetiresSource);
        Assert.False(plan.RequiresConfirmation);
    }
}
```

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockChangePlanTests"`
Expected: FAIL to compile - `StockChangePlan` does not exist.

- [ ] **Step 2: Write the planner**

Create `src/MultiChannelAgent.Domain/Inventories/StockChangePlan.cs`:

```csharp
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
```

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~StockChangePlanTests"`
Expected: PASS, 20 tests.

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/StockChangePlan.cs tests/MultiChannelAgent.Domain.Tests/Inventories/StockChangePlanTests.cs
git commit -m "feat(inventories): plan Move, Rename, and Forget as pure domain rules for #32"
```

---

## Task 3: Issue confirmation tokens that are only ever stored hashed

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ConfirmationToken.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationTokenTests.cs`

Why: the token is the only bearer secret this ticket introduces. Deciding here - in the domain, before any store exists - that the plaintext is issued once and never persisted makes "never store a reusable plaintext token" a property of the type rather than a rule a store has to remember.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationTokenTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public sealed class ConfirmationTokenTests
{
    [Fact]
    public void An_issued_token_is_opaque_url_safe_text_of_a_fixed_length()
    {
        var token = ConfirmationToken.Issue();

        Assert.Equal(ConfirmationToken.TextLength, token.Length);
        Assert.True(ConfirmationToken.IsWellFormed(token));
        Assert.All(token, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    [Fact]
    public void Two_issued_tokens_are_never_the_same()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => ConfirmationToken.Issue()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_hash_is_fixed_length_lowercase_hexadecimal_and_is_not_the_token()
    {
        var token = ConfirmationToken.Issue();

        var hash = ConfirmationToken.HashOf(token);

        Assert.Equal(ConfirmationToken.HashTextLength, hash.Value.Length);
        Assert.All(hash.Value, c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));
        Assert.DoesNotContain(token, hash.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Hashing_the_same_token_twice_gives_the_same_hash()
    {
        var token = ConfirmationToken.Issue();

        Assert.Equal(ConfirmationToken.HashOf(token), ConfirmationToken.HashOf(token));
    }

    [Fact]
    public void A_stored_hash_matches_only_the_token_it_was_made_from()
    {
        var token = ConfirmationToken.Issue();
        var hash = ConfirmationToken.HashOf(token);

        Assert.True(ConfirmationToken.Matches(hash, token));
        Assert.False(ConfirmationToken.Matches(hash, ConfirmationToken.Issue()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    public void Text_that_is_not_a_well_formed_token_never_matches_anything(string? presented)
    {
        var hash = ConfirmationToken.HashOf(ConfirmationToken.Issue());

        Assert.False(ConfirmationToken.IsWellFormed(presented));
        Assert.False(ConfirmationToken.Matches(hash, presented));
    }

    [Fact]
    public void A_hash_read_back_from_storage_still_matches_its_token()
    {
        var token = ConfirmationToken.Issue();
        var roundTripped = new ConfirmationTokenHash(ConfirmationToken.HashOf(token).Value);

        Assert.True(ConfirmationToken.Matches(roundTripped, token));
    }
}
```

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ConfirmationTokenTests"`
Expected: FAIL to compile - `ConfirmationToken` and `ConfirmationTokenHash` do not exist.

- [ ] **Step 2: Write the token**

Create `src/MultiChannelAgent.Domain/Inventories/ConfirmationToken.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stored form of a confirmation token: 64 lowercase hexadecimal characters of SHA-256. This -
/// and never the token itself - is what a pending proposal carries, so someone who can read the
/// database can see that a proposal exists but can never confirm it.
/// </summary>
public readonly record struct ConfirmationTokenHash(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// The opaque, single-use secret that binds an explicit confirmation to one exact stored proposal.
///
/// The plaintext is generated from 32 cryptographically random bytes, handed to the Participant
/// exactly once in the answer that asks them to confirm, and then forgotten by this process. Only
/// <see cref="HashOf"/> is ever persisted. Guessing one means guessing 256 bits, so a wrong token can
/// safely be answered without invalidating the pending proposal - there is no brute-force attack to
/// defend against by burning the Participant's own proposal.
///
/// The token alone never authorizes anything: the application also requires that the current Turn's
/// direct content explicitly confirmed, and that the proposal is bound to this Participant,
/// ChannelConversation, and Inventory.
/// </summary>
public static class ConfirmationToken
{
    /// <summary>How many random bytes back one token. 256 bits, so a token is not guessable.</summary>
    public const int ByteLength = 32;

    /// <summary>The exact length of a token's text: 32 bytes in unpadded base64url.</summary>
    public const int TextLength = 43;

    /// <summary>The exact length of a hash's text: SHA-256 as lowercase hexadecimal.</summary>
    public const int HashTextLength = 64;

    public static string Issue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ByteLength));

    /// <summary>
    /// Whether text could be a token at all. Checked before hashing so obviously malformed input is
    /// rejected without spending a hash on it, and so a caller can never accidentally hash - and then
    /// compare - a truncated or padded value.
    /// </summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TextLength)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static ConfirmationTokenHash HashOf(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new ConfirmationTokenHash(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
    }

    /// <summary>
    /// Whether the presented text is the token behind <paramref name="storedHash"/>. The comparison
    /// is fixed-time so it cannot be turned into an oracle that leaks the stored hash a character at
    /// a time, and malformed text is refused before hashing rather than compared as a near-miss.
    /// </summary>
    public static bool Matches(ConfirmationTokenHash storedHash, string? presented)
    {
        if (!IsWellFormed(presented))
        {
            return false;
        }

        var presentedHash = HashOf(presented!);

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash.Value), Encoding.ASCII.GetBytes(presentedHash.Value));
    }
}
```

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ConfirmationTokenTests"`
Expected: PASS, 12 tests (the theory contributes 6).

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ConfirmationToken.cs tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationTokenTests.cs
git commit -m "feat(inventories): issue confirmation tokens that are only ever stored hashed for #32"
```

---

## Task 4: Model an exact, bound, single-use confirmation proposal

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs`
- Modify: `src/MultiChannelAgent.Domain/Inventories/StockOperationId.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs`

Why: this is the type the whole ticket turns on. It must carry everything needed to execute without re-resolving anything, it must be bound tightly enough that it cannot be replayed elsewhere, and its execution identity must be derivable from the proposal alone.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public sealed class ConfirmationProposalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly InventoryId Inventory = new(Guid.NewGuid());
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly TurnId Turn = TurnId.NewId();
    private const string Conversation = "web:profile-1";

    private static ProposedEntryState Entry(Quantity previous, Quantity resulting, bool retired = false, StockEntryId? id = null) => new(
        id ?? new StockEntryId(Guid.NewGuid()),
        "Steel Bolts",
        "steel bolts",
        new UnitId(Guid.NewGuid()),
        "each",
        LocationId: null,
        LocationName: null,
        Note: null,
        previous,
        resulting,
        retired);

    private static ProposedChange ForgetChange(StockEntryId id) => new()
    {
        Order = 1,
        Kind = StockMutationKind.Forget,
        Effect = StockChangeEffectKind.Forgotten,
        Source = Entry(Quantity.Zero, Quantity.Zero, retired: true, id),
    };

    private static ConfirmationProposal Create(
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        IReadOnlyList<ExpectedEquivalentStockAbsence>? absences = null) =>
        ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            Conversation,
            Inventory,
            Turn,
            changes,
            versions,
            absences ?? [],
            Now);

    [Fact]
    public void A_proposal_expires_exactly_ten_minutes_after_it_was_created()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var proposal = Create([ForgetChange(id)], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        Assert.Equal(10, ConfirmationProposal.LifetimeMinutes);
        Assert.Equal(Now.AddMinutes(10), proposal.ExpiresAt);
        Assert.False(proposal.IsExpired(Now.AddMinutes(9).AddSeconds(59)));
        Assert.True(proposal.IsExpired(Now.AddMinutes(10)));
        Assert.True(proposal.IsExpired(Now.AddMinutes(11)));
    }

    [Fact]
    public void A_proposal_is_bound_to_one_Participant_one_conversation_and_one_Inventory()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var proposal = Create([ForgetChange(id)], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        Assert.True(proposal.BelongsTo(Participant, Conversation, Inventory));
        Assert.False(proposal.BelongsTo(new ParticipantId(Guid.NewGuid()), Conversation, Inventory));
        Assert.False(proposal.BelongsTo(Participant, "web:profile-2", Inventory));
        Assert.False(proposal.BelongsTo(Participant, Conversation, new InventoryId(Guid.NewGuid())));
    }

    [Fact]
    public void A_proposals_execution_identity_comes_from_the_proposal_not_from_who_confirms_it()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var proposal = Create([ForgetChange(id)], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        Assert.Equal(StockOperationId.DeriveForProposal(proposal.Id), proposal.ExecutionOperationId);
        Assert.NotEqual(default, proposal.ExecutionOperationId.Value);
    }

    [Fact]
    public void A_proposal_must_carry_at_least_one_change()
    {
        Assert.Throws<ArgumentException>(() => Create([], []));
    }

    [Fact]
    public void A_proposal_may_not_carry_more_changes_than_a_Participant_can_review()
    {
        var changes = Enumerable.Range(1, ConfirmationProposal.MaxChanges + 1)
            .Select(order => ForgetChange(new StockEntryId(Guid.NewGuid())) with { Order = order })
            .ToList();
        var versions = changes.Select(c => new ExpectedEntryVersion(c.Source.StockEntryId!.Value, Guid.NewGuid())).ToList();

        Assert.Equal(25, ConfirmationProposal.MaxChanges);
        Assert.Throws<ArgumentException>(() => Create(changes, versions));
    }

    [Fact]
    public void A_proposals_changes_must_be_ordered_uniquely_so_execution_order_is_never_ambiguous()
    {
        var first = ForgetChange(new StockEntryId(Guid.NewGuid()));
        var second = ForgetChange(new StockEntryId(Guid.NewGuid()));
        var versions = new[]
        {
            new ExpectedEntryVersion(first.Source.StockEntryId!.Value, Guid.NewGuid()),
            new ExpectedEntryVersion(second.Source.StockEntryId!.Value, Guid.NewGuid()),
        };

        Assert.Throws<ArgumentException>(() => Create([first, second], versions));
    }

    [Fact]
    public void Every_existing_Stock_Entry_a_proposal_touches_must_carry_an_expected_version()
    {
        var id = new StockEntryId(Guid.NewGuid());

        // No expected version at all: executing this could overwrite a change made since.
        Assert.Throws<ArgumentException>(() => Create([ForgetChange(id)], []));
    }

    [Fact]
    public void A_proposal_that_only_creates_Stock_needs_an_expected_absence_rather_than_a_version()
    {
        var unitId = new UnitId(Guid.NewGuid());
        var create = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Add,
            Effect = StockChangeEffectKind.Created,
            Source = new ProposedEntryState(
                StockEntryId: null,
                "Brass Rivets",
                "brass rivets",
                unitId,
                "each",
                LocationId: null,
                LocationName: null,
                Note: null,
                Quantity.Zero,
                Quantity.Create(4m),
                Retired: false),
        };

        var proposal = Create([create], [], [new ExpectedEquivalentStockAbsence("brass rivets", unitId, null)]);

        Assert.Single(proposal.Changes);
        Assert.Empty(proposal.ExpectedVersions);
        Assert.Single(proposal.ExpectedAbsences);
    }

    [Fact]
    public void A_proposal_reports_the_survivor_and_the_retired_source_of_every_merge()
    {
        var sourceId = new StockEntryId(Guid.NewGuid());
        var destinationId = new StockEntryId(Guid.NewGuid());
        var merge = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Rename,
            Effect = StockChangeEffectKind.RenameMerged,
            Source = Entry(Quantity.Create(4m), Quantity.Zero, retired: true, sourceId),
            Destination = Entry(Quantity.Create(6m), Quantity.Create(10m), retired: false, destinationId),
            TransferredQuantity = Quantity.Create(4m),
            NewName = "Brass Rivets",
            NewNormalizedName = "brass rivets",
        };

        var proposal = Create(
            [merge],
            [new ExpectedEntryVersion(sourceId, Guid.NewGuid()), new ExpectedEntryVersion(destinationId, Guid.NewGuid())]);

        var change = Assert.Single(proposal.Changes);
        Assert.Equal(destinationId, change.SurvivingStockEntryId);
        Assert.Equal(sourceId, change.RetiredStockEntryId);
        Assert.True(change.RetiresSource);
    }

    [Fact]
    public void A_change_that_retires_nothing_reports_its_own_Stock_Entry_as_the_survivor()
    {
        var id = new StockEntryId(Guid.NewGuid());
        var rename = new ProposedChange
        {
            Order = 1,
            Kind = StockMutationKind.Rename,
            Effect = StockChangeEffectKind.Renamed,
            Source = Entry(Quantity.Create(4m), Quantity.Create(4m), retired: false, id),
            NewName = "Brass Rivets",
            NewNormalizedName = "brass rivets",
        };

        var proposal = Create([rename], [new ExpectedEntryVersion(id, Guid.NewGuid())]);

        var change = Assert.Single(proposal.Changes);
        Assert.Equal(id, change.SurvivingStockEntryId);
        Assert.Null(change.RetiredStockEntryId);
    }
}
```

Append to `tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void A_proposals_execution_identity_is_derived_and_therefore_survives_a_restart()
    {
        var proposalId = new ProposalId(Guid.NewGuid());

        Assert.Equal(StockOperationId.DeriveForProposal(proposalId), StockOperationId.DeriveForProposal(proposalId));
    }

    [Fact]
    public void Two_proposals_never_share_an_execution_identity()
    {
        Assert.NotEqual(
            StockOperationId.DeriveForProposal(new ProposalId(Guid.NewGuid())),
            StockOperationId.DeriveForProposal(new ProposalId(Guid.NewGuid())));
    }

    [Fact]
    public void A_proposals_execution_identity_can_never_collide_with_a_Turns_tool_identity()
    {
        // The two derivations hash differently shaped material, so no Turn/tool/sequence triple can
        // ever produce a proposal's identity - the two ledgers stay disjoint by construction.
        var shared = Guid.NewGuid();

        Assert.NotEqual(
            StockOperationId.DeriveForProposal(new ProposalId(shared)),
            StockOperationId.Derive(new TurnId(shared), "confirm_inventory_operation", sequence: 0));
    }
```

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ConfirmationProposalTests|FullyQualifiedName~StockOperationIdTests"`
Expected: FAIL to compile - `ProposalId`, `ProposedEntryState`, `ProposedChange`, `ExpectedEntryVersion`, `ExpectedEquivalentStockAbsence`, `ConfirmationProposal`, and `StockOperationId.DeriveForProposal` do not exist.

- [ ] **Step 2: Write the proposal**

Create `src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs`:

```csharp
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Strongly typed identity of one stored confirmation proposal.</summary>
public readonly record struct ProposalId(Guid Value)
{
    public static ProposalId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Where a stored proposal ended up. Every status other than <see cref="Pending"/> is terminal:
/// nothing ever returns to pending, so a proposal can be executed at most once no matter how many
/// times it is confirmed.
/// </summary>
public enum ProposalStatus
{
    /// <summary>Awaiting an explicit direct confirmation. At most one of these may exist per Participant and ChannelConversation.</summary>
    Pending,

    /// <summary>Executed. Set inside the very transaction that applied the changes.</summary>
    Confirmed,

    /// <summary>The Participant explicitly rejected it in direct content.</summary>
    Rejected,

    /// <summary>A newer proposal replaced it in the same conversation.</summary>
    Superseded,

    /// <summary>Its ten-minute lifetime ran out.</summary>
    Expired,

    /// <summary>The Participant lost access to the Inventory it was bound to.</summary>
    AccessLost,

    /// <summary>The conversation's Active Inventory changed, so the proposal no longer describes what the Participant is working in.</summary>
    InventorySwitched,

    /// <summary>An interrupted Turn arrived, so nothing said in this conversation may be treated as approval.</summary>
    Interrupted,

    /// <summary>Execution found current state no longer matching the expected versions, so nothing was applied.</summary>
    Conflicted,
}

/// <summary>
/// Exactly what one Stock Entry looks like before and after one proposed change. When
/// <see cref="StockEntryId"/> is null this state describes an entry the change will create, and the
/// name, Unit, and Location are the Equivalent Stock key it will be created at.
///
/// Everything the executor needs is here - identities, the resolved Unit and Location, the display
/// names for reporting, the Note, and both exact amounts - which is precisely why confirmation never
/// re-resolves anything and never recomputes against newer state.
/// </summary>
public sealed record ProposedEntryState(
    StockEntryId? StockEntryId,
    string Name,
    string NormalizedName,
    UnitId UnitId,
    string UnitCanonicalName,
    LocationId? LocationId,
    string? LocationName,
    string? Note,
    Quantity PreviousQuantity,
    Quantity ResultingQuantity,
    bool Retired);

/// <summary>
/// One exactly-decided change within a proposal. Which fields carry meaning is fixed by
/// <see cref="Effect"/>; the executor switches on it and reads only those fields.
/// </summary>
public sealed record ProposedChange
{
    /// <summary>1-based position within the proposal. Execution follows it, so effects apply in the order the Participant reviewed.</summary>
    public required int Order { get; init; }

    public required StockMutationKind Kind { get; init; }

    public required StockChangeEffectKind Effect { get; init; }

    public required ProposedEntryState Source { get; init; }

    /// <summary>Where stock lands, when the effect has a destination distinct from the source's own row.</summary>
    public ProposedEntryState? Destination { get; init; }

    public Quantity TransferredQuantity { get; init; } = Quantity.Zero;

    /// <summary>The exact new display name; set only for <see cref="StockChangeEffectKind.Renamed"/> and <see cref="StockChangeEffectKind.RenameMerged"/>.</summary>
    public string? NewName { get; init; }

    /// <summary>The normalized form of <see cref="NewName"/>, computed once while planning so the executor never re-normalizes.</summary>
    public string? NewNormalizedName { get; init; }

    public bool RetiresSource => StockAuditFacts.RetiresSource(Effect);

    /// <summary>
    /// The Stock Entry that still exists once this change is applied. When a merge retires the
    /// source, that is the destination; otherwise it is the entry itself. Null only while the
    /// surviving entry is still to be created.
    /// </summary>
    public StockEntryId? SurvivingStockEntryId => RetiresSource ? Destination?.StockEntryId : Source.StockEntryId;

    /// <summary>The Stock Entry whose identity this change ends, or null when it ends none.</summary>
    public StockEntryId? RetiredStockEntryId => RetiresSource ? Source.StockEntryId : null;
}

/// <summary>
/// The version one existing Stock Entry carried when the proposal was made. Execution refuses unless
/// the row still carries it, so a proposal decided against a state nobody holds any more can never
/// land. The stamp - not the Quantity - is the version: an unrelated write that happened to restore
/// the same amount still changes the stamp, and must still invalidate the proposal.
/// </summary>
public sealed record ExpectedEntryVersion(StockEntryId StockEntryId, Guid ConcurrencyStamp);

/// <summary>
/// The Equivalent Stock key a proposal expects to still be empty, because it intends to create it.
/// Enforced at execution by the same filtered unique indexes that define Equivalent Stock, so a
/// competing writer that created it first turns into a conflict rather than a duplicate.
/// </summary>
public sealed record ExpectedEquivalentStockAbsence(string NormalizedName, UnitId UnitId, LocationId? LocationId);

/// <summary>
/// One exact, immutable, server-stored set of changes awaiting explicit confirmation, bound to the
/// Participant, ChannelConversation, Inventory, and Turn that produced it, carrying the expected
/// versions it was decided against and the hash of its single-use token.
///
/// Nothing here can be re-derived at confirmation time: the whole point is that what the Participant
/// reviewed is exactly what commits.
/// </summary>
public sealed record ConfirmationProposal
{
    /// <summary>How long a confirmation stays valid. Ten minutes, per the specification.</summary>
    public const int LifetimeMinutes = 10;

    /// <summary>How many changes one proposal may carry - the bound on what a Participant can actually review in one answer.</summary>
    public const int MaxChanges = 25;

    public required ProposalId Id { get; init; }

    public required ConfirmationTokenHash TokenHash { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required string ChannelConversationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Turn that produced this proposal, recorded so a proposal is traceable to the request it came from.</summary>
    public required TurnId ProposedInTurnId { get; init; }

    public required IReadOnlyList<ProposedChange> Changes { get; init; }

    public required IReadOnlyList<ExpectedEntryVersion> ExpectedVersions { get; init; }

    public required IReadOnlyList<ExpectedEquivalentStockAbsence> ExpectedAbsences { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The ledger identity this proposal's execution is recorded under. Derived from the proposal
    /// itself rather than from whichever Turn confirms it, so the identity is fixed the moment the
    /// proposal exists and a re-driven confirmation cannot mint a second one.
    /// </summary>
    public StockOperationId ExecutionOperationId => StockOperationId.DeriveForProposal(Id);

    public DateTimeOffset ExpiresAt => CreatedAt.AddMinutes(LifetimeMinutes);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>
    /// Whether this proposal belongs to exactly this trusted context. A proposal that does not is
    /// treated as if it did not exist, so a token can never be replayed into another Participant's
    /// conversation or another Inventory.
    /// </summary>
    public bool BelongsTo(ParticipantId participantId, string channelConversationId, InventoryId inventoryId) =>
        ParticipantId == participantId
        && string.Equals(ChannelConversationId, channelConversationId, StringComparison.Ordinal)
        && InventoryId == inventoryId;

    public static ConfirmationProposal Create(
        ConfirmationTokenHash tokenHash,
        ParticipantId participantId,
        string? channelConversationId,
        InventoryId inventoryId,
        TurnId proposedInTurnId,
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> expectedVersions,
        IReadOnlyList<ExpectedEquivalentStockAbsence> expectedAbsences,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelConversationId);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(expectedVersions);
        ArgumentNullException.ThrowIfNull(expectedAbsences);

        if (changes.Count == 0)
        {
            throw new ArgumentException("A proposal must carry at least one change.", nameof(changes));
        }

        if (changes.Count > MaxChanges)
        {
            throw new ArgumentException($"A proposal must not carry more than {MaxChanges} changes.", nameof(changes));
        }

        if (changes.Select(change => change.Order).Distinct().Count() != changes.Count)
        {
            throw new ArgumentException("Change order must be unique within a proposal.", nameof(changes));
        }

        // Every existing row this proposal will write to must be pinned to the version it was decided
        // against. A missing one is not a small omission: it is the difference between "apply what was
        // reviewed" and "overwrite whatever is there now".
        var versioned = expectedVersions.Select(version => version.StockEntryId).ToHashSet();
        foreach (var change in changes)
        {
            RequireVersioned(change.Source, versioned, nameof(expectedVersions));
            if (change.Destination is { } destination)
            {
                RequireVersioned(destination, versioned, nameof(expectedVersions));
            }
        }

        return new ConfirmationProposal
        {
            Id = ProposalId.NewId(),
            TokenHash = tokenHash,
            ParticipantId = participantId,
            ChannelConversationId = channelConversationId.Trim(),
            InventoryId = inventoryId,
            ProposedInTurnId = proposedInTurnId,
            Changes = changes.OrderBy(change => change.Order).ToList(),
            ExpectedVersions = expectedVersions.ToList(),
            ExpectedAbsences = expectedAbsences.ToList(),
            CreatedAt = createdAt,
        };
    }

    private static void RequireVersioned(ProposedEntryState state, HashSet<StockEntryId> versioned, string parameterName)
    {
        // A state with no identity is one this proposal creates; its safety comes from an expected
        // absence and the Equivalent Stock uniqueness index, not from a version.
        if (state.StockEntryId is { } id && !versioned.Contains(id))
        {
            throw new ArgumentException("Every existing Stock Entry a proposal touches must carry an expected version.", parameterName);
        }
    }
}
```

- [ ] **Step 3: Derive the proposal's execution identity**

In `src/MultiChannelAgent.Domain/Inventories/StockOperationId.cs`, add this member to `StockOperationId`, directly after `Derive`:

```csharp
    /// <summary>
    /// The stable identity a confirmed proposal's execution is recorded under. It is derived from the
    /// proposal rather than from the Turn that confirms it, so the ledger key is fixed the moment the
    /// proposal is stored: the proposal is consumed by execution, and a Turn re-driven afterwards
    /// must still be able to find what its own first attempt did.
    ///
    /// The material is deliberately shaped unlike <see cref="Derive"/>'s, so no Turn, tool, and
    /// sequence triple can ever hash to a proposal's identity.
    /// </summary>
    public static StockOperationId DeriveForProposal(ProposalId proposalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"proposal|{proposalId.Value:D}"));

        return new StockOperationId(new Guid(digest.AsSpan(0, 16)));
    }
```

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ConfirmationProposalTests|FullyQualifiedName~StockOperationIdTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs src/MultiChannelAgent.Domain/Inventories/StockOperationId.cs tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs tests/MultiChannelAgent.Domain.Tests/Inventories/StockOperationIdTests.cs
git commit -m "feat(inventories): model an exact, bound, single-use confirmation proposal for #32"
```

---

## Task 5: Let a channel declare a Turn interrupted

**Files:**
- Modify: `src/MultiChannelAgent.Domain/Turns/InboundTurn.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs`
- Modify: `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/InboundTurnTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs`

Why now: "interruption invalidates a pending proposal" is an acceptance criterion, and interruption is a *channel* fact, not something the core can infer. Modelling it at the boundary now means the later voice adapter has one flag to set, and it means the confirmation evidence reader (Task 6) can refuse an interrupted Turn from the very start rather than being retrofitted.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MultiChannelAgent.Domain.Tests/InboundTurnTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void A_Turn_is_not_interrupted_unless_its_channel_says_so()
    {
        var turn = InboundTurn.Create(InboundTurnDraft.DirectText(
            "native-1",
            new ParticipantId(Guid.NewGuid()),
            "web:profile-1",
            "web",
            ChannelPrincipal.EntraUser("subject", "tenant"),
            ChannelCapabilities.Text,
            "confirm",
            locale: null,
            DateTimeOffset.UnixEpoch,
            traceId: null));

        Assert.False(turn.WasInterrupted);
    }

    [Fact]
    public void A_channel_that_reports_an_interrupted_utterance_keeps_that_on_the_Turn()
    {
        var turn = InboundTurn.Create(InboundTurnDraft.DirectText(
            "native-2",
            new ParticipantId(Guid.NewGuid()),
            "web:profile-1",
            "web",
            ChannelPrincipal.EntraUser("subject", "tenant"),
            ChannelCapabilities.Text,
            "confirm",
            locale: null,
            DateTimeOffset.UnixEpoch,
            traceId: null,
            wasInterrupted: true));

        Assert.True(turn.WasInterrupted);
    }
```

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~InboundTurnTests"`
Expected: FAIL to compile - `InboundTurn.WasInterrupted` and the `wasInterrupted` parameter do not exist.

- [ ] **Step 2: Carry interruption through the domain contract**

In `src/MultiChannelAgent.Domain/Turns/InboundTurn.cs`:

Add to `InboundTurnDraft`, after `TraceId`:

```csharp
    /// <summary>
    /// Whether the channel observed this utterance being interrupted - cut off mid-sentence, barged
    /// in on, or otherwise left unfinished. Only the adapter can know this, and it changes what the
    /// content may be used for: an interrupted utterance is never a reliable statement of intent, so
    /// it may never confirm anything and it invalidates whatever confirmation was pending.
    /// </summary>
    public bool WasInterrupted { get; init; }
```

Add the same property to `InboundTurn`, after `TraceId`:

```csharp
    /// <summary>See <see cref="InboundTurnDraft.WasInterrupted"/>. Durable, because it decides what this Turn may authorize.</summary>
    public bool WasInterrupted { get; init; }
```

Add a trailing optional parameter to `InboundTurnDraft.DirectText` after `traceId`, and set it in the returned draft. The whole factory then reads:

```csharp
    public static InboundTurnDraft DirectText(
        string? nativeMessageId,
        ParticipantId participantId,
        string? channelConversationId,
        string? channel,
        ChannelPrincipal principal,
        ChannelCapabilities capabilities,
        string? contentText,
        string? locale,
        DateTimeOffset receivedAt,
        string? traceId,
        bool wasInterrupted = false) => new()
        {
            NativeMessageId = nativeMessageId,
            ParticipantId = participantId,
            ChannelConversationId = channelConversationId,
            Channel = channel,
            Principal = principal,
            Capabilities = capabilities,
            ContentParts = [TurnContentPart.Create(1, ContentProvenance.Direct, contentText)],
            Locale = locale,
            ReceivedAt = receivedAt,
            TraceId = traceId,
            WasInterrupted = wasInterrupted,
        };
```

And set it in `InboundTurn.Create`'s returned object, after `TraceId`:

```csharp
            WasInterrupted = draft.WasInterrupted,
```

- [ ] **Step 3: Carry it through acceptance**

In `src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs`, add a trailing parameter to the positional record:

```csharp
public sealed record SubmitTurnRequest(
    string NativeMessageId,
    ParticipantId ParticipantId,
    string ChannelConversationId,
    string Channel,
    ChannelPrincipal Principal,
    ChannelCapabilities Capabilities,
    string ContentText,
    string? Locale,
    string? TraceId,
    bool WasInterrupted = false);
```

In `src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs`, pass it to the draft:

```csharp
            request.TraceId,
            request.WasInterrupted));
```

- [ ] **Step 4: Persist it**

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/InboxEntryEntity.cs`, add after `TraceId`:

```csharp
    /// <summary>
    /// Whether the channel reported this utterance as interrupted. Durable because it is part of what
    /// the Turn <em>is</em>: a Turn re-driven after a restart must still refuse to confirm anything.
    /// </summary>
    public bool WasInterrupted { get; set; }
```

In `src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs`, set it where `TraceId` is written (around line 74):

```csharp
                TraceId = turn.TraceId,
                WasInterrupted = turn.WasInterrupted,
```

and read it back where `TraceId` is mapped (around line 228):

```csharp
            TraceId = entity.TraceId,
            WasInterrupted = entity.WasInterrupted,
```

No configuration change is needed: a `bool` maps to a non-nullable `bit` by convention.

- [ ] **Step 5: Accept it at the web boundary**

In `src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs`, extend the wire shape:

```csharp
public sealed record SubmitTurnHttpRequest(
    string? NativeMessageId,
    string? ContentText,
    string? Locale,
    string? TraceId,
    bool Interrupted = false);
```

and pass it into the acceptance request as the trailing argument, after `request.TraceId`:

```csharp
                    request.TraceId,
                    request.Interrupted),
```

This is channel evidence, not identity: a client can only ever use it to make its own Turn *less* trusted, never more.

- [ ] **Step 6: Generate the migration**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddTurnInterruption \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --output-dir Persistence/Migrations
```

Open the generated `*_AddTurnInterruption.cs` and confirm it adds exactly one column, `WasInterrupted`, to `InboxEntries`, with `nullable: false` and `defaultValue: false`. That default **is** the backfill: every Turn accepted before this migration was not interrupted. If the generated migration contains anything else, delete it, fix the model, and regenerate rather than hand-editing it.

- [ ] **Step 7: Let a scenario submit an interrupted Turn**

In `tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs`, add:

```csharp
    /// <summary>Submits a Turn the channel reports as interrupted - a cut-off utterance that may authorize nothing.</summary>
    public async Task<Guid> SubmitInterruptedTurnAsync(string nativeMessageId, string contentText)
    {
        var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/turns")
            {
                Content = JsonContent.Create(new { nativeMessageId, contentText, interrupted = true }),
            },
            withCsrf: true);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();
    }
```

- [ ] **Step 8: Verify**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~InboundTurnTests"`
Expected: PASS.

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~InboundTurnContractSqliteTests"`
Expected: PASS - the durable channel contract still round-trips.

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Domain/Turns/InboundTurn.cs src/MultiChannelAgent.Application/Turns/SubmitTurnRequest.cs src/MultiChannelAgent.Application/Turns/TurnAcceptanceService.cs src/MultiChannelAgent.Infrastructure/Persistence src/MultiChannelAgent.Infrastructure/Turns/SqlInboxStore.cs src/MultiChannelAgent.Host/Endpoints/TurnEndpoints.cs tests/MultiChannelAgent.Domain.Tests/InboundTurnTests.cs tests/MultiChannelAgent.IntegrationTests/ConversationTestClient.cs
git commit -m "feat(turns): let a channel declare a Turn interrupted for #32"
```

---

## Task 6: Derive confirmation evidence from direct content only

**Files:**
- Create: `src/MultiChannelAgent.Application/Turns/DirectConfirmationEvidence.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Turns/DirectConfirmationEvidenceTests.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs`

Why: the model must never be the reason a mutation is approved. `InboundTurn.ContentText` already excludes quoted, forwarded, attached, retrieved, tool-produced, and model-derived parts, so deriving evidence from it - in the application, before any tool runs - makes "only a new direct explicit affirmative confirms" structural.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Turns/DirectConfirmationEvidenceTests.cs`:

```csharp
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Turns;

public sealed class DirectConfirmationEvidenceTests
{
    private static InboundTurn Turn(IReadOnlyList<TurnContentPart> parts, bool wasInterrupted = false) =>
        InboundTurn.Create(new InboundTurnDraft
        {
            NativeMessageId = "native-1",
            ParticipantId = new ParticipantId(Guid.NewGuid()),
            ChannelConversationId = "web:profile-1",
            Channel = "web",
            Principal = ChannelPrincipal.EntraUser("subject", "tenant"),
            Capabilities = ChannelCapabilities.Text,
            ContentParts = parts,
            ReceivedAt = DateTimeOffset.UnixEpoch,
            WasInterrupted = wasInterrupted,
        });

    private static InboundTurn DirectTurn(string text, bool wasInterrupted = false) =>
        Turn([TurnContentPart.Create(1, ContentProvenance.Direct, text)], wasInterrupted);

    [Theory]
    [InlineData("confirm")]
    [InlineData("Confirm")]
    [InlineData("CONFIRM")]
    [InlineData("confirmed")]
    [InlineData("yes")]
    [InlineData("Yes, please")]
    [InlineData("approve")]
    [InlineData("approved")]
    [InlineData("do it")]
    [InlineData("go ahead")]
    [InlineData("confirm 8Fh3kQ")]
    public void A_direct_explicit_affirmative_confirms(string text) =>
        Assert.Equal(DirectConfirmationEvidence.Confirmed, DirectConfirmationEvidenceReader.Read(DirectTurn(text)));

    [Theory]
    [InlineData("reject")]
    [InlineData("rejected")]
    [InlineData("no")]
    [InlineData("No thanks")]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("don't")]
    [InlineData("do not")]
    public void A_direct_explicit_negative_rejects(string text) =>
        Assert.Equal(DirectConfirmationEvidence.Rejected, DirectConfirmationEvidenceReader.Read(DirectTurn(text)));

    [Theory]
    [InlineData("list stock")]
    [InlineData("please confirm the order with the supplier")]
    [InlineData("yesterday we counted 40")]
    [InlineData("confirmation is needed")]
    [InlineData("nobody has these")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_that_is_not_an_explicit_leading_answer_confirms_nothing(string text)
    {
        var parts = text.Trim().Length == 0
            ? new[] { TurnContentPart.Create(1, ContentProvenance.Direct, "list stock") }
            : [TurnContentPart.Create(1, ContentProvenance.Direct, text)];

        var evidence = DirectConfirmationEvidenceReader.Read(Turn(parts));

        Assert.Equal(DirectConfirmationEvidence.None, evidence);
    }

    [Fact]
    public void Quoted_content_that_says_confirm_never_confirms()
    {
        var turn = Turn(
        [
            TurnContentPart.Create(1, ContentProvenance.Direct, "what does this say?"),
            TurnContentPart.Create(2, ContentProvenance.Quoted, "confirm"),
        ]);

        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void Model_derived_and_tool_produced_content_that_says_confirm_never_confirms()
    {
        var turn = Turn(
        [
            TurnContentPart.Create(1, ContentProvenance.Direct, "summarize that"),
            TurnContentPart.Create(2, ContentProvenance.ToolProduced, "confirm"),
            TurnContentPart.Create(3, ContentProvenance.ModelDerived, "yes"),
        ]);

        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(turn));
    }

    [Fact]
    public void An_interrupted_utterance_never_confirms_however_affirmative_it_reads()
    {
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(DirectTurn("confirm", wasInterrupted: true)));
    }

    [Fact]
    public void An_interrupted_utterance_does_not_reject_either_so_nothing_is_read_into_it()
    {
        Assert.Equal(DirectConfirmationEvidence.None, DirectConfirmationEvidenceReader.Read(DirectTurn("no", wasInterrupted: true)));
    }
}
```

Append to `tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs`, inside the existing class:

```csharp
    [Fact]
    public async Task The_trusted_context_carries_the_Turns_own_confirmation_evidence()
    {
        var harness = new Harness();
        var turn = harness.DirectTurn("confirm");

        var context = await harness.Factory.CreateAsync(turn, harness.Now, CancellationToken.None);

        Assert.Equal(DirectConfirmationEvidence.Confirmed, context.Confirmation);
        Assert.False(context.WasInterrupted);
    }

    [Fact]
    public async Task An_interrupted_Turn_reaches_tool_dispatch_marked_as_such_and_confirming_nothing()
    {
        var harness = new Harness();
        var turn = harness.DirectTurn("confirm", wasInterrupted: true);

        var context = await harness.Factory.CreateAsync(turn, harness.Now, CancellationToken.None);

        Assert.Equal(DirectConfirmationEvidence.None, context.Confirmation);
        Assert.True(context.WasInterrupted);
    }
```

If `TurnExecutionContextFactoryTests` has no `Harness` type, add whatever minimal private helper the existing tests already use to build a Turn and a factory, and give it a `DirectTurn(string text, bool wasInterrupted = false)` method plus a `Now` property; do not restructure the existing tests.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~DirectConfirmationEvidenceTests|FullyQualifiedName~TurnExecutionContextFactoryTests"`
Expected: FAIL to compile - `DirectConfirmationEvidence`, `DirectConfirmationEvidenceReader`, and the two new context properties do not exist.

- [ ] **Step 2: Write the reader**

Create `src/MultiChannelAgent.Application/Turns/DirectConfirmationEvidence.cs`:

```csharp
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>What the Participant themselves said, in this Turn, about a pending proposal.</summary>
public enum DirectConfirmationEvidence
{
    /// <summary>Nothing in this Turn is an explicit answer, so nothing may be confirmed or rejected by it.</summary>
    None,

    /// <summary>The Participant explicitly approved, in their own direct content, in this very Turn.</summary>
    Confirmed,

    /// <summary>The Participant explicitly declined, in their own direct content, in this very Turn.</summary>
    Rejected,
}

/// <summary>
/// Derives <see cref="DirectConfirmationEvidence"/> from a Turn, and from nothing else. This is the
/// only thing that may authorize executing a stored proposal, which is why it is deliberately dull:
/// it reads <see cref="InboundTurn.ContentText"/> - already restricted to
/// <see cref="ContentProvenance.Direct"/> parts - and looks for one explicit answer at the very
/// start of it.
///
/// Three consequences follow, and each of them is an acceptance criterion:
///
/// <list type="bullet">
/// <item>Quoted, forwarded, attached, retrieved, tool-produced, and model-derived text cannot
/// confirm, because none of it is in <see cref="InboundTurn.ContentText"/> at all.</item>
/// <item>A model proposing <c>confirm_inventory_operation</c> on its own confirms nothing, because
/// the model does not contribute to this at all.</item>
/// <item>An interrupted utterance confirms nothing, because a cut-off sentence is not a statement of
/// intent - and it is not read as a rejection either, since inventing a refusal from silence is its
/// own kind of guessing.</item>
/// </list>
///
/// Matching is anchored at the start and requires a whole word, so "please confirm the order with the
/// supplier" and "yesterday we counted 40" are ordinary requests, not approvals.
/// </summary>
public static class DirectConfirmationEvidenceReader
{
    // Longest first, so "do not" is never read as the "do it"-shaped start of something else and
    // "confirmed" is never truncated to "confirm".
    private static readonly string[] Affirmatives = ["go ahead", "confirmed", "approved", "confirm", "approve", "do it", "yes"];

    private static readonly string[] Negatives = ["rejected", "cancelled", "do not", "cancel", "reject", "don't", "stop", "no"];

    public static DirectConfirmationEvidence Read(InboundTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.WasInterrupted)
        {
            return DirectConfirmationEvidence.None;
        }

        var text = turn.ContentText.TrimStart();

        // Negatives are considered first: declining is the safe reading of an ambiguous answer, and
        // the two vocabularies share no leading word, so this ordering only ever matters if one is
        // later added carelessly.
        if (StartsWithAnswer(text, Negatives))
        {
            return DirectConfirmationEvidence.Rejected;
        }

        return StartsWithAnswer(text, Affirmatives) ? DirectConfirmationEvidence.Confirmed : DirectConfirmationEvidence.None;
    }

    private static bool StartsWithAnswer(string text, string[] answers)
    {
        foreach (var answer in answers)
        {
            if (!text.StartsWith(answer, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The answer must stand as its own word: "confirmation is needed" is not a confirmation,
            // and "nobody" is not a "no".
            if (text.Length == answer.Length || !char.IsLetterOrDigit(text[answer.Length]))
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 3: Put the evidence in trusted context**

In `src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs`, extend the record and the factory:

```csharp
public sealed record TurnExecutionContext(
    TurnId TurnId,
    ParticipantId ParticipantId,
    ChannelConversationId ChannelConversationId,
    FoundryConversationId FoundryConversationId,
    int FoundryConversationGeneration,
    InventoryId? ActiveInventoryId,
    string? TraceId,
    DirectConfirmationEvidence Confirmation = DirectConfirmationEvidence.None,
    bool WasInterrupted = false);
```

and, in `TurnExecutionContextFactory.CreateAsync`, pass them as the two trailing arguments:

```csharp
        return new TurnExecutionContext(
            turn.TurnId,
            turn.ParticipantId,
            turn.ChannelConversationId,
            binding.FoundryConversationId,
            binding.Generation,
            activeInventoryId,
            turn.TraceId,

            // Derived from the Turn's own direct content, here, before the model is asked anything -
            // so no proposal the model makes can ever be the reason a mutation was approved.
            DirectConfirmationEvidenceReader.Read(turn),
            turn.WasInterrupted);
```

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~DirectConfirmationEvidenceTests|FullyQualifiedName~TurnExecutionContextFactoryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/DirectConfirmationEvidence.cs src/MultiChannelAgent.Application/Turns/TurnExecutionContext.cs tests/MultiChannelAgent.Application.Tests/Turns/DirectConfirmationEvidenceTests.cs tests/MultiChannelAgent.Application.Tests/TurnExecutionContextFactoryTests.cs
git commit -m "feat(turns): read explicit confirmation from direct current-turn content only for #32"
```

---

## Task 7: Read a Stock Entry's version and a reference's authoritative name

**Files:**
- Modify: `src/MultiChannelAgent.Application/Inventories/IStockStore.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/IInventoryReferenceStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryReferenceStore.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryInventoryReferenceStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockStoreQueryTests.cs`

Why: two reads are missing, and a proposal needs both. It must pin every existing row it will write to, and the display projection (`StockEntrySummary`) deliberately does not carry a concurrency stamp - that is a persistence concern, and every List and Find result would then be carrying one - so versions are read through their own narrow method. And a proposal that *creates* a Stock Entry, or names a destination Location, must report the reference's **authoritative** name rather than echoing whatever text the request happened to use, or an exact proposal would show a name the Inventory does not actually hold.

- [ ] **Step 1: Write the failing test**

Append to `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockStoreQueryTests.cs`, inside the existing class (match the class's existing seeding helpers; the point of the test is the three assertions):

```csharp
    [SkippableFact]
    public async Task Reading_versions_returns_one_per_known_Stock_Entry_and_nothing_for_the_rest()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed Stock version read.");

        var (inventoryId, unitId) = await SeedInventoryAsync();
        var first = await SeedStockAsync(inventoryId, unitId, "Steel Bolts", 4m);
        var second = await SeedStockAsync(inventoryId, unitId, "Brass Rivets", 6m);
        var store = Store();

        var versions = await store.ReadVersionsAsync(
            new InventoryId(inventoryId),
            [new StockEntryId(first), new StockEntryId(second), new StockEntryId(Guid.NewGuid())],
            CancellationToken.None);

        Assert.Equal(2, versions.Count);
        Assert.All(versions, version => Assert.NotEqual(Guid.Empty, version.ConcurrencyStamp));
        Assert.Empty(await store.ReadVersionsAsync(new InventoryId(Guid.NewGuid()), [new StockEntryId(first)], CancellationToken.None));
    }
```

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockStoreQueryTests"`
Expected: FAIL to compile - `IStockStore.ReadVersionsAsync` does not exist. (Without Docker this test skips; the compile failure still happens, which is the red you need.)

- [ ] **Step 2: Add the seam**

In `src/MultiChannelAgent.Application/Inventories/IStockStore.cs`, add to the interface:

```csharp
    /// <summary>
    /// The current optimistic-concurrency version of each named Stock Entry within
    /// <paramref name="inventoryId"/>. Entries that do not exist, or that belong to another
    /// Inventory, are simply absent - a version can never be read across an Inventory boundary.
    ///
    /// This exists separately from the display projection on purpose: a concurrency stamp is a
    /// persistence concern, and every List row and Find candidate would otherwise carry one into
    /// views and payloads that must never expose it.
    /// </summary>
    Task<IReadOnlyList<StockEntryVersion>> ReadVersionsAsync(
        InventoryId inventoryId, IReadOnlyList<StockEntryId> stockEntryIds, CancellationToken cancellationToken);
```

and, next to `StockMatchFacets`:

```csharp
/// <summary>
/// One Stock Entry's current version and Quantity. The stamp - not the Quantity - is what a proposal
/// pins: an unrelated write that happened to restore the same amount still changes the stamp, and
/// must still invalidate a proposal decided before it.
/// </summary>
public sealed record StockEntryVersion(StockEntryId StockEntryId, Guid ConcurrencyStamp, Quantity Quantity);
```

- [ ] **Step 2b: Add the reference-name seam**

In `src/MultiChannelAgent.Application/Inventories/IInventoryReferenceStore.cs`, add to the interface:

```csharp
    /// <summary>
    /// The canonical name of an active Unit in this Inventory, or null when there is no such Unit
    /// here. A proposal reports this rather than the alias or the raw text a request happened to use,
    /// so what a Participant reviews is the name the Inventory actually holds.
    /// </summary>
    Task<string?> FindUnitCanonicalNameAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken);

    /// <summary>The name of an active Location in this Inventory, or null when there is no such Location here. See <see cref="FindUnitCanonicalNameAsync"/>.</summary>
    Task<string?> FindLocationNameAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken);
```

Implement both in `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryReferenceStore.cs` as single scoped `Select`/`FirstOrDefaultAsync` reads over `Units` and `Locations`, filtered by `InventoryId` exactly as every other method in that class is, and in `InMemoryInventoryReferenceStore` from whatever it already stores for its seeded references.

- [ ] **Step 3: Implement it against SQL**

In `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockStore.cs`, add:

```csharp
    public async Task<IReadOnlyList<StockEntryVersion>> ReadVersionsAsync(
        InventoryId inventoryId, IReadOnlyList<StockEntryId> stockEntryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stockEntryIds);

        if (stockEntryIds.Count == 0)
        {
            return [];
        }

        var ids = stockEntryIds.Select(id => id.Value).Distinct().ToList();

        var rows = await db.StockEntries
            .AsNoTracking()
            .Where(e => e.InventoryId == inventoryId.Value && ids.Contains(e.Id))
            .Select(e => new { e.Id, e.ConcurrencyStamp, e.Quantity })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new StockEntryVersion(new StockEntryId(row.Id), row.ConcurrencyStamp, Quantity.Create(row.Quantity)))
            .ToList();
    }
```

- [ ] **Step 4: Implement it in the double**

In `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs`, keep a per-row `Guid` stamp alongside each row, regenerate it in every mutating helper the double already has (`SetQuantity`, `CreateRow`), and add:

```csharp
    public Task<IReadOnlyList<StockEntryVersion>> ReadVersionsAsync(
        InventoryId inventoryId, IReadOnlyList<StockEntryId> stockEntryIds, CancellationToken cancellationToken)
    {
        var wanted = stockEntryIds.ToHashSet();

        IReadOnlyList<StockEntryVersion> versions = Rows(inventoryId)
            .Where(row => wanted.Contains(row.Id))
            .Select(row => new StockEntryVersion(row.Id, StampOf(row.Id), row.Quantity))
            .ToList();

        return Task.FromResult(versions);
    }
```

Use whatever the double already calls its per-Inventory row lookup in place of `Rows`, and add a private `StampOf` backed by a `Dictionary<StockEntryId, Guid>` that returns a stable stamp per row, regenerated whenever that row changes.

- [ ] **Step 5: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - the double compiles against the widened seam and nothing else changed.

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockStoreQueryTests"`
Expected: PASS. If Docker is unavailable, confirm the test reports as skipped rather than failed and rely on CI.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IStockStore.cs src/MultiChannelAgent.Application/Inventories/IInventoryReferenceStore.cs src/MultiChannelAgent.Infrastructure/Inventories/SqlStockStore.cs src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryReferenceStore.cs tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockStoreQueryTests.cs
git commit -m "feat(inventories): read a Stock Entry version and a reference's authoritative name for #32"
```

---

## Task 8: Define the pending proposal store seam

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/IConfirmationProposalStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/InMemoryConfirmationProposalStoreTests.cs`

Why: the seam states the invariants the SQL adapter must satisfy - one pending proposal per Participant and ChannelConversation, replacement that supersedes atomically, a guarded settle that only the first caller wins - so both the double and the SQL store are written against the same contract, and the Application tests that follow are meaningful.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/InMemoryConfirmationProposalStoreTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// Pins the contract the double and SqlConfirmationProposalStore must both satisfy. The SQL twin of
/// these assertions lives in SqlConfirmationProposalStoreTests, where the same invariants are proved
/// against real relational constraints.
/// </summary>
public sealed class InMemoryConfirmationProposalStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly InventoryId Inventory = new(Guid.NewGuid());
    private const string Conversation = "web:profile-1";

    private static ConfirmationProposal Proposal(string? conversation = null, ParticipantId? participantId = null)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            participantId ?? Participant,
            conversation ?? Conversation,
            Inventory,
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", new UnitId(Guid.NewGuid()), "each",
                        null, null, null, Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    [Fact]
    public async Task A_stored_proposal_is_found_by_its_Participant_and_conversation()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();

        var replacement = await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.False(replacement.SupersededExisting);
        Assert.Equal(proposal.Id, (await store.FindPendingAsync(Participant, Conversation, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task Storing_a_new_proposal_supersedes_the_pending_one_in_that_conversation()
    {
        var store = new InMemoryConfirmationProposalStore();
        var first = Proposal();
        var second = Proposal();
        await store.StoreAsync(first, Now, CancellationToken.None);

        var replacement = await store.StoreAsync(second, Now, CancellationToken.None);

        Assert.True(replacement.SupersededExisting);
        Assert.Equal(ProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Equal(second.Id, (await store.FindPendingAsync(Participant, Conversation, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task One_conversations_proposal_is_invisible_to_another_conversation_and_to_another_Participant()
    {
        var store = new InMemoryConfirmationProposalStore();
        await store.StoreAsync(Proposal(), Now, CancellationToken.None);

        Assert.Null(await store.FindPendingAsync(Participant, "web:profile-2", CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(new ParticipantId(Guid.NewGuid()), Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task Two_conversations_may_each_hold_their_own_pending_proposal()
    {
        var store = new InMemoryConfirmationProposalStore();
        var first = Proposal();
        var second = Proposal("web:profile-2");

        await store.StoreAsync(first, Now, CancellationToken.None);
        await store.StoreAsync(second, Now, CancellationToken.None);

        Assert.Equal(first.Id, (await store.FindPendingAsync(Participant, Conversation, CancellationToken.None))!.Id);
        Assert.Equal(second.Id, (await store.FindPendingAsync(Participant, "web:profile-2", CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task Only_the_first_caller_settles_a_pending_proposal()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.True(await store.SettleAsync(proposal.Id, ProposalStatus.Rejected, Now, CancellationToken.None));
        Assert.False(await store.SettleAsync(proposal.Id, ProposalStatus.Confirmed, Now, CancellationToken.None));
        Assert.Equal(ProposalStatus.Rejected, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(Participant, Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task Invalidating_the_pending_proposal_settles_exactly_the_one_in_that_conversation()
    {
        var store = new InMemoryConfirmationProposalStore();
        var mine = Proposal();
        var other = Proposal("web:profile-2");
        await store.StoreAsync(mine, Now, CancellationToken.None);
        await store.StoreAsync(other, Now, CancellationToken.None);

        var invalidated = await store.InvalidatePendingAsync(
            Participant, Conversation, ProposalStatus.InventorySwitched, Now, CancellationToken.None);

        Assert.Equal(1, invalidated);
        Assert.Equal(ProposalStatus.InventorySwitched, await store.FindStatusAsync(mine.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await store.FindStatusAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Expiring_settles_only_proposals_whose_lifetime_has_run_out()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.Equal(0, await store.ExpirePendingBeforeAsync(Now.AddMinutes(9), 100, CancellationToken.None));
        Assert.Equal(1, await store.ExpirePendingBeforeAsync(Now.AddMinutes(10), 100, CancellationToken.None));
        Assert.Equal(ProposalStatus.Expired, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Settled_proposals_are_deleted_only_once_they_are_past_retention()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);
        await store.SettleAsync(proposal.Id, ProposalStatus.Rejected, Now, CancellationToken.None);

        Assert.Equal(0, await store.DeleteSettledBeforeAsync(Now.AddHours(23), 100, CancellationToken.None));
        Assert.Equal(1, await store.DeleteSettledBeforeAsync(Now.AddHours(25), 100, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
}
```

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~InMemoryConfirmationProposalStoreTests"`
Expected: FAIL to compile - neither the seam nor the double exists.

- [ ] **Step 2: Write the seam**

Create `src/MultiChannelAgent.Application/Inventories/IConfirmationProposalStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>What storing a proposal did to whatever was pending in that conversation before it.</summary>
public sealed record StoredProposalReplacement(bool SupersededExisting);

/// <summary>
/// The durable home of pending confirmation proposals.
///
/// One invariant dominates the contract and must be enforced by the database rather than by
/// convention: <b>at most one Pending proposal may exist per Participant and ChannelConversation</b>.
/// That is what makes "confirm" unambiguous - there is only ever one thing it could mean - and it is
/// why <see cref="StoreAsync"/> supersedes and inserts atomically rather than leaving a window in
/// which a conversation has two, or none.
///
/// Lookup is deliberately by Participant and ChannelConversation, never by token: a token belonging
/// to someone else, or to another conversation, cannot even be looked up here, so non-disclosure is
/// structural rather than a code path someone has to remember to write.
/// </summary>
public interface IConfirmationProposalStore
{
    /// <summary>The one Pending proposal for this Participant and ChannelConversation, or null when there is none.</summary>
    Task<ConfirmationProposal?> FindPendingAsync(
        ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new Pending proposal, atomically superseding whatever was Pending for the same
    /// Participant and ChannelConversation. A stale confirmation can therefore never execute the
    /// proposal a replacement replaced.
    /// </summary>
    Task<StoredProposalReplacement> StoreAsync(ConfirmationProposal proposal, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a Pending proposal to a terminal status. Returns false when it was not Pending any more,
    /// which is exactly how single use is enforced: the second confirmation, rejection, or
    /// invalidation of one proposal loses and must be answered as such rather than acting.
    /// </summary>
    Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken);

    /// <summary>The status of a proposal, or null when no such proposal is retained. For diagnosis and tests, never for authorization.</summary>
    Task<ProposalStatus?> FindStatusAsync(ProposalId proposalId, CancellationToken cancellationToken);

    /// <summary>
    /// Settles whatever is Pending for this Participant and ChannelConversation, returning how many
    /// rows moved (0 or 1). This is the one entry point for every invalidation that is not a
    /// confirmation or a rejection: access loss, an Inventory switch, and an interrupted Turn.
    /// </summary>
    Task<int> InvalidatePendingAsync(
        ParticipantId participantId,
        string channelConversationId,
        ProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles up to <paramref name="maxRows"/> Pending proposals whose <c>ExpiresAt</c> is at or
    /// before <paramref name="now"/>. Reading also enforces expiry, so this is hygiene rather than
    /// the guarantee - it stops expired rows occupying the one-pending-per-conversation slot forever.
    /// </summary>
    Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="maxRows"/> settled proposals settled at or before
    /// <paramref name="cutoff"/>. Settled rows are retained briefly - the proposal cleanup
    /// coordinator owns that window - so a confirmation that arrives just after a rejection can
    /// still be answered truthfully rather than as "unknown proposal".
    /// </summary>
    Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Write the double**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// In-memory <see cref="IConfirmationProposalStore"/> holding exactly the invariants the SQL store
/// enforces relationally: one Pending proposal per Participant and ChannelConversation, replacement
/// that supersedes, and a settle that only the first caller wins.
/// </summary>
public sealed class InMemoryConfirmationProposalStore : IConfirmationProposalStore
{
    private sealed record Row(ConfirmationProposal Proposal, ProposalStatus Status, DateTimeOffset? SettledAt);

    private readonly Dictionary<ProposalId, Row> _rows = [];

    public Task<ConfirmationProposal?> FindPendingAsync(
        ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken) =>
        Task.FromResult(FindPendingRow(participantId, channelConversationId)?.Proposal);

    public Task<StoredProposalReplacement> StoreAsync(
        ConfirmationProposal proposal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = FindPendingRow(proposal.ParticipantId, proposal.ChannelConversationId);
        if (existing is not null)
        {
            _rows[existing.Proposal.Id] = existing with { Status = ProposalStatus.Superseded, SettledAt = now };
        }

        _rows[proposal.Id] = new Row(proposal, ProposalStatus.Pending, null);

        return Task.FromResult(new StoredProposalReplacement(existing is not null));
    }

    public Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(proposalId, out var row) || row.Status != ProposalStatus.Pending)
        {
            return Task.FromResult(false);
        }

        _rows[proposalId] = row with { Status = status, SettledAt = settledAt };
        return Task.FromResult(true);
    }

    public Task<ProposalStatus?> FindStatusAsync(ProposalId proposalId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(proposalId, out var row) ? row.Status : (ProposalStatus?)null);

    public async Task<int> InvalidatePendingAsync(
        ParticipantId participantId,
        string channelConversationId,
        ProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = FindPendingRow(participantId, channelConversationId);

        return pending is not null && await SettleAsync(pending.Proposal.Id, status, now, cancellationToken) ? 1 : 0;
    }

    public Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var expired = _rows.Values
            .Where(row => row.Status == ProposalStatus.Pending && row.Proposal.IsExpired(now))
            .Take(maxRows)
            .ToList();

        foreach (var row in expired)
        {
            _rows[row.Proposal.Id] = row with { Status = ProposalStatus.Expired, SettledAt = now };
        }

        return Task.FromResult(expired.Count);
    }

    public Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var deletable = _rows.Values
            .Where(row => row.SettledAt is { } settledAt && settledAt <= cutoff)
            .Take(maxRows)
            .ToList();

        foreach (var row in deletable)
        {
            _rows.Remove(row.Proposal.Id);
        }

        return Task.FromResult(deletable.Count);
    }

    private Row? FindPendingRow(ParticipantId participantId, string channelConversationId) => _rows.Values.SingleOrDefault(row =>
        row.Status == ProposalStatus.Pending
        && row.Proposal.ParticipantId == participantId
        && string.Equals(row.Proposal.ChannelConversationId, channelConversationId, StringComparison.Ordinal));
}
```

Note: `DeleteSettledBeforeAsync` in the double compares against the settle instant, and the SQL store must do the same - a proposal settled at `Now` is deleted once the cutoff reaches `Now + 24h`, which is what the `Now.AddHours(23)`/`Now.AddHours(25)` assertions pin. `ConfirmationProposalCleanupCoordinator` (Task 19) computes that cutoff; the store only applies it.

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~InMemoryConfirmationProposalStoreTests"`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IConfirmationProposalStore.cs tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs tests/MultiChannelAgent.Application.Tests/Inventories/InMemoryConfirmationProposalStoreTests.cs
git commit -m "feat(inventories): define the pending proposal store seam for #32"
```

---

## Task 9: Define the atomic change-set store seam

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/IStockChangeSetStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockChangeSetStore.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs`

Why: one writer applies one *or many* changes, or changes nothing. Stating that as a seam - together with the two replay lookups and the three outcomes - is what lets the services above it be written and tested before any SQL exists.

- [ ] **Step 1: Write the seam**

Create `src/MultiChannelAgent.Application/Inventories/IStockChangeSetStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How a change set was settled by the store.</summary>
public enum StockChangeSetStoreOutcome
{
    /// <summary>Every change was applied, and the state changes, audit facts, ledger, and proposal consumption committed together.</summary>
    Applied,

    /// <summary>This operation identity had already been applied; the recorded effects are returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>Current state no longer matches what was proposed, or the proposal was already consumed. Nothing at all was applied.</summary>
    Conflict,
}

/// <summary>
/// One Stock Entry as it stood before and after one applied change. Deliberately semantic: no row
/// versions, concurrency stamps, audit identities, or SQL detail ever appear here.
/// </summary>
public sealed record RecordedEntryState(
    StockEntryId StockEntryId,
    string Name,
    string UnitCanonicalName,
    string? LocationName,
    Quantity PreviousQuantity,
    Quantity ResultingQuantity,
    bool Retired);

/// <summary>
/// What one change actually did. <see cref="SurvivingStockEntryId"/> and
/// <see cref="RetiredStockEntryId"/> are the answer to "which identity survived and which one was
/// retired", which every merge-retiring Move and Rename owes the Participant.
/// </summary>
public sealed record RecordedStockChangeEffect(
    int Order,
    StockMutationKind Kind,
    StockChangeEffectKind Effect,
    RecordedEntryState Source,
    RecordedEntryState? Destination,
    Quantity TransferredQuantity)
{
    /// <summary>The exact new display name a Rename applied, or null for every other effect.</summary>
    public string? NewName { get; init; }

    /// <summary>
    /// The Stock Entry that still exists once this change was applied: the destination when a merge
    /// retired the source, the entry itself otherwise. Null for a Forget, which leaves nothing
    /// behind - reporting the forgotten entry as its own survivor would be the one lie this record
    /// could tell.
    /// </summary>
    public StockEntryId? SurvivingStockEntryId =>
        StockAuditFacts.RetiresSource(Effect) ? Destination?.StockEntryId : Source.StockEntryId;

    public StockEntryId? RetiredStockEntryId => Source.Retired ? Source.StockEntryId : null;
}

/// <summary>Everything a retry of one applied change set must be able to re-report without touching Inventory state again.</summary>
public sealed record RecordedStockChangeSet(
    StockOperationId OperationId, ProposalId? ProposalId, IReadOnlyList<RecordedStockChangeEffect> Effects);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="StockChangeSetStoreOutcome.Conflict"/>.</summary>
public sealed record StockChangeSetStoreResult(StockChangeSetStoreOutcome Outcome, RecordedStockChangeSet? Recorded);

/// <summary>
/// One fully decided set of changes, ready to apply. Everything ambiguous has already been resolved:
/// each <see cref="ProposedChange"/> names its exact targets, amounts, and effect, and the expected
/// versions and absences say exactly what current state must still look like.
/// </summary>
public sealed record StockChangeSetCommand
{
    /// <summary>The retry-stable identity this execution is recorded under; the ledger is keyed by it.</summary>
    public required StockOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Editor-or-better Membership authorized this; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    /// <summary>
    /// The Turn that caused this execution. Recorded and uniquely indexed per Inventory, so a Turn
    /// re-driven after a crash finds what its own first attempt did without needing the proposal -
    /// which, by then, has been consumed.
    /// </summary>
    public required TurnId ConfirmedByTurnId { get; init; }

    /// <summary>The proposal to consume in the very same transaction, or null for an immediate change that needed none.</summary>
    public ProposalId? ConsumesProposalId { get; init; }

    public required IReadOnlyList<ProposedChange> Changes { get; init; }

    public required IReadOnlyList<ExpectedEntryVersion> ExpectedVersions { get; init; }

    public required IReadOnlyList<ExpectedEquivalentStockAbsence> ExpectedAbsences { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The single atomic writer for one or many stock changes. One call must, in one transaction: refuse
/// if this operation identity was already applied (returning what it did), consume the proposal it
/// names (refusing if something already did), refuse if any touched row no longer carries its
/// expected version, and otherwise apply every change, append one minimal semantic audit fact per
/// change, and record the ledger - together.
///
/// Partial application is never acceptable. A caller that sees
/// <see cref="StockChangeSetStoreOutcome.Conflict"/> must be able to rely on nothing at all having
/// happened, which is exactly what "a failed atomic batch changes nothing" means.
/// </summary>
public interface IStockChangeSetStore
{
    /// <summary>
    /// What this operation identity already did in this Inventory, or null when it has never been
    /// applied there. Scoped to the Inventory from trusted context, so a recorded operation can never
    /// be re-reported into - or disclosed through - a different Inventory.
    /// </summary>
    Task<RecordedStockChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken);

    /// <summary>
    /// What this Turn already did in this Inventory, or null when it did nothing.
    ///
    /// This is the replay lookup, and it is deliberately keyed by the Turn rather than by the
    /// operation identity: a confirmation consumes its proposal, so a Turn re-driven after a crash
    /// between the mutation transaction and the Outcome transaction can no longer find the proposal
    /// its identity would have been derived from. Asking "what did this Turn already do here" needs
    /// nothing but trusted context, and answering from it is what stops a completed mutation ever
    /// being re-planned.
    /// </summary>
    Task<RecordedStockChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken);

    Task<StockChangeSetStoreResult> ApplyAsync(StockChangeSetCommand command, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Widen the read double so it can execute every effect**

In `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockStore.cs`, add these mutating helpers alongside the existing `Find`, `SetQuantity`, and `CreateRow` (each must regenerate the row's version stamp added in Task 7):

```csharp
    /// <summary>Relocates a Stock Entry, preserving its identity - what a Move to an empty placement does.</summary>
    public StockEntrySummary Relocate(InventoryId inventoryId, StockEntryId id, LocationId? locationId, string? locationName);

    /// <summary>Renames a Stock Entry, preserving its identity.</summary>
    public StockEntrySummary Rename(InventoryId inventoryId, StockEntryId id, string name, string normalizedName);

    /// <summary>Removes a Stock Entry outright - what a Forget, and the retired side of a merge, does.</summary>
    public void Delete(InventoryId inventoryId, StockEntryId id);
```

Implement each against whatever backing collection the double already uses; each returns the row as it now stands (`Delete` returns nothing), and each throws `InvalidOperationException` when the row is not in that Inventory, so a double can never quietly diverge from what SQL would refuse.

- [ ] **Step 3: Write the change-set double**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockChangeSetStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// In-memory <see cref="IStockChangeSetStore"/> applying the same rules the SQL store must: an
/// operation identity already in the ledger re-reports and applies nothing; a proposal that is not
/// consumable is a conflict; a row whose version moved is a conflict; and a conflict applies nothing
/// at all. It writes through the same <see cref="InMemoryStockStore"/> the reads come from, so a test
/// sees one consistent Inventory.
/// </summary>
public sealed class InMemoryStockChangeSetStore(InMemoryStockStore stockStore, InMemoryConfirmationProposalStore proposalStore)
    : IStockChangeSetStore
{
    private readonly Dictionary<StockOperationId, (InventoryId InventoryId, TurnId TurnId, RecordedStockChangeSet Recorded)> _ledger = [];

    /// <summary>Every audit fact appended so far, in order, so a test can assert exactly one per applied change.</summary>
    public List<AuditFact> AuditFacts { get; } = [];

    /// <summary>Simulates a competing writer having moved a touched row since the caller planned.</summary>
    public bool ForceConflict { get; set; }

    public Task<RecordedStockChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(
            _ledger.TryGetValue(operationId, out var entry) && entry.InventoryId == inventoryId ? entry.Recorded : null);

    public Task<RecordedStockChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken) =>
        Task.FromResult(_ledger.Values
            .Where(entry => entry.InventoryId == inventoryId && entry.TurnId == turnId)
            .Select(entry => entry.Recorded)
            .SingleOrDefault());

    public async Task<StockChangeSetStoreResult> ApplyAsync(StockChangeSetCommand command, CancellationToken cancellationToken)
    {
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.AlreadyApplied, already);
        }

        if (ForceConflict)
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
        }

        // Consumed first, and guarded, exactly as the SQL store consumes it inside its transaction:
        // two confirmations of one proposal can never both execute.
        if (command.ConsumesProposalId is { } proposalId
            && !await proposalStore.SettleAsync(proposalId, ProposalStatus.Confirmed, command.Now, cancellationToken))
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
        }

        foreach (var version in command.ExpectedVersions)
        {
            var current = stockStore.Find(command.InventoryId, version.StockEntryId);
            if (current is null || stockStore.VersionOf(command.InventoryId, version.StockEntryId) != version.ConcurrencyStamp)
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
            }
        }

        foreach (var absence in command.ExpectedAbsences)
        {
            if (stockStore.FindEquivalent(command.InventoryId, absence.NormalizedName, absence.UnitId, absence.LocationId) is not null)
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
            }
        }

        var effects = command.Changes
            .OrderBy(change => change.Order)
            .Select(change => Apply(command.InventoryId, change))
            .ToList();

        foreach (var change in command.Changes)
        {
            AuditFacts.Add(AuditFact.Create(
                StockAuditFacts.EventTypeFor(change.Kind),
                AuditActorKind.Participant,
                command.ActorId.ToString(),
                command.InventoryId,
                subjectParticipantId: null,
                StockAuditFacts.OutcomeCodeFor(change.Effect),
                command.Now));
        }

        var recorded = new RecordedStockChangeSet(command.OperationId, command.ConsumesProposalId, effects);
        _ledger[command.OperationId] = (command.InventoryId, command.ConfirmedByTurnId, recorded);

        return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Applied, recorded);
    }

    private RecordedStockChangeEffect Apply(InventoryId inventoryId, ProposedChange change)
    {
        switch (change.Effect)
        {
            case StockChangeEffectKind.Created:
            {
                var created = stockStore.CreateRow(
                    inventoryId,
                    change.Source.Name,
                    change.Source.UnitId,
                    change.Source.UnitCanonicalName,
                    change.Source.LocationId,
                    change.Source.LocationName,
                    change.Source.Note,
                    change.Source.ResultingQuantity);

                return Effect(change, Recorded(created, Quantity.Zero, retired: false), null);
            }

            case StockChangeEffectKind.QuantityIncreased:
            case StockChangeEffectKind.QuantityDecreased:
            case StockChangeEffectKind.QuantitySet:
            case StockChangeEffectKind.QuantityCleared:
            {
                var updated = stockStore.SetQuantity(inventoryId, change.Source.StockEntryId!.Value, change.Source.ResultingQuantity)!;
                return Effect(change, Recorded(updated, change.Source.PreviousQuantity, retired: false), null);
            }

            case StockChangeEffectKind.Placed:
            {
                var moved = stockStore.Relocate(
                    inventoryId, change.Source.StockEntryId!.Value, change.Destination!.LocationId, change.Destination.LocationName);

                return Effect(change, Recorded(moved, change.Source.PreviousQuantity, retired: false), null);
            }

            case StockChangeEffectKind.Split:
            {
                var remainder = stockStore.SetQuantity(inventoryId, change.Source.StockEntryId!.Value, change.Source.ResultingQuantity)!;
                var destination = stockStore.CreateRow(
                    inventoryId,
                    change.Destination!.Name,
                    change.Destination.UnitId,
                    change.Destination.UnitCanonicalName,
                    change.Destination.LocationId,
                    change.Destination.LocationName,
                    change.Destination.Note,
                    change.Destination.ResultingQuantity);

                return Effect(
                    change,
                    Recorded(remainder, change.Source.PreviousQuantity, retired: false),
                    Recorded(destination, Quantity.Zero, retired: false));
            }

            case StockChangeEffectKind.SplitMerged:
            {
                var remainder = stockStore.SetQuantity(inventoryId, change.Source.StockEntryId!.Value, change.Source.ResultingQuantity)!;
                var destination = stockStore.SetQuantity(
                    inventoryId, change.Destination!.StockEntryId!.Value, change.Destination.ResultingQuantity)!;

                return Effect(
                    change,
                    Recorded(remainder, change.Source.PreviousQuantity, retired: false),
                    Recorded(destination, change.Destination.PreviousQuantity, retired: false));
            }

            case StockChangeEffectKind.Merged:
            case StockChangeEffectKind.RenameMerged:
            {
                var destination = stockStore.SetQuantity(
                    inventoryId, change.Destination!.StockEntryId!.Value, change.Destination.ResultingQuantity)!;
                var retired = RetiredSource(change);
                stockStore.Delete(inventoryId, change.Source.StockEntryId!.Value);

                return Effect(change, retired, Recorded(destination, change.Destination.PreviousQuantity, retired: false));
            }

            case StockChangeEffectKind.Renamed:
            {
                var renamed = stockStore.Rename(
                    inventoryId, change.Source.StockEntryId!.Value, change.NewName!, change.NewNormalizedName!);

                return Effect(change, Recorded(renamed, change.Source.PreviousQuantity, retired: false), null);
            }

            case StockChangeEffectKind.Forgotten:
            {
                var retired = RetiredSource(change);
                stockStore.Delete(inventoryId, change.Source.StockEntryId!.Value);

                return Effect(change, retired, null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Effect, "Unhandled stock change effect.");
        }
    }

    private static RecordedStockChangeEffect Effect(ProposedChange change, RecordedEntryState source, RecordedEntryState? destination) =>
        new(change.Order, change.Kind, change.Effect, source, destination, change.TransferredQuantity) { NewName = change.NewName };

    private static RecordedEntryState Recorded(StockEntrySummary row, Quantity previousQuantity, bool retired) =>
        new(row.Id, row.Name, row.UnitCanonicalName, row.LocationName, previousQuantity, row.Quantity, retired);

    private static RecordedEntryState RetiredSource(ProposedChange change) => new(
        change.Source.StockEntryId!.Value,
        change.Source.Name,
        change.Source.UnitCanonicalName,
        change.Source.LocationName,
        change.Source.PreviousQuantity,
        Quantity.Zero,
        Retired: true);
}
```

`InMemoryStockStore` needs two more read helpers for this: `VersionOf(InventoryId, StockEntryId)` returning the stamp from Task 7's dictionary, and `FindEquivalent(InventoryId, string normalizedName, UnitId, LocationId?)` returning the matching row or null. Add both.

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - nothing consumes the new seam yet, so this task's value is that everything still compiles and the doubles are ready. There is no red-to-green cycle here because the seam has no behavior of its own; the behavior is exercised by Tasks 12, 13, and 16.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IStockChangeSetStore.cs tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/
git commit -m "feat(inventories): define the atomic stock change-set store seam for #32"
```

---

## Task 10: Parse an untrusted stock change batch deterministically

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/StockChangeSetParser.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetParserTests.cs`

Why: a batch is the one tool argument with internal structure, so it is the one place a hostile or buggy proposal could try to smuggle something in. The parser therefore accepts a closed set of properties, a closed set of kinds, string values only, and at most `ConfirmationProposal.MaxChanges` elements - and refuses everything else outright rather than ignoring what it does not understand.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetParserTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class StockChangeSetParserTests
{
    [Fact]
    public void A_well_formed_batch_parses_into_ordered_requests()
    {
        const string Json = """
        [
          {"kind":"add","reference":"Steel Bolts","quantity":"5","note":"Blue box"},
          {"kind":"move","reference":"Brass Rivets","all":"true","to":"Shelf A"},
          {"kind":"rename","reference":"Old Name","newName":"New Name"},
          {"kind":"forget","reference":"Empty Thing","unlocated":"true"}
        ]
        """;

        Assert.True(StockChangeSetParser.TryParse(Json, out var requests, out var code));

        Assert.Equal(string.Empty, code);
        Assert.Equal(4, requests.Count);
        Assert.Equal([1, 2, 3, 4], requests.Select(r => r.Order));
        Assert.Equal(StockMutationKind.Add, requests[0].Kind);
        Assert.Equal("Steel Bolts", requests[0].Reference);
        Assert.Equal("5", requests[0].QuantityText);
        Assert.Equal("Blue box", requests[0].Note);
        Assert.Equal(StockMutationKind.Move, requests[1].Kind);
        Assert.True(requests[1].MoveAll);
        Assert.Equal("Shelf A", requests[1].DestinationLocationReference);
        Assert.Equal("New Name", requests[2].NewName);
        Assert.True(requests[3].UnlocatedOnly);
    }

    [Fact]
    public void A_move_to_the_unlocated_state_is_stated_as_its_own_flag_rather_than_a_Location_named_unlocated()
    {
        Assert.True(StockChangeSetParser.TryParse("""[{"kind":"move","reference":"X","all":"true","toUnlocated":"true"}]""", out var requests, out _));

        Assert.True(requests[0].DestinationUnlocated);
        Assert.Null(requests[0].DestinationLocationReference);
    }

    [Theory]
    [InlineData(null, "invalid_changes")]
    [InlineData("", "invalid_changes")]
    [InlineData("   ", "invalid_changes")]
    [InlineData("not json", "invalid_changes")]
    [InlineData("{}", "invalid_changes")]
    [InlineData("[]", "invalid_changes")]
    [InlineData("""["add"]""", "invalid_changes")]
    [InlineData("""[{"reference":"X"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"Add","reference":"X"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"destroy","reference":"X"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","participantId":"me"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","inventoryId":"other"}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","quantity":5}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","quantity":null}]""", "invalid_changes")]
    [InlineData("""[{"kind":"add","reference":"X","nested":{"a":"b"}}]""", "invalid_changes")]
    public void Anything_that_is_not_exactly_the_agreed_shape_is_refused_rather_than_partly_understood(string? json, string expectedCode)
    {
        Assert.False(StockChangeSetParser.TryParse(json, out var requests, out var code));

        Assert.Empty(requests);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void A_batch_larger_than_a_Participant_can_review_is_refused_on_its_own_terms()
    {
        var elements = string.Join(",", Enumerable.Range(0, ConfirmationProposal.MaxChanges + 1)
            .Select(i => $$"""{"kind":"add","reference":"Thing {{i}}","quantity":"1"}"""));

        Assert.False(StockChangeSetParser.TryParse($"[{elements}]", out var requests, out var code));

        Assert.Empty(requests);
        Assert.Equal("too_many_changes", code);
    }

    [Fact]
    public void A_batch_of_exactly_the_maximum_size_still_parses()
    {
        var elements = string.Join(",", Enumerable.Range(0, ConfirmationProposal.MaxChanges)
            .Select(i => $$"""{"kind":"add","reference":"Thing {{i}}","quantity":"1"}"""));

        Assert.True(StockChangeSetParser.TryParse($"[{elements}]", out var requests, out _));

        Assert.Equal(ConfirmationProposal.MaxChanges, requests.Count);
    }

    [Fact]
    public void Flags_are_only_ever_an_explicit_true_so_stray_text_can_never_widen_a_change()
    {
        Assert.True(StockChangeSetParser.TryParse("""[{"kind":"forget","reference":"X","unlocated":"yes"}]""", out var requests, out _));

        Assert.False(requests[0].UnlocatedOnly);
    }
}
```

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockChangeSetParserTests"`
Expected: FAIL to compile - `StockChangeSetParser` and `StockChangeRequest` do not exist.

- [ ] **Step 2: Write the parser**

Create `src/MultiChannelAgent.Application/Inventories/StockChangeSetParser.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// One requested change, as proposed. Every field is untrusted text: nothing here is identity, and
/// nothing here is ever pattern-matched or guessed. <see cref="Order"/> is assigned by the parser
/// from the request's own position, never taken from the request, so a proposal cannot reorder or
/// collide the execution order.
/// </summary>
public sealed record StockChangeRequest
{
    public required int Order { get; init; }

    public required StockMutationKind Kind { get; init; }

    /// <summary>The Stock Entry to act on: an opaque identity, or an exact name.</summary>
    public string? Reference { get; init; }

    /// <summary>Invariant decimal text. Required by Add, Remove, and Set; optional for a partial Move.</summary>
    public string? QuantityText { get; init; }

    /// <summary>A Move of everything on hand, stated instead of an amount.</summary>
    public bool MoveAll { get; init; }

    /// <summary>Narrows the target by Unit: an opaque identity, an exact canonical name, or an exact active alias.</summary>
    public string? UnitReference { get; init; }

    /// <summary>Narrows the target by Location: an opaque identity or an exact name.</summary>
    public string? LocationReference { get; init; }

    /// <summary>Narrows the target to Stock kept nowhere in particular.</summary>
    public bool UnlocatedOnly { get; init; }

    /// <summary>Where a Move sends stock: an opaque Location identity or an exact Location name.</summary>
    public string? DestinationLocationReference { get; init; }

    /// <summary>A Move to the unlocated state. Its own flag, because "unlocated" is the absence of a Location and never a Location's name.</summary>
    public bool DestinationUnlocated { get; init; }

    /// <summary>The exact new display name a Rename asks for.</summary>
    public string? NewName { get; init; }

    /// <summary>A Note, only ever applied when a change creates a Stock Entry.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Reads the one structured tool argument this application accepts: the untrusted <c>changes</c>
/// array a batch tool call carries.
///
/// It is deliberately unforgiving. A property it does not know, a value that is not a string, a kind
/// spelled differently, or one element too many is a refusal - never a partly understood batch, and
/// never a silently narrowed one. That matters more here than anywhere else in the tool surface: a
/// batch is the only argument with internal structure, so it is the only one where "ignore what you
/// do not understand" could quietly change what commits.
/// </summary>
public static class StockChangeSetParser
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "kind", "reference", "quantity", "all", "unit", "location", "unlocated", "to", "toUnlocated", "newName", "note",
    };

    /// <summary>
    /// Parses <paramref name="json"/> into ordered requests. On failure <paramref name="code"/> is the
    /// machine code to answer with - <c>invalid_changes</c> or <c>too_many_changes</c> - and
    /// <paramref name="requests"/> is empty.
    /// </summary>
    public static bool TryParse(string? json, out IReadOnlyList<StockChangeRequest> requests, out string code)
    {
        requests = [];
        code = "invalid_changes";

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var elements = document.RootElement.EnumerateArray().ToList();
            if (elements.Count == 0)
            {
                return false;
            }

            if (elements.Count > ConfirmationProposal.MaxChanges)
            {
                code = "too_many_changes";
                return false;
            }

            var parsed = new List<StockChangeRequest>(elements.Count);
            for (var index = 0; index < elements.Count; index++)
            {
                if (!TryParseElement(elements[index], index + 1, out var request))
                {
                    return false;
                }

                parsed.Add(request!);
            }

            requests = parsed;
            code = string.Empty;
            return true;
        }
    }

    private static bool TryParseElement(JsonElement element, int order, out StockChangeRequest? request)
    {
        request = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // A property this application does not know is not noise to skip past: it is a proposal
            // asking for something that was never agreed, and the safe reading of that is "no".
            if (!KnownProperties.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        if (!values.TryGetValue("kind", out var kindText) || !StockMutationKinds.TryParse(kindText, out var kind))
        {
            return false;
        }

        request = new StockChangeRequest
        {
            Order = order,
            Kind = kind,
            Reference = Optional(values, "reference"),
            QuantityText = Optional(values, "quantity"),
            MoveAll = Flag(values, "all"),
            UnitReference = Optional(values, "unit"),
            LocationReference = Optional(values, "location"),
            UnlocatedOnly = Flag(values, "unlocated"),
            DestinationLocationReference = Optional(values, "to"),
            DestinationUnlocated = Flag(values, "toUnlocated"),
            NewName = Optional(values, "newName"),
            Note = Optional(values, "note"),
        };

        return true;
    }

    private static string? Optional(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>A flag is an explicit "true" and nothing else, so stray text can only ever leave a change narrower.</summary>
    private static bool Flag(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed) && parsed;
}
```

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockChangeSetParserTests"`
Expected: PASS, 20 tests (the theory contributes 15).

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockChangeSetParser.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetParserTests.cs
git commit -m "feat(inventories): parse an untrusted stock change batch deterministically for #32"
```

---

## Task 11: Resolve every change against current state

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/StockChangeResolver.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeResolverTests.cs`

Why: this is where an untrusted request becomes an exactly-decided `ProposedChange` - the thing a proposal stores and an executor applies without re-deciding anything. It resolves targets through the very same deterministic matching Find uses, so a mutation can never act on a reference Find would have called ambiguous.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeResolverTests.cs`. Build the harness from the existing doubles (`InMemoryStockStore`, `InMemoryInventoryReferenceStore`), seeding an Inventory with the reserved `each` Unit and two Locations, then:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class StockChangeResolverTests
{
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly InMemoryStockStore _stock = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly UnitId _each = new(Guid.NewGuid());
    private readonly LocationId _shelfA = new(Guid.NewGuid());
    private readonly LocationId _shelfB = new(Guid.NewGuid());

    public StockChangeResolverTests()
    {
        _references.AddUnit(_inventory, _each, "each", ["piece", "pieces", "pc", "pcs"]);
        _references.AddLocation(_inventory, _shelfA, "Shelf A");
        _references.AddLocation(_inventory, _shelfB, "Shelf B");
    }

    private StockChangeResolver Resolver() => new(_stock, _references);

    private StockEntrySummary Seed(string name, string quantity, LocationId? locationId = null, string? note = null) =>
        _stock.CreateRow(
            _inventory,
            name,
            _each,
            "each",
            locationId,
            locationId == _shelfA ? "Shelf A" : locationId == _shelfB ? "Shelf B" : null,
            note,
            Quantity.Create(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));

    private async Task<StockChangeResolution> ResolveAsync(StockChangeRequest request) =>
        await Resolver().ResolveAsync(_inventory, request, CancellationToken.None);

    [Fact]
    public async Task Moving_part_of_a_Stock_Entry_to_an_empty_Location_splits_it()
    {
        var source = Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            QuantityText = "3",
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal(StockChangeResolutionKind.Resolved, resolution.Kind);
        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Split, change.Effect);
        Assert.Equal(source.Id, change.Source.StockEntryId);
        Assert.Equal("7", change.Source.ResultingQuantity.ToInvariantText());
        Assert.Null(change.Destination!.StockEntryId);
        Assert.Equal(_shelfA, change.Destination.LocationId);
        Assert.Equal("3", change.Destination.ResultingQuantity.ToInvariantText());
        Assert.Single(resolution.ExpectedVersions!);
        Assert.NotNull(resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Moving_all_of_a_Stock_Entry_into_Equivalent_Stock_merges_and_names_both_identities()
    {
        var source = Seed("Steel Bolts", "10");
        var destination = Seed("Steel Bolts", "4", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            UnlocatedOnly = true,
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Merged, change.Effect);
        Assert.Equal(destination.Id, change.SurvivingStockEntryId);
        Assert.Equal(source.Id, change.RetiredStockEntryId);
        Assert.Equal("14", change.Destination!.ResultingQuantity.ToInvariantText());
        Assert.Equal(2, resolution.ExpectedVersions!.Count);
        Assert.Null(resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Moving_all_of_a_Stock_Entry_to_an_empty_Location_relocates_it_and_keeps_its_identity()
    {
        var source = Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Placed, change.Effect);
        Assert.Equal(source.Id, change.SurvivingStockEntryId);
        Assert.Null(change.RetiredStockEntryId);
        Assert.Equal(_shelfA, change.Destination!.LocationId);
    }

    [Fact]
    public async Task Moving_Stock_to_the_unlocated_state_is_a_destination_of_its_own()
    {
        Seed("Steel Bolts", "10", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationUnlocated = true,
        });

        Assert.Equal(StockChangeEffectKind.Placed, resolution.Change!.Effect);
        Assert.Null(resolution.Change.Destination!.LocationId);
    }

    [Fact]
    public async Task A_Move_that_names_no_destination_at_all_is_invalid()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Move, Reference = "Steel Bolts", MoveAll = true,
        });

        Assert.Equal(StockChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_destination", resolution.Code);
    }

    [Fact]
    public async Task A_Move_that_names_both_a_Location_and_the_unlocated_state_is_invalid()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
            DestinationUnlocated = true,
        });

        Assert.Equal("invalid_destination", resolution.Code);
    }

    [Fact]
    public async Task A_Move_that_states_both_an_amount_and_all_is_invalid()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            QuantityText = "3",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal(StockChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_quantity", resolution.Code);
    }

    [Fact]
    public async Task A_Move_to_where_the_Stock_already_is_conflicts_rather_than_pretending_to_work()
    {
        Seed("Steel Bolts", "10", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal(StockChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("no_change", resolution.Code);
    }

    [Fact]
    public async Task A_Move_to_a_Location_this_Inventory_does_not_have_is_reported_never_created()
    {
        Seed("Steel Bolts", "10");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            MoveAll = true,
            DestinationLocationReference = "Loading Bay",
        });

        Assert.Equal(StockChangeResolutionKind.ReferenceNotFound, resolution.Kind);
        Assert.Equal(StockReferenceKind.Location, resolution.UnresolvedReference);
    }

    [Fact]
    public async Task A_split_carries_the_sources_Note_to_the_Stock_Entry_it_creates()
    {
        Seed("Steel Bolts", "10", note: "Blue box");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1,
            Kind = StockMutationKind.Move,
            Reference = "Steel Bolts",
            QuantityText = "3",
            DestinationLocationReference = "Shelf A",
        });

        Assert.Equal("Blue box", resolution.Change!.Destination!.Note);
    }

    [Fact]
    public async Task Renaming_without_a_collision_preserves_identity_and_carries_the_exact_new_name()
    {
        var source = Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = " Brass  Rivets ",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Renamed, change.Effect);
        Assert.Equal(source.Id, change.SurvivingStockEntryId);
        Assert.Equal("Brass  Rivets", change.NewName);
        Assert.Equal("brass rivets", change.NewNormalizedName);
    }

    [Fact]
    public async Task Renaming_into_Equivalent_Stock_merges_and_names_the_survivor_and_the_retired_source()
    {
        var source = Seed("Steel Bolts", "4");
        var colliding = Seed("Brass Rivets", "6");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.RenameMerged, change.Effect);
        Assert.Equal(colliding.Id, change.SurvivingStockEntryId);
        Assert.Equal(source.Id, change.RetiredStockEntryId);
        Assert.Equal("10", change.Destination!.ResultingQuantity.ToInvariantText());
    }

    [Fact]
    public async Task A_Rename_only_collides_with_Equivalent_Stock_at_the_same_Unit_and_Location()
    {
        Seed("Steel Bolts", "4");
        Seed("Brass Rivets", "6", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = "Brass Rivets",
        });

        Assert.Equal(StockChangeEffectKind.Renamed, resolution.Change!.Effect);
    }

    [Theory]
    [InlineData(null, "invalid_name")]
    [InlineData("", "invalid_name")]
    [InlineData("   ", "invalid_name")]
    public async Task A_Rename_must_state_a_name(string? newName, string expectedCode)
    {
        Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Rename, Reference = "Steel Bolts", NewName = newName,
        });

        Assert.Equal(StockChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal(expectedCode, resolution.Code);
    }

    [Fact]
    public async Task Forgetting_an_empty_Stock_Entry_resolves_and_retires_it()
    {
        var source = Seed("Steel Bolts", "0");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Forgotten, change.Effect);
        Assert.Equal(source.Id, change.RetiredStockEntryId);
        Assert.Null(change.SurvivingStockEntryId);
    }

    [Fact]
    public async Task Forgetting_Stock_that_is_still_on_hand_conflicts_so_it_cannot_bypass_Remove()
    {
        Seed("Steel Bolts", "1");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts",
        });

        Assert.Equal(StockChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("forget_requires_zero_quantity", resolution.Code);
    }

    [Fact]
    public async Task An_ambiguous_reference_offers_candidates_rather_than_choosing_one()
    {
        Seed("Steel Bolts", "1");
        Seed("Steel Bolts", "2", _shelfA);

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Forget, Reference = "Steel Bolts",
        });

        Assert.Equal(StockChangeResolutionKind.Ambiguous, resolution.Kind);
        Assert.Equal("ambiguous", resolution.Code);
        Assert.Equal(2, resolution.Candidates!.Candidates.Count);
    }

    [Fact]
    public async Task A_reference_that_matches_nothing_is_simply_not_found()
    {
        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Move, Reference = "Brass Rivets", MoveAll = true, DestinationUnlocated = true,
        });

        Assert.Equal(StockChangeResolutionKind.NotFound, resolution.Kind);
        Assert.Equal("not_found", resolution.Code);
    }

    [Fact]
    public async Task Adding_to_nothing_resolves_to_creating_Equivalent_Stock_at_the_reserved_each_Unit()
    {
        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Add, Reference = "Brass Rivets", QuantityText = "4", Note = "Blue box",
        });

        var change = resolution.Change!;
        Assert.Equal(StockChangeEffectKind.Created, change.Effect);
        Assert.Null(change.Source.StockEntryId);
        Assert.Equal(_each, change.Source.UnitId);
        Assert.Equal("Blue box", change.Source.Note);
        Assert.Empty(resolution.ExpectedVersions!);
        Assert.Equal(new ExpectedEquivalentStockAbsence("brass rivets", _each, null), resolution.ExpectedAbsence);
    }

    [Fact]
    public async Task Adding_to_existing_Stock_never_rewrites_its_Note()
    {
        Seed("Steel Bolts", "4", note: "Blue box");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Add, Reference = "Steel Bolts", QuantityText = "1", Note = "Red box",
        });

        Assert.Equal(StockChangeEffectKind.QuantityIncreased, resolution.Change!.Effect);
        Assert.Equal("Blue box", resolution.Change.Source.Note);
    }

    [Fact]
    public async Task Setting_Stock_to_zero_resolves_to_clearing_it_and_asks_to_be_confirmed()
    {
        Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Set, Reference = "Steel Bolts", QuantityText = "0",
        });

        Assert.Equal(StockChangeEffectKind.QuantityCleared, resolution.Change!.Effect);
        Assert.True(StockAuditFacts.RequiresConfirmation(resolution.Change.Effect));
    }

    [Fact]
    public async Task Removing_more_than_is_on_hand_conflicts_and_resolves_no_change()
    {
        Seed("Steel Bolts", "4");

        var resolution = await ResolveAsync(new StockChangeRequest
        {
            Order = 1, Kind = StockMutationKind.Remove, Reference = "Steel Bolts", QuantityText = "5",
        });

        Assert.Equal(StockChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("insufficient_quantity", resolution.Code);
        Assert.Null(resolution.Change);
    }
}
```

If `InMemoryInventoryReferenceStore` has no `AddUnit`/`AddLocation` in that exact shape, use the seeding methods it already exposes rather than changing it.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockChangeResolverTests"`
Expected: FAIL to compile - `StockChangeResolver` and `StockChangeResolution` do not exist.

- [ ] **Step 2: Write the resolver**

Create `src/MultiChannelAgent.Application/Inventories/StockChangeResolver.cs`:

```csharp
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
            StockMutationKind.Forget => await ResolveForgetAsync(inventoryId, target, cancellationToken),
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

        if (plan.Effect == StockChangeEffectKind.Split)
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

        var touched = colliding is null ? new[] { target.Id } : [target.Id, colliding.Id];

        return await VersionedAsync(inventoryId, change, touched, cancellationToken);
    }

    private async Task<StockChangeResolution> ResolveForgetAsync(
        InventoryId inventoryId, StockEntrySummary? target, CancellationToken cancellationToken)
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
            Order = 1,
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
```

`StockFindingService.MaxCandidates` and `StockListingService.ToRowView` are already `public`/`internal` to this assembly and are reused deliberately, so mutation ambiguity and Find ambiguity can never diverge.

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockChangeResolverTests"`
Expected: PASS, 24 tests (the theory contributes 3).

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockChangeResolver.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeResolverTests.cs
git commit -m "feat(inventories): resolve every stock change against current state for #32"
```

---

## Task 12: Apply a lone low-risk change, or propose everything else

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/StockChangeSetService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetServiceTests.cs`

Why: this is where "which changes need confirming" becomes behavior. It authorizes, answers a replay from the ledger *before* re-planning anything, resolves every change, and then either applies one low-risk change immediately or stores an exact proposal and hands back its one-time token.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetServiceTests.cs`. Build the harness from `InMemoryStockStore`, `InMemoryInventoryReferenceStore`, `InMemoryInventoryStore` (for Membership), `InMemoryInventoryAuthorizationAuditStore`, `InMemoryConfirmationProposalStore`, and `InMemoryStockChangeSetStore`, exactly as `StockMutationServiceTests` already builds its own. The tests to write:

```csharp
    [Fact]
    public async Task A_lone_low_risk_change_applies_immediately_and_reports_the_exact_read_back()

    [Fact]
    public async Task A_lone_low_risk_change_appends_exactly_one_audit_fact()

    [Fact]
    public async Task A_lone_merge_retiring_Move_is_proposed_rather_than_applied()

    [Fact]
    public async Task A_lone_merge_retiring_Rename_is_proposed_rather_than_applied()

    [Fact]
    public async Task A_Forget_is_proposed_rather_than_applied()

    [Fact]
    public async Task A_Set_to_zero_is_proposed_rather_than_applied()

    [Fact]
    public async Task Every_batch_of_more_than_one_change_is_proposed_even_when_each_change_is_low_risk()

    [Fact]
    public async Task A_proposal_carries_a_single_use_token_the_Participant_can_read_exactly_once()

    [Fact]
    public async Task A_proposal_carries_the_exact_effects_including_the_survivor_and_the_retired_source()

    [Fact]
    public async Task A_proposal_pins_the_version_of_every_existing_Stock_Entry_it_would_touch()

    [Fact]
    public async Task A_stored_proposal_expires_ten_minutes_after_it_was_made()

    [Fact]
    public async Task A_new_proposal_supersedes_the_pending_one_in_the_same_conversation()

    [Fact]
    public async Task A_batch_whose_first_refusal_is_ambiguous_refuses_the_whole_batch_and_applies_nothing()

    [Fact]
    public async Task A_batch_that_names_the_same_Stock_Entry_twice_is_refused_rather_than_planned_against_stale_state()

    [Fact]
    public async Task An_empty_change_set_is_invalid()

    [Fact]
    public async Task A_Viewer_may_see_this_Inventory_but_may_not_change_or_propose_anything()

    [Fact]
    public async Task A_non_member_cannot_tell_this_Inventory_apart_from_one_that_does_not_exist()

    [Fact]
    public async Task A_Turn_that_already_applied_a_change_set_is_answered_from_the_ledger_and_never_re_planned()
```

Write each body to assert exactly what its name says, using these anchors so the assertions are unambiguous:

- The immediate path asserts `StockChangeSetResultKind.Completed`, `result.Applied!.Changes` with one entry whose `Effect` is `"placed"`, the surviving Stock Entry id, and that `proposalStore.FindPendingAsync` returns null.
- Every proposal path asserts `StockChangeSetResultKind.ConfirmationRequired`, code `"confirmation_required"`, `result.Proposal!.Token.Length == ConfirmationToken.TextLength`, that `changeSetStore.AuditFacts` is empty, and that current Stock is unchanged.
- `A_proposal_carries_the_exact_effects...` asserts the single `StockChangeView` has `SurvivingStockEntryId` equal to the destination and `RetiredStockEntryId` equal to the source.
- `A_proposal_pins_the_version...` reads the stored proposal back through `proposalStore.FindPendingAsync` and asserts `ExpectedVersions` has one entry per existing touched Stock Entry, each stamp equal to `stockStore.VersionOf(...)`.
- `A_stored_proposal_expires...` asserts `stored.ExpiresAt == now.AddMinutes(10)` and that `result.Proposal!.ExpiresAt` is that instant in round-trip ("O") text.
- `A_new_proposal_supersedes...` makes two proposals and asserts the first is `ProposalStatus.Superseded` and the pending one is the second.
- `A_batch_that_names_the_same_Stock_Entry_twice...` asserts `StockChangeSetResultKind.Invalid` with code `"conflicting_changes"`.
- The replay test applies a change set, then calls the service again with the *same* `TurnId` but a request that would now refuse (for example moving stock that is no longer where it was), and asserts `Completed` with the originally recorded effects - proving a completed mutation is never re-planned.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockChangeSetServiceTests"`
Expected: FAIL to compile - `StockChangeSetService` does not exist.

- [ ] **Step 2: Write the service**

Create `src/MultiChannelAgent.Application/Inventories/StockChangeSetService.cs`:

```csharp
using System.Globalization;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

// System.Globalization is used for exactly one thing here: rendering a proposal's expiry as
// culture-invariant round-trip text, so a client in any locale reads the same instant.

/// <summary>Semantic outcome shape for one change set.</summary>
public enum StockChangeSetResultKind
{
    Completed,

    /// <summary>The changes are understood and authorized but too consequential to apply unasked; an exact proposal is stored.</summary>
    ConfirmationRequired,

    Ambiguous,
    NotFound,
    ReferenceNotFound,
    Forbidden,
    Conflict,
    Invalid,
}

/// <summary>One Stock Entry's before-and-after, as exposed at the application boundary. Quantities are exact invariant decimal text.</summary>
public sealed record StockEntryStateView(
    string? StockEntryId,
    string Name,
    string Unit,
    string? Location,
    string? Note,
    string PreviousQuantity,
    string Quantity,
    bool Retired);

/// <summary>
/// One change, exactly as proposed or exactly as applied. <see cref="SurvivingStockEntryId"/> and
/// <see cref="RetiredStockEntryId"/> are the answer every merge-retiring Move and Rename owes the
/// Participant.
/// </summary>
public sealed record StockChangeView(
    int Order,
    string Operation,
    string Effect,
    StockEntryStateView Source,
    StockEntryStateView? Destination,
    string TransferredQuantity,
    string? NewName,
    string? SurvivingStockEntryId,
    string? RetiredStockEntryId);

/// <summary>What one applied change set did.</summary>
public sealed record StockChangeSetView(IReadOnlyList<StockChangeView> Changes);

/// <summary>
/// An exact stored proposal, as shown to the Participant. <see cref="Token"/> is the only time the
/// plaintext token exists outside the Participant's own screen - the store keeps only its hash.
/// </summary>
public sealed record StockProposalView(string Token, string ExpiresAt, IReadOnlyList<StockChangeView> Changes);

/// <summary>The semantic result of a change-set request. Never SQL detail, row versions, audit identities, or unauthorized existence.</summary>
public sealed record StockChangeSetResult(
    StockChangeSetResultKind Kind,
    string Code,
    StockChangeSetView? Applied = null,
    StockProposalView? Proposal = null,
    StockFindView? Candidates = null,
    StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// The deterministic authority for one set of stock changes: authorize, answer a replay, resolve
/// every change against current state, and then either apply one low-risk change immediately or
/// store an exact proposal and hand back its one-time token.
///
/// The confirmation rule lives in one expression - more than one change, or any change whose effect
/// requires it - so a batch, a Set to zero, a Forget, and a merge-retiring Move or Rename all take
/// the same path for the same stated reason.
///
/// Callers only ever supply an InventoryId already scoped by trusted context, and an unauthorized
/// Inventory stays indistinguishable from one that does not exist.
/// </summary>
public sealed class StockChangeSetService(
    StockChangeResolver resolver,
    IStockChangeSetStore changeSetStore,
    IConfirmationProposalStore proposalStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<StockChangeSetResult> ApplyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        StockOperationId operationId,
        IReadOnlyList<StockChangeRequest> requests,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.NotFound, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.Forbidden, "forbidden");
        }

        // Answered from the ledger before anything is resolved or re-planned, because a replayed Turn
        // meets Stock its own first attempt already changed. Re-planning first would see the entry it
        // merged away as missing and report "not found", telling the Participant nothing happened
        // after everything had. Deliberately after authorization, so a Viewer or a non-member learns
        // nothing from a replay they could not learn from a first attempt.
        if (await changeSetStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyRecorded)
        {
            return Applied(alreadyRecorded);
        }

        if (requests.Count == 0)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "invalid_changes");
        }

        if (requests.Count > ConfirmationProposal.MaxChanges)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "too_many_changes");
        }

        var changes = new List<ProposedChange>(requests.Count);
        var versions = new Dictionary<StockEntryId, ExpectedEntryVersion>();
        var absences = new List<ExpectedEquivalentStockAbsence>();
        var touched = new HashSet<StockEntryId>();

        foreach (var request in requests.OrderBy(request => request.Order))
        {
            var resolution = await resolver.ResolveAsync(inventoryId, request, cancellationToken);
            if (resolution.Kind != StockChangeResolutionKind.Resolved)
            {
                // One refusal refuses the whole set. A batch is atomic, so answering "these three
                // worked and that one did not" would be describing a state that never exists.
                return Refused(resolution);
            }

            var change = resolution.Change!;

            // Every change in a set is resolved against the state the set started from, so two
            // changes to one Stock Entry would each be planned as if the other had not happened.
            // Refusing is the only answer that cannot silently apply arithmetic nobody asked for.
            foreach (var id in new[] { change.Source.StockEntryId, change.Destination?.StockEntryId })
            {
                if (id is { } stockEntryId && !touched.Add(stockEntryId))
                {
                    return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "conflicting_changes");
                }
            }

            changes.Add(change);

            foreach (var version in resolution.ExpectedVersions ?? [])
            {
                versions[version.StockEntryId] = version;
            }

            if (resolution.ExpectedAbsence is { } absence)
            {
                absences.Add(absence);
            }
        }

        var requiresConfirmation = changes.Count > 1 || changes.Any(change => StockAuditFacts.RequiresConfirmation(change.Effect));

        return requiresConfirmation
            ? await ProposeAsync(participantId, inventoryId, turnId, channelConversationId, changes, versions.Values.ToList(), absences, now, cancellationToken)
            : await ApplyImmediatelyAsync(participantId, inventoryId, turnId, operationId, changes, versions.Values.ToList(), absences, now, cancellationToken);
    }

    private async Task<StockChangeSetResult> ProposeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string channelConversationId,
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        IReadOnlyList<ExpectedEquivalentStockAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The plaintext exists here and nowhere else: it goes into the answer, and only its hash is
        // stored, so nothing that can be read later can be used to confirm.
        var token = ConfirmationToken.Issue();

        var proposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId,
            channelConversationId,
            inventoryId,
            turnId,
            changes,
            versions,
            absences,
            now);

        // Storing supersedes whatever was pending in this conversation, atomically, so a confirmation
        // arriving now can only ever mean this proposal.
        await proposalStore.StoreAsync(proposal, now, cancellationToken);

        return new StockChangeSetResult(
            StockChangeSetResultKind.ConfirmationRequired,
            "confirmation_required",
            Proposal: new StockProposalView(
                token,
                proposal.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                proposal.Changes.Select(change => ToChangeView(change)).ToList()));
    }

    private async Task<StockChangeSetResult> ApplyImmediatelyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        StockOperationId operationId,
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        IReadOnlyList<ExpectedEquivalentStockAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await changeSetStore.ApplyAsync(
            new StockChangeSetCommand
            {
                OperationId = operationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = null,
                Changes = changes,
                ExpectedVersions = versions,
                ExpectedAbsences = absences,
                Now = now,
            },
            cancellationToken);

        return stored.Outcome == StockChangeSetStoreOutcome.Conflict
            ? new StockChangeSetResult(StockChangeSetResultKind.Conflict, "state_changed")
            : Applied(stored.Recorded!);
    }

    /// <summary>
    /// The one place an applied change set becomes an answer, so a replay served from the ledger, a
    /// store that converged on an already-applied operation, and a first attempt that has just
    /// written are literally indistinguishable to a Participant.
    /// </summary>
    internal static StockChangeSetResult Applied(RecordedStockChangeSet recorded) => new(
        StockChangeSetResultKind.Completed,
        "completed",
        new StockChangeSetView(recorded.Effects.Select(effect => ToChangeView(effect)).ToList()));

    internal static StockChangeView ToChangeView(RecordedStockChangeEffect effect) => new(
        effect.Order,
        StockMutationKinds.ToMachineText(effect.Kind),
        EffectText(effect.Effect),
        ToStateView(effect.Source),
        effect.Destination is null ? null : ToStateView(effect.Destination),
        effect.TransferredQuantity.ToInvariantText(),
        effect.NewName,
        effect.SurvivingStockEntryId?.ToString(),
        effect.RetiredStockEntryId?.ToString());

    internal static StockChangeView ToChangeView(ProposedChange change) => new(
        change.Order,
        StockMutationKinds.ToMachineText(change.Kind),
        EffectText(change.Effect),
        ToStateView(change.Source),
        change.Destination is null ? null : ToStateView(change.Destination),
        change.TransferredQuantity.ToInvariantText(),
        change.NewName,
        change.SurvivingStockEntryId?.ToString(),
        change.RetiredStockEntryId?.ToString());

    /// <summary>The stable machine text for an effect, in the same lower_snake shape every other code uses.</summary>
    internal static string EffectText(StockChangeEffectKind effect) => effect switch
    {
        StockChangeEffectKind.Created => "created",
        StockChangeEffectKind.QuantityIncreased => "quantity_increased",
        StockChangeEffectKind.QuantityDecreased => "quantity_decreased",
        StockChangeEffectKind.QuantitySet => "quantity_set",
        StockChangeEffectKind.QuantityCleared => "quantity_cleared",
        StockChangeEffectKind.Placed => "placed",
        StockChangeEffectKind.Split => "split",
        StockChangeEffectKind.SplitMerged => "split_merged",
        StockChangeEffectKind.Merged => "merged",
        StockChangeEffectKind.Renamed => "renamed",
        StockChangeEffectKind.RenameMerged => "rename_merged",
        StockChangeEffectKind.Forgotten => "forgotten",
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unhandled stock change effect."),
    };

    private static StockEntryStateView ToStateView(ProposedEntryState state) => new(
        state.StockEntryId?.ToString(),
        state.Name,
        state.UnitCanonicalName,
        state.LocationName,
        state.Note,
        state.PreviousQuantity.ToInvariantText(),
        state.ResultingQuantity.ToInvariantText(),
        state.Retired);

    private static StockEntryStateView ToStateView(RecordedEntryState state) => new(
        state.StockEntryId.ToString(),
        state.Name,
        state.UnitCanonicalName,
        state.LocationName,
        Note: null,
        state.PreviousQuantity.ToInvariantText(),
        state.ResultingQuantity.ToInvariantText(),
        state.Retired);

    private static StockChangeSetResult Refused(StockChangeResolution resolution) => resolution.Kind switch
    {
        StockChangeResolutionKind.Ambiguous => new StockChangeSetResult(
            StockChangeSetResultKind.Ambiguous, resolution.Code, Candidates: resolution.Candidates),
        StockChangeResolutionKind.NotFound => new StockChangeSetResult(StockChangeSetResultKind.NotFound, resolution.Code),
        StockChangeResolutionKind.ReferenceNotFound => new StockChangeSetResult(
            StockChangeSetResultKind.ReferenceNotFound, resolution.Code, UnresolvedReference: resolution.UnresolvedReference),
        StockChangeResolutionKind.Conflict => new StockChangeSetResult(StockChangeSetResultKind.Conflict, resolution.Code),
        _ => new StockChangeSetResult(StockChangeSetResultKind.Invalid, resolution.Code),
    };
}
```

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockChangeSetServiceTests"`
Expected: PASS, 18 tests.

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockChangeSetService.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockChangeSetServiceTests.cs
git commit -m "feat(inventories): apply a lone low-risk change or propose everything else for #32"
```

---

## Task 13: Execute or reject a stored proposal

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/StockConfirmationService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockConfirmationServiceTests.cs`

Why: this is the acceptance criterion in one class - direct explicit confirmation executes the stored proposal atomically, and rejection, expiry, replacement, conflict, and every mismatch invalidate it or refuse generically.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/StockConfirmationServiceTests.cs` on the same harness as Task 12 (make a proposal through `StockChangeSetService`, then confirm or reject it through `StockConfirmationService`). Write these tests:

```csharp
    [Fact]
    public async Task Direct_explicit_confirmation_executes_the_stored_proposal_exactly()

    [Fact]
    public async Task Executing_a_proposal_consumes_it_so_a_second_confirmation_finds_nothing_pending()

    [Fact]
    public async Task Confirming_without_direct_explicit_evidence_executes_nothing_and_leaves_the_proposal_pending()

    [Fact]
    public async Task A_wrong_token_executes_nothing_and_leaves_the_proposal_pending()

    [Fact]
    public async Task A_malformed_token_is_refused_exactly_like_a_wrong_one()

    [Fact]
    public async Task Confirming_after_ten_minutes_expires_the_proposal_and_executes_nothing()

    [Fact]
    public async Task Confirming_a_proposal_bound_to_another_Inventory_is_indistinguishable_from_no_proposal_at_all()

    [Fact]
    public async Task Confirming_from_another_conversation_is_indistinguishable_from_no_proposal_at_all()

    [Fact]
    public async Task Confirming_a_proposal_that_was_superseded_finds_only_the_replacement()

    [Fact]
    public async Task A_proposal_whose_Stock_moved_underneath_it_conflicts_invalidates_and_changes_nothing()

    [Fact]
    public async Task Direct_explicit_rejection_settles_the_proposal_and_changes_nothing()

    [Fact]
    public async Task Rejecting_without_direct_explicit_evidence_settles_nothing()

    [Fact]
    public async Task Rejecting_when_nothing_is_pending_is_answered_generically()

    [Fact]
    public async Task A_Viewer_can_neither_confirm_nor_reject_and_learns_nothing_from_trying()

    [Fact]
    public async Task A_Turn_that_already_executed_a_proposal_is_answered_from_the_ledger_even_though_the_proposal_is_gone()
```

Anchors for the assertions:

- The happy path asserts `StockConfirmationResultKind.Completed`, that `changeSetStore.AuditFacts` gained exactly one fact per change, that the surviving Stock Entry now carries the merged Quantity, that the retired one is gone from `stockStore`, and that `proposalStore.FindStatusAsync` reads `ProposalStatus.Confirmed`.
- Every refusal path asserts the exact code (`confirmation_evidence_missing`, `proposal_token_mismatch`, `proposal_expired`, `proposal_not_found`, `state_changed`), that `changeSetStore.AuditFacts` is empty, and that Stock is byte-for-byte unchanged.
- The "leaves it pending" tests assert `FindPendingAsync` still returns the same proposal id afterwards.
- The expiry test calls `ConfirmAsync` with `now + 10 minutes` and asserts the status became `ProposalStatus.Expired`.
- The conflict test sets `changeSetStore.ForceConflict = true` and asserts the status became `ProposalStatus.Conflicted`.
- The replay test executes a proposal, then calls `ConfirmAsync` again with the *same* `TurnId` and asserts `Completed` with the same recorded effects, even though nothing is pending any more.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockConfirmationServiceTests"`
Expected: FAIL to compile - `StockConfirmationService` does not exist.

- [ ] **Step 2: Write the service**

Create `src/MultiChannelAgent.Application/Inventories/StockConfirmationService.cs`:

```csharp
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for confirming or rejecting a stored proposal.</summary>
public enum StockConfirmationResultKind
{
    /// <summary>The stored proposal was executed - or had already been executed by this very Turn.</summary>
    Completed,

    /// <summary>The stored proposal was explicitly rejected and will never execute.</summary>
    Rejected,

    /// <summary>There is no proposal this Participant may act on. Deliberately identical whether it never existed, belongs to someone else, or was already settled.</summary>
    NotFound,

    Forbidden,

    /// <summary>The proposal could no longer execute: it expired, or current state no longer matches what was proposed.</summary>
    Conflict,

    /// <summary>The request itself could not authorize anything - no direct explicit answer, or a token that does not match.</summary>
    Invalid,
}

/// <summary>The semantic result of a confirmation or rejection. Never names a Stock Entry, an Inventory, or another Participant on a refusal.</summary>
public sealed record StockConfirmationResult(
    StockConfirmationResultKind Kind, string Code, StockChangeSetView? Applied = null);

/// <summary>
/// Executes or rejects the one stored proposal a Participant has pending in this conversation.
///
/// Four things must all hold before anything is applied, and each of them is an acceptance criterion:
/// the Participant is still an Editor of the Inventory; the current Turn's <em>direct</em> content
/// explicitly confirmed (a model proposing a confirmation tool call is not evidence of anything); the
/// presented token matches the stored hash; and the proposal is still Pending, still bound to this
/// Participant, ChannelConversation, and Inventory, and not yet expired.
///
/// Execution then consumes the proposal and applies every change in one transaction, so two
/// confirmations of one proposal can never both execute. Nothing is ever re-resolved or re-planned:
/// what the Participant reviewed is exactly what commits.
/// </summary>
public sealed class StockConfirmationService(
    IConfirmationProposalStore proposalStore,
    IStockChangeSetStore changeSetStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<StockConfirmationResult> ConfirmAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string? presentedToken,
        DirectConfirmationEvidence evidence,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return refusal;
        }

        // Asked first, and asked of the ledger rather than of the proposal: a confirmation consumes
        // its proposal, so a Turn re-driven after a crash between the mutation transaction and the
        // Outcome transaction has nothing pending left to find. Without this it would report "no
        // proposal" after having applied everything.
        if (await changeSetStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyExecuted)
        {
            return Completed(alreadyExecuted);
        }

        // The model does not get a vote. Only what the authenticated Participant themselves said, in
        // this Turn, in direct content, can approve a mutation.
        if (evidence != DirectConfirmationEvidence.Confirmed)
        {
            return Invalid("confirmation_evidence_missing");
        }

        var pending = await proposalStore.FindPendingAsync(participantId, channelConversationId, cancellationToken);
        if (pending is null)
        {
            return NotFound();
        }

        // The Active Inventory moved since this proposal was made, so it no longer describes what the
        // Participant is working in. Settle it and answer as if it were not there - which, for this
        // Inventory, it is not.
        if (!pending.BelongsTo(participantId, channelConversationId, inventoryId))
        {
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.InventorySwitched, now, cancellationToken);
            return NotFound();
        }

        if (pending.IsExpired(now))
        {
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.Expired, now, cancellationToken);
            return new StockConfirmationResult(StockConfirmationResultKind.Conflict, "proposal_expired");
        }

        // A wrong token deliberately leaves the proposal pending. The token is 256 bits, so there is
        // no brute-force attack to defend against by burning the Participant's own proposal - and a
        // mistyped confirmation should not destroy work they still mean to approve.
        if (!ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return Invalid("proposal_token_mismatch");
        }

        var stored = await changeSetStore.ApplyAsync(
            new StockChangeSetCommand
            {
                OperationId = pending.ExecutionOperationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = pending.Id,
                Changes = pending.Changes,
                ExpectedVersions = pending.ExpectedVersions,
                ExpectedAbsences = pending.ExpectedAbsences,
                Now = now,
            },
            cancellationToken);

        if (stored.Outcome == StockChangeSetStoreOutcome.Conflict)
        {
            // Current state no longer matches what was reviewed, and nothing was applied. The proposal
            // describes a change that can never commit now, so it is settled rather than left to be
            // confirmed again into the same conflict.
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.Conflicted, now, cancellationToken);
            return new StockConfirmationResult(StockConfirmationResultKind.Conflict, "state_changed");
        }

        return Completed(stored.Recorded!);
    }

    public async Task<StockConfirmationResult> RejectAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string? presentedToken,
        DirectConfirmationEvidence evidence,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return refusal;
        }

        if (evidence != DirectConfirmationEvidence.Rejected)
        {
            return Invalid("rejection_evidence_missing");
        }

        var pending = await proposalStore.FindPendingAsync(participantId, channelConversationId, cancellationToken);
        if (pending is null || !pending.BelongsTo(participantId, channelConversationId, inventoryId))
        {
            return NotFound();
        }

        // A token is optional when rejecting: declining is always safe, and a Participant should never
        // have to quote a token to stop something from happening. When one is presented it must still
        // be the right one, so a stale rejection cannot settle a proposal that replaced it.
        if (presentedToken is not null && !ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return Invalid("proposal_token_mismatch");
        }

        return await proposalStore.SettleAsync(pending.Id, ProposalStatus.Rejected, now, cancellationToken)
            ? new StockConfirmationResult(StockConfirmationResultKind.Rejected, "rejected")
            : NotFound();
    }

    private async Task<StockConfirmationResult?> AuthorizeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.NotFound => NotFound(),
            InventoryAuthorizationOutcome.Forbidden => new StockConfirmationResult(StockConfirmationResultKind.Forbidden, "forbidden"),
            _ => null,
        };
    }

    private static StockConfirmationResult Completed(RecordedStockChangeSet recorded) => new(
        StockConfirmationResultKind.Completed,
        "completed",
        new StockChangeSetView(recorded.Effects.Select(effect => StockChangeSetService.ToChangeView(effect)).ToList()));

    private static StockConfirmationResult NotFound() =>
        new(StockConfirmationResultKind.NotFound, "proposal_not_found");

    private static StockConfirmationResult Invalid(string code) => new(StockConfirmationResultKind.Invalid, code);
}
```

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockConfirmationServiceTests"`
Expected: PASS, 15 tests.

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockConfirmationService.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockConfirmationServiceTests.cs
git commit -m "feat(inventories): execute or reject a stored proposal under direct confirmation for #32"
```

---

## Task 14: Store pending proposals relationally

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ConfirmationProposalEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ConfirmationProposalEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlConfirmationProposalStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs`

Why: "one pending proposal per Participant and ChannelConversation" must be a database guarantee, not a convention two code paths agree to keep. A filtered unique index makes a second pending row impossible to create at all.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs`, deriving from `SqlIntegrationTestBase` and skipping when Docker is unavailable (match the shape `SqlStockMutationStoreTests` already uses). Write these `[SkippableFact]`s:

```csharp
    public async Task A_stored_proposal_round_trips_every_exact_effect_and_expected_version()
    public async Task A_second_pending_proposal_for_one_conversation_cannot_exist_at_all()
    public async Task Storing_a_replacement_supersedes_the_previous_one_and_leaves_exactly_one_pending()
    public async Task Two_conversations_may_each_hold_their_own_pending_proposal()
    public async Task Only_the_first_of_two_concurrent_settles_wins()
    public async Task A_token_hash_is_unique_and_the_token_itself_is_nowhere_in_the_row()
    public async Task Expiring_settles_only_pending_proposals_past_their_lifetime()
    public async Task Deleting_settled_proposals_leaves_pending_ones_alone()
```

Anchors:

- The round-trip test proposes a merge-retiring Rename, reads it back with `FindPendingAsync`, and asserts every field of both `ProposedEntryState`s, both Quantities as invariant text, `NewName`, `NewNormalizedName`, `TransferredQuantity`, the two `ExpectedEntryVersion`s, and `ExpiresAt`.
- The "cannot exist at all" test bypasses the store and inserts a second `ConfirmationProposalEntity` with `Status = "Pending"` for the same Participant and conversation directly through the `DbContext`, then asserts `SaveChangesAsync` throws `DbUpdateException` - proving the index, not the code, is what enforces the invariant.
- The concurrent-settle test runs two `SettleAsync` calls on separate scopes/`DbContext`s with `Task.WhenAll` and asserts exactly one returned true.
- The token test asserts the persisted row's `TokenHash` has length `ConfirmationToken.HashTextLength` and that the raw token text appears in no column of the row.

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlConfirmationProposalStoreTests"`
Expected: FAIL to compile - none of the persistence types exist.

- [ ] **Step 2: Write the entity**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ConfirmationProposalEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one confirmation proposal. It carries the hash of its single-use token - never
/// the token - its binding, its exact serialized contents, and its lifetime.
///
/// Only <see cref="Status"/> ever changes after insert, and only ever from <c>Pending</c> to a
/// terminal value, which is what makes single use enforceable by a guarded update rather than by
/// hoping two callers do not race.
/// </summary>
public sealed class ConfirmationProposalEntity
{
    public Guid ProposalId { get; set; }

    /// <summary>SHA-256 of the token, as 64 lowercase hexadecimal characters. Unique, so a token can never back two proposals.</summary>
    public required string TokenHash { get; set; }

    public Guid ParticipantId { get; set; }

    public required string ChannelConversationId { get; set; }

    public Guid InventoryId { get; set; }

    public Guid ProposedInTurnId { get; set; }

    /// <summary>The <c>ProposalStatus</c> as text, so the filtered unique index can be written in provider-neutral SQL.</summary>
    public required string Status { get; set; }

    /// <summary>The exact proposed changes, serialized (see <c>ConfirmationProposalMapper</c>). What the Participant reviewed is what commits.</summary>
    public required string ChangesJson { get; set; }

    public required string ExpectedVersionsJson { get; set; }

    public required string ExpectedAbsencesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When it left <c>Pending</c>; null while it is still pending. Retention is measured from here.</summary>
    public DateTimeOffset? SettledAt { get; set; }
}
```

- [ ] **Step 3: Configure it**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ConfirmationProposalEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ConfirmationProposalEntityConfiguration : IEntityTypeConfiguration<ConfirmationProposalEntity>
{
    /// <summary>The exact text of the one status the filtered index cares about. Written literally so the filter is valid SQL on every provider.</summary>
    private const string PendingStatus = nameof(ProposalStatus.Pending);

    public void Configure(EntityTypeBuilder<ConfirmationProposalEntity> builder)
    {
        builder.ToTable("ConfirmationProposals");
        builder.HasKey(e => e.ProposalId);

        builder.Property(e => e.TokenHash).HasMaxLength(ConfirmationToken.HashTextLength).IsRequired();
        builder.Property(e => e.ChannelConversationId).HasMaxLength(InboundTurn.MaxChannelConversationIdLength).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();

        // Unbounded on purpose: the contents are bounded by ConfirmationProposal.MaxChanges, not by a
        // character count, and nvarchar(n) cannot express the ceiling that bound implies.
        builder.Property(e => e.ChangesJson).IsRequired();
        builder.Property(e => e.ExpectedVersionsJson).IsRequired();
        builder.Property(e => e.ExpectedAbsencesJson).IsRequired();

        // THE invariant of this ticket: at most one Pending proposal per Participant and
        // ChannelConversation. Enforced here rather than in code, so no race, no replica, and no
        // future caller can produce a conversation with two things "confirm" could mean. The filter
        // is written as plain SQL text valid on both SQL Server and SQLite.
        builder.HasIndex(e => new { e.ParticipantId, e.ChannelConversationId })
            .IsUnique()
            .HasFilter($"Status = '{PendingStatus}'");

        // A token can never back two proposals, whatever else goes wrong.
        builder.HasIndex(e => e.TokenHash).IsUnique();

        // Supports the expiry sweep (pending rows past their lifetime) and the retention sweep.
        builder.HasIndex(e => new { e.Status, e.ExpiresAt });
        builder.HasIndex(e => e.SettledAt);

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

In `MultiChannelAgentDbContext`, add:

```csharp
    public DbSet<ConfirmationProposalEntity> ConfirmationProposals => Set<ConfirmationProposalEntity>();
```

- [ ] **Step 4: Serialize the contents exactly**

Create `src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// The exact, versioned serialization of a proposal's contents.
///
/// Quantities cross this boundary as invariant decimal text rather than as numbers: a proposal is a
/// promise about exact amounts, and JSON numbers are the one representation that could quietly round
/// one. Every identity crosses as its Guid text. <see cref="SchemaVersion"/> is written so a later
/// shape change can be detected rather than silently mis-read - a row it cannot read is refused, not
/// guessed at.
/// </summary>
internal static class ConfirmationProposalMapper
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record EntryStateDto(
        int Version,
        Guid? StockEntryId,
        string Name,
        string NormalizedName,
        Guid UnitId,
        string UnitCanonicalName,
        Guid? LocationId,
        string? LocationName,
        string? Note,
        string PreviousQuantity,
        string ResultingQuantity,
        bool Retired);

    private sealed record ChangeDto(
        int Order,
        string Kind,
        string Effect,
        EntryStateDto Source,
        EntryStateDto? Destination,
        string TransferredQuantity,
        string? NewName,
        string? NewNormalizedName);

    private sealed record VersionDto(Guid StockEntryId, Guid ConcurrencyStamp);

    private sealed record AbsenceDto(string NormalizedName, Guid UnitId, Guid? LocationId);

    private sealed record ChangesEnvelope(int Version, IReadOnlyList<ChangeDto> Changes);

    public static ConfirmationProposalEntity ToEntity(ConfirmationProposal proposal) => new()
    {
        ProposalId = proposal.Id.Value,
        TokenHash = proposal.TokenHash.Value,
        ParticipantId = proposal.ParticipantId.Value,
        ChannelConversationId = proposal.ChannelConversationId,
        InventoryId = proposal.InventoryId.Value,
        ProposedInTurnId = proposal.ProposedInTurnId.Value,
        Status = nameof(ProposalStatus.Pending),
        ChangesJson = JsonSerializer.Serialize(
            new ChangesEnvelope(SchemaVersion, proposal.Changes.Select(ToDto).ToList()), Options),
        ExpectedVersionsJson = JsonSerializer.Serialize(
            proposal.ExpectedVersions.Select(v => new VersionDto(v.StockEntryId.Value, v.ConcurrencyStamp)).ToList(), Options),
        ExpectedAbsencesJson = JsonSerializer.Serialize(
            proposal.ExpectedAbsences.Select(a => new AbsenceDto(a.NormalizedName, a.UnitId.Value, a.LocationId?.Value)).ToList(), Options),
        CreatedAt = proposal.CreatedAt,
        ExpiresAt = proposal.ExpiresAt,
        SettledAt = null,
    };

    public static ConfirmationProposal ToDomain(ConfirmationProposalEntity entity)
    {
        var envelope = JsonSerializer.Deserialize<ChangesEnvelope>(entity.ChangesJson, Options)
            ?? throw new InvalidOperationException("A stored proposal carried no changes.");

        if (envelope.Version != SchemaVersion)
        {
            // A proposal is only ever ten minutes old, so a shape this process cannot read is a
            // deployment mistake, not a migration case to guess at.
            throw new InvalidOperationException($"A stored proposal uses unsupported schema version {envelope.Version}.");
        }

        var versions = JsonSerializer.Deserialize<List<VersionDto>>(entity.ExpectedVersionsJson, Options) ?? [];
        var absences = JsonSerializer.Deserialize<List<AbsenceDto>>(entity.ExpectedAbsencesJson, Options) ?? [];

        return new ConfirmationProposal
        {
            Id = new ProposalId(entity.ProposalId),
            TokenHash = new ConfirmationTokenHash(entity.TokenHash),
            ParticipantId = new ParticipantId(entity.ParticipantId),
            ChannelConversationId = entity.ChannelConversationId,
            InventoryId = new InventoryId(entity.InventoryId),
            ProposedInTurnId = new TurnId(entity.ProposedInTurnId),
            Changes = envelope.Changes.Select(ToDomain).ToList(),
            ExpectedVersions = versions
                .Select(v => new ExpectedEntryVersion(new StockEntryId(v.StockEntryId), v.ConcurrencyStamp))
                .ToList(),
            ExpectedAbsences = absences
                .Select(a => new ExpectedEquivalentStockAbsence(
                    a.NormalizedName, new UnitId(a.UnitId), a.LocationId is { } id ? new LocationId(id) : null))
                .ToList(),
            CreatedAt = entity.CreatedAt,
        };
    }

    private static ChangeDto ToDto(ProposedChange change) => new(
        change.Order,
        StockMutationKinds.ToMachineText(change.Kind),
        change.Effect.ToString(),
        ToDto(change.Source),
        change.Destination is null ? null : ToDto(change.Destination),
        change.TransferredQuantity.ToInvariantText(),
        change.NewName,
        change.NewNormalizedName);

    private static EntryStateDto ToDto(ProposedEntryState state) => new(
        SchemaVersion,
        state.StockEntryId?.Value,
        state.Name,
        state.NormalizedName,
        state.UnitId.Value,
        state.UnitCanonicalName,
        state.LocationId?.Value,
        state.LocationName,
        state.Note,
        state.PreviousQuantity.ToInvariantText(),
        state.ResultingQuantity.ToInvariantText(),
        state.Retired);

    private static ProposedChange ToDomain(ChangeDto dto)
    {
        if (!StockMutationKinds.TryParse(dto.Kind, out var kind)
            || !Enum.TryParse<StockChangeEffectKind>(dto.Effect, ignoreCase: false, out var effect))
        {
            throw new InvalidOperationException("A stored proposal carried an unreadable change kind or effect.");
        }

        return new ProposedChange
        {
            Order = dto.Order,
            Kind = kind,
            Effect = effect,
            Source = ToDomain(dto.Source),
            Destination = dto.Destination is null ? null : ToDomain(dto.Destination),
            TransferredQuantity = ParseQuantity(dto.TransferredQuantity),
            NewName = dto.NewName,
            NewNormalizedName = dto.NewNormalizedName,
        };
    }

    private static ProposedEntryState ToDomain(EntryStateDto dto) => new(
        dto.StockEntryId is { } id ? new StockEntryId(id) : null,
        dto.Name,
        dto.NormalizedName,
        new UnitId(dto.UnitId),
        dto.UnitCanonicalName,
        dto.LocationId is { } locationId ? new LocationId(locationId) : null,
        dto.LocationName,
        dto.Note,
        ParseQuantity(dto.PreviousQuantity),
        ParseQuantity(dto.ResultingQuantity),
        dto.Retired);

    private static Quantity ParseQuantity(string text) => Quantity.TryParseInvariant(text, out var quantity)
        ? quantity
        : throw new InvalidOperationException("A stored proposal carried an unreadable Quantity.");
}
```

- [ ] **Step 5: Write the SQL store**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlConfirmationProposalStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IConfirmationProposalStore"/>.
///
/// Two things here are load-bearing. First, <see cref="StoreAsync"/> supersedes and inserts inside
/// one transaction, so a conversation is never briefly holding two pending proposals - or none - and
/// a confirmation arriving mid-replacement can only ever mean one of them. Second, every status
/// change is a single guarded UPDATE with <c>Status = 'Pending'</c> in its predicate, so "single
/// use" is decided by the database's own row lock rather than by a read-then-write this process
/// could lose.
/// </summary>
public sealed class SqlConfirmationProposalStore(MultiChannelAgentDbContext db) : IConfirmationProposalStore
{
    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);

    public async Task<ConfirmationProposal?> FindPendingAsync(
        ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken)
    {
        var entity = await db.ConfirmationProposals
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.ParticipantId == participantId.Value
                    && p.ChannelConversationId == channelConversationId
                    && p.Status == PendingStatus,
                cancellationToken);

        return entity is null ? null : ConfirmationProposalMapper.ToDomain(entity);
    }

    public async Task<StoredProposalReplacement> StoreAsync(
        ConfirmationProposal proposal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var superseded = await db.ConfirmationProposals
            .Where(p => p.ParticipantId == proposal.ParticipantId.Value
                && p.ChannelConversationId == proposal.ChannelConversationId
                && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Status, nameof(ProposalStatus.Superseded))
                    .SetProperty(p => p.SettledAt, now),
                cancellationToken);

        db.ConfirmationProposals.Add(ConfirmationProposalMapper.ToEntity(proposal));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new StoredProposalReplacement(superseded > 0);
    }

    public async Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken)
    {
        // Guarded on Status: the second caller updates zero rows and is told so, which is exactly how
        // a proposal is used at most once.
        var settled = await db.ConfirmationProposals
            .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(p => p.Status, status.ToString()).SetProperty(p => p.SettledAt, settledAt),
                cancellationToken);

        return settled == 1;
    }

    public async Task<ProposalStatus?> FindStatusAsync(ProposalId proposalId, CancellationToken cancellationToken)
    {
        var status = await db.ConfirmationProposals
            .AsNoTracking()
            .Where(p => p.ProposalId == proposalId.Value)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null ? null : Enum.Parse<ProposalStatus>(status);
    }

    public async Task<int> InvalidatePendingAsync(
        ParticipantId participantId,
        string channelConversationId,
        ProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await db.ConfirmationProposals
            .Where(p => p.ParticipantId == participantId.Value
                && p.ChannelConversationId == channelConversationId
                && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(p => p.Status, status.ToString()).SetProperty(p => p.SettledAt, now),
                cancellationToken);

    public async Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var expiring = db.ConfirmationProposals
            .Where(p => p.Status == PendingStatus && p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt)
            .Take(maxRows);

        return await expiring.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(p => p.Status, nameof(ProposalStatus.Expired))
                .SetProperty(p => p.SettledAt, now),
            cancellationToken);
    }

    public async Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var deletable = db.ConfirmationProposals
            .Where(p => p.SettledAt != null && p.SettledAt <= cutoff)
            .OrderBy(p => p.SettledAt)
            .Take(maxRows);

        return await deletable.ExecuteDeleteAsync(cancellationToken);
    }
}
```

- [ ] **Step 6: Generate the migration**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddConfirmationProposals \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --output-dir Persistence/Migrations
```

Confirm the generated migration creates exactly the `ConfirmationProposals` table, its four indexes (the filtered unique `(ParticipantId, ChannelConversationId)`, the unique `TokenHash`, `(Status, ExpiresAt)`, and `SettledAt`), and the cascade foreign key to `Inventories` - and nothing else. There is no backfill: no proposal has ever existed.

- [ ] **Step 7: Verify**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlConfirmationProposalStoreTests"`
Expected: PASS, 8 tests. If Docker is genuinely unavailable, run without the environment variable, confirm they report as skipped rather than failed, and say so plainly in the commit message.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Persistence src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs src/MultiChannelAgent.Infrastructure/Inventories/SqlConfirmationProposalStore.cs tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs
git commit -m "feat(infrastructure): store one pending proposal per Participant and conversation for #32"
```

---

## Task 15: Apply a change set atomically, or change nothing

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockChangeSetOperationEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockChangeSetEffectEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockChangeSetOperationEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockChangeSetEffectEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockChangeSetStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockChangeSetStoreTests.cs`

Why: this is the transaction the whole ticket rests on. Proposal consumption, every state change, every audit fact, and the ledger commit together, or nothing does.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockChangeSetStoreTests.cs`, deriving from `SqlIntegrationTestBase`. Write these `[SkippableFact]`s:

```csharp
    public async Task A_merge_retiring_Move_updates_the_destination_deletes_the_source_and_records_both_identities()
    public async Task A_Rename_collision_merges_into_the_colliding_entry_and_retires_the_source()
    public async Task A_Forget_removes_the_Stock_Entry_and_leaves_its_ledger_row_behind()
    public async Task Applying_a_change_set_writes_its_state_changes_audits_and_ledger_together()
    public async Task A_change_set_whose_expected_version_moved_applies_nothing_at_all()
    public async Task A_change_set_whose_expected_absence_was_filled_applies_nothing_at_all()
    public async Task Consuming_a_proposal_and_applying_it_happen_in_one_transaction()
    public async Task Two_concurrent_confirmations_of_one_proposal_apply_it_exactly_once()
    public async Task Applying_the_same_operation_identity_again_re_reports_instead_of_re_applying()
    public async Task A_recorded_change_set_is_findable_by_the_Turn_that_confirmed_it_and_invisible_from_other_Inventories()
```

Anchors:

- `A_merge_retiring_Move...` asserts the destination row's Quantity, that the source row is gone, that the returned `RecordedStockChangeEffect.SurvivingStockEntryId` is the destination and `RetiredStockEntryId` is the source, and that exactly one `StockMoved` audit fact exists.
- `A_change_set_whose_expected_version_moved...` mutates the row through a second `DbContext` first, then applies, and asserts `StockChangeSetStoreOutcome.Conflict`, that every Stock row is byte-for-byte unchanged, that no audit fact was appended, that no ledger row exists, **and that the proposal is still Pending** - a rolled-back consumption must roll back too.
- `Two_concurrent_confirmations...` builds two scopes with their own `DbContext` and store, and runs both `ApplyAsync` calls for the same proposal with `Task.WhenAll`. It asserts exactly one `Applied` and one `Conflict` (or `AlreadyApplied`), exactly one ledger header, and the merged Quantity applied exactly once.
- `A_recorded_change_set_is_findable_by_the_Turn...` asserts `FindRecordedByTurnAsync` returns it for the right Inventory and null for a different `InventoryId`.

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockChangeSetStoreTests"`
Expected: FAIL to compile - none of the ledger types exist.

- [ ] **Step 2: Write the ledger entities**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockChangeSetOperationEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger header for one applied change set. Its whole purpose is retry safety: a
/// re-driven Turn finds its own row here and re-reports exactly what it did, so one confirmed
/// proposal can never be applied twice by a redelivery, a restart, or a competing replica.
///
/// It carries semantic facts and nothing more - no prompts, no raw payloads, no concurrency stamps,
/// and no audit identity.
/// </summary>
public sealed class StockChangeSetOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid InventoryId { get; set; }

    /// <summary>
    /// The Turn that caused this execution. Unique per Inventory, which is what makes the replay
    /// lookup deterministic without needing the proposal - by replay time it has been consumed.
    /// </summary>
    public Guid ConfirmedByTurnId { get; set; }

    /// <summary>The proposal this consumed, or null for an immediate change that needed none.</summary>
    public Guid? ProposalId { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/StockChangeSetEffectEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One recorded effect of one applied change. Stock Entry identities are stored as plain values with
/// no foreign key on purpose: a merge or a Forget deletes the very row this records, and the record
/// of what happened must outlive it.
/// </summary>
public sealed class StockChangeSetEffectEntity
{
    public Guid Id { get; set; }

    public Guid OperationId { get; set; }

    /// <summary>1-based position within the change set, so the recorded effects read back in the order they were applied.</summary>
    public int Order { get; set; }

    public required string Kind { get; set; }

    public required string Effect { get; set; }

    public Guid SourceStockEntryId { get; set; }

    public required string SourceName { get; set; }

    public required string SourceUnitCanonicalName { get; set; }

    public string? SourceLocationName { get; set; }

    public decimal SourcePreviousQuantity { get; set; }

    public decimal SourceResultingQuantity { get; set; }

    public bool SourceRetired { get; set; }

    public Guid? DestinationStockEntryId { get; set; }

    public string? DestinationName { get; set; }

    public string? DestinationUnitCanonicalName { get; set; }

    public string? DestinationLocationName { get; set; }

    public decimal? DestinationPreviousQuantity { get; set; }

    public decimal? DestinationResultingQuantity { get; set; }

    /// <summary>How much this change actually moved from source to destination; zero when nothing moved.</summary>
    public decimal TransferredQuantity { get; set; }

    /// <summary>The exact new display name a Rename applied, or null for every other effect.</summary>
    public string? NewName { get; set; }
}
```

- [ ] **Step 3: Configure them**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockChangeSetOperationEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class StockChangeSetOperationEntityConfiguration : IEntityTypeConfiguration<StockChangeSetOperationEntity>
{
    public void Configure(EntityTypeBuilder<StockChangeSetOperationEntity> builder)
    {
        builder.ToTable("StockChangeSetOperations");

        // The operation identity IS the key, so recording one operation twice is impossible by
        // construction rather than by convention.
        builder.HasKey(e => e.OperationId);

        // The replay key. It is unique because a Turn dispatches exactly one tool call today; when
        // multi-tool-call agent runs arrive, this index must gain that call's sequence, and
        // FindRecordedByTurnAsync must take it as an argument.
        builder.HasIndex(e => new { e.InventoryId, e.ConfirmedByTurnId }).IsUnique();

        // A proposal is consumed at most once, so at most one operation can name it.
        builder.HasIndex(e => e.ProposalId).IsUnique().HasFilter("ProposalId IS NOT NULL");

        builder.HasIndex(e => e.AppliedAt);

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/StockChangeSetEffectEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class StockChangeSetEffectEntityConfiguration : IEntityTypeConfiguration<StockChangeSetEffectEntity>
{
    /// <summary>Matches <c>UnitEntityConfiguration</c>'s canonical name length, since these columns store copies of one.</summary>
    private const int UnitCanonicalNameLength = 100;

    public void Configure(EntityTypeBuilder<StockChangeSetEffectEntity> builder)
    {
        builder.ToTable("StockChangeSetEffects");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Effect).HasMaxLength(32).IsRequired();
        builder.Property(e => e.SourceName).HasMaxLength(StockEntry.MaxNameLength).IsRequired();
        builder.Property(e => e.SourceUnitCanonicalName).HasMaxLength(UnitCanonicalNameLength).IsRequired();
        builder.Property(e => e.SourceLocationName).HasMaxLength(Location.MaxNameLength);
        builder.Property(e => e.DestinationName).HasMaxLength(StockEntry.MaxNameLength);
        builder.Property(e => e.DestinationUnitCanonicalName).HasMaxLength(UnitCanonicalNameLength);
        builder.Property(e => e.DestinationLocationName).HasMaxLength(Location.MaxNameLength);

        // The same precision and scale StockEntries uses, so a recorded amount is byte-for-byte the
        // amount that was written and a retry re-reports it exactly.
        builder.Property(e => e.SourcePreviousQuantity).HasPrecision(28, 10);
        builder.Property(e => e.SourceResultingQuantity).HasPrecision(28, 10);
        builder.Property(e => e.DestinationPreviousQuantity).HasPrecision(28, 10);
        builder.Property(e => e.DestinationResultingQuantity).HasPrecision(28, 10);
        builder.Property(e => e.TransferredQuantity).HasPrecision(28, 10);
        builder.Property(e => e.NewName).HasMaxLength(StockEntry.MaxNameLength);

        builder.HasOne<StockChangeSetOperationEntity>()
            .WithMany()
            .HasForeignKey(e => e.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reads are always "every effect of this operation, in order".
        builder.HasIndex(e => new { e.OperationId, e.Order }).IsUnique();
    }
}
```

In `MultiChannelAgentDbContext`, add:

```csharp
    public DbSet<StockChangeSetOperationEntity> StockChangeSetOperations => Set<StockChangeSetOperationEntity>();

    public DbSet<StockChangeSetEffectEntity> StockChangeSetEffects => Set<StockChangeSetEffectEntity>();
```

- [ ] **Step 4: Write the atomic executor**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockChangeSetStore.cs`. The structure is fixed; write it exactly as described, in this order:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockChangeSetStore"/>: the one transaction the confirmation
/// protocol rests on.
///
/// One <see cref="ApplyAsync"/> call consumes the proposal, verifies and locks every touched row,
/// applies every effect, appends one minimal semantic audit fact per change, and writes the ledger -
/// all inside one explicit transaction. Any failure rolls the whole thing back, so a caller that
/// sees <see cref="StockChangeSetStoreOutcome.Conflict"/> can rely on nothing at all having happened,
/// including the proposal still being pending.
///
/// Locking is deliberate rather than incidental. The second pass below touches every row the change
/// set will write to, in one globally agreed order (the ordinal text of the Stock Entry identity),
/// with a single guarded UPDATE per row that both takes the row's exclusive lock and checks its
/// expected version. Two concurrent batches over overlapping rows therefore contend in the same
/// order and one of them simply loses, instead of deadlocking each other halfway through.
///
/// The ledger commits with the state change rather than after it, exactly as
/// <see cref="SqlStockMutationStore"/> does: the terminal Outcome is written later, in its own atomic
/// write, and if the process dies in between, the Turn is reprocessed, finds its ledger row through
/// <see cref="FindRecordedByTurnAsync"/>, and re-reports instead of re-applying.
/// </summary>
public sealed class SqlStockChangeSetStore(MultiChannelAgentDbContext db) : IStockChangeSetStore
{
    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);

    public async Task<RecordedStockChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken)
    {
        var header = await db.StockChangeSetOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<RecordedStockChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken)
    {
        var header = await db.StockChangeSetOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.InventoryId == inventoryId.Value && o.ConfirmedByTurnId == turnId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<StockChangeSetStoreResult> ApplyAsync(StockChangeSetCommand command, CancellationToken cancellationToken)
    {
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.AlreadyApplied, already);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Consume the proposal, guarded. Doing this first means a losing confirmation stops
            //    here, before it has touched any Stock at all.
            if (command.ConsumesProposalId is { } proposalId)
            {
                var consumed = await db.ConfirmationProposals
                    .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(p => p.Status, nameof(ProposalStatus.Confirmed))
                            .SetProperty(p => p.SettledAt, command.Now),
                        cancellationToken);

                if (consumed != 1)
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }
            }

            // 2. Lock and verify every touched row, in one globally agreed order. Each statement both
            //    takes the row's exclusive lock and asserts the version the proposal was decided
            //    against, so a row that moved since stops the whole set here.
            foreach (var expected in command.ExpectedVersions.OrderBy(v => v.StockEntryId.Value.ToString("D"), StringComparer.Ordinal))
            {
                var freshStamp = Guid.NewGuid();

                var locked = await db.StockEntries
                    .Where(e => e.Id == expected.StockEntryId.Value
                        && e.InventoryId == command.InventoryId.Value
                        && e.ConcurrencyStamp == expected.ConcurrencyStamp)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ConcurrencyStamp, freshStamp), cancellationToken);

                if (locked != 1)
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }
            }

            // 3. Verify every expected absence. The Equivalent Stock unique indexes are the real
            //    guarantee; this check turns the common case into a clean conflict rather than an
            //    exception.
            foreach (var absence in command.ExpectedAbsences)
            {
                if (await EquivalentExistsAsync(command.InventoryId, absence, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }
            }

            // 4. Apply the effects in the order the Participant reviewed.
            var effects = new List<RecordedStockChangeEffect>(command.Changes.Count);
            foreach (var change in command.Changes.OrderBy(change => change.Order))
            {
                effects.Add(await ApplyChangeAsync(command, change, cancellationToken));
            }

            // 5. Ledger, effects, and one minimal semantic audit fact per change.
            db.StockChangeSetOperations.Add(new StockChangeSetOperationEntity
            {
                OperationId = command.OperationId.Value,
                InventoryId = command.InventoryId.Value,
                ConfirmedByTurnId = command.ConfirmedByTurnId.Value,
                ProposalId = command.ConsumesProposalId?.Value,
                AppliedAt = command.Now,
            });

            foreach (var effect in effects)
            {
                db.StockChangeSetEffects.Add(ToEntity(command.OperationId, effect));
            }

            foreach (var change in command.Changes)
            {
                db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
                    StockAuditFacts.EventTypeFor(change.Kind),
                    AuditActorKind.Participant,
                    command.ActorId.ToString(),
                    command.InventoryId,
                    subjectParticipantId: null,
                    StockAuditFacts.OutcomeCodeFor(change.Effect),
                    command.Now)));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new StockChangeSetStoreResult(
                StockChangeSetStoreOutcome.Applied,
                new RecordedStockChangeSet(command.OperationId, command.ConsumesProposalId, effects));
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // A competing writer may have been this very operation, applied by another replica. Its
            // ledger row is the authoritative record of what happened, so converge on re-reporting it
            // rather than claiming a conflict against ourselves.
            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.AlreadyApplied, converged);
            }

            // Equivalent Stock is unique in the database, so a competing writer that created the row
            // this set meant to create makes the insert fail. That is a conflict; anything else is a
            // real fault and must keep propagating.
            if (exception is DbUpdateConcurrencyException || await AnyExpectedAbsenceFilledAsync(command, cancellationToken))
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
            }

            throw;
        }
    }
}
```

Then add these private members to the same class:

- `ApplyChangeAsync(StockChangeSetCommand, ProposedChange, CancellationToken)` - one `switch` over `change.Effect` implementing exactly the table in "Domain and state-machine decisions", decision 2. `Created` and `Split`'s destination `db.StockEntries.Add(...)` a new `StockEntryEntity` built from `StockEntry.Create(...)` so the domain validates the name and Note; `QuantityIncreased`/`QuantityDecreased`/`QuantitySet`/`QuantityCleared` and the quantity side of `SplitMerged`/`Merged`/`RenameMerged` use `ExecuteUpdateAsync` setting `Quantity` (and a fresh `ConcurrencyStamp`); `Placed` sets `LocationId`; `Renamed` sets `Name` and `NormalizedName`; `Merged`, `RenameMerged`, and `Forgotten` end with `ExecuteDeleteAsync` on the source row. Each returns the `RecordedStockChangeEffect` for that change, built from the proposal's own states (which are exact - they are what was reviewed) plus the identity of anything created.
- `RolledBackConflictAsync(IDbContextTransaction, CancellationToken)` - rolls back, clears the change tracker, and returns `new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null)`.
- `EquivalentExistsAsync(InventoryId, ExpectedEquivalentStockAbsence, CancellationToken)` - the same shape `SqlStockMutationStore.EquivalentExistsAsync` already uses, asking for `LocationId == null` explicitly when the absence is unlocated, because relational NULL semantics never match a null parameter.
- `AnyExpectedAbsenceFilledAsync(StockChangeSetCommand, CancellationToken)` - true when any expected absence is now filled.
- `ReadRecordedAsync(StockChangeSetOperationEntity, CancellationToken)` - loads the effect rows ordered by `Order` and maps them back to `RecordedStockChangeEffect`.
- `ToEntity(StockOperationId, RecordedStockChangeEffect)` - the inverse mapping.

Every `ExecuteUpdateAsync` in `ApplyChangeAsync` must assert exactly one row affected and go through `RolledBackConflictAsync` otherwise; a change that touched no row means state moved between the lock pass and here, which cannot happen while the locks are held and must therefore fail loudly rather than silently.

- [ ] **Step 5: Register everything**

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, alongside the existing Inventory registrations:

```csharp
        services.AddScoped<IConfirmationProposalStore, SqlConfirmationProposalStore>();
        services.AddScoped<IStockChangeSetStore, SqlStockChangeSetStore>();
        services.AddScoped<StockChangeResolver>();
        services.AddScoped<StockChangeSetService>();
        services.AddScoped<StockConfirmationService>();
```

- [ ] **Step 6: Generate the migration**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddStockChangeSetLedger \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --output-dir Persistence/Migrations
```

Confirm it creates exactly `StockChangeSetOperations` and `StockChangeSetEffects` with their indexes and nothing else. There is no backfill: no change set has ever been applied.

- [ ] **Step 7: Verify**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlStockChangeSetStoreTests"`
Expected: PASS, 10 tests.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure tests/MultiChannelAgent.IntegrationTests/Inventories/SqlStockChangeSetStoreTests.cs
git commit -m "feat(infrastructure): apply a confirmed stock change set atomically for #32"
```

---

## Task 16: Invalidate a pending proposal on interruption, an Inventory switch, and access loss

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ConfirmationProposalLifecycle.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/InventorySelectionService.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalLifecycleTests.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/InventorySelectionServiceTests.cs`

Why: rejection, replacement, expiry, and conflict are all handled where they happen. The three that are *not* the Participant answering the proposal - an interrupted Turn, an explicit Inventory switch, and losing access - need one place that runs before a Turn is interpreted and one hook where selection changes.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalLifecycleTests.cs` with these tests, built on `InMemoryConfirmationProposalStore`:

```csharp
    [Fact]
    public async Task An_interrupted_Turn_invalidates_whatever_was_pending_in_that_conversation()

    [Fact]
    public async Task An_interrupted_Turn_leaves_other_conversations_proposals_alone()

    [Fact]
    public async Task Losing_access_to_the_Inventory_invalidates_the_pending_proposal()

    [Fact]
    public async Task A_Turn_whose_Active_Inventory_is_now_a_different_one_invalidates_the_pending_proposal()

    [Fact]
    public async Task An_ordinary_Turn_in_the_same_Inventory_leaves_the_pending_proposal_alone()
```

Anchors: each drives `ConfirmationProposalLifecycle.ReconcileAsync(context, now, ct)` with a `TurnExecutionContext` shaped for the case, then asserts the resulting `ProposalStatus` (`Interrupted`, `AccessLost`, `InventorySwitched`, or still `Pending`).

Append to `tests/MultiChannelAgent.Application.Tests/Inventories/InventorySelectionServiceTests.cs`:

```csharp
    [Fact]
    public async Task Switching_the_Active_Inventory_invalidates_the_pending_proposal_in_that_conversation()

    [Fact]
    public async Task Selecting_the_Inventory_that_is_already_active_leaves_the_pending_proposal_alone()
```

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ConfirmationProposalLifecycleTests|FullyQualifiedName~InventorySelectionServiceTests"`
Expected: FAIL to compile - `ConfirmationProposalLifecycle` does not exist and `InventorySelectionService` takes no proposal store.

- [ ] **Step 2: Write the lifecycle**

Create `src/MultiChannelAgent.Application/Inventories/ConfirmationProposalLifecycle.cs`:

```csharp
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The one place every invalidation that is <em>not</em> the Participant answering their proposal
/// lives. It runs once per Turn, immediately after the trusted context is assembled and before the
/// model is asked anything, so a proposal that must not survive this Turn is already settled by the
/// time any tool could reach it.
///
/// Rejection, replacement, expiry, and execution conflicts are handled where they happen -
/// <see cref="StockConfirmationService"/>, <see cref="IConfirmationProposalStore.StoreAsync"/>, and
/// the change-set store - because each of those already holds the context needed to decide.
/// </summary>
public sealed class ConfirmationProposalLifecycle(IConfirmationProposalStore proposalStore)
{
    /// <summary>
    /// Settles the pending proposal for this Turn's Participant and ChannelConversation when this
    /// Turn makes it untrustworthy, and returns the status it was settled with (or null when it was
    /// left alone). Returning the status rather than nothing lets a caller say what happened instead
    /// of leaving the Participant to discover their proposal quietly stopped working.
    /// </summary>
    public async Task<ProposalStatus?> ReconcileAsync(
        TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var conversationId = context.ChannelConversationId.Value;

        var pending = await proposalStore.FindPendingAsync(context.ParticipantId, conversationId, cancellationToken);
        if (pending is null)
        {
            return null;
        }

        // A cut-off utterance is not a statement of intent, and a conversation that has just been
        // interrupted is not one in which a stored approval should keep waiting to be triggered.
        var status = context switch
        {
            { WasInterrupted: true } => ProposalStatus.Interrupted,

            // Trusted context rechecks Membership every Turn. No Active Inventory now means access to
            // it was lost (or the selection was cleared), and a proposal bound to an Inventory the
            // Participant may no longer touch must never execute.
            { ActiveInventoryId: null } => ProposalStatus.AccessLost,

            // The conversation is working somewhere else now, so the proposal no longer describes what
            // the Participant is doing.
            { ActiveInventoryId: { } active } when active != pending.InventoryId => ProposalStatus.InventorySwitched,
            _ => (ProposalStatus?)null,
        };

        if (status is not { } terminal)
        {
            return null;
        }

        return await proposalStore.SettleAsync(pending.Id, terminal, now, cancellationToken) ? terminal : null;
    }
}
```

- [ ] **Step 3: Invalidate on an explicit switch**

In `src/MultiChannelAgent.Application/Inventories/InventorySelectionService.cs`, take the proposal store and settle on a real change:

```csharp
public sealed class InventorySelectionService(
    InventoryAuthorizationService authorizationService,
    IActiveInventorySelectionStore selectionStore,
    IConfirmationProposalStore proposalStore)
```

and, in `SelectAsync`, immediately before the `UpsertAsync` call:

```csharp
        // An explicit switch changes what "confirm" would mean in this conversation, so whatever was
        // pending stops being confirmable. Re-selecting the Inventory that is already active changes
        // nothing and must not throw the Participant's own proposal away.
        var previous = await selectionStore.FindAsync(participantId, channelConversationId, cancellationToken);
        if (previous is not null && previous.InventoryId != inventoryId)
        {
            await proposalStore.InvalidatePendingAsync(
                participantId, channelConversationId, ProposalStatus.InventorySwitched, now, cancellationToken);
        }
```

- [ ] **Step 4: Run it once per Turn**

In `src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs`, take the lifecycle as a constructor dependency and call it in `ProcessOneAsync`, immediately after the execution context is built and before `modelBoundary.ProposeAsync`:

```csharp
        var executionContext = await executionContextFactory.CreateAsync(turn, now, cancellationToken);

        // Settled before the model is asked anything, so an interrupted Turn, a switched Active
        // Inventory, or lost access can never leave a confirmable proposal behind for this Turn - or
        // any later one - to trigger.
        await proposalLifecycle.ReconcileAsync(executionContext, now, cancellationToken);
```

Update the class's `<summary>` to say it reconciles pending confirmation state before interpreting a Turn.

- [ ] **Step 5: Register it**

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`:

```csharp
        services.AddScoped<ConfirmationProposalLifecycle>();
```

- [ ] **Step 6: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS. `TurnProcessingCoordinatorTests` will need the new dependency supplied; construct it with an `InMemoryConfirmationProposalStore` rather than changing the coordinator's shape.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ConfirmationProposalLifecycle.cs src/MultiChannelAgent.Application/Inventories/InventorySelectionService.cs src/MultiChannelAgent.Application/Turns/TurnProcessingCoordinator.cs src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs tests/MultiChannelAgent.Application.Tests
git commit -m "feat(inventories): invalidate a pending proposal on interruption, switch, and access loss for #32"
```

---

## Task 17: Dispatch Move, Rename, Forget, batches, confirmation, and rejection

**Files:**
- Modify: `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs`

Why: this is the trust boundary made concrete. Six new tool names, all executed under `TurnExecutionContext`, none of them accepting identity, and confirmation supplied by the application rather than by the proposal.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs`:

```csharp
    [Fact]
    public async Task Move_stock_transfers_and_reports_the_exact_read_back()

    [Fact]
    public async Task Move_stock_that_would_retire_its_source_answers_confirmation_required_with_an_exact_proposal()

    [Fact]
    public async Task Rename_stock_preserves_identity_and_reports_the_new_name()

    [Fact]
    public async Task Forget_stock_always_answers_confirmation_required()

    [Fact]
    public async Task Apply_stock_changes_proposes_the_whole_batch_atomically()

    [Fact]
    public async Task Apply_stock_changes_refuses_a_malformed_changes_argument_without_touching_Stock()

    [Fact]
    public async Task Confirm_inventory_operation_executes_only_when_the_Turn_itself_confirmed()

    [Fact]
    public async Task Confirm_inventory_operation_proposed_by_the_model_alone_executes_nothing()

    [Fact]
    public async Task Reject_inventory_operation_settles_the_proposal_when_the_Turn_itself_rejected()

    [Fact]
    public async Task Every_new_tool_derives_its_operation_identity_from_the_Turn_and_never_from_its_arguments()

    [Fact]
    public async Task A_proposal_payload_never_carries_a_row_version_an_audit_id_or_a_proposal_identity()
```

Anchors:

- The proposal tests assert `OutcomeCategory.ConfirmationRequired`, that the payload's `kind` is `"stock_proposal"`, that it carries `token`, `expiresAt`, and one `changes` element with `survivingStockEntryId` and `retiredStockEntryId`, and that the summary text names no Inventory.
- `Confirm_inventory_operation_proposed_by_the_model_alone...` dispatches with a `TurnExecutionContext` whose `Confirmation` is `DirectConfirmationEvidence.None`, and asserts `OutcomeCategory.Invalid` with code `confirmation_evidence_missing` and that Stock is unchanged.
- `Every_new_tool_derives_its_operation_identity...` dispatches the same tool twice with the same `TurnId` and asserts the second answer is the recorded one and the ledger holds one operation.
- The last test serializes the payload and asserts it contains none of the substrings `"concurrencyStamp"`, `"proposalId"`, `"rowVersion"`, or `"auditId"`.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockToolDispatcherTests"`
Expected: FAIL - the new tool names are unrecognized, so every new test gets `unknown_tool`.

- [ ] **Step 2: Extend the dispatcher**

In `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`:

Take the two new services:

```csharp
public sealed class StockToolDispatcher(
    StockListingService listingService,
    StockFindingService findingService,
    StockMutationService mutationService,
    StockChangeSetService changeSetService,
    StockConfirmationService confirmationService) : IToolDispatcher
```

Add the tool names:

```csharp
    public const string MoveStockToolName = "move_stock";
    public const string RenameStockToolName = "rename_stock";
    public const string ForgetStockToolName = "forget_stock";
    public const string ApplyStockChangesToolName = "apply_stock_changes";
    public const string ConfirmToolName = "confirm_inventory_operation";
    public const string RejectToolName = "reject_inventory_operation";
```

Add the arms to the `switch`, before the `_ =>` case:

```csharp
            MoveStockToolName => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Move, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            RenameStockToolName => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Rename, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            ForgetStockToolName => await DispatchChangeSetAsync(
                SingleChange(Domain.Inventories.StockMutationKind.Forget, proposal.UntrustedArgs),
                proposal, context, inventoryId, now, cancellationToken),
            ApplyStockChangesToolName => await DispatchBatchAsync(proposal, context, inventoryId, now, cancellationToken),
            ConfirmToolName => await DispatchConfirmAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
            RejectToolName => await DispatchRejectAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken),
```

Add these members:

```csharp
    /// <summary>
    /// Reads one change from a single-change tool's untrusted arguments. Every value is untrusted
    /// text - a name, an amount, exact Unit/Location references, a destination, a new name. None of
    /// them is identity, and none can widen what this Turn may touch.
    /// </summary>
    private static IReadOnlyList<StockChangeRequest> SingleChange(
        Domain.Inventories.StockMutationKind kind, IReadOnlyDictionary<string, string> untrustedArgs) =>
    [
        new StockChangeRequest
        {
            Order = 1,
            Kind = kind,
            Reference = untrustedArgs.GetValueOrDefault("reference"),
            QuantityText = untrustedArgs.GetValueOrDefault("quantity"),
            MoveAll = ParseFlag(untrustedArgs, "all"),
            UnitReference = untrustedArgs.GetValueOrDefault("unit"),
            LocationReference = untrustedArgs.GetValueOrDefault("location"),
            UnlocatedOnly = ParseFlag(untrustedArgs, "unlocated"),
            DestinationLocationReference = untrustedArgs.GetValueOrDefault("to"),
            DestinationUnlocated = ParseFlag(untrustedArgs, "toUnlocated"),
            NewName = untrustedArgs.GetValueOrDefault("newName"),
            Note = untrustedArgs.GetValueOrDefault("note"),
        },
    ];

    private async Task<ModelDecision> DispatchBatchAsync(
        ToolCallProposal proposal,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!StockChangeSetParser.TryParse(proposal.UntrustedArgs.GetValueOrDefault("changes"), out var requests, out var code))
        {
            return Semantic(OutcomeCategory.Invalid, code, InvalidChangeSetSummary(code));
        }

        return await DispatchChangeSetAsync(requests, proposal, context, inventoryId, now, cancellationToken);
    }

    private async Task<ModelDecision> DispatchChangeSetAsync(
        IReadOnlyList<StockChangeRequest> requests,
        ToolCallProposal proposal,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Derived from the durably accepted Turn and the tool being executed - both trusted, both
        // stable across retries - so replaying this Turn re-reports the recorded effect instead of
        // applying a second one. Nothing the model proposes contributes to it.
        var operationId = Domain.Inventories.StockOperationId.Derive(context.TurnId, proposal.ToolName, sequence: 0);

        var result = await changeSetService.ApplyAsync(
            context.ParticipantId,
            inventoryId,
            context.TurnId,
            operationId,
            requests,
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return result.Kind switch
        {
            StockChangeSetResultKind.Completed => Completed(
                "completed",
                SummarizeChanges(result.Applied!),
                JsonSerializer.Serialize(new StockChangesPayload(1, "stock_changes", result.Applied!.Changes), PayloadOptions)),
            StockChangeSetResultKind.ConfirmationRequired => ConfirmationRequired(result.Proposal!),
            StockChangeSetResultKind.Ambiguous => Ambiguous(
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
            StockChangeSetResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No matching Stock Entry was found."),
            StockChangeSetResultKind.ReferenceNotFound => Semantic(
                OutcomeCategory.NotFound, "reference_not_found", UnresolvedReferenceSummary(result.UnresolvedReference)),
            StockChangeSetResultKind.Conflict => Semantic(OutcomeCategory.Conflict, result.Code, ChangeSetConflictSummary(result.Code)),
            StockChangeSetResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidChangeSetSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchConfirmAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The token is the only thing the model may supply here. Whether the Participant actually
        // confirmed comes from trusted context, derived from their own direct content in this Turn -
        // so a tool call the model invented on its own approves nothing.
        var result = await confirmationService.ConfirmAsync(
            context.ParticipantId,
            inventoryId,
            context.TurnId,
            untrustedArgs.GetValueOrDefault("token"),
            context.Confirmation,
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return ToDecision(result);
    }

    private async Task<ModelDecision> DispatchRejectAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        Domain.Inventories.InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await confirmationService.RejectAsync(
            context.ParticipantId,
            inventoryId,
            context.TurnId,
            untrustedArgs.GetValueOrDefault("token"),
            context.Confirmation,
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return ToDecision(result);
    }

    private static ModelDecision ToDecision(StockConfirmationResult result) => result.Kind switch
    {
        StockConfirmationResultKind.Completed => Completed(
            "completed",
            SummarizeChanges(result.Applied!),
            JsonSerializer.Serialize(new StockChangesPayload(1, "stock_changes", result.Applied!.Changes), PayloadOptions)),
        StockConfirmationResultKind.Rejected => Semantic(
            OutcomeCategory.Completed, "rejected", "That change was not made, and nothing was changed."),
        StockConfirmationResultKind.NotFound => Semantic(
            OutcomeCategory.NotFound, result.Code, "There is nothing waiting for your confirmation here."),
        StockConfirmationResultKind.Conflict => Semantic(OutcomeCategory.Conflict, result.Code, ConfirmationConflictSummary(result.Code)),
        StockConfirmationResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidConfirmationSummary(result.Code)),
        _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
    };

    private static ModelDecision ConfirmationRequired(StockProposalView proposal) => new()
    {
        Category = OutcomeCategory.ConfirmationRequired,
        Code = "confirmation_required",
        Summary = SummarizeProposal(proposal),
        Payload = JsonSerializer.Serialize(
            new StockProposalPayload(1, "stock_proposal", proposal.Token, proposal.ExpiresAt, proposal.Changes), PayloadOptions),

        // The proposal is the answer's channel-neutral content: the summary alone would lose the
        // exact effects the Participant is being asked to approve.
        Deliveries =
        [
            new RequestedDelivery(
                ResponseChannel,
                JsonSerializer.Serialize(
                    new StockProposalPayload(1, "stock_proposal", proposal.Token, proposal.ExpiresAt, proposal.Changes),
                    PayloadOptions)),
        ],
    };

    /// <summary>States exactly what will happen and what it costs in identity, then asks. It names no Inventory and no other Participant.</summary>
    private static string SummarizeProposal(StockProposalView proposal)
    {
        var lines = proposal.Changes.Select(DescribeChange).ToList();
        var opening = lines.Count == 1
            ? $"This needs your confirmation: {lines[0]}"
            : $"These {lines.Count} changes apply together, or not at all: {string.Join(" ", lines)}";

        return $"{opening} Reply with \"confirm {proposal.Token}\" to apply it, or \"reject\" to leave everything as it is.";
    }

    private static string SummarizeChanges(StockChangeSetView applied) =>
        string.Join(" ", applied.Changes.Select(DescribeChange));

    /// <summary>One change in plain words, always naming the surviving Stock Entry and any identity that ends.</summary>
    private static string DescribeChange(StockChangeView change)
    {
        var placement = change.Source.Location is null ? "unlocated" : $"in {change.Source.Location}";
        var destination = change.Destination?.Location is null ? "unlocated" : $"in {change.Destination.Location}";

        return change.Effect switch
        {
            "created" => $"Create {change.Source.Name} ({placement}) at {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_increased" => $"Add to {change.Source.Name} ({placement}): {change.Source.PreviousQuantity} becomes {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_decreased" => $"Remove from {change.Source.Name} ({placement}): {change.Source.PreviousQuantity} becomes {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_set" => $"Set {change.Source.Name} ({placement}) to {change.Source.Quantity} {change.Source.Unit}.",
            "quantity_cleared" => $"Clear {change.Source.Name} ({placement}), leaving 0 {change.Source.Unit} and an empty record.",
            "placed" => $"Move all {change.Source.Quantity} {change.Source.Unit} of {change.Source.Name} from {placement} to {destination}.",
            "split" => $"Move {change.TransferredQuantity} {change.Source.Unit} of {change.Source.Name} from {placement} to {destination}, leaving {change.Source.Quantity}.",
            "split_merged" => $"Move {change.TransferredQuantity} {change.Source.Unit} of {change.Source.Name} into the {change.Destination!.PreviousQuantity} already {destination}, leaving {change.Source.Quantity}.",
            "merged" => $"Merge all {change.TransferredQuantity} {change.Source.Unit} of {change.Source.Name} from {placement} into the entry {destination}, which becomes {change.Destination!.Quantity}. The {placement} entry is retired.",
            "renamed" => $"Rename {change.Source.Name} ({placement}) to {change.NewName ?? change.Source.Name}.",
            "rename_merged" => $"Merge {change.Source.Name} ({placement}) into {change.Destination!.Name}, which becomes {change.Destination.Quantity} {change.Destination.Unit}. The {change.Source.Name} entry is retired.",
            "forgotten" => $"Forget the empty {change.Source.Name} ({placement}) entry permanently.",
            _ => $"Change {change.Source.Name} ({placement}).",
        };
    }

    private static string ChangeSetConflictSummary(string code) => code switch
    {
        "insufficient_quantity" => "That is more than the Quantity on hand, so nothing was changed.",
        "forget_requires_zero_quantity" => "Only an empty Stock Entry can be forgotten. Remove or Set its Quantity to zero first.",
        "no_change" => "That would leave everything exactly as it is, so nothing was changed.",
        "state_changed" => "That Stock changed while this request was being prepared, so nothing was changed. Ask again.",
        _ => "That request conflicts with current Stock, so nothing was changed.",
    };

    private static string ConfirmationConflictSummary(string code) => code switch
    {
        "proposal_expired" => "That confirmation is older than ten minutes, so nothing was changed. Ask again to get a fresh one.",
        "state_changed" => "That Stock changed since this was proposed, so nothing was changed. Ask again.",
        _ => "That could no longer be applied, so nothing was changed.",
    };

    private static string InvalidConfirmationSummary(string code) => code switch
    {
        "confirmation_evidence_missing" => "Confirm in your own words - for example \"confirm\" followed by the code - and nothing else will do it for you.",
        "rejection_evidence_missing" => "Say so in your own words - for example \"reject\" - to leave everything as it is.",
        "proposal_token_mismatch" => "That confirmation code does not match what is waiting here, so nothing was changed.",
        _ => "That request could not be understood.",
    };

    private static string InvalidChangeSetSummary(string code) => code switch
    {
        "invalid_changes" => "State each change plainly - what to change, and by how much.",
        "too_many_changes" => $"Ask for at most {Domain.Inventories.ConfirmationProposal.MaxChanges} changes at a time.",
        "conflicting_changes" => "Two of those changes act on the same Stock Entry. Ask for them one at a time.",
        "invalid_destination" => "Name one destination: either a Location, or unlocated stock.",
        "invalid_quantity" => "State a Quantity as a plain decimal number, or ask to move all of it - not both.",
        "invalid_name" => $"A Stock Entry name must be 1 to {Domain.Inventories.StockEntry.MaxNameLength} characters.",
        "invalid_note" => $"A Note must not exceed {Domain.Inventories.StockEntry.MaxNoteLength} characters.",
        "invalid_reference" => "Name the Stock Entry to change.",
        "quantity_out_of_bounds" =>
            $"That Quantity is larger than an Inventory can record ({Domain.Inventories.Quantity.MaxIntegerDigits} digits "
            + $"before the decimal point and {Domain.Inventories.Quantity.MaxScale} after it).",
        _ => "That request could not be understood.",
    };

    /// <summary>The exact proposal a Participant is being asked to approve, versioned like every other payload.</summary>
    private sealed record StockProposalPayload(
        int Version, string Kind, string Token, string ExpiresAt, IReadOnlyList<StockChangeView> Changes);

    /// <summary>The typed read-back one applied change set leaves behind.</summary>
    private sealed record StockChangesPayload(int Version, string Kind, IReadOnlyList<StockChangeView> Changes);
```

Both new payload records carry only `StockChangeView`s, which carry only semantic fields - no concurrency stamp, no proposal identity, no audit identity, and no SQL detail ever reaches a payload.

- [ ] **Step 3: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~StockToolDispatcherTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs
git commit -m "feat(inventories): dispatch Move, Rename, Forget, batches, confirm, and reject for #32"
```

---

## Task 18: Recognize the new commands in the scripted grammar

**Files:**
- Modify: `src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs`

Why: the deterministic boundary is what an end-to-end scenario drives. It must be able to say all six mutations, a batch, a confirmation, and a rejection - while still parsing nothing but direct content and still supplying no identity.

- [ ] **Step 1: Write the failing tests**

Append to `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs`:

```csharp
    [Fact]
    public void Move_stock_to_a_Location_proposes_a_move_with_that_destination()

    [Fact]
    public void Move_stock_all_to_unlocated_proposes_a_move_to_the_unlocated_state()

    [Fact]
    public void Move_stock_with_an_amount_proposes_a_partial_move()

    [Fact]
    public void Rename_stock_proposes_the_exact_new_name()

    [Fact]
    public void Forget_stock_proposes_a_forget_for_that_reference()

    [Fact]
    public void Change_stock_proposes_one_batch_carrying_every_sub_command_in_order()

    [Fact]
    public void Confirm_with_a_code_proposes_the_confirmation_tool_carrying_only_that_code()

    [Fact]
    public void Reject_proposes_the_rejection_tool()

    [Fact]
    public void A_reference_containing_the_word_to_is_not_split_at_it()

    [Fact]
    public void A_change_stock_command_with_an_unrecognized_sub_command_is_not_recognized_at_all()
```

Anchors: each asserts `ModelProposalKind.ToolCall`, the exact tool name, and the exact untrusted argument dictionary. For example `move stock Steel Bolts quantity 3 to Shelf A` must produce `move_stock` with `reference="Steel Bolts"`, `quantity="3"`, `to="Shelf A"`; `move stock Steel Bolts all to unlocated` must produce `all="true"` and `toUnlocated="true"` with no `to`; `change stock: add Bolts quantity 2; forget Rivets` must produce `apply_stock_changes` whose `changes` argument parses (through `StockChangeSetParser`) into two requests with orders 1 and 2.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ScriptedModelBoundaryTests"`
Expected: FAIL - the new commands fall through to the echo behavior.

- [ ] **Step 2: Extend the clause grammar**

In `src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs`:

```csharp
    /// <summary>Clauses that stand alone; anything a caller writes after them belongs to the next clause.</summary>
    private static readonly string[] FlagClauses = ["including zero", "unlocated", "to unlocated", "all"];

    // "to unlocated" precedes "to" and "unlocated" so a destination of "nowhere in particular" is
    // read as its own flag rather than as a Location that happens to be called "unlocated".
    [GeneratedRegex(
        @"\b(including zero|to unlocated|unlocated|named|unit|in|page size|after|quantity|note|to|all)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClauseScanner { get; }
```

- [ ] **Step 3: Extend the scripted boundary**

In `src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs`:

Add the command constants and extend `MutationCommands` so the reference-command parser serves the three new single-change tools:

```csharp
    private const string MoveStockCommand = "move stock";
    private const string RenameStockCommand = "rename stock";
    private const string ForgetStockCommand = "forget stock";
    private const string ChangeStockCommand = "change stock";
    private const string ConfirmCommand = "confirm";
    private const string RejectCommand = "reject";

    private static readonly (string Command, string ToolName)[] MutationCommands =
    [
        (AddStockCommand, "add_stock"),
        (RemoveStockCommand, "remove_stock"),
        (SetStockCommand, "set_stock"),
        (MoveStockCommand, "move_stock"),
        (RenameStockCommand, "rename_stock"),
        (ForgetStockCommand, "forget_stock"),
    ];
```

Extend `FindFirstClauseIndex`'s clause list to `[" unit ", " in ", " unlocated", " quantity ", " note ", " to unlocated", " to ", " all"]`, and extend `TryProposeReferenceCommand`'s argument copying:

```csharp
        CopyFlag(clauses, "all", args, "all");
        CopyFlag(clauses, "to unlocated", args, "toUnlocated");
        CopyValue(clauses, "to", args, "to");
```

For a Rename the `to` clause is the new name, so `TryProposeReferenceCommand` takes the tool name into account for that one mapping: when `toolName == "rename_stock"`, copy the `to` clause into `newName` instead of `to`. Write it as an explicit conditional with a comment saying why - "rename stock X to Y" reads naturally, and the destination of a Rename is a name rather than a place.

Add, before the `find` arm in `ProposeAsync`:

```csharp
        if (TryProposeBatch(content, out var batchProposal))
        {
            return Task.FromResult(batchProposal!);
        }

        if (TryProposeConfirmation(content, out var confirmationProposal))
        {
            return Task.FromResult(confirmationProposal!);
        }
```

and these members:

```csharp
    /// <summary>
    /// Parses <c>change stock: &lt;sub&gt;; &lt;sub&gt;</c> into one batch tool call. Each
    /// sub-command is one of the six mutation verbs followed by the same reference-and-clauses shape
    /// a single-change command uses, so there is one grammar rather than two. A sub-command that is
    /// not recognized makes the whole command unrecognized: a partly understood batch is exactly what
    /// must never be proposed.
    /// </summary>
    private static bool TryProposeBatch(string content, out ModelProposal? proposal)
    {
        proposal = null;

        if (!StartsWithCommand(content, ChangeStockCommand, out var remainder))
        {
            return false;
        }

        var body = remainder.StartsWith(':') ? remainder[1..].Trim() : remainder;
        if (body.Length == 0)
        {
            return false;
        }

        var elements = new List<string>();
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseSubCommand(part, out var element))
            {
                return false;
            }

            elements.Add(element!);
        }

        if (elements.Count == 0)
        {
            return false;
        }

        proposal = ModelProposal.Tool("apply_stock_changes", new Dictionary<string, string>
        {
            ["changes"] = $"[{string.Join(",", elements)}]",
        });

        return true;
    }

    /// <summary>Turns one sub-command into one JSON object of untrusted string values, using the same clause grammar.</summary>
    private static bool TryParseSubCommand(string part, out string? element)
    {
        element = null;

        foreach (var (command, toolName) in MutationCommands)
        {
            // "add stock Bolts" and "add Bolts" both read naturally inside a batch, so the bare verb
            // is accepted as well as the full command.
            // Every mutation command ends in " stock", so the bare verb is what precedes it.
            var verb = command[..^" stock".Length];

            if (!TryProposeReferenceCommand(part, command, toolName, out var proposal)
                && !TryProposeReferenceCommand(part, verb, toolName, out proposal))
            {
                continue;
            }

            var kind = toolName[..toolName.IndexOf('_')];
            var properties = proposal!.ToolCall!.UntrustedArgs
                .Select(pair => $"{JsonSerializer.Serialize(pair.Key)}:{JsonSerializer.Serialize(pair.Value)}")
                .Prepend($"\"kind\":{JsonSerializer.Serialize(kind)}");

            element = $"{{{string.Join(",", properties)}}}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses <c>confirm &lt;code&gt;</c> and <c>reject [code]</c>. The code is the only thing carried
    /// through: whether the Participant actually confirmed is decided by the application from this
    /// Turn's own direct content, never from this proposal.
    /// </summary>
    private static bool TryProposeConfirmation(string content, out ModelProposal? proposal)
    {
        proposal = null;

        foreach (var (command, toolName) in ((string, string)[])
                 [(ConfirmCommand, "confirm_inventory_operation"), (RejectCommand, "reject_inventory_operation")])
        {
            if (!StartsWithCommand(content, command, out var remainder))
            {
                continue;
            }

            var args = new Dictionary<string, string>();
            var token = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (token is not null)
            {
                args["token"] = token;
            }

            proposal = ModelProposal.Tool(toolName, args);
            return true;
        }

        return false;
    }
```

Add `using System.Text.Json;` to the file. Note that `TryProposeReferenceCommand` currently requires a non-empty reference, which is exactly right for all six mutations and for both confirmation commands' *absence* from that path.

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ScriptedModelBoundaryTests"`
Expected: PASS. Adding `to` and `all` to the clause scanner changes how existing commands split, so run the whole Application suite too:

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS. If an existing `find`/`list stock` test now splits differently, the reference genuinely contained a clause word standing alone; fix `FindFirstClauseIndex`'s boundaries rather than weakening the test.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs
git commit -m "feat(turns): recognize move, rename, forget, batch, confirm, and reject commands for #32"
```

---

## Task 19: Sweep expired and settled proposals

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ConfirmationProposalCleanupCoordinator.cs`
- Create: `src/MultiChannelAgent.Host/Workers/ConfirmationProposalCleanupWorker.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalCleanupCoordinatorTests.cs`

Why: proposals are ephemeral workflow records, and the specification requires ephemeral records to have explicit expiry and scheduled cleanup. Without the sweep an expired proposal would occupy the one-pending-per-conversation slot forever, and settled rows would accumulate for the life of the database.

- [ ] **Step 1: Write the failing tests**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalCleanupCoordinatorTests.cs`:

```csharp
    [Fact]
    public async Task A_pass_expires_pending_proposals_whose_ten_minutes_have_run_out()

    [Fact]
    public async Task A_pass_deletes_settled_proposals_past_retention_and_keeps_newer_ones()

    [Fact]
    public async Task A_pass_that_cannot_take_the_lease_does_nothing()
```

Anchors: build the coordinator over `InMemoryConfirmationProposalStore`, `InMemoryLeaseCoordinator`, and a `FakeTimeProvider`; assert the returned counts and the resulting statuses; for the lease test, hold the lease first and assert the pass returns 0 and changed nothing.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ConfirmationProposalCleanupCoordinatorTests"`
Expected: FAIL to compile - the coordinator does not exist.

- [ ] **Step 2: Write the coordinator**

Create `src/MultiChannelAgent.Application/Inventories/ConfirmationProposalCleanupCoordinator.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Expires pending confirmation proposals whose ten minutes have run out, then discards settled ones
/// past retention.
///
/// Reading a proposal already enforces expiry, so this is not what makes an old confirmation safe -
/// it is what stops an expired proposal occupying the one-pending-per-conversation slot forever, and
/// what stops settled rows accumulating for the life of the database. Settled rows are kept briefly
/// on purpose: a confirmation that arrives moments after a rejection can then be answered truthfully
/// instead of as "unknown proposal".
///
/// Runs under its own exclusive lease, so several hosted replicas never duplicate the work, and
/// exposes a deterministic one-shot operation so tests can drive it without timing a background loop.
/// </summary>
public sealed class ConfirmationProposalCleanupCoordinator(
    IConfirmationProposalStore proposalStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<ConfirmationProposalCleanupCoordinator> logger)
{
    private const string LeaseName = "confirmation-proposal-cleanup";

    /// <summary>Bounds one pass so a large backlog is drained over several passes instead of one long transaction.</summary>
    private const int MaxBatchSize = 500;

    /// <summary>How long a settled proposal is retained, so a late answer can still be told what happened.</summary>
    public static readonly TimeSpan SettledRetention = TimeSpan.FromHours(24);

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var lease = await leaseCoordinator.TryAcquireAsync(
            LeaseName,
            ownerId: Guid.NewGuid().ToString("N"),
            duration: TimeSpan.FromSeconds(30),
            cancellationToken);

        if (lease is null)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var expired = await proposalStore.ExpirePendingBeforeAsync(now, MaxBatchSize, cancellationToken);
        var deleted = await proposalStore.DeleteSettledBeforeAsync(now - SettledRetention, MaxBatchSize, cancellationToken);

        if (expired > 0 || deleted > 0)
        {
            logger.LogInformation(
                "Expired {ExpiredCount} pending confirmation proposals and discarded {DeletedCount} settled ones.", expired, deleted);
        }

        return expired + deleted;
    }
}
```

- [ ] **Step 3: Write the worker**

Create `src/MultiChannelAgent.Host/Workers/ConfirmationProposalCleanupWorker.cs`, mirroring `OutcomePayloadCleanupWorker` exactly, with `Period = TimeSpan.FromMinutes(5)` and a summary explaining that a proposal's lifetime is ten minutes, so five-minute granularity keeps the pending slot free without polling.

Register it in `src/MultiChannelAgent.Host/Program.cs`, next to the other hosted services:

```csharp
builder.Services.AddHostedService<ConfirmationProposalCleanupWorker>();
```

and register the coordinator in `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`:

```csharp
        services.AddScoped<ConfirmationProposalCleanupCoordinator>();
```

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ConfirmationProposalCleanupCoordinatorTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ConfirmationProposalCleanupCoordinator.cs src/MultiChannelAgent.Host/Workers/ConfirmationProposalCleanupWorker.cs src/MultiChannelAgent.Host/Program.cs src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs tests/MultiChannelAgent.Application.Tests/Inventories/ConfirmationProposalCleanupCoordinatorTests.cs
git commit -m "feat(inventories): sweep expired and settled confirmation proposals for #32"
```

---

## Task 20: Show the exact proposal and what a confirmation did

**Files:**
- Modify: `src/web/src/turnsApi.ts`
- Modify: `src/web/src/TurnTracer.tsx`

Why: a proposal the Participant cannot read exactly is not a confirmation protocol. The web client must render every change, name the survivor and the retired source, show the expiry, and offer the exact commands - and it must keep refreshing the workspace after any terminal Outcome, which it already does.

- [ ] **Step 1: Add the payload types**

In `src/web/src/turnsApi.ts`, after `StockMutationPayload`:

```ts
/** One Stock Entry's before-and-after within a proposed or applied change. Quantities are exact decimal text, never numbers. */
export interface StockEntryStateView {
  stockEntryId: string | null;
  name: string;
  unit: string;
  location: string | null;
  note: string | null;
  previousQuantity: string;
  quantity: string;
  /** True when this Stock Entry's identity ends - merged away, or forgotten. */
  retired: boolean;
}

/** One change, exactly as proposed or exactly as applied. */
export interface StockChangeView {
  order: number;
  operation: 'add' | 'remove' | 'set' | 'move' | 'rename' | 'forget';
  effect:
    | 'created'
    | 'quantity_increased'
    | 'quantity_decreased'
    | 'quantity_set'
    | 'quantity_cleared'
    | 'placed'
    | 'split'
    | 'split_merged'
    | 'merged'
    | 'renamed'
    | 'rename_merged'
    | 'forgotten';
  source: StockEntryStateView;
  destination: StockEntryStateView | null;
  transferredQuantity: string;
  newName: string | null;
  /** The Stock Entry that still exists afterwards. */
  survivingStockEntryId: string | null;
  /** The Stock Entry whose identity this change ends, or null when it ends none. */
  retiredStockEntryId: string | null;
}

/**
 * An exact set of changes awaiting explicit confirmation. `token` is single-use and expires; it is
 * the only time the plaintext exists outside the server's own memory, and the server stores only its
 * hash.
 */
export interface StockProposalPayload {
  version: number;
  kind: 'stock_proposal';
  token: string;
  /** ISO-8601 round-trip instant, ten minutes after the proposal was made. */
  expiresAt: string;
  changes: StockChangeView[];
}

/** What one applied change set did. */
export interface StockChangesPayload {
  version: number;
  kind: 'stock_changes';
  changes: StockChangeView[];
}

export type TurnOutcomePayload =
  | StockListPayload
  | StockFindPayload
  | StockMutationPayload
  | StockProposalPayload
  | StockChangesPayload;
```

Replace the existing `TurnOutcomePayload` union with the one above (do not leave the old two-member union behind).

Also extend `SubmitTurnRequest` with the channel's interruption evidence:

```ts
export interface SubmitTurnRequest {
  nativeMessageId: string;
  contentText: string;
  locale?: string;
  traceId?: string;
  /**
   * Whether this utterance was cut off. The server treats an interrupted Turn as authorizing
   * nothing, and invalidates whatever confirmation was pending - a client can only ever use this to
   * make its own Turn less trusted.
   */
  interrupted?: boolean;
}
```

- [ ] **Step 2: Render them**

In `src/web/src/TurnTracer.tsx`:

Add a `StockChangeRows` component rendering a table of `StockChangeView`s with columns "Change", "Stock Entry", "Quantity", and "Identity", where:

- "Change" is the `effect` rendered as readable words.
- "Stock Entry" is `source.name` with its location (`source.location ?? 'Unlocated'`), and the destination on a second line when present.
- "Quantity" is `previousQuantity → quantity` for the source, and the same for the destination when present.
- "Identity" reads `Survives: <id>` and, when `retiredStockEntryId` is not null, `Retires: <id>` - so a merge always shows both.

Add a `StockProposal` component that renders `<h3>Confirm these changes</h3>`, the `StockChangeRows`, a line reading `Expires at {new Date(payload.expiresAt).toLocaleTimeString()}`, and two buttons that fill the input with `confirm {payload.token}` and `reject` respectively (they must *fill* the input rather than submit, so the Participant's own next Turn carries the affirmative in direct content - that is what the server requires).

Add a `StockChanges` component rendering `<h3>Applied</h3>` and the same `StockChangeRows`.

Extend the payload switch that already renders `stock_list`, `stock_find`, and `stock_mutation` with `stock_proposal` and `stock_changes` arms.

Add `'move stock Steel Bolts all to Shelf A'`, `'rename stock Steel Bolts to Brass Rivets'`, `'forget stock Steel Bolts'`, and `'change stock: add Steel Bolts quantity 2; forget Brass Rivets'` to whatever list of example commands the component already offers as hints.

- [ ] **Step 3: Verify**

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed. The discriminated union means a missing arm is a TypeScript error, which is exactly the check that matters here.

- [ ] **Step 4: Commit**

```bash
git add src/web/src/turnsApi.ts src/web/src/TurnTracer.tsx
git commit -m "feat(web): show an exact confirmation proposal and what confirming it did for #32"
```

---

## Task 21: Prove the whole protocol end to end

**Files:**
- Create: `tests/MultiChannelAgent.IntegrationTests/ConfirmedStockMutationScenario.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/ConfirmedStockMutationSqliteTests.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/ConfirmationExpirySqliteTests.cs`
- Modify: `tests/MultiChannelAgent.IntegrationTests/SqliteWebApplicationFactory.cs`
- Modify: `tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs`

Why: the highest required correctness seam is one SQL-backed application-boundary suite. Everything above is proved in pieces; this proves the pieces are wired into the real HTTP application and behave as one protocol.

- [ ] **Step 1: Let the Docker-free factory control time**

In `tests/MultiChannelAgent.IntegrationTests/SqliteWebApplicationFactory.cs`, add an optional constructor parameter and register it last so it wins over the infrastructure's `TimeProvider.System`:

```csharp
    private readonly TimeProvider? _timeProvider;

    /// <summary>
    /// <paramref name="timeProvider"/> lets a scenario advance time deliberately - the ten-minute
    /// confirmation lifetime is behavior, and a test that proved it by sleeping would be both slow
    /// and flaky.
    /// </summary>
    public SqliteWebApplicationFactory(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();
    }
```

and at the end of `ConfigureServices`:

```csharp
            if (_timeProvider is not null)
            {
                services.AddSingleton(_timeProvider);
            }
```

- [ ] **Step 2: Write the scenario**

Create `tests/MultiChannelAgent.IntegrationTests/ConfirmedStockMutationScenario.cs` as an `internal static class` with `RunAsync(WebApplicationFactory<Program> factory)`, modelled exactly on `StockMutationScenario` (same `ConversationTestClient`, same `OutcomeAsync`/`CompleteAsync`/`ProcessPendingAsync`/`AssertProjectionAsync` helpers, same audit and ledger counting through the `DbContext`). It must walk this exact sequence and assert at every step:

1. Sign in an Owner, create and select an Inventory, seed `Shelf A` and `Shelf B` Locations directly, and add `Steel Bolts` unlocated at `10` conversationally.
2. **Partial Move applies immediately.** `move stock Steel Bolts quantity 3 to Shelf A` completes; the payload kind is `stock_changes`; the effect is `split`; the unlocated entry reads `7` and `Shelf A` reads `3`; the projection agrees; exactly one `StockMoved` audit fact exists.
3. **Merge-retiring Move proposes.** `move stock Steel Bolts unlocated all to Shelf A` returns `confirmation_required` with a `stock_proposal` payload whose single change has effect `merged`, whose `survivingStockEntryId` is the `Shelf A` entry and whose `retiredStockEntryId` is the unlocated one; **Stock is unchanged** and no new audit fact exists.
4. **Rejecting changes nothing.** `reject` completes with code `rejected`; Stock is still `7` and `3`; nothing is pending.
5. **Confirming executes exactly once.** Re-propose the same Move, then `confirm <token>`: it completes with a `stock_changes` payload naming the same survivor and retired source; `Shelf A` now reads `10`; the unlocated entry is gone; the projection agrees; exactly one new `StockMoved` audit fact and exactly one new change-set ledger header exist.
6. **A used token is used.** Submitting `confirm <token>` again as a *new* native message answers `proposal_not_found` and changes nothing.
7. **Rename preserves identity.** `rename stock Steel Bolts to Brass Rivets` completes immediately with effect `renamed`, the same Stock Entry id survives, and the projection shows the new name.
8. **Rename collision proposes and merges.** Add `Brass Rivets` unlocated at `2`, then `rename stock Brass Rivets unlocated to Steel Bolts`... instead, seed a second entry so a collision genuinely exists at the same Unit and Location, propose the colliding Rename, confirm it, and assert the survivor and the retired source in both the proposal and the applied payload.
9. **Forget is refused while Stock is on hand.** `forget stock Brass Rivets` answers `conflict` with code `forget_requires_zero_quantity`.
10. **Forget is confirmed for an empty entry.** Set that entry to zero (which itself proposes, then confirm), then `forget stock ...` proposes, then confirm: the Stock Entry is gone from `StockEntries` and from the projection, and a `StockForgotten` audit fact exists.
11. **Every batch proposes.** `change stock: add Copper Nails quantity 4; add Zinc Screws quantity 5` returns `confirmation_required` with two changes; confirming applies both; the projection shows both; exactly two new audit facts and exactly one new ledger header exist.
12. **A failed batch changes nothing.** Propose a two-change batch, mutate one of its targets directly through the `DbContext` (changing its `ConcurrencyStamp`), then confirm: the answer is `conflict` with code `state_changed`, **neither** change was applied, and no new audit fact exists.
13. **Replacement invalidates.** Propose a Forget, then propose a batch, then `confirm <first token>`: the answer is `proposal_token_mismatch` and nothing is applied.
14. **A model-invented confirmation confirms nothing.** Submit a Turn whose direct content is `list stock` but which the scripted boundary answers normally, then assert that no pending proposal was consumed. Then submit `confirm <token>` with the Turn marked interrupted via `SubmitInterruptedTurnAsync`: the proposal is invalidated and the answer names no Stock.
15. **An Inventory switch invalidates.** Propose a Forget, create and select a second Inventory, switch back, then `confirm <token>`: the answer is `proposal_not_found` and nothing is applied.
16. **A Viewer may not confirm.** Grant a second Participant Viewer, have them submit `confirm <token>`: `forbidden`, and nothing changes.
17. **Retries return recorded outcomes.** Resubmit the confirming native message id: the recorded Outcome comes straight back, `ProcessPendingAsync` returns 0, and neither the audit count nor the ledger count moves.

Every assertion about "nothing changed" must check all three of: the Stock rows, the audit count, and the change-set ledger count.

- [ ] **Step 3: Run it through the Docker-free twin**

Create `tests/MultiChannelAgent.IntegrationTests/ConfirmedStockMutationSqliteTests.cs`, modelled exactly on `StockMutationSqliteTests`:

```csharp
namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The confirmed stock mutation protocol against SQLite: the same externally observable behavior as
/// <see cref="StockConversationScenarioTests"/> proves against SQL Server, with no Docker needed.
/// </summary>
public sealed class ConfirmedStockMutationSqliteTests : IAsyncLifetime
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
    public async Task Moving_renaming_forgetting_and_confirming_stock_behaves_exactly_as_specified() =>
        await ConfirmedStockMutationScenario.RunAsync(_factory!);
}
```

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ConfirmedStockMutationSqliteTests"`
Expected: FAIL on the first assertion the pipeline does not yet satisfy. If every earlier task landed correctly it may instead PASS on the first run - that is a legitimate outcome for an integration scenario written after its units, and nothing needs to be manufactured to make it go red. Never weaken an assertion to make this test pass.

Most likely fixes, in order of likelihood:
1. The scripted grammar splits `move stock X all to Shelf A` differently than expected - check `FindFirstClauseIndex`'s handling of `" to "` versus `" to unlocated"` (Task 18).
2. `StockChangeSetService` or `StockConfirmationService` is not registered in DI (Task 15 Step 5).
3. The `stock_changes`/`stock_proposal` payloads do not round-trip through `Outcome.Payload`'s length bound - check `OutcomeEntityConfiguration` and, if a large batch payload genuinely exceeds it, reduce the scenario's batch size rather than widening the column.

- [ ] **Step 4: Prove the ten-minute lifetime on controlled time**

Create `tests/MultiChannelAgent.IntegrationTests/ConfirmationExpirySqliteTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The ten-minute confirmation lifetime, end to end, on controlled time - so it is proved as
/// behavior rather than asserted on a domain type alone, and without a test ever sleeping.
/// </summary>
public sealed class ConfirmationExpirySqliteTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
    private SqliteWebApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory(_time);
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
    public async Task A_confirmation_older_than_ten_minutes_executes_nothing()
    {
        await ConfirmationExpiryScenario.RunAsync(_factory!, _time);
    }
}
```

Add `ConfirmationExpiryScenario` alongside it (or as a private static method in this file): sign in, create and select an Inventory, add stock, propose a Forget of an emptied entry, advance `_time` by 10 minutes and 1 second, submit `confirm <token>`, and assert the Outcome category is `conflict` with code `proposal_expired`, that the Stock Entry still exists, and that no change-set ledger row was written. Then advance a further 5 minutes, drive `ConfirmationProposalCleanupCoordinator.SweepAsync` from a scope, and assert the proposal row's status is `Expired`.

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ConfirmationExpirySqliteTests"`
Expected: PASS, 1 test.

- [ ] **Step 5: Run the same scenario against SQL Server with production migrations**

In `tests/MultiChannelAgent.IntegrationTests/StockConversationScenarioTests.cs`, append to the class:

```csharp
    // Every confirmed stock mutation acceptance criterion for #32, end to end against real SQL Server
    // with production migrations applied. ConfirmedStockMutationSqliteTests proves the identical
    // behavior Docker-free.
    [SkippableFact]
    public async Task Moving_renaming_forgetting_and_confirming_stock_behaves_exactly_as_specified()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed confirmed mutation scenario.");

        await ConfirmedStockMutationScenario.RunAsync(Factory!);
    }
```

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~StockConversationScenarioTests"`
Expected: PASS. This takes several minutes: it pulls and starts the SQL Server image and applies every production migration.

If Docker is genuinely unavailable in this environment, run without the environment variable, confirm the SQL-backed tests report as skipped rather than failed, and state plainly in the commit that SQL Server coverage was not executed locally and CI will gate it.

- [ ] **Step 6: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests
git commit -m "test(integration): confirm stock mutations through a web conversation end to end for #32"
```

---

## Task 22: Whole-suite verification

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
grep -c "ConfirmationProposals\|StockChangeSetOperations\|StockChangeSetEffects\|WasInterrupted" ./migrations-check.sql
rm ./migrations-check.sql
```
Expected: the script generates and the grep finds the four new objects.

- [ ] **Step 4: Build and lint the web client**

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed.

- [ ] **Step 5: Confirm the architecture boundaries still hold**

Run: `dotnet test tests/MultiChannelAgent.ArchitectureTests/MultiChannelAgent.ArchitectureTests.csproj`
Expected: PASS. `ConfirmationProposal`, `ConfirmationToken`, and `StockChangePlan` live in Domain and reference only Domain; `StockChangeResolver`, `StockChangeSetService`, `StockConfirmationService`, `ConfirmationProposalLifecycle`, and both new store seams live in Application and must reference nothing from Infrastructure.

- [ ] **Step 6: Scan the diff for anything unfinished**

Run:
```bash
git diff --stat origin/copilot/implementation-base...HEAD
git diff origin/copilot/implementation-base...HEAD | grep -nE "TODO|FIXME|XXX|NotImplementedException|placeholder" || echo "clean"
```
Expected: `clean`. Any hit is a task that was not actually finished; finish it rather than deleting the marker.

- [ ] **Step 7: Commit any fixes**

```bash
git add -A
git commit -m "fix(inventories): settle the whole suite for confirmed stock mutations for #32"
```

If nothing needed fixing, skip this commit rather than creating an empty one.

---

## Acceptance criteria coverage

| Acceptance criterion | Where it is implemented | Where it is proven |
| --- | --- | --- |
| `move_stock` transfers a positive Quantity to a Location | Task 2 (`ForMove` partial), Task 11 (destination resolution), Task 17 (`move_stock`) | `StockChangePlanTests.Moving_part_of_it_somewhere_empty_splits...`, `StockChangeResolverTests.Moving_part_of_a_Stock_Entry...`, `SqlStockChangeSetStoreTests`, `ConfirmedStockMutationScenario` step 2 |
| `move_stock` transfers all to a Location | Task 2 (`Placed`/`Merged`), Task 11, Task 17 | `StockChangePlanTests.Moving_all_of_it_somewhere_empty...`, `StockChangeResolverTests.Moving_all_of_a_Stock_Entry_to_an_empty_Location...`, scenario steps 3 and 5 |
| `move_stock` transfers to the unlocated state | Task 11 (`DestinationUnlocated`), Task 18 (`to unlocated` clause) | `StockChangeResolverTests.Moving_Stock_to_the_unlocated_state...`, `ScriptedModelBoundaryTests.Move_stock_all_to_unlocated...` |
| `move_stock` merges Equivalent Stock deterministically | Task 2 (`Merged`/`SplitMerged`), Task 11 (`FindEquivalentAsync`), Task 15 (executor) | `StockChangePlanTests.Moving_all_of_it_into_Equivalent_Stock...`, `StockChangeResolverTests.Moving_all_of_a_Stock_Entry_into_Equivalent_Stock...`, `SqlStockChangeSetStoreTests.A_merge_retiring_Move...`, scenario step 5 |
| `rename_stock` preserves identity | Task 2 (`Renamed`), Task 15 (`Renamed` writes only the name) | `StockChangePlanTests.Renaming_without_a_collision...`, `StockChangeResolverTests.Renaming_without_a_collision...`, scenario step 7 |
| `rename_stock` merges only a confirmed collision | Task 1 (`RequiresConfirmation`), Task 2 (`RenameMerged`), Task 12 | `StockChangeSetServiceTests.A_lone_merge_retiring_Rename_is_proposed...`, `SqlStockChangeSetStoreTests.A_Rename_collision_merges...`, scenario step 8 |
| `rename_stock` reports survivor and retired source | Task 4 (`SurvivingStockEntryId`/`RetiredStockEntryId`), Task 9 (recorded effect), Task 17 (payload), Task 20 (rendering) | `ConfirmationProposalTests.A_proposal_reports_the_survivor...`, `StockChangeResolverTests.Renaming_into_Equivalent_Stock...`, scenario step 8 |
| `forget_stock` requires confirmation | Task 1 (`RequiresConfirmation`), Task 12 | `StockChangeSetServiceTests.A_Forget_is_proposed_rather_than_applied`, `StockToolDispatcherTests.Forget_stock_always_answers_confirmation_required`, scenario step 10 |
| `forget_stock` succeeds only for zero Quantity | Task 2 (`ForForget`), Task 11 | `StockChangePlanTests.Forgetting_Stock_that_is_still_on_hand...`, `StockChangeResolverTests.Forgetting_Stock_that_is_still_on_hand...`, scenario step 9 |
| Every multi-change batch produces an exact proposal | Task 12 (`changes.Count > 1`) | `StockChangeSetServiceTests.Every_batch_of_more_than_one_change_is_proposed...`, scenario step 11 |
| Set to zero produces an exact proposal | Task 2 (`QuantityCleared`), Task 1 (`RequiresConfirmation`) | `StockChangePlanTests.Setting_to_zero_plans_to_clear...`, `StockChangeSetServiceTests.A_Set_to_zero_is_proposed...`, scenario step 10 |
| Forget and merge-retiring Move/Rename produce an exact proposal | Task 1 (`RetiresSource`), Task 12 | `StockChangeSetServiceTests` proposal tests, scenario steps 3, 8, 10 |
| A proposal is *exact* | Task 4 (`ProposedChange` contents), Task 14 (`ConfirmationProposalMapper`) | `SqlConfirmationProposalStoreTests.A_stored_proposal_round_trips_every_exact_effect...`, `StockChangeSetServiceTests.A_proposal_carries_the_exact_effects...` |
| One pending proposal per Participant and ChannelConversation | Task 8 (seam), Task 14 (filtered unique index) | `InMemoryConfirmationProposalStoreTests`, `SqlConfirmationProposalStoreTests.A_second_pending_proposal_for_one_conversation_cannot_exist_at_all` |
| Stored with a ten-minute single-use token | Task 3 (token), Task 4 (`LifetimeMinutes`), Task 14 (guarded settle) | `ConfirmationTokenTests`, `ConfirmationProposalTests.A_proposal_expires_exactly_ten_minutes...`, `SqlConfirmationProposalStoreTests.Only_the_first_of_two_concurrent_settles_wins`, `ConfirmationExpirySqliteTests` |
| Stored with expected versions | Task 4, Task 7 (`ReadVersionsAsync`), Task 11 | `ConfirmationProposalTests.Every_existing_Stock_Entry...must_carry_an_expected_version`, `StockChangeSetServiceTests.A_proposal_pins_the_version...`, `SqlStockChangeSetStoreTests.A_change_set_whose_expected_version_moved...` |
| Direct explicit confirmation executes the proposal atomically | Task 6 (evidence), Task 13, Task 15 (one transaction) | `DirectConfirmationEvidenceTests`, `StockConfirmationServiceTests.Direct_explicit_confirmation_executes...`, `SqlStockChangeSetStoreTests.Consuming_a_proposal_and_applying_it_happen_in_one_transaction`, scenario step 5 |
| Rejection invalidates | Task 13 (`RejectAsync`) | `StockConfirmationServiceTests.Direct_explicit_rejection...`, scenario step 4 |
| Replacement invalidates | Task 8/14 (`StoreAsync` supersedes) | `InMemoryConfirmationProposalStoreTests.Storing_a_new_proposal_supersedes...`, `SqlConfirmationProposalStoreTests.Storing_a_replacement_supersedes...`, scenario step 13 |
| Access loss invalidates | Task 16 (`AccessLost`) | `ConfirmationProposalLifecycleTests.Losing_access...` |
| Inventory switch invalidates | Task 13 (binding), Task 16 (`InventorySwitched`, selection hook) | `ConfirmationProposalLifecycleTests.A_Turn_whose_Active_Inventory_is_now_a_different_one...`, `InventorySelectionServiceTests.Switching_the_Active_Inventory...`, scenario step 15 |
| Expiry invalidates | Task 4, Task 13, Task 19 | `StockConfirmationServiceTests.Confirming_after_ten_minutes...`, `ConfirmationExpirySqliteTests` |
| Interruption invalidates | Task 5 (channel contract), Task 6 (evidence), Task 16 (`Interrupted`) | `DirectConfirmationEvidenceTests.An_interrupted_utterance_never_confirms...`, `ConfirmationProposalLifecycleTests.An_interrupted_Turn...`, scenario step 14 |
| Conflict invalidates | Task 13 (`Conflicted`), Task 15 (rollback) | `StockConfirmationServiceTests.A_proposal_whose_Stock_moved_underneath_it...`, `SqlStockChangeSetStoreTests.A_change_set_whose_expected_version_moved...`, scenario step 12 |
| A failed atomic batch changes nothing | Task 15 (explicit transaction, rollback on every guard) | `SqlStockChangeSetStoreTests` conflict tests, scenario step 12 |
| Retries return recorded outcomes and never replan | Task 4 (`DeriveForProposal`), Task 9 (`FindRecordedByTurnAsync`), Tasks 12 and 13 (replay first), Task 15 (unique replay index) | `StockOperationIdTests`, `StockChangeSetServiceTests.A_Turn_that_already_applied...`, `StockConfirmationServiceTests.A_Turn_that_already_executed...`, `SqlStockChangeSetStoreTests.Applying_the_same_operation_identity_again...`, scenario step 17 |
| Non-disclosure preserved | Task 8 (lookup by Participant + conversation), Task 13 (generic codes), Task 17 (summaries) | `StockConfirmationServiceTests` (`Confirming_a_proposal_bound_to_another_Inventory...`, `Confirming_from_another_conversation...`), `StockToolDispatcherTests.A_proposal_payload_never_carries...`, scenario step 16 |
| Exact quantity preserved | Task 2 (delegates to `Quantity`), Task 14 (invariant text in JSON), Task 15 (`decimal(28,10)`) | `StockChangePlanTests`, `SqlConfirmationProposalStoreTests` round-trip, scenario quantity assertions |
| Minimal audits preserved | Task 1 (`OutcomeCodeFor`), Task 15 (one fact per change) | `StockMutationPlanTests` audit-code theory, `SqlStockChangeSetStoreTests.Applying_a_change_set_writes_its_state_changes_audits_and_ledger_together`, scenario audit counts |
| Optimistic concurrency preserved | Task 7, Task 15 (lock-and-verify pass) | `SqlStockChangeSetStoreTests.Two_concurrent_confirmations...` |
| FIFO, semantic Outcome/Delivery preserved | unchanged spine; Task 16 only adds a reconcile step before the model call | `PerConversationFifoSqliteTests`, `StockReadDeliverySqliteTests`, `TurnProcessingCoordinatorTests` |
| Web projection refresh preserved | Task 20 (`onTerminalOutcome` already refetches) | `ConfirmedStockMutationScenario.AssertProjectionAsync` after every applied change; `npm run build` type-checks the payload union |

## Deliberate design decisions worth knowing

- **The confirmation policy is one predicate.** `StockAuditFacts.RequiresConfirmation(effect)`, plus "more than one change", is the entire rule. Nothing else in the codebase decides whether something is risky, so the tool layer, the service, and the store cannot drift apart.
- **The proposal is executable, not descriptive.** It carries resolved identities, Units, Locations, Notes, and exact amounts. Confirmation therefore never re-resolves a reference and never recomputes against newer state - what the Participant reviewed is literally what commits, which is what makes "exact" mean something.
- **The token is hashed, and lookup is not by token.** The plaintext exists once, in the answer. Lookup is by Participant and ChannelConversation, so a token from another conversation cannot even be looked up. Non-disclosure is structural.
- **A wrong token does not burn the proposal.** The token is 256 bits, so there is no brute-force attack to defend against, and destroying a Participant's own pending work because they mistyped would be a worse failure than the one it prevents.
- **Replay is keyed by the Turn, not by the operation identity.** Confirming consumes the proposal, so by replay time the identity's source is gone. `FindRecordedByTurnAsync` needs nothing but trusted context, which is exactly what a re-driven Turn still has.
- **Two ledgers, not one.** `#31`'s single-mutation ledger is untouched. Rewriting shipped, proven behavior to share the batch writer would risk a working acceptance criterion for no behavioral gain, and the two identity derivations cannot collide.
- **Locking is a two-pass affair.** Pass one takes every row's exclusive lock in one globally agreed order while verifying its expected version; pass two applies effects under those locks. That is what makes concurrent overlapping batches contend predictably instead of deadlocking.
- **Interruption is a channel fact.** The core cannot infer it, so it is carried on the Turn from the adapter, persisted, and honored by both the evidence reader and the lifecycle. The voice adapter later has one flag to set and nothing to re-argue.
- **A batch refuses whole.** One unresolvable change refuses the set, and two changes naming one Stock Entry refuse it too. Both are cases where a partial or re-derived answer would describe a state that never exists.
- **No budgets.** Nothing in this plan adds a cost ceiling, spend threshold, or quota check; the parent spec puts all of them out of scope, and a "safety" limit added here would be a behavior nobody asked for.
