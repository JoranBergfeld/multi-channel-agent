# Unit and Location Administration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #33 by letting authorized Participants explicitly administer Inventory-owned Units and flat Locations through exactly ten conversational tools - `list_units`, `create_units`, `rename_units`, `add_unit_aliases`, `remove_unit_aliases`, `retire_units`, `list_locations`, `create_locations`, `rename_locations`, `retire_locations` - under trusted context, with one collision-free normalized Unit term namespace, an immutable reserved `each` Unit, flat unique alias-free Locations, identity-preserving renames that never rewrite Stock Entries or Equivalent Stock, Owner-only confirmed Retire that fails while a Stock Entry references the target and invalidates every pending proposal that references it, minimal semantic audit facts for every outcome and denial, and bounded deterministic suggestions for unknown references.

**Architecture:** The spine shipped by #28/#30/#31/#32 is unchanged - `InboundTurn -> TurnAcceptanceService -> TurnProcessingCoordinator -> TurnExecutionContextFactory -> IToolDispatcher -> deterministic Application service -> Outcome/Delivery`. This plan extends exactly that spine and adds nothing parallel to it. Four pure Domain additions carry the new rules: a `ReferenceChangeKind` vocabulary that is simultaneously the effect vocabulary, the audit vocabulary, the confirmation policy, and the role matrix; retirement, bounds, and a stable identity on `Unit` and `Location`; one pure `ReferenceChangePlan` that decides every reference change from current state alone; and a bounded deterministic `ReferenceListQuery`/`ReferenceListCursor` for the two read tools. `ConfirmationProposal` gains a second *payload* - reference changes with their own expected versions and expected term absences - without relaxing one stock invariant: the shipped `Create` factory keeps every rule it has, and a new `CreateForReferences` factory carries the parallel ones. Two new Application store seams carry the state: `IReferenceCatalogStore` (active-only catalog reads, versions, stock-reference counts, and bounded suggestions) and `IReferenceAdministrationStore` (one atomic writer that consumes the proposal, verifies expected versions and term absences, re-checks every Retire against current Stock Entries under `Serializable` isolation, applies every change, appends one minimal semantic audit fact per change, writes its own ledger, and settles every pending proposal that references a retired identity - all in one transaction, or changes nothing). Three new Application services compose them: `ReferenceChangeResolver`, `ReferenceAdministrationService`, and `ReferenceListingService`. `StockConfirmationService` becomes `InventoryConfirmationService` and routes by proposal kind, enforcing Owner for any proposal carrying a Retire. A new `InventoryToolRouter` is the single registered `IToolDispatcher` and routes tool names to `StockToolDispatcher` or the new `ReferenceToolDispatcher`. Identity never comes from the model: Participant, ChannelConversation, Inventory, Turn, confirmation evidence, role, and every operation identity come from `TurnExecutionContext` and the stored proposal.

**Tech Stack:** C#/.NET 10, EF Core 10 (SQL Server provider in production, SQLite for Docker-free relational tests), xUnit 2.9, `Xunit.SkippableFact`, `Microsoft.Extensions.TimeProvider.Testing` for deterministic expiry, Testcontainers `MsSql` for the SQL-backed application-boundary suite, React 19 + TypeScript + Vite + oxlint for the web client.

---

## Scope and non-goals

In scope (issue #33 acceptance criteria, verbatim):

1. Viewer can list active Units and Locations; Owner and Editor can create, rename, and manage Unit aliases; only Owner can Retire.
2. All ten specified Unit and Location tools use trusted Inventory context, exact IDs or names, homogeneous atomic change arrays, and existing typed statuses.
3. Unit names and aliases share one collision-free normalized namespace and the reserved `each` Unit and fixed aliases cannot be renamed, retired, removed, or reassigned.
4. Location names remain flat, unique, and alias-free; unlocated remains absence of a reference.
5. Rename preserves stable identities and does not rewrite Stock Entries or alter Equivalent Stock.
6. Confirmed Retire fails for currently referenced data, preserves the retired identity, and invalidates pending proposals that reference it.
7. Administration outcomes and denials produce the specified semantic audit facts and unknown references provide bounded deterministic suggestions.

Preserved from #28/#30/#31/#32 and never regressed by this plan: trusted context injection, non-disclosing refusals, per-ChannelConversation FIFO, semantic Outcome separated from Delivery, exact invariant-decimal Quantity, the operation-ledger replay rule, minimal semantic audits, optimistic concurrency, one pending proposal per Participant and ChannelConversation, the ten-minute single-use confirmation token, and web projection refresh after any change.

Explicitly **out of scope** for this slice:

- **Monetary budgets, spend thresholds, chargeback, cost ceilings, and quota purchase.** The parent spec (#26) puts every one of these out of scope. **No task in this plan may add a cost check, a spend ceiling, or a budget policy of any kind.**
- **Initial Import (#35) and any CSV parsing, channel adapter, or transport work.** No task in this plan touches Teams, email, Graph, voice, or import. Import will later accept active Unit terms and active Location names through the very seams this plan makes active-only; nothing here anticipates it further.
- Unit conversion, dimensions, packaging relations, Unit-specific precision, and pluralization. `NameNormalization` still only folds case and collapses whitespace, so `box` and `boxes` remain distinct terms unless an alias explicitly says otherwise.
- Fuzzy matching. The bounded suggestions this plan adds are exact-prefix and order-based only; no edit distance, phonetics, or ranking heuristic is introduced.
- Hierarchical or aliased Locations. Locations stay flat, alias-free, and unique.
- Reference merge, reassignment, restore, un-retire, tenant catalogs, bulk reference import, and a separate reference CRUD UI. A retired reference is never revived and never absorbs another.
- Cascading or implicit stock reassignment. Retire never rewrites, moves, or deletes a Stock Entry - it refuses instead.
- Foundry-backed model integration and multi-tool-call agent runs. One Turn still dispatches exactly one tool call.

---

## File responsibility map

### Domain (`src/MultiChannelAgent.Domain/`)

| File | Responsibility |
| --- | --- |
| `Inventories/ReferenceAdministration.cs` (create) | `ReferenceKind` (Unit/Location), `ReferenceChangeKind` (the eight mutating operations), and `ReferenceAdministrationFacts`: machine text, strict parsing, the audit event type and outcome code per kind, `RequiresConfirmation`, and `RequiredRole`. This one vocabulary *is* the effect vocabulary, the audit vocabulary, the confirmation policy, and the role matrix, so none of them can drift apart. |
| `Inventories/ReferenceOperationId.cs` (create) | The retry-stable ledger identity of one reference administration execution, derived from the Turn and tool, or from the proposal. Its hash material is shaped so it can never collide with a `StockOperationId`. |
| `Inventories/Unit.cs` (modify) | `MaxNameLength`, `UnitTerm`, `RetiredAt`/`IsActive`, `ReservedEachCanonicalName`, `IsReservedEachTerm`, and a validating `Create` factory for a non-reserved Unit. |
| `Inventories/Location.cs` (modify) | `RetiredAt`/`IsActive`. |
| `Inventories/AuditFact.cs` (modify) | `AuditEventType` gains `UnitCreated`, `UnitRenamed`, `UnitAliasAdded`, `UnitAliasRemoved`, `UnitRetired`, `LocationCreated`, `LocationRenamed`, and `LocationRetired`. No new denial vocabulary: denials keep using the shipped `AccessDenied` fact. |
| `Inventories/ReferenceChangePlan.cs` (create) | The single pure planner for every reference change, decided from current state alone. Reads and writes nothing. |
| `Inventories/ReferenceListQuery.cs` (create) | `ReferenceListQuery` (bounded page size, decoded cursor) and `ReferenceOrderKey` - the deterministic `(NormalizedName, Id)` ordering both catalog lists share. |
| `Inventories/ReferenceListCursor.cs` (create) | The opaque base64url keyset cursor, refused by a list of a different kind or version. |
| `Inventories/ConfirmationProposal.cs` (modify) | `ProposalKind`, `ProposedReferenceState`, `ProposedReferenceChange`, `ExpectedReferenceVersion`, `ExpectedTermAbsence`, the `CreateForReferences` factory, `RequiredRole`, `ReferenceExecutionOperationId`, and the derived `ReferencedUnitIds`/`ReferencedLocationIds` that make retire-driven invalidation exact. The shipped stock `Create` factory keeps every invariant it already enforces. |

### Application (`src/MultiChannelAgent.Application/`)

| File | Responsibility |
| --- | --- |
| `Inventories/IInventoryReferenceStore.cs` (modify) | Documents that resolution is **active-only**: a retired Unit or Location resolves to nothing, exactly like one that never existed. |
| `Inventories/IReferenceCatalogStore.cs` (create) | The administration read seam: list active Units (with their active aliases) and active Locations in bounded deterministic pages, resolve a reference *for administration* (returning reserved/retired state and the term set), read expected versions, count Stock Entries referencing a Unit or Location, and produce bounded deterministic suggestions. |
| `Inventories/IReferenceAdministrationStore.cs` (create) | The one atomic writer: `ReferenceChangeSetCommand`, `RecordedReferenceChange`, `RecordedReferenceChangeSet`, `ReferenceAdministrationStoreOutcome`, and the replay lookups. |
| `Inventories/ReferenceChangeSetParser.cs` (create) | `ReferenceChangeRequest` plus the strict per-tool parser for the untrusted homogeneous `changes` array: a closed property set per kind, string values only, non-empty, bounded, and every element the same kind. |
| `Inventories/ReferenceChangeResolver.cs` (create) | Turns one untrusted `ReferenceChangeRequest` into one exactly-decided `ProposedReferenceChange` (with its expected versions and expected term absences) or one typed refusal carrying bounded suggestions. |
| `Inventories/ReferenceAdministrationService.cs` (create) | Authorizes the role the change kinds demand, answers a replay from the ledger before re-planning anything, resolves every change, refuses a heterogeneous or self-colliding set, and then either applies a lone non-destructive change immediately or stores an exact proposal and hands back its one-time token. Owns `ReferenceChangeView`, `ReferenceChangeSetView`, and `ReferenceProposalView`. |
| `Inventories/ReferenceListingService.cs` (create) | The Viewer-authorized catalog reads behind `list_units`, `list_locations`, and the two web projection endpoints. Owns `UnitView`, `LocationView`, `UnitListView`, and `LocationListView`. |
| `Inventories/StockConfirmationService.cs` -> `Inventories/InventoryConfirmationService.cs` (rename + modify) | Confirms or rejects the one pending proposal of *either* kind. Requires Owner before executing any proposal carrying a Retire, asks both ledgers for a replay, and never re-resolves or re-plans anything. |
| `Inventories/ReferenceToolDispatcher.cs` (create) | Executes the ten Unit and Location tool calls under trusted `TurnExecutionContext`, and shapes the `unit_list`, `location_list`, `reference_changes`, `reference_proposal`, and `reference_suggestions` payloads. |
| `Inventories/InventoryToolRouter.cs` (create) | The single registered `IToolDispatcher`. Routes a tool name to the stock dispatcher or the reference dispatcher by an explicit closed set, and answers `unknown_tool` for anything else. |
| `Inventories/StockToolDispatcher.cs` (modify) | Exposes its `ToolNames` set for the router and takes the renamed confirmation service. Its stock behavior is otherwise untouched. |
| `Turns/ConversationalClauses.cs` (modify) | The bounded clause grammar gains `alias`, `aliases`, and `called`. |
| `Turns/ScriptedModelBoundary.cs` (modify) | Recognizes the ten administration commands and proposes their bounded tool calls. It still parses only direct content and still supplies no identity. |

### Infrastructure (`src/MultiChannelAgent.Infrastructure/`)

| File | Responsibility |
| --- | --- |
| `Persistence/Entities/UnitEntity.cs` (modify) | Gains `ConcurrencyStamp` and `RetiredAt`. |
| `Persistence/Entities/UnitTermEntity.cs` (modify) | Gains `IsReserved` and `RetiredAt`. |
| `Persistence/Entities/LocationEntity.cs` (modify) | Gains `ConcurrencyStamp` and `RetiredAt`. |
| `Persistence/Entities/ConfirmationProposalEntity.cs` (modify) | Gains `Kind`, `ReferenceChangesJson`, and `ExpectedReferenceVersionsJson`. |
| `Persistence/Entities/ConfirmationProposalReferenceEntity.cs` (create) | The queryable index of every Unit and Location one proposal touches, so retiring an identity settles exactly the pending proposals that reference it - including stock mutation proposals. |
| `Persistence/Entities/ReferenceOperationEntity.cs` (create) | The reference ledger header: operation identity, Inventory, confirming Turn, optional proposal, applied-at. |
| `Persistence/Entities/ReferenceEffectEntity.cs` (create) | One recorded reference change per row: order, kind, reference identity, names before and after, alias, and the initial alias list a create established. |
| `Persistence/Configurations/UnitEntityConfiguration.cs` (modify) | Adds the retirement column and keeps the composite alternate key. |
| `Persistence/Configurations/UnitTermEntityConfiguration.cs` (modify) | Replaces the unique term index with a **filtered** one over active terms only. |
| `Persistence/Configurations/LocationEntityConfiguration.cs` (modify) | Replaces the unique name index with a **filtered** one over active Locations only. |
| `Persistence/Configurations/ConfirmationProposalEntityConfiguration.cs` (modify) | Bounds `Kind`; the shipped filtered unique index and token-hash index are untouched. |
| `Persistence/Configurations/ConfirmationProposalReferenceEntityConfiguration.cs` (create) | Composite key, cascade from the proposal only (one cascade path), and the lookup index retirement uses. |
| `Persistence/Configurations/ReferenceOperationEntityConfiguration.cs` (create) | Operation identity as the key, the unique replay index, and the unique proposal index. |
| `Persistence/Configurations/ReferenceEffectEntityConfiguration.cs` (create) | Cascade from the ledger header and ordering by `Order`. |
| `Persistence/Migrations/*_AddReferenceAdministration.cs` (generate) | One migration: the new columns, the two replaced filtered unique indexes, the three new tables, and the exact backfills for `IsReserved` and both `ConcurrencyStamp` columns. |
| `Inventories/SqlInventoryReferenceStore.cs` (modify) | Resolution excludes retired Units, retired terms, and retired Locations. |
| `Inventories/SqlReferenceCatalogStore.cs` (create) | The SQL implementation of the catalog read seam. |
| `Inventories/SqlReferenceAdministrationStore.cs` (create) | The one atomic reference writer, including the `Serializable` retire path, the retire conflict re-check, the audits, the ledger, and pending-proposal invalidation. |
| `Inventories/ConfirmationProposalMapper.cs` (modify) | Serializes and reads both proposal payloads, still versioned, still refusing an unreadable shape. |
| `Inventories/SqlConfirmationProposalStore.cs` (modify) | Writes the proposal reference index inside the same transaction that stores the proposal. |
| `ServiceCollectionExtensions.cs` (modify) | Registers the new stores, services, dispatchers, and the router; renames the confirmation service registration. |

### Host (`src/MultiChannelAgent.Host/`)

| File | Responsibility |
| --- | --- |
| `Endpoints/ReferenceEndpoints.cs` (create) | The two authorized catalog projections the Inventory workspace refetches: `GET /api/inventories/{id}/units` and `GET /api/inventories/{id}/locations`. |
| `Program.cs` (modify) | Maps the new endpoints. |

### Web (`src/web/src/`)

| File | Responsibility |
| --- | --- |
| `referenceApi.ts` (create) | Typed fetches for the two catalog projections. |
| `ReferenceWorkspace.tsx` (create) | The authoritative active Unit and Location projection, refetched on the same terminal-Outcome token the Stock workspace uses. |
| `turnsApi.ts` (modify) | The payload union gains `unit_list`, `location_list`, `reference_changes`, `reference_proposal`, and `reference_suggestions`. |
| `TurnTracer.tsx` (modify) | Renders those five payload kinds semantically. |
| `App.tsx` (modify) | Mounts `ReferenceWorkspace` beside `StockWorkspace`. |

### Tests

| File | Responsibility |
| --- | --- |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceAdministrationFactsTests.cs` (create) | The vocabulary, the confirmation policy, and the role matrix. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceChangePlanTests.cs` (create) | Every planned outcome and every refusal, from current state alone. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceListCursorTests.cs` (create) | Round-trip, kind mismatch, and malformed cursors. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/UnitTests.cs` (modify) | Bounds, retirement, and the reserved term rules. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/LocationTests.cs` (modify) | Retirement. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs` (modify) | Reference proposals, `RequiredRole`, referenced identities, and that stock invariants are unchanged. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeSetParserTests.cs` (create) | Closed property sets, homogeneity, bounds, and string-only values. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeResolverTests.cs` (create) | Exact resolution, refusals, and bounded deterministic suggestions. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceAdministrationServiceTests.cs` (create) | The role matrix, immediate application, proposals, atomic refusal, and replay. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceListingServiceTests.cs` (create) | Viewer access, active-only, ordering, and paging. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryConfirmationServiceTests.cs` (renamed + modified) | The shipped stock cases plus Owner-gated reference execution. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceToolDispatcherTests.cs` (create) | Trusted context, typed statuses, payload shapes, and non-disclosure. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryToolRouterTests.cs` (create) | Routing and `unknown_tool`. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryReferenceCatalogStore.cs` (create) | The catalog double. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryReferenceAdministrationStore.cs` (create) | The atomic writer double, including retire conflicts and proposal invalidation. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs` (modify) | Records the proposal reference index and supports invalidation by reference. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryInventoryReferenceStore.cs` (modify) | Retirement, so resolution is active-only in unit tests too. |
| `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs` (modify) | The ten new commands. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceRelationalModelTests.cs` (create) | Docker-free model assertions: the two filtered unique indexes and the single cascade path into the proposal reference index. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceCatalogStoreTests.cs` (create) | SQL catalog reads, active-only, ordering, paging, suggestions. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreTests.cs` (create) | Atomicity, retire conflicts, audits, ledger, replay, and proposal invalidation. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreConcurrencyTests.cs` (create) | Two concurrent retires, and a retire racing a stock write. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreChangeTrackerIsolationTests.cs` (create) | A failed change set never contaminates its scope. |
| `tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationScenario.cs` (create) | The whole protocol through the real HTTP application boundary. |
| `tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationSqliteTests.cs` (create) | The Docker-free twin. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceAdministrationSqlScenarioTests.cs` (create) | The SQL Server-backed run of the same scenario. |

---

## Domain, state-machine, and authorization decisions

These are settled here so no task has to re-decide them.

### 1. Exactly ten tools, and they come from #26

Issue #26 states them verbatim: "Expose `list_units`, `create_units`, `rename_units`, `add_unit_aliases`, `remove_unit_aliases`, `retire_units`, `list_locations`, `create_locations`, `rename_locations`, and `retire_locations`."

Two are reads. Eight are mutating, and each maps to exactly one `ReferenceChangeKind`:

| Tool | `ReferenceChangeKind` | Minimum role | Confirmed |
| --- | --- | --- | --- |
| `list_units` | (read) | Viewer | no |
| `create_units` | `CreateUnit` | Editor | only as part of a multi-change batch |
| `rename_units` | `RenameUnit` | Editor | only as part of a multi-change batch |
| `add_unit_aliases` | `AddUnitAlias` | Editor | only as part of a multi-change batch |
| `remove_unit_aliases` | `RemoveUnitAlias` | Editor | only as part of a multi-change batch |
| `retire_units` | `RetireUnit` | **Owner** | **always** |
| `list_locations` | (read) | Viewer | no |
| `create_locations` | `CreateLocation` | Editor | only as part of a multi-change batch |
| `rename_locations` | `RenameLocation` | Editor | only as part of a multi-change batch |
| `retire_locations` | `RetireLocation` | **Owner** | **always** |

No tool is invented and none is omitted. There is no separate effect enum: `ReferenceChangeKind` *is* the effect vocabulary, because every kind has exactly one effect. Adding a second enum whose members mapped one-to-one would be ceremony with two things to keep in step.

The confirmation rule is one expression, mirroring #32 exactly, and satisfies #26's "Confirm every Unit/Location Retire and every multi-change reference-administration batch":

```
requiresConfirmation = changes.Count > 1 || changes.Any(c => ReferenceAdministrationFacts.RequiresConfirmation(c.Kind))
```

The role rule is one expression:

```
requiredRole = changes.Any(c => ReferenceAdministrationFacts.RequiredRole(c.Kind) == Owner) ? Owner : Editor
```

### 2. Homogeneous atomic change arrays

Every mutating tool takes exactly one untrusted argument, `changes`: a JSON array of objects whose property values are all strings. The **tool name fixes the kind**, so homogeneity is structural rather than checked - `ReferenceChangeSetParser.TryParse(toolName, json, ...)` knows which kind it is parsing and rejects any element carrying a property that kind does not have. A `kind` property inside an element is itself an unknown property and refuses the whole array; a batch that mixed kinds could not be expressed.

The closed property set per kind:

| Kind | Element properties |
| --- | --- |
| `CreateUnit` | `name` (required), `aliases` (optional, comma-separated ordered list) |
| `RenameUnit` | `unit` (required), `newName` (required) |
| `AddUnitAlias` | `unit` (required), `alias` (required) |
| `RemoveUnitAlias` | `unit` (required), `alias` (required) |
| `RetireUnit` | `unit` (required) |
| `CreateLocation` | `name` (required) |
| `RenameLocation` | `location` (required), `newName` (required) |
| `RetireLocation` | `location` (required) |

`CreateUnit` carries its initial aliases because a Unit's whole term set is established atomically with it - exactly as the reserved `each` Unit is created with `piece`, `pieces`, `pc`, and `pcs`. Afterwards aliases are managed one at a time, which is what makes each `AddUnitAlias`/`RemoveUnitAlias` change one auditable fact.

The `aliases` value is a comma-separated ordered list, split on `,` with blank segments discarded. A comma can therefore never survive into an alias, which is a stated and deliberate bound: an alias is a spoken term, and a comma inside one would be unpronounceable anyway. An `aliases` property present but listing nothing is a refusal rather than "no aliases" - a caller that meant none omits the property.

The array must be non-empty and carry at most `ConfirmationProposal.MaxChanges` (25) elements. `Order` is assigned by the parser from the element's own position, never read from the element, so a proposal cannot reorder or collide execution order.

### 3. One collision-free normalized namespace, and what retirement does to it

A Unit's canonical name and each of its aliases is one `UnitTerm` row. Within one Inventory, **at most one active term may carry any normalized form**, whether canonical or alias. That is enforced by the database, not by convention:

```
UNIQUE INDEX IX_UnitTerms_InventoryId_NormalizedTerm ON UnitTerms (InventoryId, NormalizedTerm) WHERE RetiredAt IS NULL
```

Retiring a Unit sets `RetiredAt` on the Unit **and on every one of its terms**, in the same transaction. Its identity is preserved - the rows stay, `Units.Id` and `UnitTerms.Id` never change, and prior audits keep meaning what they meant - but its terms leave the active namespace, so the name becomes available again. That is the only reading consistent with all three of #26's statements together: "a term identifies at most one **active** Unit", "retired Units and Locations excluded from matching and ordinary Lists", and "Retire marks an unused reference unavailable **while retaining identity**". Restore is explicitly out of scope, so a freed name can never collide with a revived Unit.

Locations are the same shape without aliases:

```
UNIQUE INDEX IX_Locations_InventoryId_NormalizedName ON Locations (InventoryId, NormalizedName) WHERE RetiredAt IS NULL
```

Both filters are written as plain unquoted SQL text, exactly like the shipped Equivalent Stock and pending-proposal filters, so they are valid on both SQL Server and SQLite.

The uniqueness index is the guarantee; `ExpectedTermAbsence` is the *courtesy*. Every change that introduces a term (a create's name and each initial alias, a rename's new name, an alias add) records one expected absence, verified before the writes so the common case is a clean typed `conflict` rather than a caught unique-index violation. A losing race still ends as a `conflict`, because the store classifies a unique-index violation on a term or Location name it expected to be free as exactly that.

### 4. The reserved `each` Unit and its fixed aliases

Every Inventory begins with the reserved `each` Unit and the fixed aliases `piece`, `pieces`, `pc`, and `pcs` (already shipped by #28). `UnitEntity.IsReserved` marks the Unit; a new `UnitTermEntity.IsReserved` marks its five original terms individually.

- `RenameUnit` on a reserved Unit is refused `conflict` / `reserved_unit`.
- `RetireUnit` on a reserved Unit is refused `conflict` / `reserved_unit`.
- `RemoveUnitAlias` naming a reserved term is refused `conflict` / `reserved_term`.
- `RemoveUnitAlias` naming a Unit's canonical term is refused `conflict` / `canonical_term` - a Unit's own name is not one of its aliases.
- **Reassignment is impossible by construction**, not by a rule: `each`, `piece`, `pieces`, `pc`, and `pcs` are active terms, so `AddUnitAlias` naming any of them on another Unit collides in the shared namespace and is refused `conflict` / `term_in_use`. There is no reassign operation at all.
- Adding a *new*, non-reserved alias to the reserved `each` Unit is allowed, and that alias is removable. #26 protects the reserved Unit and its **fixed** aliases; forbidding a Participant from teaching `each` a local word would be a rule nobody wrote. Per-row `IsReserved` is what keeps the two cases apart.

### 5. Locations are flat, and unlocated is absence

Locations have no aliases, no hierarchy, and no parent. There is no `add_location_aliases` tool and no location `alias` property anywhere in the parser's closed property sets. "Unlocated" is the absence of a `LocationId` on a Stock Entry - it is never a Location row, so it can never be listed, renamed, retired, or resolved. `ResolveLocationAsync` is never asked to resolve the word: the stock tools carry a separate `unlocated` flag, which this plan does not touch.

### 6. Rename preserves identity and never touches stock

- `RenameUnit` writes `Units.CanonicalName`, `Units.NormalizedCanonicalName`, the **canonical** `UnitTerms` row's `Term`/`NormalizedTerm`, and `Units.ConcurrencyStamp`. Nothing else.
- `RenameLocation` writes `Locations.Name`, `Locations.NormalizedName`, and `Locations.ConcurrencyStamp`. Nothing else.
- `StockEntries` is not read and not written by either. Stock Entries reference `UnitId` and `LocationId`, which never change, so Equivalent Stock - unique by `(InventoryId, NormalizedName, UnitId, LocationId)` - is bit-for-bit unaffected. A Stock Entry's own `ConcurrencyStamp` therefore does not move either, so no pending stock proposal is invalidated by a rename. This is asserted directly: the SQL test snapshots every Stock Entry row before the rename and asserts equality afterwards.
- Renaming to a display name whose normalized form is unchanged (`Box` -> `BOX`) is a display-only change and is planned, exactly like `StockChangePlan.ForRename` treats the same case. Renaming to the identical display text is `conflict` / `no_change`. Renaming onto any other active term - including one of the Unit's **own** aliases - is `conflict` / `term_in_use`; promoting an alias to canonical would be a reference merge, which is out of scope.

### 7. Retire: what blocks it, what survives it, and what it invalidates

- Retire is **Owner-only** and **always confirmed**.
- It is blocked while any Stock Entry references the target: `conflict` / `reference_in_use`. This is checked at plan time (so the Participant is told before being asked to confirm) **and re-checked inside the execution transaction**, which is the authoritative check - "Confirmed Retire fails for currently referenced data" means current at execution, not at proposal.
- It never cascades, never reassigns, and never deletes. `StockEntries` is only ever *read* by the retire path.
- The identity survives: rows stay, `RetiredAt` is set, and prior audits keep resolving.
- Inside the very same transaction, every **pending** proposal that references a retired Unit or Location is settled `Conflicted` - including stock mutation proposals, because a proposal that would create or move stock at a Unit or Location that no longer exists must never execute. The proposal being confirmed right now cannot be caught by this: step 1 of the transaction has already moved it out of `Pending`.
- Exactness comes from `ConfirmationProposalReferences`, a small index table written when a proposal is stored, listing every Unit and Location the proposal touches. Scanning serialized JSON for a Guid would work by accident; a keyed table works by construction and is indexable.
- Retire change sets run at `IsolationLevel.Serializable`. Under read-committed, a Stock Entry insert could be decided against an active Unit and commit just after the retire commits, leaving a retired Unit with stock referencing it. Serializable makes the conflict-check range query take a range lock, so the two serialize. It is scoped to change sets that carry a Retire, because nothing else needs it.

### 8. Expected versions

`Units` and `Locations` each gain a `ConcurrencyStamp` `Guid`, regenerated on every write by this store. Every **existing** reference a proposal touches contributes one `ExpectedReferenceVersion(Kind, ReferenceId, ConcurrencyStamp)`, read at proposal time. Execution verifies each with a single guarded `ExecuteUpdateAsync ... WHERE Id = @id AND InventoryId = @inv AND ConcurrencyStamp = @expected` that both takes the row's exclusive lock and asserts the version, in one globally agreed order (the ordinal text of the identity, Units before Locations). Anything other than exactly one row affected rolls the whole transaction back.

Deliberately **not** marked `IsConcurrencyToken()`, unlike `StockEntryEntity.ConcurrencyStamp`. This store never mutates references through the change tracker - every write is a guarded `ExecuteUpdate`/`ExecuteDelete` - and marking the column would make `SqlInventoryStore.CreateAsync`'s tracked `Units.Add` and any future tracked write subject to EF's own concurrency handling for no benefit here.

### 9. Reference resolution is exact, active-only, and never ambiguous

A reference is an opaque identifier or an exact name: for a Unit, any of its **active** terms (canonical or alias); for a Location, its exact active name. Nothing is guessed, fuzzy-matched, prefix-matched, or created. A reference therefore either resolves to exactly one identity or to none - `ambiguous` is unreachable for references and no code path produces it, which is why the reference dispatcher never returns that status.

`SqlInventoryReferenceStore` gains `WHERE RetiredAt IS NULL` on every lookup, so a retired reference is `reference_not_found`, indistinguishable from one that never existed. That is also what makes Import (#35) inherit "retired references are unknown" for free.

### 10. Bounded deterministic suggestions

An unresolved reference answers `not_found` / `reference_not_found` with at most **five** suggestions, produced by `IReferenceCatalogStore.SuggestAsync`:

1. Active terms (for a Unit) or active Location names whose **normalized form starts with** the normalized reference, ordered ordinally by normalized form then by identity, take 5.
2. When that yields none, the first 5 active terms/names in that same order.

Exact-prefix and stable ordering only. No edit distance, no phonetic matching, no ranking heuristic - fuzzy matching is out of scope in #26 and nothing here approaches it. The same input against the same Inventory always yields the same list. Suggestions are only ever produced after the caller has been authorized for the Inventory, and only name references the caller could list anyway, so they disclose nothing new.

### 11. Minimal semantic audit facts

One `AuditFact` per applied change, appended in the same transaction that applied it:

| Kind | `AuditEventType` | `OutcomeCode` |
| --- | --- | --- |
| `CreateUnit` | `UnitCreated` | `Unit:Created` |
| `RenameUnit` | `UnitRenamed` | `Unit:Renamed` |
| `AddUnitAlias` | `UnitAliasAdded` | `Unit:AliasAdded` |
| `RemoveUnitAlias` | `UnitAliasRemoved` | `Unit:AliasRemoved` |
| `RetireUnit` | `UnitRetired` | `Unit:Retired` |
| `CreateLocation` | `LocationCreated` | `Location:Created` |
| `RenameLocation` | `LocationRenamed` | `Location:Renamed` |
| `RetireLocation` | `LocationRetired` | `Location:Retired` |

The fact records `EventType`, `ActorKind`, `ActorId`, `InventoryId`, `OutcomeCode`, and `OccurredAt`, with `SubjectParticipantId` null. It carries **no** Unit name, alias, Location name, identity, prompt, or SQL detail - "an Editor created a Unit in this Inventory", never which one.

Denials produce the audit fact the shipped `InventoryAuthorizationService` already writes: `AccessDenied` with `Denied:NotAMember` or `Denied:InsufficientRole`. A Viewer attempting `create_units`, an Editor attempting `retire_units`, and a non-member attempting anything all land there. No new denial vocabulary is invented, because #26 specifies none.

### 12. The proposal carries a second payload, and stock keeps every invariant

`ConfirmationProposal` gains `Kind` (`Stock` or `ReferenceAdministration`) plus three reference collections. Two things make this safe:

- The shipped `Create` factory is untouched except to stamp `Kind = Stock` and to pass empty reference collections. Every rule it enforces today - non-empty changes, the 25 bound, unique orders, and "every existing Stock Entry touched must carry an expected version" - still runs on exactly the same inputs.
- `CreateForReferences` enforces the parallel rules on its own inputs: non-empty reference changes, the same 25 bound, unique orders, and "every existing Unit or Location touched must carry an expected version". It passes empty stock collections, so no stock rule is weakened or bypassed - there is simply no stock to check.

Everything else is shared and unchanged: the ten-minute lifetime, the single-use hashed token, the binding predicate, the one-pending-per-Participant-and-ChannelConversation filtered unique index, and the `ProposalStatus` state machine (`Pending -> Confirmed | Rejected | Superseded | Expired | AccessLost | InventorySwitched | Interrupted | Conflicted`, every arrow terminal). #26's "keep one pending proposal per Participant and ChannelConversation across stock, import, and administration" is therefore satisfied by construction: administration proposals compete for the same single slot, and storing one supersedes a pending stock proposal exactly as a stock proposal would.

The execution identity of a confirmed reference proposal is `ReferenceOperationId.DeriveForProposal(proposal.Id)`, hashed from material shaped `"reference-proposal|{id}"` so it can never equal a `StockOperationId`. Immediate (unconfirmed) reference change sets use `ReferenceOperationId.Derive(turnId, toolName, 0)`, material `"reference|{turn}|{tool}|{seq}"`.

### 13. Replay, and why both ledgers are asked

`ReferenceOperations` is keyed by its operation identity and uniquely indexed on `(InventoryId, ConfirmedByTurnId)`, exactly like `StockChangeSetOperations`. A Turn dispatches exactly one tool call today, so a Turn writes to at most one ledger.

`ReferenceAdministrationService.ApplyAsync` asks `IReferenceAdministrationStore.FindRecordedByTurnAsync` immediately after authorization and before resolving anything, because a replayed Turn meets a catalog its own first attempt already changed - re-planning would report `term_in_use` against a Unit it had itself created.

`InventoryConfirmationService.ConfirmAsync` asks **both** `IStockChangeSetStore.FindRecordedByTurnAsync` and `IReferenceAdministrationStore.FindRecordedByTurnAsync`, because by replay time the proposal is consumed and its kind is no longer knowable. Both are single indexed lookups, both are scoped to the Inventory from trusted context, and at most one can answer.

### 14. Confirmation authorization for a reference proposal

`InventoryConfirmationService` keeps its shipped preamble unchanged - authorize `Editor`, answer a replay, require direct confirmation evidence, find the one pending proposal, check binding, check expiry, verify the token. It then adds exactly one step before executing: when `pending.RequiredRole == MembershipRole.Owner`, re-authorize at `Owner` and answer `forbidden` when that fails. That call is the shipped `InventoryAuthorizationService`, so the `Denied:InsufficientRole` audit fact is written for free.

A denied confirmation deliberately leaves the proposal `Pending`. Lookup is per-Participant, so nobody else can reach it, and it expires in ten minutes. Burning a Participant's own pending work because their role changed mid-conversation would be a worse failure than the one it prevents. Stock proposals always report `RequiredRole == Editor`, so this step is a no-op for them and the shipped path is bit-for-bit unchanged.

### 15. Typed statuses, and non-disclosure

Only the shipped statuses are used: `completed`, `confirmation_required`, `ambiguous` (never produced here), `not_found`, `forbidden`, `conflict`, `invalid`, `transient_failure`. The machine codes are a closed set:

| Code | Status | Meaning |
| --- | --- | --- |
| `completed` | `completed` | Applied. |
| `confirmation_required` | `confirmation_required` | An exact proposal is stored. |
| `reference_not_found` | `not_found` | The named Unit or Location does not exist here (or is retired). Carries bounded suggestions. |
| `alias_not_found` | `not_found` | That term is not an active alias of that Unit. |
| `forbidden` | `forbidden` | The Participant's role does not allow it. |
| `not_found` | `not_found` | No accessible Inventory - identical whether it does not exist or is not authorized. |
| `term_in_use` | `conflict` | The term already identifies an active Unit here. |
| `name_in_use` | `conflict` | An active Location here already carries that name. |
| `reserved_unit` | `conflict` | The reserved `each` Unit cannot be renamed or retired. |
| `reserved_term` | `conflict` | A fixed alias cannot be removed. |
| `canonical_term` | `conflict` | A Unit's own name is not one of its aliases. |
| `reference_in_use` | `conflict` | A Stock Entry still references it, so it cannot be retired. |
| `no_change` | `conflict` | A semantic no-op. |
| `state_changed` | `conflict` | Current state no longer matches what was proposed. |
| `invalid_changes` | `invalid` | The `changes` array could not be understood. |
| `too_many_changes` | `invalid` | More than 25 changes. |
| `conflicting_changes` | `invalid` | Two changes in one set act on the same reference or claim the same term. |
| `invalid_name` | `invalid` | A Unit name or alias outside 1..100 characters, or a Location name outside 1..200. |
| `invalid_reference` | `invalid` | No reference was named. |
| `invalid_page_size` / `invalid_cursor` | `invalid` | Bounds on a catalog read. |

No summary names another Participant, another Inventory, a Stock Entry, a row version, an audit identity, a proposal identity, or any SQL detail. A reference in an Inventory the caller cannot access is `not_found`, identical to one that does not exist.

### 16. The tool contract never carries identity

The model proposes a tool name and a flat dictionary of untrusted string arguments; that is unchanged. Every mutating administration tool accepts exactly one key, `changes`; every read tool accepts `pageSize` and `cursor`. None of them accepts a Participant, an Inventory, a conversation, a Turn, a proposal identity, a role, or a version - all of those come from `TurnExecutionContext` and the stored proposal. A hostile or buggy proposal can only ever make a request narrower, malformed, or unresolvable.

---

## Task 1: Name the reference administration vocabulary

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ReferenceAdministration.cs`
- Create: `src/MultiChannelAgent.Domain/Inventories/ReferenceOperationId.cs`
- Modify: `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceAdministrationFactsTests.cs`

Why: the confirmation policy, the role matrix, the audit vocabulary, and the machine text a tool argument and a ledger row both use must be decided in exactly one place. A kind that reads `retire_unit` in one file and `RetireUnit` in another is a bug waiting for a retry to find it.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceAdministrationFactsTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ReferenceAdministrationFactsTests
{
    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, "create_unit")]
    [InlineData(ReferenceChangeKind.RenameUnit, "rename_unit")]
    [InlineData(ReferenceChangeKind.AddUnitAlias, "add_unit_alias")]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, "remove_unit_alias")]
    [InlineData(ReferenceChangeKind.RetireUnit, "retire_unit")]
    [InlineData(ReferenceChangeKind.CreateLocation, "create_location")]
    [InlineData(ReferenceChangeKind.RenameLocation, "rename_location")]
    [InlineData(ReferenceChangeKind.RetireLocation, "retire_location")]
    public void Every_kind_has_stable_machine_text_that_round_trips(ReferenceChangeKind kind, string text)
    {
        Assert.Equal(text, ReferenceAdministrationFacts.ToMachineText(kind));
        Assert.True(ReferenceAdministrationFacts.TryParse(text, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void Machine_text_is_exact_and_case_sensitive()
    {
        Assert.False(ReferenceAdministrationFacts.TryParse("Retire_Unit", out _));
        Assert.False(ReferenceAdministrationFacts.TryParse("retire", out _));
        Assert.False(ReferenceAdministrationFacts.TryParse(null, out _));
    }

    [Theory]
    [InlineData(ReferenceChangeKind.RetireUnit)]
    [InlineData(ReferenceChangeKind.RetireLocation)]
    public void Only_a_Retire_requires_confirmation_on_its_own(ReferenceChangeKind kind) =>
        Assert.True(ReferenceAdministrationFacts.RequiresConfirmation(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit)]
    [InlineData(ReferenceChangeKind.RenameUnit)]
    [InlineData(ReferenceChangeKind.AddUnitAlias)]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias)]
    [InlineData(ReferenceChangeKind.CreateLocation)]
    [InlineData(ReferenceChangeKind.RenameLocation)]
    public void Every_non_destructive_kind_applies_without_being_asked(ReferenceChangeKind kind) =>
        Assert.False(ReferenceAdministrationFacts.RequiresConfirmation(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.RetireUnit, MembershipRole.Owner)]
    [InlineData(ReferenceChangeKind.RetireLocation, MembershipRole.Owner)]
    [InlineData(ReferenceChangeKind.CreateUnit, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.RenameUnit, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.AddUnitAlias, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.CreateLocation, MembershipRole.Editor)]
    [InlineData(ReferenceChangeKind.RenameLocation, MembershipRole.Editor)]
    public void Only_Retire_demands_the_Owner(ReferenceChangeKind kind, MembershipRole role) =>
        Assert.Equal(role, ReferenceAdministrationFacts.RequiredRole(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, ReferenceKind.Unit)]
    [InlineData(ReferenceChangeKind.RetireUnit, ReferenceKind.Unit)]
    [InlineData(ReferenceChangeKind.CreateLocation, ReferenceKind.Location)]
    [InlineData(ReferenceChangeKind.RetireLocation, ReferenceKind.Location)]
    public void Every_kind_names_the_reference_it_administers(ReferenceChangeKind kind, ReferenceKind reference) =>
        Assert.Equal(reference, ReferenceAdministrationFacts.ReferenceKindFor(kind));

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, AuditEventType.UnitCreated, "Unit:Created")]
    [InlineData(ReferenceChangeKind.RenameUnit, AuditEventType.UnitRenamed, "Unit:Renamed")]
    [InlineData(ReferenceChangeKind.AddUnitAlias, AuditEventType.UnitAliasAdded, "Unit:AliasAdded")]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, AuditEventType.UnitAliasRemoved, "Unit:AliasRemoved")]
    [InlineData(ReferenceChangeKind.RetireUnit, AuditEventType.UnitRetired, "Unit:Retired")]
    [InlineData(ReferenceChangeKind.CreateLocation, AuditEventType.LocationCreated, "Location:Created")]
    [InlineData(ReferenceChangeKind.RenameLocation, AuditEventType.LocationRenamed, "Location:Renamed")]
    [InlineData(ReferenceChangeKind.RetireLocation, AuditEventType.LocationRetired, "Location:Retired")]
    public void Every_kind_audits_one_minimal_fact(ReferenceChangeKind kind, AuditEventType eventType, string outcomeCode)
    {
        Assert.Equal(eventType, ReferenceAdministrationFacts.EventTypeFor(kind));
        Assert.Equal(outcomeCode, ReferenceAdministrationFacts.OutcomeCodeFor(kind));
    }

    [Fact]
    public void A_reference_operation_identity_is_derived_and_stable_across_retries()
    {
        var turnId = new TurnId(Guid.NewGuid());

        Assert.Equal(
            ReferenceOperationId.Derive(turnId, "retire_units", 0),
            ReferenceOperationId.Derive(turnId, "retire_units", 0));
        Assert.NotEqual(
            ReferenceOperationId.Derive(turnId, "retire_units", 0),
            ReferenceOperationId.Derive(turnId, "create_units", 0));
    }

    [Fact]
    public void A_reference_operation_identity_can_never_collide_with_a_stock_one()
    {
        var proposalId = ProposalId.NewId();
        var turnId = new TurnId(Guid.NewGuid());

        Assert.NotEqual(
            StockOperationId.DeriveForProposal(proposalId).Value,
            ReferenceOperationId.DeriveForProposal(proposalId).Value);
        Assert.NotEqual(
            StockOperationId.Derive(turnId, "create_units", 0).Value,
            ReferenceOperationId.Derive(turnId, "create_units", 0).Value);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ReferenceAdministrationFactsTests"`
Expected: FAIL to compile - `ReferenceChangeKind`, `ReferenceKind`, `ReferenceAdministrationFacts`, `ReferenceOperationId`, and the eight new `AuditEventType` members do not exist.

- [ ] **Step 3: Add the audit vocabulary**

In `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`, add these members to `AuditEventType`, immediately after `StockForgotten`:

```csharp
    /// <summary>An Inventory-owned Unit was created. The fact records that it happened, never which Unit or under what name.</summary>
    UnitCreated,

    /// <summary>A Unit's canonical name changed. Its identity, and every Stock Entry referencing it, are untouched.</summary>
    UnitRenamed,

    /// <summary>A non-reserved alias was added to a Unit's shared term namespace.</summary>
    UnitAliasAdded,

    /// <summary>A non-reserved alias was removed from a Unit's shared term namespace.</summary>
    UnitAliasRemoved,

    /// <summary>An unused Unit was withdrawn from matching and assignment after explicit Owner confirmation. Its identity remains.</summary>
    UnitRetired,

    /// <summary>An Inventory-owned Location was created.</summary>
    LocationCreated,

    /// <summary>A Location's name changed. Its identity, and every Stock Entry placed there, are untouched.</summary>
    LocationRenamed,

    /// <summary>An unused Location was withdrawn from matching and assignment after explicit Owner confirmation. Its identity remains.</summary>
    LocationRetired,
```

- [ ] **Step 4: Add the vocabulary**

Create `src/MultiChannelAgent.Domain/Inventories/ReferenceAdministration.cs`:

```csharp
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
```

- [ ] **Step 5: Add the operation identity**

Create `src/MultiChannelAgent.Domain/Inventories/ReferenceOperationId.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stable identity of one attempted reference administration change set. Like
/// <see cref="StockOperationId"/>, it is <em>derived</em> - never generated - from identities the
/// application already trusts, so retrying a Turn re-reports what it did instead of doing it twice,
/// and nothing a model proposes contributes to it.
///
/// It is a distinct type from <see cref="StockOperationId"/>, and its hash material is deliberately
/// shaped differently, because the two ledgers are separate tables: an identity that could belong to
/// either would make "what did this operation do" ambiguous.
/// </summary>
public readonly record struct ReferenceOperationId(Guid Value)
{
    public static ReferenceOperationId Derive(TurnId turnId, string toolName, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"reference|{turnId.Value:D}|{toolName}|{sequence}"));

        return new ReferenceOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    /// <summary>
    /// The identity a confirmed reference proposal's execution is recorded under, derived from the
    /// proposal rather than from the Turn that confirms it - the proposal is consumed by execution,
    /// and a Turn re-driven afterwards must still find what its own first attempt did.
    /// </summary>
    public static ReferenceOperationId DeriveForProposal(ProposalId proposalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"reference-proposal|{proposalId.Value:D}"));

        return new ReferenceOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString();
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ReferenceAdministrationFactsTests"`
Expected: PASS - every case in the class.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings. `TreatWarningsAsErrors` is on, so an unhandled switch arm anywhere over `AuditEventType` would surface here.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ReferenceAdministration.cs \
        src/MultiChannelAgent.Domain/Inventories/ReferenceOperationId.cs \
        src/MultiChannelAgent.Domain/Inventories/AuditFact.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceAdministrationFactsTests.cs
git commit -m "feat(inventories): name every Unit and Location administration change once for #33"
```

---

## Task 2: Give Units and Locations retirement, bounds, and a stable identity

**Files:**
- Modify: `src/MultiChannelAgent.Domain/Inventories/Unit.cs`
- Modify: `src/MultiChannelAgent.Domain/Inventories/Location.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/UnitTests.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/LocationTests.cs`

Why: `Unit` today knows only how to build the reserved `each`, has no length bound, and has no notion of retirement; `Location` has bounds but no retirement. Both need a stable identity that survives retirement, and a Unit needs its terms modelled individually so a fixed alias can be told apart from one a Participant added later.

- [ ] **Step 1: Write the failing Unit test**

Append to `tests/MultiChannelAgent.Domain.Tests/Inventories/UnitTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void A_created_Unit_normalizes_and_bounds_its_canonical_name()
    {
        var inventoryId = new InventoryId(Guid.NewGuid());
        var createdAt = DateTimeOffset.UnixEpoch;

        var unit = Unit.Create(inventoryId, "  Cardboard   Box  ", ["Boxes", " BX "], createdAt);

        Assert.Equal("Cardboard Box", unit.CanonicalName);
        Assert.False(unit.IsReserved);
        Assert.True(unit.IsActive);
        Assert.Null(unit.RetiredAt);
        Assert.Equal(["Boxes", "BX"], unit.Aliases);
        Assert.NotEqual(Guid.Empty, unit.Id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_Unit_name_must_not_be_blank(string? name) =>
        Assert.Throws<ArgumentException>(() => Unit.Create(new InventoryId(Guid.NewGuid()), name, [], DateTimeOffset.UnixEpoch));

    [Fact]
    public void A_Unit_name_must_not_exceed_the_column_it_is_stored_in() =>
        Assert.Throws<ArgumentException>(() => Unit.Create(
            new InventoryId(Guid.NewGuid()), new string('b', Unit.MaxNameLength + 1), [], DateTimeOffset.UnixEpoch));

    [Fact]
    public void A_Unit_alias_must_not_exceed_the_column_it_is_stored_in() =>
        Assert.Throws<ArgumentException>(() => Unit.Create(
            new InventoryId(Guid.NewGuid()), "box", [new string('b', Unit.MaxNameLength + 1)], DateTimeOffset.UnixEpoch));

    [Fact]
    public void The_reserved_each_Unit_still_carries_exactly_its_four_fixed_aliases()
    {
        var unit = Unit.CreateReservedEach(new InventoryId(Guid.NewGuid()), DateTimeOffset.UnixEpoch);

        Assert.Equal(Unit.ReservedEachCanonicalName, unit.CanonicalName);
        Assert.True(unit.IsReserved);
        Assert.True(unit.IsActive);
        Assert.Equal(["piece", "pieces", "pc", "pcs"], unit.Aliases);
    }

    [Theory]
    [InlineData("each")]
    [InlineData("EACH")]
    [InlineData("piece")]
    [InlineData("pieces")]
    [InlineData("pc")]
    [InlineData(" Pcs ")]
    public void Every_reserved_term_is_recognized_however_it_is_written(string term) =>
        Assert.True(Unit.IsReservedEachTerm(term));

    [Theory]
    [InlineData("box")]
    [InlineData("eaches")]
    [InlineData("piecework")]
    public void Nothing_else_is_a_reserved_term(string term) =>
        Assert.False(Unit.IsReservedEachTerm(term));

    [Fact]
    public void A_retired_Unit_keeps_its_identity_and_stops_being_active()
    {
        var unit = Unit.Create(new InventoryId(Guid.NewGuid()), "box", [], DateTimeOffset.UnixEpoch);
        var retiredAt = DateTimeOffset.UnixEpoch.AddDays(1);

        var retired = unit with { RetiredAt = retiredAt };

        Assert.Equal(unit.Id, retired.Id);
        Assert.Equal(unit.CanonicalName, retired.CanonicalName);
        Assert.False(retired.IsActive);
        Assert.Equal(retiredAt, retired.RetiredAt);
    }

    [Fact]
    public void A_Unit_term_carries_its_normalized_form_and_whether_it_is_fixed()
    {
        var canonical = UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false);
        var alias = UnitTerm.Create(" boxes ", isCanonical: false, isReserved: false);

        Assert.Equal("Cardboard Box", canonical.Term);
        Assert.Equal("cardboard box", canonical.NormalizedTerm);
        Assert.True(canonical.IsCanonical);
        Assert.Equal("boxes", alias.Term);
        Assert.Equal("boxes", alias.NormalizedTerm);
        Assert.False(alias.IsCanonical);
        Assert.False(alias.IsReserved);
    }
```

- [ ] **Step 2: Write the failing Location test**

Append to `tests/MultiChannelAgent.Domain.Tests/Inventories/LocationTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void A_created_Location_is_active_and_has_never_been_retired()
    {
        var location = Location.Create(new InventoryId(Guid.NewGuid()), "Shelf A", DateTimeOffset.UnixEpoch);

        Assert.True(location.IsActive);
        Assert.Null(location.RetiredAt);
    }

    [Fact]
    public void A_retired_Location_keeps_its_identity_and_stops_being_active()
    {
        var location = Location.Create(new InventoryId(Guid.NewGuid()), "Shelf A", DateTimeOffset.UnixEpoch);
        var retiredAt = DateTimeOffset.UnixEpoch.AddDays(1);

        var retired = location with { RetiredAt = retiredAt };

        Assert.Equal(location.Id, retired.Id);
        Assert.Equal(location.Name, retired.Name);
        Assert.Equal(location.NormalizedName, retired.NormalizedName);
        Assert.False(retired.IsActive);
        Assert.Equal(retiredAt, retired.RetiredAt);
    }
```

- [ ] **Step 3: Run both to verify they fail**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~UnitTests|FullyQualifiedName~LocationTests"`
Expected: FAIL to compile - `Unit.Create`, `Unit.MaxNameLength`, `Unit.ReservedEachCanonicalName`, `Unit.IsReservedEachTerm`, `Unit.RetiredAt`, `Unit.IsActive`, `UnitTerm`, `Location.RetiredAt`, and `Location.IsActive` do not exist.

- [ ] **Step 4: Extend Unit**

Replace the whole body of `src/MultiChannelAgent.Domain/Inventories/Unit.cs` below the `UnitId` declaration with:

```csharp
/// <summary>
/// One term in a Unit's shared, collision-free namespace: its canonical name, or one alias. The
/// normalized form is what uniqueness is enforced against, computed the same way every other name
/// comparison in this domain is (<see cref="NameNormalization"/>).
///
/// <see cref="IsReserved"/> is per-term rather than derived from the Unit, so the reserved `each`
/// Unit's five fixed terms can be protected while an alias a Participant later teaches it stays
/// removable.
/// </summary>
public sealed record UnitTerm
{
    public required string Term { get; init; }

    public required string NormalizedTerm { get; init; }

    public required bool IsCanonical { get; init; }

    public required bool IsReserved { get; init; }

    public static UnitTerm Create(string? term, bool isCanonical, bool isReserved)
    {
        var trimmed = Unit.RequireTermWithinBounds(term, nameof(term));

        return new UnitTerm
        {
            Term = trimmed,
            NormalizedTerm = NameNormalization.Normalize(trimmed),
            IsCanonical = isCanonical,
            IsReserved = isReserved,
        };
    }
}

/// <summary>
/// An Inventory-owned controlled measure. Every Inventory starts with exactly one reserved Unit:
/// the canonical `each`, with the fixed aliases `piece`, `pieces`, `pc`, and `pcs`. Unit names and
/// aliases share one collision-free namespace within an Inventory.
///
/// <see cref="Id"/> is stable for the life of the Unit: renaming changes only what it is called, and
/// retiring withdraws it from matching and assignment without ending the identity that prior Stock
/// Entry references and audits depend on.
/// </summary>
public sealed record Unit
{
    /// <summary>
    /// The authoritative maximum length for a canonical name and for every alias, matching the EF
    /// Core columns' <c>HasMaxLength</c> configuration so an oversized term is rejected here - as a
    /// domain validation error - long before it could reach the database as an unhandled exception.
    /// </summary>
    public const int MaxNameLength = 100;

    /// <summary>The canonical name of the reserved Unit every Inventory starts with.</summary>
    public const string ReservedEachCanonicalName = "each";

    public static readonly IReadOnlyList<string> ReservedEachAliases = ["piece", "pieces", "pc", "pcs"];

    public required UnitId Id { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required string CanonicalName { get; init; }

    public required bool IsReserved { get; init; }

    public required IReadOnlyList<string> Aliases { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When this Unit was withdrawn from matching and assignment, or null while it is active.</summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>Active Units are the only ones that resolve, match, or appear in ordinary Lists.</summary>
    public bool IsActive => RetiredAt is null;

    public static Unit CreateReservedEach(InventoryId inventoryId, DateTimeOffset createdAt) => new()
    {
        Id = new UnitId(Guid.NewGuid()),
        InventoryId = inventoryId,
        CanonicalName = ReservedEachCanonicalName,
        IsReserved = true,
        Aliases = ReservedEachAliases,
        CreatedAt = createdAt,
    };

    /// <summary>
    /// Creates a non-reserved Unit with a canonical name and an ordered set of initial aliases. The
    /// aliases are part of creating the Unit - exactly as the reserved `each` Unit is created with
    /// its four - so a Unit's whole term set is established atomically and every later alias change
    /// is one auditable fact of its own.
    ///
    /// Collision against other Units is not decided here: this type knows nothing about the rest of
    /// the Inventory. It only refuses what is malformed on its own terms.
    /// </summary>
    public static Unit Create(
        InventoryId inventoryId, string? canonicalName, IReadOnlyList<string> aliases, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        return new Unit
        {
            Id = new UnitId(Guid.NewGuid()),
            InventoryId = inventoryId,
            CanonicalName = RequireTermWithinBounds(canonicalName, nameof(canonicalName)),
            IsReserved = false,
            Aliases = aliases.Select(alias => RequireTermWithinBounds(alias, nameof(aliases))).ToList(),
            CreatedAt = createdAt,
        };
    }

    /// <summary>
    /// The full term set this Unit contributes to its Inventory's shared namespace: its canonical
    /// name first, then its aliases in the order they were given.
    /// </summary>
    public IReadOnlyList<UnitTerm> Terms() =>
    [
        UnitTerm.Create(CanonicalName, isCanonical: true, isReserved: IsReserved),
        .. Aliases.Select(alias => UnitTerm.Create(alias, isCanonical: false, isReserved: IsReserved)),
    ];

    /// <summary>
    /// Whether a term is one of the five the reserved `each` Unit is born with. Compared on the
    /// normalized form, so casing and stray whitespace cannot smuggle one past.
    /// </summary>
    public static bool IsReservedEachTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var normalized = NameNormalization.Normalize(term);

        return normalized == ReservedEachCanonicalName
            || ReservedEachAliases.Any(alias => alias == normalized);
    }

    internal static string RequireTermWithinBounds(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        var trimmed = NameNormalization.Collapse(value);

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Value must not exceed {MaxNameLength} characters.", parameterName);
        }

        return trimmed;
    }
}
```

- [ ] **Step 5: Add the display-form collapse it depends on**

`Unit.RequireTermWithinBounds` trims and collapses internal whitespace *without* folding case, so the stored display term is tidy while `NameNormalization.Normalize` keeps doing exactly what it always did. Add to `src/MultiChannelAgent.Domain/Inventories/NameNormalization.cs`, inside the existing static class:

```csharp
    /// <summary>
    /// The tidy <em>display</em> form of a name: trimmed, with runs of internal whitespace collapsed
    /// to one space, and case left exactly as written. <see cref="Normalize"/> is what comparison and
    /// uniqueness use; this is what is stored and shown, so "Cardboard   Box" is kept as
    /// "Cardboard Box" rather than as typed, and never as "cardboard box".
    /// </summary>
    public static string Collapse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();
        var builder = new StringBuilder(trimmed.Length);
        var lastWasWhitespace = false;

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                }

                lastWasWhitespace = true;
                continue;
            }

            lastWasWhitespace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }
```

- [ ] **Step 6: Extend Location**

In `src/MultiChannelAgent.Domain/Inventories/Location.cs`, add these two members immediately after `CreatedAt`:

```csharp
    /// <summary>When this Location was withdrawn from matching and assignment, or null while it is active.</summary>
    public DateTimeOffset? RetiredAt { get; init; }

    /// <summary>Active Locations are the only ones that resolve, match, or appear in ordinary Lists. Unlocated stock is the absence of a reference and can never be retired.</summary>
    public bool IsActive => RetiredAt is null;
```

and change the body of `Create` to use the shared collapse so a Location name is tidied exactly like a Unit term:

```csharp
    public static Location Create(InventoryId inventoryId, string? name, DateTimeOffset createdAt)
    {
        var displayName = RequireWithinBounds(RequireNonBlank(name, nameof(name)), MaxNameLength, nameof(name));

        return new Location
        {
            Id = new LocationId(Guid.NewGuid()),
            InventoryId = inventoryId,
            Name = displayName,
            NormalizedName = NameNormalization.Normalize(displayName),
            CreatedAt = createdAt,
        };
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return NameNormalization.Collapse(value);
    }
```

- [ ] **Step 7: Run both to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj`
Expected: PASS. `NameNormalizationTests` is untouched, because `Normalize` did not change.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/Unit.cs \
        src/MultiChannelAgent.Domain/Inventories/Location.cs \
        src/MultiChannelAgent.Domain/Inventories/NameNormalization.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/UnitTests.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/LocationTests.cs
git commit -m "feat(inventories): give Units and Locations bounded names and retirement for #33"
```

---

## Task 3: Plan every reference change as a pure domain rule

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ReferenceChangePlan.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceChangePlanTests.cs`

Why: whether a change is possible, and exactly what it produces, must be decidable from current state alone - no store, no authorization, no persistence. That is what lets the reserved-term rules, the shared-namespace rules, the no-op rules, and the retire-blocking rule be reasoned about and tested on their own, and it is why the resolver in Task 8 contains no rules of its own.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceChangePlanTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ReferenceChangePlanTests
{
    private static IReadOnlySet<string> Terms(params string[] terms) => terms.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<UnitTerm> UnitTerms() =>
    [
        UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false),
        UnitTerm.Create("boxes", isCanonical: false, isReserved: false),
    ];

    private static IReadOnlyList<UnitTerm> ReservedEachTerms() =>
        Unit.CreateReservedEach(new InventoryId(Guid.NewGuid()), DateTimeOffset.UnixEpoch).Terms();

    [Fact]
    public void Creating_a_Unit_produces_its_canonical_term_first_then_its_aliases_in_order()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("  Cardboard   Box ", ["Boxes", "BX"], Terms("each", "piece"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Cardboard Box", plan.DisplayName);
        Assert.Equal("cardboard box", plan.NormalizedName);
        Assert.Equal(["Cardboard Box", "Boxes", "BX"], plan.Terms.Select(term => term.Term));
        Assert.Equal(["cardboard box", "boxes", "bx"], plan.Terms.Select(term => term.NormalizedTerm));
        Assert.True(plan.Terms[0].IsCanonical);
        Assert.All(plan.Terms, term => Assert.False(term.IsReserved));
    }

    [Fact]
    public void Creating_a_Unit_whose_name_already_identifies_an_active_Unit_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("EACH", [], Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Unit_whose_alias_already_identifies_an_active_Unit_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("Cardboard Box", ["PCS"], Terms("each", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Unit_that_would_claim_one_term_twice_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("Box", ["boxes", "BOXES"], Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Unit_whose_alias_repeats_its_own_canonical_name_is_refused()
    {
        var plan = ReferenceChangePlan.ForCreateUnit("Box", ["box"], Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Creating_a_Unit_without_a_name_is_invalid(string? name) =>
        Assert.Equal(ReferenceChangePlanOutcome.InvalidName, ReferenceChangePlan.ForCreateUnit(name, [], Terms()).Outcome);

    [Fact]
    public void Creating_a_Unit_with_an_oversized_name_is_invalid() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.InvalidName,
            ReferenceChangePlan.ForCreateUnit(new string('b', Unit.MaxNameLength + 1), [], Terms()).Outcome);

    [Fact]
    public void Creating_a_Unit_with_an_oversized_alias_is_invalid() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.InvalidName,
            ReferenceChangePlan.ForCreateUnit("Box", [new string('b', Unit.MaxNameLength + 1)], Terms()).Outcome);

    [Fact]
    public void Renaming_a_Unit_to_a_free_term_is_planned()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "Carton", Terms("each", "boxes"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Carton", plan.DisplayName);
        Assert.Equal("carton", plan.NormalizedName);
    }

    [Fact]
    public void Renaming_a_Unit_only_in_its_display_form_is_planned_because_it_can_collide_with_nothing()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "CARDBOARD BOX", Terms("each", "cardboard box"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("CARDBOARD BOX", plan.DisplayName);
        Assert.Equal("cardboard box", plan.NormalizedName);
    }

    [Fact]
    public void Renaming_a_Unit_to_exactly_what_it_is_called_is_a_no_op()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", " Cardboard  Box ", Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Renaming_a_Unit_onto_another_active_term_is_refused()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "Piece", Terms("each", "piece"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void Renaming_a_Unit_onto_one_of_its_own_aliases_is_refused_because_promoting_one_would_be_a_merge()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(
            isReserved: false, "Cardboard Box", "cardboard box", "Boxes", Terms("each", "boxes"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void The_reserved_Unit_can_never_be_renamed()
    {
        var plan = ReferenceChangePlan.ForRenameUnit(isReserved: true, "each", "each", "item", Terms());

        Assert.Equal(ReferenceChangePlanOutcome.ReservedUnit, plan.Outcome);
    }

    [Fact]
    public void Adding_a_free_alias_is_planned()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias(" Cartons ", UnitTerms(), Terms("each", "piece"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Cartons", plan.Term!.Term);
        Assert.Equal("cartons", plan.Term.NormalizedTerm);
        Assert.False(plan.Term.IsCanonical);
        Assert.False(plan.Term.IsReserved);
    }

    [Fact]
    public void Adding_an_alias_the_Unit_already_carries_is_a_no_op()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("BOXES", UnitTerms(), Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Adding_an_alias_that_repeats_the_Units_own_name_is_a_no_op()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("cardboard box", UnitTerms(), Terms("each"));

        Assert.Equal(ReferenceChangePlanOutcome.NoChange, plan.Outcome);
    }

    [Fact]
    public void Adding_an_alias_that_already_identifies_another_active_Unit_is_refused()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("pcs", UnitTerms(), Terms("each", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void A_reserved_term_can_never_be_reassigned_to_another_Unit()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("piece", UnitTerms(), Terms("each", "piece", "pieces", "pc", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.TermInUse, plan.Outcome);
    }

    [Fact]
    public void A_non_reserved_alias_may_be_added_to_the_reserved_Unit()
    {
        var plan = ReferenceChangePlan.ForAddUnitAlias("stuks", ReservedEachTerms(), Terms("each", "piece", "pieces", "pc", "pcs"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.False(plan.Term!.IsReserved);
    }

    [Fact]
    public void Removing_an_alias_the_Unit_carries_is_planned()
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias("BOXES", UnitTerms());

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("boxes", plan.Term!.NormalizedTerm);
    }

    [Fact]
    public void Removing_a_term_the_Unit_does_not_carry_finds_nothing()
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias("cartons", UnitTerms());

        Assert.Equal(ReferenceChangePlanOutcome.AliasNotFound, plan.Outcome);
    }

    [Fact]
    public void A_Units_own_name_is_not_one_of_its_aliases()
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias("Cardboard Box", UnitTerms());

        Assert.Equal(ReferenceChangePlanOutcome.CanonicalTerm, plan.Outcome);
    }

    [Theory]
    [InlineData("piece")]
    [InlineData("pieces")]
    [InlineData("pc")]
    [InlineData("pcs")]
    public void A_fixed_alias_of_the_reserved_Unit_can_never_be_removed(string alias)
    {
        var plan = ReferenceChangePlan.ForRemoveUnitAlias(alias, ReservedEachTerms());

        Assert.Equal(ReferenceChangePlanOutcome.ReservedTerm, plan.Outcome);
    }

    [Fact]
    public void An_unused_Unit_may_be_retired()
    {
        var plan = ReferenceChangePlan.ForRetireUnit(isReserved: false, stockReferenceCount: 0);

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
    }

    [Fact]
    public void A_Unit_a_Stock_Entry_still_references_may_not_be_retired()
    {
        var plan = ReferenceChangePlan.ForRetireUnit(isReserved: false, stockReferenceCount: 1);

        Assert.Equal(ReferenceChangePlanOutcome.ReferenceInUse, plan.Outcome);
    }

    [Fact]
    public void The_reserved_Unit_can_never_be_retired()
    {
        var plan = ReferenceChangePlan.ForRetireUnit(isReserved: true, stockReferenceCount: 0);

        Assert.Equal(ReferenceChangePlanOutcome.ReservedUnit, plan.Outcome);
    }

    [Fact]
    public void Creating_a_Location_is_planned_when_no_active_Location_carries_that_name()
    {
        var plan = ReferenceChangePlan.ForCreateLocation("  Shelf   A ", Terms("shelf b"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Shelf A", plan.DisplayName);
        Assert.Equal("shelf a", plan.NormalizedName);
    }

    [Fact]
    public void Creating_a_Location_whose_name_is_already_taken_is_refused() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.NameInUse,
            ReferenceChangePlan.ForCreateLocation("SHELF A", Terms("shelf a")).Outcome);

    [Fact]
    public void Creating_a_Location_with_an_oversized_name_is_invalid() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.InvalidName,
            ReferenceChangePlan.ForCreateLocation(new string('s', Location.MaxNameLength + 1), Terms()).Outcome);

    [Fact]
    public void Renaming_a_Location_to_a_free_name_is_planned()
    {
        var plan = ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", "Aisle 3", Terms("shelf b"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("Aisle 3", plan.DisplayName);
        Assert.Equal("aisle 3", plan.NormalizedName);
    }

    [Fact]
    public void Renaming_a_Location_only_in_its_display_form_is_planned()
    {
        var plan = ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", "SHELF A", Terms("shelf a"));

        Assert.Equal(ReferenceChangePlanOutcome.Planned, plan.Outcome);
        Assert.Equal("SHELF A", plan.DisplayName);
    }

    [Fact]
    public void Renaming_a_Location_to_exactly_what_it_is_called_is_a_no_op() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.NoChange,
            ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", " Shelf  A ", Terms()).Outcome);

    [Fact]
    public void Renaming_a_Location_onto_another_active_Location_is_refused() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.NameInUse,
            ReferenceChangePlan.ForRenameLocation("Shelf A", "shelf a", "Shelf B", Terms("shelf b")).Outcome);

    [Fact]
    public void An_unused_Location_may_be_retired() =>
        Assert.Equal(ReferenceChangePlanOutcome.Planned, ReferenceChangePlan.ForRetireLocation(stockReferenceCount: 0).Outcome);

    [Fact]
    public void A_Location_a_Stock_Entry_is_still_placed_in_may_not_be_retired() =>
        Assert.Equal(
            ReferenceChangePlanOutcome.ReferenceInUse,
            ReferenceChangePlan.ForRetireLocation(stockReferenceCount: 3).Outcome);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ReferenceChangePlanTests"`
Expected: FAIL to compile - `ReferenceChangePlan` and `ReferenceChangePlanOutcome` do not exist.

- [ ] **Step 3: Write the planner**

Create `src/MultiChannelAgent.Domain/Inventories/ReferenceChangePlan.cs`:

```csharp
namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Whether one administration change could be decided at all, and if not, why not.</summary>
public enum ReferenceChangePlanOutcome
{
    /// <summary>Decided exactly, and ready to be applied or proposed.</summary>
    Planned,

    /// <summary>A name or alias was blank, or longer than it can be stored.</summary>
    InvalidName,

    /// <summary>The term already identifies an active Unit in this Inventory - possibly this very one.</summary>
    TermInUse,

    /// <summary>An active Location in this Inventory already carries that name.</summary>
    NameInUse,

    /// <summary>The change would leave the Inventory exactly as it is, so it is a semantic no-op rather than work.</summary>
    NoChange,

    /// <summary>The reserved `each` Unit can never be renamed or retired.</summary>
    ReservedUnit,

    /// <summary>A fixed alias of the reserved `each` Unit can never be removed.</summary>
    ReservedTerm,

    /// <summary>A Unit's own canonical name is not one of its aliases, so it cannot be removed as one.</summary>
    CanonicalTerm,

    /// <summary>That term is not an active alias of that Unit.</summary>
    AliasNotFound,

    /// <summary>A Stock Entry still references it, so retiring it would rewrite stock - which administration never does.</summary>
    ReferenceInUse,
}

/// <summary>
/// The pure decision one administration change amounts to, given only current state: what the
/// Inventory's active terms and Location names are, which terms the target Unit carries, whether it
/// is the reserved one, and how many Stock Entries reference it. It reads and writes nothing -
/// authorization, resolution, proposals, and persistence all live outside it - so every rule about
/// the shared namespace, the reserved Unit, no-ops, and retire-blocking can be reasoned about, and
/// tested, on its own.
///
/// Nothing here is fuzzy: comparisons are on the normalized form and are exact.
/// </summary>
public sealed record ReferenceChangePlan
{
    public required ReferenceChangePlanOutcome Outcome { get; init; }

    /// <summary>The tidied display name a create or rename establishes; empty for every other kind and every refusal.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The normalized form of <see cref="DisplayName"/>, computed once here so no executor re-normalizes.</summary>
    public string NormalizedName { get; init; } = string.Empty;

    /// <summary>The full ordered term set a Unit creation establishes - canonical first, then aliases. Empty for every other kind.</summary>
    public IReadOnlyList<UnitTerm> Terms { get; init; } = [];

    /// <summary>The single term an alias add establishes or an alias removal ends; null for every other kind.</summary>
    public UnitTerm? Term { get; init; }

    /// <summary>
    /// Plans creating a Unit. <paramref name="activeNormalizedTerms"/> is every normalized term that
    /// currently identifies an active Unit anywhere in this Inventory.
    /// </summary>
    public static ReferenceChangePlan ForCreateUnit(
        string? canonicalName, IReadOnlyList<string> aliases, IReadOnlySet<string> activeNormalizedTerms)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(activeNormalizedTerms);

        if (!TryTidy(canonicalName, Unit.MaxNameLength, out var name))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var terms = new List<UnitTerm> { UnitTerm.Create(name, isCanonical: true, isReserved: false) };

        foreach (var alias in aliases)
        {
            if (!TryTidy(alias, Unit.MaxNameLength, out var tidyAlias))
            {
                return Refused(ReferenceChangePlanOutcome.InvalidName);
            }

            terms.Add(UnitTerm.Create(tidyAlias, isCanonical: false, isReserved: false));
        }

        // Two collisions matter and they are the same failure: a term already identifying an active
        // Unit, and one this very creation would claim twice. Left to the database both are a unique
        // index violation halfway through a transaction; refused here, both are one plain answer.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (activeNormalizedTerms.Contains(term.NormalizedTerm) || !claimed.Add(term.NormalizedTerm))
            {
                return Refused(ReferenceChangePlanOutcome.TermInUse);
            }
        }

        return new ReferenceChangePlan
        {
            Outcome = ReferenceChangePlanOutcome.Planned,
            DisplayName = name,
            NormalizedName = terms[0].NormalizedTerm,
            Terms = terms,
        };
    }

    /// <summary>
    /// Plans renaming a Unit. <paramref name="otherActiveNormalizedTerms"/> is every normalized term
    /// identifying an active Unit here <em>except</em> this Unit's own canonical term - its own
    /// aliases are included, because promoting an alias to canonical would be a reference merge, and
    /// merging is out of scope.
    /// </summary>
    public static ReferenceChangePlan ForRenameUnit(
        bool isReserved,
        string currentDisplayName,
        string currentNormalizedName,
        string? newName,
        IReadOnlySet<string> otherActiveNormalizedTerms)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNormalizedName);
        ArgumentNullException.ThrowIfNull(otherActiveNormalizedTerms);

        if (isReserved)
        {
            return Refused(ReferenceChangePlanOutcome.ReservedUnit);
        }

        if (!TryTidy(newName, Unit.MaxNameLength, out var name))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        return Rename(currentDisplayName, currentNormalizedName, name, otherActiveNormalizedTerms, ReferenceChangePlanOutcome.TermInUse);
    }

    /// <summary>
    /// Plans adding one alias. <paramref name="unitTerms"/> is what the target Unit already carries;
    /// <paramref name="otherActiveNormalizedTerms"/> is every active term belonging to a
    /// <em>different</em> Unit here.
    /// </summary>
    public static ReferenceChangePlan ForAddUnitAlias(
        string? alias, IReadOnlyList<UnitTerm> unitTerms, IReadOnlySet<string> otherActiveNormalizedTerms)
    {
        ArgumentNullException.ThrowIfNull(unitTerms);
        ArgumentNullException.ThrowIfNull(otherActiveNormalizedTerms);

        if (!TryTidy(alias, Unit.MaxNameLength, out var tidyAlias))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var normalized = NameNormalization.Normalize(tidyAlias);

        // A term the Unit already answers to - canonical or alias - would change nothing at all.
        if (unitTerms.Any(term => term.NormalizedTerm == normalized))
        {
            return Refused(ReferenceChangePlanOutcome.NoChange);
        }

        // A term identifying another active Unit cannot be taken. This is also exactly why a reserved
        // term can never be reassigned: `each`, `piece`, `pieces`, `pc`, and `pcs` are always active
        // terms of the reserved Unit, so they are always in this set for every other Unit.
        if (otherActiveNormalizedTerms.Contains(normalized))
        {
            return Refused(ReferenceChangePlanOutcome.TermInUse);
        }

        return new ReferenceChangePlan
        {
            Outcome = ReferenceChangePlanOutcome.Planned,
            Term = UnitTerm.Create(tidyAlias, isCanonical: false, isReserved: false),
        };
    }

    /// <summary>Plans removing one alias from the terms the target Unit carries.</summary>
    public static ReferenceChangePlan ForRemoveUnitAlias(string? alias, IReadOnlyList<UnitTerm> unitTerms)
    {
        ArgumentNullException.ThrowIfNull(unitTerms);

        if (!TryTidy(alias, Unit.MaxNameLength, out var tidyAlias))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var normalized = NameNormalization.Normalize(tidyAlias);
        var existing = unitTerms.FirstOrDefault(term => term.NormalizedTerm == normalized);

        return existing switch
        {
            null => Refused(ReferenceChangePlanOutcome.AliasNotFound),
            { IsCanonical: true } => Refused(ReferenceChangePlanOutcome.CanonicalTerm),
            { IsReserved: true } => Refused(ReferenceChangePlanOutcome.ReservedTerm),
            _ => new ReferenceChangePlan { Outcome = ReferenceChangePlanOutcome.Planned, Term = existing },
        };
    }

    /// <summary>Plans retiring a Unit. Retire withdraws an <em>unused</em> reference; it never rewrites stock.</summary>
    public static ReferenceChangePlan ForRetireUnit(bool isReserved, int stockReferenceCount)
    {
        if (isReserved)
        {
            return Refused(ReferenceChangePlanOutcome.ReservedUnit);
        }

        return stockReferenceCount > 0
            ? Refused(ReferenceChangePlanOutcome.ReferenceInUse)
            : new ReferenceChangePlan { Outcome = ReferenceChangePlanOutcome.Planned };
    }

    /// <summary>Plans creating a Location. <paramref name="activeNormalizedNames"/> is every active Location name here.</summary>
    public static ReferenceChangePlan ForCreateLocation(string? name, IReadOnlySet<string> activeNormalizedNames)
    {
        ArgumentNullException.ThrowIfNull(activeNormalizedNames);

        if (!TryTidy(name, Location.MaxNameLength, out var displayName))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        var normalized = NameNormalization.Normalize(displayName);

        return activeNormalizedNames.Contains(normalized)
            ? Refused(ReferenceChangePlanOutcome.NameInUse)
            : new ReferenceChangePlan
            {
                Outcome = ReferenceChangePlanOutcome.Planned,
                DisplayName = displayName,
                NormalizedName = normalized,
            };
    }

    /// <summary>
    /// Plans renaming a Location. <paramref name="otherActiveNormalizedNames"/> is every active
    /// Location name here except this Location's own.
    /// </summary>
    public static ReferenceChangePlan ForRenameLocation(
        string currentDisplayName,
        string currentNormalizedName,
        string? newName,
        IReadOnlySet<string> otherActiveNormalizedNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentNormalizedName);
        ArgumentNullException.ThrowIfNull(otherActiveNormalizedNames);

        if (!TryTidy(newName, Location.MaxNameLength, out var displayName))
        {
            return Refused(ReferenceChangePlanOutcome.InvalidName);
        }

        return Rename(
            currentDisplayName, currentNormalizedName, displayName, otherActiveNormalizedNames, ReferenceChangePlanOutcome.NameInUse);
    }

    /// <summary>Plans retiring a Location. Unlocated stock is the absence of a reference, so it is never a target here.</summary>
    public static ReferenceChangePlan ForRetireLocation(int stockReferenceCount) => stockReferenceCount > 0
        ? Refused(ReferenceChangePlanOutcome.ReferenceInUse)
        : new ReferenceChangePlan { Outcome = ReferenceChangePlanOutcome.Planned };

    /// <summary>
    /// The rename decision both kinds share. Only case and whitespace are normalized away, so a new
    /// name whose normalized form is unchanged can collide with nothing but the reference itself: the
    /// displayed name changes and the identity is untouched.
    /// </summary>
    private static ReferenceChangePlan Rename(
        string currentDisplayName,
        string currentNormalizedName,
        string newDisplayName,
        IReadOnlySet<string> takenNormalizedNames,
        ReferenceChangePlanOutcome collisionOutcome)
    {
        if (string.Equals(currentDisplayName, newDisplayName, StringComparison.Ordinal))
        {
            return Refused(ReferenceChangePlanOutcome.NoChange);
        }

        var normalized = NameNormalization.Normalize(newDisplayName);

        if (normalized != currentNormalizedName && takenNormalizedNames.Contains(normalized))
        {
            return Refused(collisionOutcome);
        }

        return new ReferenceChangePlan
        {
            Outcome = ReferenceChangePlanOutcome.Planned,
            DisplayName = newDisplayName,
            NormalizedName = normalized,
        };
    }

    /// <summary>
    /// Tidies an untrusted name into the display form that will be stored, or refuses it. Case is
    /// deliberately left exactly as written: <see cref="NameNormalization.Normalize"/> is what
    /// comparison uses, and folding case here would store a name nobody asked for.
    /// </summary>
    private static bool TryTidy(string? value, int maxLength, out string tidy)
    {
        tidy = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var collapsed = NameNormalization.Collapse(value);
        if (collapsed.Length == 0 || collapsed.Length > maxLength)
        {
            return false;
        }

        tidy = collapsed;
        return true;
    }

    private static ReferenceChangePlan Refused(ReferenceChangePlanOutcome outcome) => new() { Outcome = outcome };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ReferenceChangePlanTests"`
Expected: PASS - every case in the class.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ReferenceChangePlan.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceChangePlanTests.cs
git commit -m "feat(inventories): plan every Unit and Location change as a pure rule for #33"
```

---

## Task 4: Order and page the reference catalog deterministically

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ReferenceListQuery.cs`
- Create: `src/MultiChannelAgent.Domain/Inventories/ReferenceListCursor.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceListCursorTests.cs`

Why: `list_units` and `list_locations` must be bounded and stable, and a cursor issued for one must never resume the other. This mirrors the shipped `StockListQuery`/`StockListCursor` pair rather than inventing a second paging idea.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceListCursorTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ReferenceListCursorTests
{
    private static readonly ReferenceOrderKey Key = new("shelf a", "0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void A_cursor_round_trips_its_order_key_and_its_kind()
    {
        var encoded = new ReferenceListCursor(ReferenceKind.Location, Key).Encode();

        Assert.True(ReferenceListCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(ReferenceKind.Location, decoded!.Kind);
        Assert.Equal(Key, decoded.OrderKey);
    }

    [Fact]
    public void An_absent_cursor_decodes_to_starting_from_the_first_page()
    {
        Assert.True(ReferenceListCursor.TryDecode(null, out var decoded));
        Assert.Null(decoded);

        Assert.True(ReferenceListCursor.TryDecode("   ", out var blank));
        Assert.Null(blank);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("!!!!")]
    public void A_malformed_cursor_is_refused(string cursor) =>
        Assert.False(ReferenceListCursor.TryDecode(cursor, out _));

    [Fact]
    public void A_cursor_issued_for_Units_can_never_resume_a_Location_list()
    {
        var encoded = new ReferenceListCursor(ReferenceKind.Unit, Key).Encode();

        Assert.True(ReferenceListCursor.TryDecode(encoded, out var decoded));
        Assert.False(decoded!.Matches(ReferenceKind.Location));
        Assert.True(decoded.Matches(ReferenceKind.Unit));
    }

    [Fact]
    public void A_query_defaults_to_a_bounded_page_and_no_cursor()
    {
        var query = ReferenceListQuery.Create(new InventoryId(Guid.NewGuid()), ReferenceKind.Unit, pageSize: null, cursor: null);

        Assert.Equal(ReferenceListQuery.DefaultPageSize, query.PageSize);
        Assert.Null(query.Cursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ReferenceListQuery.MaxPageSize + 1)]
    public void A_page_size_outside_the_bound_is_refused(int pageSize)
    {
        var invalid = Assert.Throws<ArgumentException>(() => ReferenceListQuery.Create(
            new InventoryId(Guid.NewGuid()), ReferenceKind.Unit, pageSize, cursor: null));

        Assert.Equal("pageSize", invalid.ParamName);
    }

    [Fact]
    public void A_cursor_from_the_other_kind_is_refused_by_the_query()
    {
        var encoded = new ReferenceListCursor(ReferenceKind.Unit, Key).Encode();

        var invalid = Assert.Throws<ArgumentException>(() => ReferenceListQuery.Create(
            new InventoryId(Guid.NewGuid()), ReferenceKind.Location, pageSize: null, encoded));

        Assert.Equal("cursor", invalid.ParamName);
    }

    [Fact]
    public void An_order_key_orders_by_normalized_name_then_identity_ordinally()
    {
        var keys = new[]
        {
            new ReferenceOrderKey("shelf b", "00000000-0000-0000-0000-000000000001"),
            new ReferenceOrderKey("shelf a", "ffffffff-ffff-ffff-ffff-ffffffffffff"),
            new ReferenceOrderKey("shelf a", "00000000-0000-0000-0000-000000000002"),
        };

        var ordered = keys.OrderBy(key => key, ReferenceOrderKey.Comparer).ToList();

        Assert.Equal("shelf a", ordered[0].NormalizedName);
        Assert.Equal("00000000-0000-0000-0000-000000000002", ordered[0].IdOrderKey);
        Assert.Equal("shelf a", ordered[1].NormalizedName);
        Assert.Equal("shelf b", ordered[2].NormalizedName);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ReferenceListCursorTests"`
Expected: FAIL to compile - `ReferenceListCursor`, `ReferenceOrderKey`, and `ReferenceListQuery` do not exist.

- [ ] **Step 3: Write the query**

Create `src/MultiChannelAgent.Domain/Inventories/ReferenceListQuery.cs`:

```csharp
namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The deterministic display order both catalog lists share: normalized name, then identity, both
/// compared ordinally so the database's order is the domain's order rather than a locale-dependent
/// approximation of it.
///
/// The normalized name alone is already unique among the active references of one Inventory - the
/// filtered unique indexes guarantee it - so <see cref="IdOrderKey"/> never actually decides an
/// order for valid data; it exists so the key stays total, exactly as
/// <see cref="StockEntryOrderKey.IdOrderKey"/> does.
/// </summary>
public sealed record ReferenceOrderKey(string NormalizedName, string IdOrderKey)
{
    public static readonly IComparer<ReferenceOrderKey> Comparer = Comparer<ReferenceOrderKey>.Create((left, right) =>
    {
        var byName = string.CompareOrdinal(left.NormalizedName, right.NormalizedName);

        return byName != 0 ? byName : string.CompareOrdinal(left.IdOrderKey, right.IdOrderKey);
    });
}

/// <summary>
/// One bounded, validated catalog read. Callers only ever supply an InventoryId already scoped by
/// trusted context; this type owns the bounds, and refuses a cursor issued for the other kind of
/// reference so a page marker can only ever continue the question that produced it.
/// </summary>
public sealed record ReferenceListQuery
{
    public const int DefaultPageSize = 20;

    public const int MaxPageSize = 50;

    public required InventoryId InventoryId { get; init; }

    public required ReferenceKind Kind { get; init; }

    public required int PageSize { get; init; }

    public ReferenceListCursor? Cursor { get; init; }

    public static ReferenceListQuery Create(InventoryId inventoryId, ReferenceKind kind, int? pageSize, string? cursor)
    {
        var boundedPageSize = pageSize ?? DefaultPageSize;
        if (boundedPageSize < 1 || boundedPageSize > MaxPageSize)
        {
            throw new ArgumentException($"Page size must be between 1 and {MaxPageSize}.", nameof(pageSize));
        }

        if (!ReferenceListCursor.TryDecode(cursor, out var decoded))
        {
            throw new ArgumentException("Cursor is not a valid reference list cursor.", nameof(cursor));
        }

        if (decoded is not null && !decoded.Matches(kind))
        {
            throw new ArgumentException("Cursor was issued for a different reference list.", nameof(cursor));
        }

        return new ReferenceListQuery
        {
            InventoryId = inventoryId,
            Kind = kind,
            PageSize = boundedPageSize,
            Cursor = decoded,
        };
    }
}
```

- [ ] **Step 4: Write the cursor**

Create `src/MultiChannelAgent.Domain/Inventories/ReferenceListCursor.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// An opaque, deterministic keyset cursor for a catalog list: the last returned row's
/// <see cref="ReferenceOrderKey"/> together with the <see cref="ReferenceKind"/> it was issued for.
/// A list always resumes strictly after that exact key, so paging stays stable as unrelated
/// references are created - and only ever within the same question, because a cursor issued for
/// Units is refused by a Location list.
///
/// The wire form is base64url JSON: opaque to callers, but not intended to hide anything - it
/// carries the same fields already visible in the row it was derived from.
/// </summary>
public sealed record ReferenceListCursor(ReferenceKind Kind, ReferenceOrderKey OrderKey)
{
    /// <summary>Bumped whenever this cursor's payload shape changes, so an old cursor is refused rather than misread.</summary>
    public const int Version = 1;

    public bool Matches(ReferenceKind kind) => Kind == kind;

    public string Encode()
    {
        var json = JsonSerializer.Serialize(new CursorPayload(Version, Kind.ToString(), OrderKey.NormalizedName, OrderKey.IdOrderKey));
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Decodes <paramref name="cursor"/>. A null or blank cursor decodes successfully to an absent
    /// cursor (<paramref name="result"/> is null, meaning "start from the first page") rather than
    /// being treated as invalid; only a non-blank value that fails to decode returns false.
    /// </summary>
    public static bool TryDecode(string? cursor, out ReferenceListCursor? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            var padded = base64.Length % 4 == 0 ? base64 : base64 + new string('=', 4 - (base64.Length % 4));
            var payload = JsonSerializer.Deserialize<CursorPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(padded)));

            if (payload is null
                || payload.Version != Version
                || !Enum.TryParse<ReferenceKind>(payload.Kind, ignoreCase: false, out var kind)
                || string.IsNullOrEmpty(payload.NormalizedName)
                || string.IsNullOrEmpty(payload.IdOrderKey))
            {
                return false;
            }

            result = new ReferenceListCursor(kind, new ReferenceOrderKey(payload.NormalizedName, payload.IdOrderKey));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("k")] string Kind,
        [property: JsonPropertyName("n")] string NormalizedName,
        [property: JsonPropertyName("i")] string IdOrderKey);
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ReferenceListCursorTests"`
Expected: PASS, 11 tests.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ReferenceListQuery.cs \
        src/MultiChannelAgent.Domain/Inventories/ReferenceListCursor.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ReferenceListCursorTests.cs
git commit -m "feat(inventories): order and page the Unit and Location catalog deterministically for #33"
```

---

## Task 5: Carry reference changes in the shipped proposal without weakening stock proposals

**Files:**
- Modify: `src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs`

Why: #26 requires **one** pending proposal per Participant and ChannelConversation "across stock, import, and administration". Two proposal types would be two slots, two token vocabularies, and two lifecycles. One aggregate with two payloads keeps the single slot, the ten-minute single-use token, the binding predicate, and the whole `ProposalStatus` state machine exactly as #32 shipped them - and the shipped stock factory keeps every invariant it enforces today, because `CreateForReferences` never touches it.

- [ ] **Step 1: Write the failing test**

Append to `tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs`, inside the existing class:

```csharp
    private static ProposedReferenceChange RetireUnitChange(Guid unitId, int order = 1) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.RetireUnit,
        Target = new ProposedReferenceState(ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", Reserved: false),
    };

    private static ProposedReferenceChange CreateLocationChange(Guid locationId, int order = 1) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.CreateLocation,
        Target = new ProposedReferenceState(ReferenceKind.Location, locationId, "Shelf A", "shelf a", Reserved: false),
    };

    private static ConfirmationProposal ReferenceProposal(
        IReadOnlyList<ProposedReferenceChange> changes, IReadOnlyList<ExpectedReferenceVersion> versions) =>
        ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(Guid.NewGuid()),
            "web-conversation-1",
            new InventoryId(Guid.NewGuid()),
            new Domain.Turns.TurnId(Guid.NewGuid()),
            changes,
            versions,
            [],
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_reference_proposal_carries_its_changes_and_no_stock_at_all()
    {
        var unitId = Guid.NewGuid();

        var proposal = ReferenceProposal(
            [RetireUnitChange(unitId)],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())]);

        Assert.Equal(ProposalKind.ReferenceAdministration, proposal.Kind);
        Assert.Single(proposal.ReferenceChanges);
        Assert.Empty(proposal.Changes);
        Assert.Empty(proposal.ExpectedVersions);
        Assert.Empty(proposal.ExpectedAbsences);
    }

    [Fact]
    public void A_reference_proposal_shares_the_shipped_ten_minute_single_use_lifetime()
    {
        var unitId = Guid.NewGuid();

        var proposal = ReferenceProposal(
            [RetireUnitChange(unitId)],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())]);

        Assert.Equal(proposal.CreatedAt.AddMinutes(ConfirmationProposal.LifetimeMinutes), proposal.ExpiresAt);
        Assert.False(proposal.IsExpired(proposal.ExpiresAt.AddTicks(-1)));
        Assert.True(proposal.IsExpired(proposal.ExpiresAt));
    }

    [Fact]
    public void Every_existing_reference_a_proposal_touches_must_carry_an_expected_version()
    {
        var invalid = Assert.Throws<ArgumentException>(() => ReferenceProposal([RetireUnitChange(Guid.NewGuid())], []));

        Assert.Equal("expectedReferenceVersions", invalid.ParamName);
    }

    [Fact]
    public void A_reference_a_proposal_creates_needs_no_expected_version_because_it_does_not_exist_yet()
    {
        var proposal = ReferenceProposal([CreateLocationChange(Guid.NewGuid())], []);

        Assert.Single(proposal.ReferenceChanges);
    }

    [Fact]
    public void A_reference_proposal_must_carry_at_least_one_change() =>
        Assert.Throws<ArgumentException>(() => ReferenceProposal([], []));

    [Fact]
    public void A_reference_proposal_must_not_exceed_the_reviewable_bound()
    {
        var changes = Enumerable
            .Range(1, ConfirmationProposal.MaxChanges + 1)
            .Select(order => CreateLocationChange(Guid.NewGuid(), order))
            .ToList();

        Assert.Throws<ArgumentException>(() => ReferenceProposal(changes, []));
    }

    [Fact]
    public void Reference_change_order_must_be_unique()
    {
        var changes = new[] { CreateLocationChange(Guid.NewGuid()), CreateLocationChange(Guid.NewGuid()) };

        Assert.Throws<ArgumentException>(() => ReferenceProposal(changes, []));
    }

    [Fact]
    public void A_proposal_that_retires_anything_demands_the_Owner()
    {
        var unitId = Guid.NewGuid();

        var retiring = ReferenceProposal(
            [RetireUnitChange(unitId)],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())]);
        var creating = ReferenceProposal([CreateLocationChange(Guid.NewGuid())], []);

        Assert.Equal(MembershipRole.Owner, retiring.RequiredRole);
        Assert.Equal(MembershipRole.Editor, creating.RequiredRole);
    }

    [Fact]
    public void A_reference_proposal_names_every_identity_a_retirement_would_have_to_invalidate()
    {
        var unitId = Guid.NewGuid();
        var locationId = Guid.NewGuid();

        var proposal = ReferenceProposal(
            [RetireUnitChange(unitId), CreateLocationChange(locationId, order: 2)],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())]);

        Assert.Equal([new UnitId(unitId)], proposal.ReferencedUnitIds);
        Assert.Equal([new LocationId(locationId)], proposal.ReferencedLocationIds);
    }

    [Fact]
    public void A_reference_proposal_executes_under_its_own_ledger_identity()
    {
        var unitId = Guid.NewGuid();

        var proposal = ReferenceProposal(
            [RetireUnitChange(unitId)],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())]);

        Assert.Equal(ReferenceOperationId.DeriveForProposal(proposal.Id), proposal.ReferenceExecutionOperationId);
        Assert.NotEqual(proposal.ExecutionOperationId.Value, proposal.ReferenceExecutionOperationId.Value);
    }
```

Also append the two assertions that keep the shipped stock path honest. Reuse whichever helper the existing class already has for building a stock proposal (it is the one every shipped test in this file uses):

```csharp
    [Fact]
    public void A_stock_proposal_is_still_a_stock_proposal_and_still_demands_only_an_Editor()
    {
        var proposal = ProposalWithOneChange();

        Assert.Equal(ProposalKind.Stock, proposal.Kind);
        Assert.Equal(MembershipRole.Editor, proposal.RequiredRole);
        Assert.Empty(proposal.ReferenceChanges);
        Assert.Empty(proposal.ExpectedReferenceVersions);
        Assert.Empty(proposal.ExpectedTermAbsences);
    }

    [Fact]
    public void A_stock_proposal_names_every_Unit_and_Location_it_depends_on()
    {
        var proposal = ProposalWithOneChange();
        var change = proposal.Changes[0];

        Assert.Contains(change.Source.UnitId, proposal.ReferencedUnitIds);
        if (change.Source.LocationId is { } locationId)
        {
            Assert.Contains(locationId, proposal.ReferencedLocationIds);
        }
    }
```

If the class has no such helper under that exact name, add a private `ProposalWithOneChange()` that builds a one-change stock proposal exactly the way the neighbouring shipped tests already build theirs, and use it from both new tests.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ConfirmationProposalTests"`
Expected: FAIL to compile - `ProposalKind`, `ProposedReferenceState`, `ProposedReferenceChange`, `ExpectedReferenceVersion`, `CreateForReferences`, `RequiredRole`, `ReferencedUnitIds`, `ReferencedLocationIds`, and `ReferenceExecutionOperationId` do not exist.

- [ ] **Step 3: Add the reference payload types**

In `src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs`, insert these declarations immediately above `public sealed record ConfirmationProposal`:

```csharp
/// <summary>
/// Which kind of work one stored proposal describes. The two payloads are disjoint: a
/// <see cref="Stock"/> proposal carries only stock changes, and a
/// <see cref="ReferenceAdministration"/> proposal carries only reference changes. They share
/// everything else - the single pending slot per Participant and ChannelConversation, the ten-minute
/// single-use token, the binding predicate, and the whole status state machine - which is exactly
/// what "one pending proposal across stock, import, and administration" means.
/// </summary>
public enum ProposalKind
{
    Stock,
    ReferenceAdministration,
}

/// <summary>
/// The exact reference one administration change acts on, as it stood when the proposal was made.
/// For a change that <em>creates</em> a reference, <see cref="ReferenceId"/> is the identity the
/// execution will mint - decided here, at proposal time - so confirming creates exactly the identity
/// that was reviewed rather than a fresh one.
/// </summary>
public sealed record ProposedReferenceState(
    ReferenceKind Kind, Guid ReferenceId, string Name, string NormalizedName, bool Reserved);

/// <summary>
/// One exactly-decided administration change. Which fields carry meaning is fixed by
/// <see cref="Kind"/>; the executor switches on it and reads only those:
///
/// <list type="bullet">
/// <item><c>CreateUnit</c>: <see cref="Terms"/> (canonical first, then aliases).</item>
/// <item><c>RenameUnit</c> / <c>RenameLocation</c>: <see cref="NewName"/> and <see cref="NewNormalizedName"/>.</item>
/// <item><c>AddUnitAlias</c> / <c>RemoveUnitAlias</c>: <see cref="Term"/>.</item>
/// <item><c>CreateLocation</c>: nothing beyond <see cref="Target"/>, which already carries the name.</item>
/// <item><c>RetireUnit</c> / <c>RetireLocation</c>: nothing beyond <see cref="Target"/>.</item>
/// </list>
/// </summary>
public sealed record ProposedReferenceChange
{
    /// <summary>1-based position within the proposal. Execution follows it, so effects apply in the order the Participant reviewed.</summary>
    public required int Order { get; init; }

    public required ReferenceChangeKind Kind { get; init; }

    public required ProposedReferenceState Target { get; init; }

    /// <summary>The exact new display name; set only for <c>RenameUnit</c> and <c>RenameLocation</c>.</summary>
    public string? NewName { get; init; }

    /// <summary>The normalized form of <see cref="NewName"/>, computed once while planning so the executor never re-normalizes.</summary>
    public string? NewNormalizedName { get; init; }

    /// <summary>The single term an alias add establishes or an alias removal ends.</summary>
    public UnitTerm? Term { get; init; }

    /// <summary>The full ordered term set a Unit creation establishes, canonical first.</summary>
    public IReadOnlyList<UnitTerm> Terms { get; init; } = [];

    /// <summary>True when this change brings a reference into existence, so there is no version to pin and nothing to lock.</summary>
    public bool CreatesReference =>
        Kind is ReferenceChangeKind.CreateUnit or ReferenceChangeKind.CreateLocation;

    /// <summary>True when this change withdraws a reference, which is what makes the whole proposal Owner-only and confirmable.</summary>
    public bool RetiresReference => ReferenceAdministrationFacts.RequiresConfirmation(Kind);
}

/// <summary>
/// The version one existing Unit or Location carried when the proposal was made. Execution refuses
/// unless the row still carries it, so a proposal decided against a state nobody holds any more can
/// never land - exactly as <see cref="ExpectedEntryVersion"/> does for a Stock Entry.
/// </summary>
public sealed record ExpectedReferenceVersion(ReferenceKind Kind, Guid ReferenceId, Guid ConcurrencyStamp);

/// <summary>
/// A normalized term - a Unit term or a Location name - the proposal expects to still be free,
/// because it intends to claim it. Enforced at execution by the same filtered unique indexes that
/// define the namespace, so a competing writer that claimed it first turns into a typed conflict
/// rather than a duplicate.
/// </summary>
public sealed record ExpectedTermAbsence(ReferenceKind Kind, string NormalizedTerm);
```

- [ ] **Step 4: Widen the aggregate**

In the same file, add these members to `ConfirmationProposal`, immediately after `ExpectedAbsences`:

```csharp
    /// <summary>Which payload this proposal carries. Set by the factory, never by a caller.</summary>
    public required ProposalKind Kind { get; init; }

    /// <summary>The exact administration changes; empty for a stock proposal.</summary>
    public IReadOnlyList<ProposedReferenceChange> ReferenceChanges { get; init; } = [];

    /// <summary>The versions every existing Unit and Location this proposal touches carried when it was made; empty for a stock proposal.</summary>
    public IReadOnlyList<ExpectedReferenceVersion> ExpectedReferenceVersions { get; init; } = [];

    /// <summary>The normalized terms this proposal expects to still be free; empty for a stock proposal.</summary>
    public IReadOnlyList<ExpectedTermAbsence> ExpectedTermAbsences { get; init; } = [];
```

and these derived members, immediately after `ExecutionOperationId`:

```csharp
    /// <summary>
    /// The reference ledger identity this proposal's execution is recorded under. Like
    /// <see cref="ExecutionOperationId"/> it is derived from the proposal rather than from whichever
    /// Turn confirms it, and its hash material is shaped so it can never equal a stock identity.
    /// </summary>
    public ReferenceOperationId ReferenceExecutionOperationId => ReferenceOperationId.DeriveForProposal(Id);

    /// <summary>
    /// The least Membership role a Participant must still hold to execute this proposal. Only a
    /// Retire raises it, so every stock proposal reports Editor and the shipped confirmation path is
    /// unchanged by this ticket.
    /// </summary>
    public MembershipRole RequiredRole => ReferenceChanges.Any(change => change.RetiresReference)
        ? MembershipRole.Owner
        : MembershipRole.Editor;

    /// <summary>
    /// Every Unit this proposal depends on. Retiring one of them must settle this proposal, because
    /// what it describes could no longer be applied - and that is true of a stock proposal that would
    /// create stock at a Unit just as much as of an administration proposal that would rename one.
    /// </summary>
    public IReadOnlyList<UnitId> ReferencedUnitIds =>
    [
        .. Changes
            .SelectMany(change => new[] { (UnitId?)change.Source.UnitId, change.Destination?.UnitId })
            .Concat(ExpectedAbsences.Select(absence => (UnitId?)absence.UnitId))
            .Concat(ReferenceChanges
                .Where(change => change.Target.Kind == ReferenceKind.Unit)
                .Select(change => (UnitId?)new UnitId(change.Target.ReferenceId)))
            .OfType<UnitId>()
            .Distinct(),
    ];

    /// <summary>Every Location this proposal depends on. See <see cref="ReferencedUnitIds"/>.</summary>
    public IReadOnlyList<LocationId> ReferencedLocationIds =>
    [
        .. Changes
            .SelectMany(change => new[] { change.Source.LocationId, change.Destination?.LocationId })
            .Concat(ExpectedAbsences.Select(absence => absence.LocationId))
            .Concat(ReferenceChanges
                .Where(change => change.Target.Kind == ReferenceKind.Location)
                .Select(change => (LocationId?)new LocationId(change.Target.ReferenceId)))
            .OfType<LocationId>()
            .Distinct(),
    ];
```

- [ ] **Step 5: Stamp the shipped factory and add the new one**

In the same file, inside the existing `Create` factory's returned object initializer, add one line so a stock proposal says what it is (nothing else in `Create` changes - every rule it enforces still runs on exactly the same inputs):

```csharp
            Kind = ProposalKind.Stock,
```

Then add the reference factory immediately after `Create`:

```csharp
    /// <summary>
    /// Creates a reference administration proposal. It enforces the exact parallel of every rule
    /// <see cref="Create"/> enforces for stock - non-empty, bounded by <see cref="MaxChanges"/>,
    /// unique order, and an expected version for every <em>existing</em> reference it touches - over
    /// its own inputs, and passes no stock at all, so no stock invariant is relaxed or bypassed.
    /// </summary>
    public static ConfirmationProposal CreateForReferences(
        ConfirmationTokenHash tokenHash,
        ParticipantId participantId,
        string? channelConversationId,
        InventoryId inventoryId,
        TurnId proposedInTurnId,
        IReadOnlyList<ProposedReferenceChange> referenceChanges,
        IReadOnlyList<ExpectedReferenceVersion> expectedReferenceVersions,
        IReadOnlyList<ExpectedTermAbsence> expectedTermAbsences,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelConversationId);
        ArgumentNullException.ThrowIfNull(referenceChanges);
        ArgumentNullException.ThrowIfNull(expectedReferenceVersions);
        ArgumentNullException.ThrowIfNull(expectedTermAbsences);

        if (referenceChanges.Count == 0)
        {
            throw new ArgumentException("A proposal must carry at least one change.", nameof(referenceChanges));
        }

        if (referenceChanges.Count > MaxChanges)
        {
            throw new ArgumentException($"A proposal must not carry more than {MaxChanges} changes.", nameof(referenceChanges));
        }

        if (referenceChanges.Select(change => change.Order).Distinct().Count() != referenceChanges.Count)
        {
            throw new ArgumentException("Change order must be unique within a proposal.", nameof(referenceChanges));
        }

        // Every reference that already exists must be pinned to the version this proposal was decided
        // against. A reference this proposal creates has no version to pin: its safety comes from an
        // expected term absence and the filtered uniqueness index.
        var versioned = expectedReferenceVersions.Select(version => (version.Kind, version.ReferenceId)).ToHashSet();
        foreach (var change in referenceChanges.Where(change => !change.CreatesReference))
        {
            if (!versioned.Contains((change.Target.Kind, change.Target.ReferenceId)))
            {
                throw new ArgumentException(
                    "Every existing Unit or Location a proposal touches must carry an expected version.",
                    nameof(expectedReferenceVersions));
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
            Kind = ProposalKind.ReferenceAdministration,
            Changes = [],
            ExpectedVersions = [],
            ExpectedAbsences = [],
            ReferenceChanges = referenceChanges.OrderBy(change => change.Order).ToList(),
            ExpectedReferenceVersions = expectedReferenceVersions.ToList(),
            ExpectedTermAbsences = expectedTermAbsences.ToList(),
            CreatedAt = createdAt,
        };
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj`
Expected: FAIL - `ConfirmationProposalMapper.ToDomain` in Infrastructure does not set the now-required `Kind`, so the solution does not compile. Fix that one line now rather than deferring it:

In `src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs`, inside `ToDomain`'s returned object initializer, add:

```csharp
            Kind = ProposalKind.Stock,
```

That is deliberately temporary and correct: nothing writes a reference proposal yet. Task 14 replaces it with the real read.

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj`
Expected: PASS, including every shipped `ConfirmationProposalTests` case unchanged.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ConfirmationProposal.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ConfirmationProposalTests.cs
git commit -m "feat(inventories): carry reference changes in the one pending proposal for #33"
```

---

## Task 6: Read the reference catalog, and stop resolving retired references

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/IReferenceCatalogStore.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/IInventoryReferenceStore.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlReferenceCatalogStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryReferenceStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryReferenceCatalogStore.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryInventoryReferenceStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceCatalogStoreTests.cs`

Why: administration needs reads the stock path never needed - the whole active term namespace, a Unit's own terms, a reference's reserved and retired state, its current version, how many Stock Entries reference it, and bounded suggestions. And the shipped resolver must stop resolving retired references, which is what makes "retired references are unknown" true everywhere at once, including for the later Import slice.

Note: the columns this task reads - `Units.RetiredAt`, `Units.ConcurrencyStamp`, `UnitTerms.IsReserved`, `UnitTerms.RetiredAt`, `Locations.RetiredAt`, `Locations.ConcurrencyStamp` - are added in Task 13. Write this task's SQL against them now; the SQL test at the end of this task will not run until Task 13 lands, so **run only the compile check here and the full SQL test after Task 13**. That ordering is deliberate: the seam's shape is what Tasks 8 and 10 are written against, and inventing it after them would mean rewriting them.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceCatalogStoreTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The SQL-backed catalog reads Unit and Location administration rests on: active-only listing in
/// the deterministic display order, keyset paging, a Unit's own terms with their reserved state,
/// current versions, how many Stock Entries reference something, and bounded deterministic
/// suggestions.
/// </summary>
public sealed class SqlReferenceCatalogStoreTests : SqlIntegrationTestBase
{
    private MultiChannelAgentDbContext Db(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private async Task<(Guid InventoryId, Guid EachUnitId)> SeedInventoryAsync()
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var inventoryId = Guid.NewGuid();
        var participantId = Guid.NewGuid();

        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Catalog Owner",
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryId,
            Name = "Catalog Warehouse",
            NormalizedName = "catalog warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = inventoryId,
            ParticipantId = participantId,
            Role = MembershipRole.Owner,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        var each = Unit.CreateReservedEach(new InventoryId(inventoryId), DateTimeOffset.UnixEpoch);
        db.Units.Add(new UnitEntity
        {
            Id = each.Id.Value,
            InventoryId = inventoryId,
            CanonicalName = each.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(each.CanonicalName),
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        foreach (var term in each.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = each.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = true,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await db.SaveChangesAsync();

        return (inventoryId, each.Id.Value);
    }

    private async Task<Guid> SeedUnitAsync(Guid inventoryId, string canonicalName, string[] aliases, bool retired = false)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var unit = Unit.Create(new InventoryId(inventoryId), canonicalName, aliases, DateTimeOffset.UnixEpoch);
        var retiredAt = retired ? (DateTimeOffset?)DateTimeOffset.UnixEpoch.AddDays(1) : null;

        db.Units.Add(new UnitEntity
        {
            Id = unit.Id.Value,
            InventoryId = inventoryId,
            CanonicalName = unit.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(unit.CanonicalName),
            IsReserved = false,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            RetiredAt = retiredAt,
        });

        foreach (var term in unit.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = inventoryId,
                UnitId = unit.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = false,
                CreatedAt = DateTimeOffset.UnixEpoch,
                RetiredAt = retiredAt,
            });
        }

        await db.SaveChangesAsync();

        return unit.Id.Value;
    }

    private async Task<Guid> SeedLocationAsync(Guid inventoryId, string name, bool retired = false)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        var location = Location.Create(new InventoryId(inventoryId), name, DateTimeOffset.UnixEpoch);

        db.Locations.Add(new LocationEntity
        {
            Id = location.Id.Value,
            InventoryId = inventoryId,
            Name = location.Name,
            NormalizedName = location.NormalizedName,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
            RetiredAt = retired ? DateTimeOffset.UnixEpoch.AddDays(1) : null,
        });

        await db.SaveChangesAsync();

        return location.Id.Value;
    }

    private async Task SeedStockAsync(Guid inventoryId, Guid unitId, Guid? locationId, string name)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unitId,
            LocationId = locationId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = 1m,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Listing_Units_returns_active_ones_in_display_order_with_their_active_aliases()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes", "bx"]);
        await SeedUnitAsync(inventoryId, "Pallet", [], retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var page = await store.ListUnitsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Unit, pageSize: null, cursor: null),
            CancellationToken.None);

        Assert.Equal(["Cardboard Box", "each"], page.Select(row => row.CanonicalName));
        Assert.Equal(["boxes", "bx"], page[0].Aliases);
        Assert.Equal(["pc", "pcs", "piece", "pieces"], page[1].Aliases.OrderBy(alias => alias, StringComparer.Ordinal));
    }

    [SkippableFact]
    public async Task Listing_Units_pages_by_keyset_without_repeating_or_skipping_a_row()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedUnitAsync(inventoryId, "Alpha", []);
        await SeedUnitAsync(inventoryId, "Bravo", []);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var first = await store.ListUnitsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Unit, pageSize: 2, cursor: null),
            CancellationToken.None);

        Assert.Equal(["Alpha", "Bravo"], first.Take(2).Select(row => row.CanonicalName));

        var cursor = new ReferenceListCursor(
            ReferenceKind.Unit, new ReferenceOrderKey(first[1].NormalizedCanonicalName, first[1].Id.Value.ToString("D"))).Encode();

        var second = await store.ListUnitsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Unit, pageSize: 2, cursor),
            CancellationToken.None);

        Assert.Equal(["each"], second.Select(row => row.CanonicalName));
    }

    [SkippableFact]
    public async Task Listing_Locations_returns_active_ones_in_display_order()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedLocationAsync(inventoryId, "Shelf B");
        await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedLocationAsync(inventoryId, "Old Bay", retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var page = await store.ListLocationsAsync(
            ReferenceListQuery.Create(new InventoryId(inventoryId), ReferenceKind.Location, pageSize: null, cursor: null),
            CancellationToken.None);

        Assert.Equal(["Shelf A", "Shelf B"], page.Select(row => row.Name));
    }

    [SkippableFact]
    public async Task Finding_a_Unit_for_administration_reports_its_terms_its_reserved_state_and_its_version()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var box = await store.FindUnitAsync(new InventoryId(inventoryId), new UnitId(boxId), CancellationToken.None);
        var each = await store.FindUnitAsync(new InventoryId(inventoryId), new UnitId(eachUnitId), CancellationToken.None);

        Assert.NotNull(box);
        Assert.False(box!.IsReserved);
        Assert.NotEqual(Guid.Empty, box.ConcurrencyStamp);
        Assert.Equal(["Cardboard Box", "boxes"], box.Terms.Select(term => term.Term));
        Assert.True(box.Terms[0].IsCanonical);

        Assert.NotNull(each);
        Assert.True(each!.IsReserved);
        Assert.All(each.Terms, term => Assert.True(term.IsReserved));
    }

    [SkippableFact]
    public async Task A_retired_reference_is_not_found_for_administration_either()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var palletId = await SeedUnitAsync(inventoryId, "Pallet", [], retired: true);
        var bayId = await SeedLocationAsync(inventoryId, "Old Bay", retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        Assert.Null(await store.FindUnitAsync(new InventoryId(inventoryId), new UnitId(palletId), CancellationToken.None));
        Assert.Null(await store.FindLocationAsync(new InventoryId(inventoryId), new LocationId(bayId), CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_active_term_namespace_excludes_retired_terms_and_can_exclude_one_Units_own()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);
        await SeedUnitAsync(inventoryId, "Pallet", ["pallets"], retired: true);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var all = await store.ReadActiveUnitTermsAsync(new InventoryId(inventoryId), excluding: null, CancellationToken.None);
        var others = await store.ReadActiveUnitTermsAsync(new InventoryId(inventoryId), new UnitId(boxId), CancellationToken.None);

        Assert.Contains("cardboard box", all);
        Assert.Contains("each", all);
        Assert.DoesNotContain("pallet", all);
        Assert.DoesNotContain("pallets", all);

        Assert.DoesNotContain("cardboard box", others);
        Assert.Contains("boxes", others);
        Assert.Contains("each", others);
    }

    [SkippableFact]
    public async Task Counting_Stock_references_answers_exactly_what_blocks_a_Retire()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedStockAsync(inventoryId, eachUnitId, shelfId, "Steel Bolts");

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        Assert.Equal(1, await store.CountStockReferencesAsync(
            new InventoryId(inventoryId), ReferenceKind.Unit, eachUnitId, CancellationToken.None));
        Assert.Equal(0, await store.CountStockReferencesAsync(
            new InventoryId(inventoryId), ReferenceKind.Unit, boxId, CancellationToken.None));
        Assert.Equal(1, await store.CountStockReferencesAsync(
            new InventoryId(inventoryId), ReferenceKind.Location, shelfId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Suggestions_are_bounded_deterministic_and_never_fuzzy()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed catalog read.");

        var (inventoryId, _) = await SeedInventoryAsync();
        await SeedUnitAsync(inventoryId, "Box Large", []);
        await SeedUnitAsync(inventoryId, "Box Small", []);
        await SeedUnitAsync(inventoryId, "Crate", []);

        using var scope = Factory!.Services.CreateScope();
        var store = new SqlReferenceCatalogStore(Db(scope));

        var prefixed = await store.SuggestAsync(new InventoryId(inventoryId), ReferenceKind.Unit, "box", CancellationToken.None);
        Assert.Equal(["Box Large", "Box Small"], prefixed);

        // "bx" shares no prefix with anything, so the answer falls back to what actually exists,
        // still bounded and still in the one deterministic order - never a nearest-match guess.
        var fallback = await store.SuggestAsync(new InventoryId(inventoryId), ReferenceKind.Unit, "zzz", CancellationToken.None);
        Assert.Equal(IReferenceCatalogStore.MaxSuggestions, fallback.Count);
        Assert.Equal("Box Large", fallback[0]);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlReferenceCatalogStoreTests"`
Expected: FAIL to compile - `IReferenceCatalogStore`, `SqlReferenceCatalogStore`, and the retirement/version columns do not exist. (The compile failure is the red you need; the assertions cannot run until Task 13 adds the columns.)

- [ ] **Step 3: Define the seam**

Create `src/MultiChannelAgent.Application/Inventories/IReferenceCatalogStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>One active Unit as administration sees it: its identity, its display order key, its full ordered term set, whether it is reserved, and its current version.</summary>
public sealed record UnitCatalogRecord(
    UnitId Id,
    string CanonicalName,
    string NormalizedCanonicalName,
    IReadOnlyList<UnitTerm> Terms,
    bool IsReserved,
    Guid ConcurrencyStamp)
{
    /// <summary>The Unit's aliases, in the order they were added - its canonical name is not one of them.</summary>
    public IReadOnlyList<string> Aliases => [.. Terms.Where(term => !term.IsCanonical).Select(term => term.Term)];
}

/// <summary>One active Location as administration sees it.</summary>
public sealed record LocationCatalogRecord(LocationId Id, string Name, string NormalizedName, Guid ConcurrencyStamp);

/// <summary>
/// Authorized, active-only catalog reads for Unit and Location administration, scoped to one
/// Inventory at a time. Everything here is only ever reached after
/// <see cref="InventoryAuthorizationService"/> has authorized the caller for that Inventory, so this
/// store never itself decides access - and it never returns a retired reference, because a retired
/// reference is exactly as unknown as one that never existed.
/// </summary>
public interface IReferenceCatalogStore
{
    /// <summary>The bound on how many suggestions an unknown reference may offer. Bounded so an answer is reviewable, never a catalog dump.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>
    /// Up to <c>query.PageSize + 1</c> active Units in <see cref="ReferenceOrderKey"/> order,
    /// keyset-paginated strictly after <see cref="ReferenceListQuery.Cursor"/> when present, so the
    /// caller can detect whether more remain without a separate count query.
    /// </summary>
    Task<IReadOnlyList<UnitCatalogRecord>> ListUnitsAsync(ReferenceListQuery query, CancellationToken cancellationToken);

    /// <summary>Up to <c>query.PageSize + 1</c> active Locations. See <see cref="ListUnitsAsync"/>.</summary>
    Task<IReadOnlyList<LocationCatalogRecord>> ListLocationsAsync(ReferenceListQuery query, CancellationToken cancellationToken);

    /// <summary>One active Unit with everything administration needs to plan against it, or null when there is no such active Unit here.</summary>
    Task<UnitCatalogRecord?> FindUnitAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken);

    /// <summary>One active Location, or null when there is no such active Location here.</summary>
    Task<LocationCatalogRecord?> FindLocationAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken);

    /// <summary>
    /// Every normalized term that currently identifies an active Unit in this Inventory. When
    /// <paramref name="excluding"/> names a Unit, that Unit's <em>canonical</em> term is left out -
    /// which is exactly the set a rename must not collide with, since renaming onto its own
    /// canonical form is a display-only change while renaming onto its own alias would be a merge.
    /// </summary>
    Task<IReadOnlySet<string>> ReadActiveUnitTermsAsync(
        InventoryId inventoryId, UnitId? excluding, CancellationToken cancellationToken);

    /// <summary>
    /// Every normalized name that currently identifies an active Location in this Inventory, minus
    /// <paramref name="excluding"/>'s own.
    /// </summary>
    Task<IReadOnlySet<string>> ReadActiveLocationNamesAsync(
        InventoryId inventoryId, LocationId? excluding, CancellationToken cancellationToken);

    /// <summary>
    /// How many Stock Entries in this Inventory reference this Unit or Location. Zero is what makes a
    /// reference retirable; anything else is what blocks it - administration never rewrites stock.
    /// </summary>
    Task<int> CountStockReferencesAsync(
        InventoryId inventoryId, ReferenceKind kind, Guid referenceId, CancellationToken cancellationToken);

    /// <summary>
    /// At most <see cref="MaxSuggestions"/> display names for an unresolved reference: active terms
    /// (or Location names) whose normalized form <em>starts with</em> the normalized reference, in
    /// the one deterministic display order; and when none does, the first
    /// <see cref="MaxSuggestions"/> in that same order.
    ///
    /// Exact-prefix and order only. No edit distance, no phonetics, no ranking - fuzzy matching is
    /// out of scope, and the same input against the same Inventory always yields the same list.
    /// </summary>
    Task<IReadOnlyList<string>> SuggestAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Say that resolution is active-only**

In `src/MultiChannelAgent.Application/Inventories/IInventoryReferenceStore.cs`, replace the interface's summary paragraph beginning "Resolution is always scoped to one Inventory" with:

```csharp
/// Resolution is always scoped to one Inventory, so a reference can never reach across Inventory
/// boundaries, and this store is only ever reached after the caller has been authorized for that
/// Inventory.
///
/// Resolution is also <b>active-only</b>: a retired Unit, a retired term, and a retired Location all
/// resolve to nothing, exactly like one that never existed. That is what makes "retired Units and
/// Locations are excluded from matching" true for every caller at once - stock reads, stock
/// mutations, and later Import - rather than a rule each of them has to remember.
```

- [ ] **Step 5: Implement the catalog against SQL**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlReferenceCatalogStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IReferenceCatalogStore"/>. Every query filters on
/// <c>RetiredAt == null</c> and on the Inventory from trusted context, so a retired reference and a
/// reference belonging to another Inventory are both simply absent rather than filtered out later by
/// a caller who might forget.
///
/// Ordering and paging are done by the database against the normalized columns, which carry a binary
/// collation on SQL Server (see <see cref="MultiChannelAgentDbContext"/>), so the database's order is
/// the domain's ordinal order rather than a locale-dependent approximation of it.
/// </summary>
public sealed class SqlReferenceCatalogStore(MultiChannelAgentDbContext db) : IReferenceCatalogStore
{
    public async Task<IReadOnlyList<UnitCatalogRecord>> ListUnitsAsync(
        ReferenceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var units = db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == query.InventoryId.Value && u.RetiredAt == null);

        if (query.Cursor is { OrderKey: var key })
        {
            // Keyset resumption strictly after the cursor's order key. A normalized canonical name is
            // already unique among the active Units of one Inventory (the filtered unique term index
            // guarantees it), so this comparison alone never skips or repeats a row - exactly the
            // argument the shipped Stock keyset relies on.
            var name = key.NormalizedName;

            units = units.Where(u => string.Compare(u.NormalizedCanonicalName, name) > 0);
        }

        var page = await units
            .OrderBy(u => u.NormalizedCanonicalName)
            .ThenBy(u => u.Id)
            .Take(query.PageSize + 1)
            .Select(u => new { u.Id, u.CanonicalName, u.NormalizedCanonicalName, u.IsReserved, u.ConcurrencyStamp })
            .ToListAsync(cancellationToken);

        var unitIds = page.Select(row => row.Id).ToList();

        var terms = await db.UnitTerms
            .AsNoTracking()
            .Where(t => t.InventoryId == query.InventoryId.Value && t.RetiredAt == null && unitIds.Contains(t.UnitId))
            .OrderByDescending(t => t.IsCanonical)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new { t.UnitId, t.Term, t.NormalizedTerm, t.IsCanonical, t.IsReserved })
            .ToListAsync(cancellationToken);

        return page
            .Select(row => new UnitCatalogRecord(
                new UnitId(row.Id),
                row.CanonicalName,
                row.NormalizedCanonicalName,
                terms
                    .Where(term => term.UnitId == row.Id)
                    .Select(term => new UnitTerm
                    {
                        Term = term.Term,
                        NormalizedTerm = term.NormalizedTerm,
                        IsCanonical = term.IsCanonical,
                        IsReserved = term.IsReserved,
                    })
                    .ToList(),
                row.IsReserved,
                row.ConcurrencyStamp))
            .ToList();
    }

    public async Task<IReadOnlyList<LocationCatalogRecord>> ListLocationsAsync(
        ReferenceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var locations = db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == query.InventoryId.Value && l.RetiredAt == null);

        if (query.Cursor is { OrderKey: var key })
        {
            // Keyset resumption, on the same argument as ListUnitsAsync: a normalized Location name is
            // unique among the active Locations of one Inventory.
            var name = key.NormalizedName;

            locations = locations.Where(l => string.Compare(l.NormalizedName, name) > 0);
        }

        return await locations
            .OrderBy(l => l.NormalizedName)
            .ThenBy(l => l.Id)
            .Take(query.PageSize + 1)
            .Select(l => new LocationCatalogRecord(new LocationId(l.Id), l.Name, l.NormalizedName, l.ConcurrencyStamp))
            .ToListAsync(cancellationToken);
    }

    public async Task<UnitCatalogRecord?> FindUnitAsync(
        InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken)
    {
        var unit = await db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == inventoryId.Value && u.Id == unitId.Value && u.RetiredAt == null)
            .Select(u => new { u.CanonicalName, u.NormalizedCanonicalName, u.IsReserved, u.ConcurrencyStamp })
            .FirstOrDefaultAsync(cancellationToken);

        if (unit is null)
        {
            return null;
        }

        var terms = await db.UnitTerms
            .AsNoTracking()
            .Where(t => t.InventoryId == inventoryId.Value && t.UnitId == unitId.Value && t.RetiredAt == null)
            .OrderByDescending(t => t.IsCanonical)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => new UnitTerm
            {
                Term = t.Term,
                NormalizedTerm = t.NormalizedTerm,
                IsCanonical = t.IsCanonical,
                IsReserved = t.IsReserved,
            })
            .ToListAsync(cancellationToken);

        return new UnitCatalogRecord(
            unitId, unit.CanonicalName, unit.NormalizedCanonicalName, terms, unit.IsReserved, unit.ConcurrencyStamp);
    }

    public async Task<LocationCatalogRecord?> FindLocationAsync(
        InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        await db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == inventoryId.Value && l.Id == locationId.Value && l.RetiredAt == null)
            .Select(l => new LocationCatalogRecord(new LocationId(l.Id), l.Name, l.NormalizedName, l.ConcurrencyStamp))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> ReadActiveUnitTermsAsync(
        InventoryId inventoryId, UnitId? excluding, CancellationToken cancellationToken)
    {
        var terms = db.UnitTerms
            .AsNoTracking()
            .Where(t => t.InventoryId == inventoryId.Value && t.RetiredAt == null);

        if (excluding is { } unitId)
        {
            terms = terms.Where(t => !(t.UnitId == unitId.Value && t.IsCanonical));
        }

        var rows = await terms.Select(t => t.NormalizedTerm).ToListAsync(cancellationToken);

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlySet<string>> ReadActiveLocationNamesAsync(
        InventoryId inventoryId, LocationId? excluding, CancellationToken cancellationToken)
    {
        var locations = db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == inventoryId.Value && l.RetiredAt == null);

        if (excluding is { } locationId)
        {
            locations = locations.Where(l => l.Id != locationId.Value);
        }

        var rows = await locations.Select(l => l.NormalizedName).ToListAsync(cancellationToken);

        return rows.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<int> CountStockReferencesAsync(
        InventoryId inventoryId, ReferenceKind kind, Guid referenceId, CancellationToken cancellationToken)
    {
        var entries = db.StockEntries.AsNoTracking().Where(e => e.InventoryId == inventoryId.Value);

        entries = kind == ReferenceKind.Unit
            ? entries.Where(e => e.UnitId == referenceId)
            : entries.Where(e => e.LocationId == referenceId);

        return await entries.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var normalized = NameNormalization.Normalize(reference);

        var candidates = kind == ReferenceKind.Unit
            ? db.UnitTerms
                .AsNoTracking()
                .Where(t => t.InventoryId == inventoryId.Value && t.RetiredAt == null)
                .OrderBy(t => t.NormalizedTerm)
                .ThenBy(t => t.Id)
                .Select(t => new { Display = t.Term, Normalized = t.NormalizedTerm })
            : db.Locations
                .AsNoTracking()
                .Where(l => l.InventoryId == inventoryId.Value && l.RetiredAt == null)
                .OrderBy(l => l.NormalizedName)
                .ThenBy(l => l.Id)
                .Select(l => new { Display = l.Name, Normalized = l.NormalizedName });

        if (normalized.Length > 0)
        {
            var prefixed = await candidates
                .Where(row => row.Normalized.StartsWith(normalized))
                .Take(IReferenceCatalogStore.MaxSuggestions)
                .Select(row => row.Display)
                .ToListAsync(cancellationToken);

            if (prefixed.Count > 0)
            {
                return prefixed;
            }
        }

        // Nothing shares a prefix, so the honest answer is "here is what this Inventory actually
        // has" - bounded, in the same one order, and never a nearest-match guess.
        return await candidates
            .Take(IReferenceCatalogStore.MaxSuggestions)
            .Select(row => row.Display)
            .ToListAsync(cancellationToken);
    }
}
```

- [ ] **Step 6: Make resolution active-only**

In `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryReferenceStore.cs`, add the retirement filter to all five queries:

```csharp
    public async Task<UnitId?> ResolveUnitAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var unitId))
        {
            var byId = await db.Units
                .AsNoTracking()
                .AnyAsync(u => u.InventoryId == inventoryId.Value && u.Id == unitId && u.RetiredAt == null, cancellationToken);

            return byId ? new UnitId(unitId) : null;
        }

        var normalizedTerm = NameNormalization.Normalize(reference);
        var term = await db.UnitTerms
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.InventoryId == inventoryId.Value && t.NormalizedTerm == normalizedTerm && t.RetiredAt == null,
                cancellationToken);

        return term is null ? null : new UnitId(term.UnitId);
    }

    public async Task<LocationId?> ResolveLocationAsync(InventoryId inventoryId, string reference, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(reference, out var locationId))
        {
            var byId = await db.Locations
                .AsNoTracking()
                .AnyAsync(l => l.InventoryId == inventoryId.Value && l.Id == locationId && l.RetiredAt == null, cancellationToken);

            return byId ? new LocationId(locationId) : null;
        }

        var normalizedName = NameNormalization.Normalize(reference);
        var location = await db.Locations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                l => l.InventoryId == inventoryId.Value && l.NormalizedName == normalizedName && l.RetiredAt == null,
                cancellationToken);

        return location is null ? null : new LocationId(location.Id);
    }

    public async Task<string?> FindUnitCanonicalNameAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken) =>
        await db.Units
            .AsNoTracking()
            .Where(u => u.InventoryId == inventoryId.Value && u.Id == unitId.Value && u.RetiredAt == null)
            .Select(u => u.CanonicalName)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<string?> FindLocationNameAsync(InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        await db.Locations
            .AsNoTracking()
            .Where(l => l.InventoryId == inventoryId.Value && l.Id == locationId.Value && l.RetiredAt == null)
            .Select(l => l.Name)
            .FirstOrDefaultAsync(cancellationToken);
```

- [ ] **Step 7: Add the doubles**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryReferenceCatalogStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IReferenceCatalogStore"/> for Application-layer unit tests. It
/// answers exactly like the SQL store - active-only, ordered by normalized name then identity, and
/// with the same bounded prefix-then-fallback suggestions - and never guesses.
/// </summary>
public sealed class InMemoryReferenceCatalogStore : IReferenceCatalogStore
{
    private sealed record UnitRow(InventoryId InventoryId, UnitCatalogRecord Record, bool Retired);

    private sealed record LocationRow(InventoryId InventoryId, LocationCatalogRecord Record, bool Retired);

    private readonly List<UnitRow> _units = [];
    private readonly List<LocationRow> _locations = [];
    private readonly Dictionary<(ReferenceKind, Guid), int> _stockReferences = [];

    public UnitId AddUnit(
        InventoryId inventoryId, string canonicalName, string[] aliases, bool isReserved = false, bool retired = false)
    {
        var unitId = new UnitId(Guid.NewGuid());
        var terms = new List<UnitTerm> { UnitTerm.Create(canonicalName, isCanonical: true, isReserved) };
        terms.AddRange(aliases.Select(alias => UnitTerm.Create(alias, isCanonical: false, isReserved)));

        _units.Add(new UnitRow(
            inventoryId,
            new UnitCatalogRecord(
                unitId, canonicalName, NameNormalization.Normalize(canonicalName), terms, isReserved, Guid.NewGuid()),
            retired));

        return unitId;
    }

    public LocationId AddLocation(InventoryId inventoryId, string name, bool retired = false)
    {
        var locationId = new LocationId(Guid.NewGuid());

        _locations.Add(new LocationRow(
            inventoryId,
            new LocationCatalogRecord(locationId, name, NameNormalization.Normalize(name), Guid.NewGuid()),
            retired));

        return locationId;
    }

    public void SetStockReferences(ReferenceKind kind, Guid referenceId, int count) =>
        _stockReferences[(kind, referenceId)] = count;

    public Task<IReadOnlyList<UnitCatalogRecord>> ListUnitsAsync(ReferenceListQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<UnitCatalogRecord> page = Ordered(
                _units
                    .Where(row => row.InventoryId == query.InventoryId && !row.Retired)
                    .Select(row => (Key: Key(row.Record.NormalizedCanonicalName, row.Record.Id.Value), row.Record)),
                query)
            .ToList();

        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<LocationCatalogRecord>> ListLocationsAsync(ReferenceListQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<LocationCatalogRecord> page = Ordered(
                _locations
                    .Where(row => row.InventoryId == query.InventoryId && !row.Retired)
                    .Select(row => (Key: Key(row.Record.NormalizedName, row.Record.Id.Value), row.Record)),
                query)
            .ToList();

        return Task.FromResult(page);
    }

    public Task<UnitCatalogRecord?> FindUnitAsync(InventoryId inventoryId, UnitId unitId, CancellationToken cancellationToken) =>
        Task.FromResult(_units
            .FirstOrDefault(row => row.InventoryId == inventoryId && row.Record.Id == unitId && !row.Retired)?.Record);

    public Task<LocationCatalogRecord?> FindLocationAsync(
        InventoryId inventoryId, LocationId locationId, CancellationToken cancellationToken) =>
        Task.FromResult(_locations
            .FirstOrDefault(row => row.InventoryId == inventoryId && row.Record.Id == locationId && !row.Retired)?.Record);

    public Task<IReadOnlySet<string>> ReadActiveUnitTermsAsync(
        InventoryId inventoryId, UnitId? excluding, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> terms = _units
            .Where(row => row.InventoryId == inventoryId && !row.Retired)
            .SelectMany(row => row.Record.Terms
                .Where(term => !(excluding == row.Record.Id && term.IsCanonical))
                .Select(term => term.NormalizedTerm))
            .ToHashSet(StringComparer.Ordinal);

        return Task.FromResult(terms);
    }

    public Task<IReadOnlySet<string>> ReadActiveLocationNamesAsync(
        InventoryId inventoryId, LocationId? excluding, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> names = _locations
            .Where(row => row.InventoryId == inventoryId && !row.Retired && excluding != row.Record.Id)
            .Select(row => row.Record.NormalizedName)
            .ToHashSet(StringComparer.Ordinal);

        return Task.FromResult(names);
    }

    public Task<int> CountStockReferencesAsync(
        InventoryId inventoryId, ReferenceKind kind, Guid referenceId, CancellationToken cancellationToken) =>
        Task.FromResult(_stockReferences.GetValueOrDefault((kind, referenceId)));

    public Task<IReadOnlyList<string>> SuggestAsync(
        InventoryId inventoryId, ReferenceKind kind, string reference, CancellationToken cancellationToken)
    {
        var normalized = NameNormalization.Normalize(reference);

        var candidates = kind == ReferenceKind.Unit
            ? _units
                .Where(row => row.InventoryId == inventoryId && !row.Retired)
                .SelectMany(row => row.Record.Terms.Select(term => (term.NormalizedTerm, Display: term.Term, row.Record.Id.Value)))
                .OrderBy(row => row.NormalizedTerm, StringComparer.Ordinal)
                .ThenBy(row => row.Value)
                .Select(row => (row.NormalizedTerm, row.Display))
                .ToList()
            : _locations
                .Where(row => row.InventoryId == inventoryId && !row.Retired)
                .OrderBy(row => row.Record.NormalizedName, StringComparer.Ordinal)
                .ThenBy(row => row.Record.Id.Value)
                .Select(row => (NormalizedTerm: row.Record.NormalizedName, Display: row.Record.Name))
                .ToList();

        var prefixed = candidates
            .Where(row => normalized.Length > 0 && row.NormalizedTerm.StartsWith(normalized, StringComparison.Ordinal))
            .Take(IReferenceCatalogStore.MaxSuggestions)
            .Select(row => row.Display)
            .ToList();

        IReadOnlyList<string> suggestions = prefixed.Count > 0
            ? prefixed
            : candidates.Take(IReferenceCatalogStore.MaxSuggestions).Select(row => row.Display).ToList();

        return Task.FromResult(suggestions);
    }

    private static ReferenceOrderKey Key(string normalizedName, Guid id) => new(normalizedName, id.ToString("D"));

    private static IEnumerable<TRecord> Ordered<TRecord>(
        IEnumerable<(ReferenceOrderKey Key, TRecord Record)> rows, ReferenceListQuery query)
    {
        var ordered = rows.OrderBy(row => row.Key, ReferenceOrderKey.Comparer);

        var after = query.Cursor is { OrderKey: var cursorKey }
            ? ordered.Where(row => ReferenceOrderKey.Comparer.Compare(row.Key, cursorKey) > 0)
            : ordered.AsEnumerable();

        return after.Take(query.PageSize + 1).Select(row => row.Record);
    }
}
```

- [ ] **Step 8: Let the resolution double retire things too**

In `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryInventoryReferenceStore.cs`, add a retirement set and honour it, so a unit test can prove that a retired reference is `reference_not_found`:

```csharp
    private readonly HashSet<(InventoryId, UnitId)> _retiredUnits = [];
    private readonly HashSet<(InventoryId, LocationId)> _retiredLocations = [];

    /// <summary>Withdraws a Unit from resolution exactly as retiring it does in SQL: it becomes as unknown as one that never existed.</summary>
    public void RetireUnit(InventoryId inventoryId, UnitId unitId) => _retiredUnits.Add((inventoryId, unitId));

    /// <summary>Withdraws a Location from resolution. See <see cref="RetireUnit"/>.</summary>
    public void RetireLocation(InventoryId inventoryId, LocationId locationId) => _retiredLocations.Add((inventoryId, locationId));
```

and add the two guards, one in each resolve/find pair - for example in `ResolveUnitAsync`, after a term or identity has matched:

```csharp
        return Task.FromResult<UnitId?>(
            _unitTerms.TryGetValue((inventoryId, NameNormalization.Normalize(reference)), out var resolved)
                && !_retiredUnits.Contains((inventoryId, resolved))
                    ? resolved
                    : null);
```

Apply the same `!_retiredUnits.Contains(...)` / `!_retiredLocations.Contains(...)` guard to the identity branch of both `Resolve*` methods and to both `Find*NameAsync` methods.

- [ ] **Step 9: Verify what can be verified now**

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings - **after Task 13 adds the columns**. Until then this task's Infrastructure file will not compile, which is expected and is exactly why Task 13 follows closely. If you are executing tasks strictly in order, run `dotnet build src/MultiChannelAgent.Application/MultiChannelAgent.Application.csproj --configuration Release` here instead and expect it to succeed, then complete the Infrastructure half of this task's verification at the end of Task 13.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - the doubles compile and no shipped behavior changed.

- [ ] **Step 10: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IReferenceCatalogStore.cs \
        src/MultiChannelAgent.Application/Inventories/IInventoryReferenceStore.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlReferenceCatalogStore.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryReferenceStore.cs \
        tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceCatalogStoreTests.cs
git commit -m "feat(inventories): read the active Unit and Location catalog for #33"
```

---

## Task 7: Parse an untrusted homogeneous reference change array

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ReferenceChangeSetParser.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeSetParserTests.cs`

Why: `changes` is the only administration argument with internal structure, so it is the only one where "ignore what you do not understand" could quietly change what commits. The tool name fixes the kind, which is what makes the array homogeneous by construction rather than by a check someone could forget.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeSetParserTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceChangeSetParserTests
{
    [Fact]
    public void A_create_Unit_array_parses_its_name_and_its_ordered_initial_aliases()
    {
        var parsed = ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateUnit,
            """[{"name":"Cardboard Box","aliases":"boxes, bx"},{"name":"Pallet"}]""",
            out var requests,
            out var code);

        Assert.True(parsed);
        Assert.Empty(code);
        Assert.Equal(2, requests.Count);
        Assert.Equal(1, requests[0].Order);
        Assert.Equal(ReferenceChangeKind.CreateUnit, requests[0].Kind);
        Assert.Equal("Cardboard Box", requests[0].Name);
        Assert.Equal(["boxes", "bx"], requests[0].Aliases);
        Assert.Equal(2, requests[1].Order);
        Assert.Equal("Pallet", requests[1].Name);
        Assert.Empty(requests[1].Aliases);
    }

    [Fact]
    public void Order_comes_from_the_position_in_the_array_never_from_the_element()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":"Shelf A","order":"7"}]""", out _, out _));
    }

    [Fact]
    public void An_element_carrying_a_property_this_kind_does_not_have_refuses_the_whole_array()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":"Shelf A","aliases":"shelf"}]""", out _, out var code));
        Assert.Equal("invalid_changes", code);
    }

    [Fact]
    public void A_kind_property_is_itself_unknown_so_a_mixed_batch_cannot_even_be_expressed()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RetireUnit, """[{"unit":"box","kind":"retire_location"}]""", out _, out _));
    }

    [Fact]
    public void A_non_string_value_refuses_the_whole_array()
    {
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":123}]""", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""[{"name":"Shelf A"},"Shelf B"]""")]
    public void Anything_that_is_not_a_non_empty_array_of_objects_is_refused(string? json)
    {
        Assert.False(ReferenceChangeSetParser.TryParse(ReferenceChangeKind.CreateLocation, json, out var requests, out var code));
        Assert.Empty(requests);
        Assert.Equal("invalid_changes", code);
    }

    [Fact]
    public void More_changes_than_a_Participant_can_review_are_refused_by_their_own_code()
    {
        var elements = Enumerable
            .Range(1, ConfirmationProposal.MaxChanges + 1)
            .Select(index => $$"""{"name":"Shelf {{index}}"}""");

        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, $"[{string.Join(",", elements)}]", out _, out var code));
        Assert.Equal("too_many_changes", code);
    }

    [Theory]
    [InlineData(ReferenceChangeKind.CreateUnit, """[{"aliases":"boxes"}]""")]
    [InlineData(ReferenceChangeKind.CreateLocation, "[{}]")]
    [InlineData(ReferenceChangeKind.RenameUnit, """[{"unit":"box"}]""")]
    [InlineData(ReferenceChangeKind.RenameUnit, """[{"newName":"Carton"}]""")]
    [InlineData(ReferenceChangeKind.RenameLocation, """[{"location":"Shelf A"}]""")]
    [InlineData(ReferenceChangeKind.AddUnitAlias, """[{"unit":"box"}]""")]
    [InlineData(ReferenceChangeKind.RemoveUnitAlias, """[{"alias":"boxes"}]""")]
    [InlineData(ReferenceChangeKind.RetireUnit, "[{}]")]
    [InlineData(ReferenceChangeKind.RetireLocation, "[{}]")]
    public void Every_kind_refuses_an_element_missing_something_it_requires(ReferenceChangeKind kind, string json) =>
        Assert.False(ReferenceChangeSetParser.TryParse(kind, json, out _, out _));

    [Fact]
    public void A_blank_required_value_is_as_absent_as_a_missing_one() =>
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateLocation, """[{"name":"   "}]""", out _, out _));

    [Fact]
    public void An_aliases_property_that_lists_nothing_is_refused_rather_than_read_as_no_aliases() =>
        Assert.False(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.CreateUnit, """[{"name":"Box","aliases":" , "}]""", out _, out _));

    [Fact]
    public void Every_mutating_kind_parses_its_own_shape()
    {
        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RenameUnit, """[{"unit":"box","newName":"Carton"}]""", out var renameUnit, out _));
        Assert.Equal("box", renameUnit[0].Reference);
        Assert.Equal("Carton", renameUnit[0].NewName);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.AddUnitAlias, """[{"unit":"box","alias":"cartons"}]""", out var addAlias, out _));
        Assert.Equal("box", addAlias[0].Reference);
        Assert.Equal("cartons", addAlias[0].Alias);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RemoveUnitAlias, """[{"unit":"box","alias":"cartons"}]""", out var removeAlias, out _));
        Assert.Equal("cartons", removeAlias[0].Alias);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RetireUnit, """[{"unit":"box"}]""", out var retireUnit, out _));
        Assert.Equal("box", retireUnit[0].Reference);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RenameLocation, """[{"location":"Shelf A","newName":"Aisle 3"}]""", out var renameLocation, out _));
        Assert.Equal("Shelf A", renameLocation[0].Reference);
        Assert.Equal("Aisle 3", renameLocation[0].NewName);

        Assert.True(ReferenceChangeSetParser.TryParse(
            ReferenceChangeKind.RetireLocation, """[{"location":"Shelf A"}]""", out var retireLocation, out _));
        Assert.Equal("Shelf A", retireLocation[0].Reference);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceChangeSetParserTests"`
Expected: FAIL to compile - `ReferenceChangeSetParser` and `ReferenceChangeRequest` do not exist.

- [ ] **Step 3: Write the parser**

Create `src/MultiChannelAgent.Application/Inventories/ReferenceChangeSetParser.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// One requested administration change, as proposed. Every field is untrusted text: nothing here is
/// identity, and nothing here is ever pattern-matched or guessed. <see cref="Order"/> is assigned by
/// the parser from the element's own position, never taken from the element, so a proposal cannot
/// reorder or collide the execution order. <see cref="Kind"/> comes from the tool that was called,
/// never from the element, so an array is homogeneous by construction.
/// </summary>
public sealed record ReferenceChangeRequest
{
    public required int Order { get; init; }

    public required ReferenceChangeKind Kind { get; init; }

    /// <summary>The name a create asks for.</summary>
    public string? Name { get; init; }

    /// <summary>The ordered initial aliases a Unit creation asks for; empty when it asked for none.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The Unit or Location to act on: an opaque identity, or an exact active name (for a Unit, any active term).</summary>
    public string? Reference { get; init; }

    /// <summary>The exact new display name a rename asks for.</summary>
    public string? NewName { get; init; }

    /// <summary>The single alias an alias add or removal names.</summary>
    public string? Alias { get; init; }
}

/// <summary>
/// Reads the one structured tool argument Unit and Location administration accepts: the untrusted
/// <c>changes</c> array a mutating administration tool carries.
///
/// It is deliberately unforgiving. A property this kind does not have, a value that is not a string,
/// a missing required value, or one element too many refuses the whole array - never a partly
/// understood batch, and never a silently narrowed one. The kind is supplied by the caller from the
/// *tool name*, so an element cannot name a kind of its own and a mixed batch cannot be expressed at
/// all.
/// </summary>
public static class ReferenceChangeSetParser
{
    /// <summary>The exact property set each kind accepts. Anything outside it refuses the array.</summary>
    private static readonly Dictionary<ReferenceChangeKind, string[]> KnownProperties = new()
    {
        [ReferenceChangeKind.CreateUnit] = ["name", "aliases"],
        [ReferenceChangeKind.RenameUnit] = ["unit", "newName"],
        [ReferenceChangeKind.AddUnitAlias] = ["unit", "alias"],
        [ReferenceChangeKind.RemoveUnitAlias] = ["unit", "alias"],
        [ReferenceChangeKind.RetireUnit] = ["unit"],
        [ReferenceChangeKind.CreateLocation] = ["name"],
        [ReferenceChangeKind.RenameLocation] = ["location", "newName"],
        [ReferenceChangeKind.RetireLocation] = ["location"],
    };

    /// <summary>
    /// Parses <paramref name="json"/> into ordered requests of exactly <paramref name="kind"/>. On
    /// failure <paramref name="code"/> is the machine code to answer with - <c>invalid_changes</c> or
    /// <c>too_many_changes</c> - and <paramref name="requests"/> is empty.
    /// </summary>
    public static bool TryParse(
        ReferenceChangeKind kind, string? json, out IReadOnlyList<ReferenceChangeRequest> requests, out string code)
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

            var parsed = new List<ReferenceChangeRequest>(elements.Count);
            for (var index = 0; index < elements.Count; index++)
            {
                if (!TryParseElement(kind, elements[index], index + 1, out var request))
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

    private static bool TryParseElement(
        ReferenceChangeKind kind, JsonElement element, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var known = KnownProperties[kind];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            // A property this kind does not have is not noise to skip past: it is a proposal asking
            // for something that was never agreed, and the safe reading of that is "no". That is also
            // what makes an element carrying its own "kind" a refusal rather than a mixed batch.
            if (!known.Contains(property.Name, StringComparer.Ordinal) || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return kind switch
        {
            ReferenceChangeKind.CreateUnit => TryCreateUnit(values, order, out request),
            ReferenceChangeKind.CreateLocation => TryOneValue(kind, values, "name", order, out request),
            ReferenceChangeKind.RenameUnit => TryRename(kind, values, "unit", order, out request),
            ReferenceChangeKind.RenameLocation => TryRename(kind, values, "location", order, out request),
            ReferenceChangeKind.AddUnitAlias or ReferenceChangeKind.RemoveUnitAlias =>
                TryAlias(kind, values, order, out request),
            ReferenceChangeKind.RetireUnit => TryReferenceOnly(kind, values, "unit", order, out request),
            ReferenceChangeKind.RetireLocation => TryReferenceOnly(kind, values, "location", order, out request),
            _ => false,
        };
    }

    private static bool TryCreateUnit(Dictionary<string, string> values, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, "name") is not { } name)
        {
            return false;
        }

        IReadOnlyList<string> aliases = [];
        if (values.TryGetValue("aliases", out var rawAliases))
        {
            // Present but listing nothing is a malformed request, not "no aliases": a caller that
            // meant none simply omits the property.
            var split = rawAliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (split.Length == 0)
            {
                return false;
            }

            aliases = split;
        }

        request = new ReferenceChangeRequest
        {
            Order = order,
            Kind = ReferenceChangeKind.CreateUnit,
            Name = name,
            Aliases = aliases,
        };

        return true;
    }

    private static bool TryOneValue(
        ReferenceChangeKind kind, Dictionary<string, string> values, string nameProperty, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, nameProperty) is not { } name)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Name = name };
        return true;
    }

    private static bool TryRename(
        ReferenceChangeKind kind,
        Dictionary<string, string> values,
        string referenceProperty,
        int order,
        out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, referenceProperty) is not { } reference || Required(values, "newName") is not { } newName)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Reference = reference, NewName = newName };
        return true;
    }

    private static bool TryAlias(
        ReferenceChangeKind kind, Dictionary<string, string> values, int order, out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, "unit") is not { } reference || Required(values, "alias") is not { } alias)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Reference = reference, Alias = alias };
        return true;
    }

    private static bool TryReferenceOnly(
        ReferenceChangeKind kind,
        Dictionary<string, string> values,
        string referenceProperty,
        int order,
        out ReferenceChangeRequest? request)
    {
        request = null;

        if (Required(values, referenceProperty) is not { } reference)
        {
            return false;
        }

        request = new ReferenceChangeRequest { Order = order, Kind = kind, Reference = reference };
        return true;
    }

    /// <summary>A required value must be present and not blank; blank is exactly as absent as missing.</summary>
    private static string? Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceChangeSetParserTests"`
Expected: PASS - every case in the class.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ReferenceChangeSetParser.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeSetParserTests.cs
git commit -m "feat(inventories): parse a homogeneous reference change array strictly for #33"
```

---

## Task 8: Resolve every reference change against current state

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ReferenceChangeResolver.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeResolverTests.cs`

Why: this is the one place an untrusted request meets current state and becomes an exactly-decided change - identities, tidied names, terms, expected versions, and expected absences - or one typed refusal carrying bounded suggestions. It contains no rules of its own: every decision comes from `ReferenceChangePlan`.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeResolverTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceChangeResolverTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryInventoryReferenceStore _references = new();

    private ReferenceChangeResolver Resolver() => new(_catalog, _references);

    private UnitId SeedUnit(string canonicalName, params string[] aliases)
    {
        var unitId = _catalog.AddUnit(_inventoryId, canonicalName, aliases);
        _references.AddUnit(_inventoryId, unitId, [canonicalName, .. aliases]);

        return unitId;
    }

    private UnitId SeedReservedEach()
    {
        var unitId = _catalog.AddUnit(_inventoryId, "each", ["piece", "pieces", "pc", "pcs"], isReserved: true);
        _references.AddUnit(_inventoryId, unitId, "each", "piece", "pieces", "pc", "pcs");

        return unitId;
    }

    private LocationId SeedLocation(string name)
    {
        var locationId = _catalog.AddLocation(_inventoryId, name);
        _references.AddLocation(_inventoryId, locationId, name);

        return locationId;
    }

    private static ReferenceChangeRequest Request(
        ReferenceChangeKind kind,
        string? name = null,
        string[]? aliases = null,
        string? reference = null,
        string? newName = null,
        string? alias = null) => new()
        {
            Order = 1,
            Kind = kind,
            Name = name,
            Aliases = aliases ?? [],
            Reference = reference,
            NewName = newName,
            Alias = alias,
        };

    [Fact]
    public async Task Creating_a_Unit_decides_its_identity_its_terms_and_the_terms_it_claims()
    {
        SeedReservedEach();

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateUnit, name: " Cardboard  Box ", aliases: ["Boxes"]), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        var change = resolution.Change!;
        Assert.Equal(ReferenceChangeKind.CreateUnit, change.Kind);
        Assert.NotEqual(Guid.Empty, change.Target.ReferenceId);
        Assert.Equal("Cardboard Box", change.Target.Name);
        Assert.Equal("cardboard box", change.Target.NormalizedName);
        Assert.Equal(["Cardboard Box", "Boxes"], change.Terms.Select(term => term.Term));
        Assert.Empty(resolution.ExpectedVersions!);
        Assert.Equal(["cardboard box", "boxes"], resolution.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
        Assert.All(resolution.ExpectedAbsences!, absence => Assert.Equal(ReferenceKind.Unit, absence.Kind));
    }

    [Fact]
    public async Task Creating_a_Unit_whose_term_is_already_taken_is_a_typed_conflict()
    {
        SeedReservedEach();

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateUnit, name: "PCS"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("term_in_use", resolution.Code);
    }

    [Fact]
    public async Task Renaming_a_Unit_pins_the_version_it_was_decided_against_and_claims_the_new_term()
    {
        SeedReservedEach();
        var boxId = SeedUnit("Cardboard Box", "boxes");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameUnit, reference: "boxes", newName: "Carton"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        var change = resolution.Change!;
        Assert.Equal(boxId.Value, change.Target.ReferenceId);
        Assert.Equal("Cardboard Box", change.Target.Name);
        Assert.Equal("Carton", change.NewName);
        Assert.Equal("carton", change.NewNormalizedName);
        Assert.Equal([boxId.Value], resolution.ExpectedVersions!.Select(version => version.ReferenceId));
        Assert.Equal(["carton"], resolution.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
    }

    [Fact]
    public async Task Renaming_a_Unit_only_in_its_display_form_claims_nothing_new()
    {
        var boxId = SeedUnit("Cardboard Box");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId,
            Request(ReferenceChangeKind.RenameUnit, reference: boxId.Value.ToString(), newName: "CARDBOARD BOX"),
            CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        Assert.Empty(resolution.ExpectedAbsences!);
    }

    [Fact]
    public async Task The_reserved_Unit_is_refused_by_name_or_by_identity()
    {
        var eachId = SeedReservedEach();

        var byAlias = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameUnit, reference: "pcs", newName: "items"), CancellationToken.None);
        var byId = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: eachId.Value.ToString()), CancellationToken.None);

        Assert.Equal("reserved_unit", byAlias.Code);
        Assert.Equal("reserved_unit", byId.Code);
    }

    [Fact]
    public async Task A_fixed_alias_can_never_be_removed()
    {
        SeedReservedEach();

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RemoveUnitAlias, reference: "each", alias: "pcs"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("reserved_term", resolution.Code);
    }

    [Fact]
    public async Task A_non_reserved_alias_added_to_the_reserved_Unit_can_be_removed_again()
    {
        var eachId = SeedReservedEach();

        var added = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.AddUnitAlias, reference: "each", alias: "stuks"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, added.Kind);
        Assert.Equal(eachId.Value, added.Change!.Target.ReferenceId);
        Assert.Equal("stuks", added.Change.Term!.Term);
        Assert.False(added.Change.Term.IsReserved);
        Assert.Equal(["stuks"], added.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
    }

    [Fact]
    public async Task A_Units_own_name_is_not_one_of_its_aliases()
    {
        SeedUnit("Cardboard Box", "boxes");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId,
            Request(ReferenceChangeKind.RemoveUnitAlias, reference: "boxes", alias: "Cardboard Box"),
            CancellationToken.None);

        Assert.Equal("canonical_term", resolution.Code);
    }

    [Fact]
    public async Task A_term_that_is_not_an_alias_of_that_Unit_is_not_found()
    {
        SeedUnit("Cardboard Box", "boxes");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RemoveUnitAlias, reference: "boxes", alias: "cartons"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.NotFound, resolution.Kind);
        Assert.Equal("alias_not_found", resolution.Code);
    }

    [Fact]
    public async Task An_unused_Unit_resolves_for_Retire_and_pins_its_version()
    {
        var boxId = SeedUnit("Cardboard Box");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: "Cardboard Box"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(ReferenceChangeKind.RetireUnit, resolution.Change!.Kind);
        Assert.Equal([boxId.Value], resolution.ExpectedVersions!.Select(version => version.ReferenceId));
        Assert.Empty(resolution.ExpectedAbsences!);
    }

    [Fact]
    public async Task A_Unit_a_Stock_Entry_still_references_is_refused_before_anyone_is_asked_to_confirm()
    {
        var boxId = SeedUnit("Cardboard Box");
        _catalog.SetStockReferences(ReferenceKind.Unit, boxId.Value, 2);

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: "Cardboard Box"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("reference_in_use", resolution.Code);
    }

    [Fact]
    public async Task A_Location_a_Stock_Entry_is_still_placed_in_is_refused()
    {
        var shelfId = SeedLocation("Shelf A");
        _catalog.SetStockReferences(ReferenceKind.Location, shelfId.Value, 1);

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireLocation, reference: "Shelf A"), CancellationToken.None);

        Assert.Equal("reference_in_use", resolution.Code);
    }

    [Fact]
    public async Task An_unknown_Unit_answers_reference_not_found_with_bounded_deterministic_suggestions()
    {
        SeedReservedEach();
        SeedUnit("Box Large");
        SeedUnit("Box Small");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireUnit, reference: "box"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.ReferenceNotFound, resolution.Kind);
        Assert.Equal("reference_not_found", resolution.Code);
        Assert.Equal(ReferenceKind.Unit, resolution.UnresolvedReference);
        Assert.Equal(["Box Large", "Box Small"], resolution.Suggestions);
        Assert.True(resolution.Suggestions!.Count <= IReferenceCatalogStore.MaxSuggestions);
    }

    [Fact]
    public async Task A_retired_reference_is_exactly_as_unknown_as_one_that_never_existed()
    {
        var boxId = SeedUnit("Cardboard Box");
        _references.RetireUnit(_inventoryId, boxId);

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameUnit, reference: "Cardboard Box", newName: "Carton"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.ReferenceNotFound, resolution.Kind);
    }

    [Fact]
    public async Task A_change_that_names_no_reference_at_all_is_invalid()
    {
        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RetireLocation), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_reference", resolution.Code);
    }

    [Fact]
    public async Task Creating_a_Location_decides_its_identity_and_claims_its_name()
    {
        SeedLocation("Shelf B");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateLocation, name: "  Shelf   A "), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(ReferenceKind.Location, resolution.Change!.Target.Kind);
        Assert.Equal("Shelf A", resolution.Change.Target.Name);
        Assert.Equal(["shelf a"], resolution.ExpectedAbsences!.Select(absence => absence.NormalizedTerm));
        Assert.All(resolution.ExpectedAbsences!, absence => Assert.Equal(ReferenceKind.Location, absence.Kind));
    }

    [Fact]
    public async Task Creating_a_Location_whose_name_is_taken_is_a_typed_conflict()
    {
        SeedLocation("Shelf A");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.CreateLocation, name: "SHELF A"), CancellationToken.None);

        Assert.Equal("name_in_use", resolution.Code);
    }

    [Fact]
    public async Task Renaming_a_Location_to_exactly_what_it_is_called_is_a_typed_no_op()
    {
        SeedLocation("Shelf A");

        var resolution = await Resolver().ResolveAsync(
            _inventoryId, Request(ReferenceChangeKind.RenameLocation, reference: "Shelf A", newName: "Shelf A"), CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Conflict, resolution.Kind);
        Assert.Equal("no_change", resolution.Code);
    }

    [Fact]
    public async Task An_oversized_name_is_invalid_rather_than_a_conflict()
    {
        var resolution = await Resolver().ResolveAsync(
            _inventoryId,
            Request(ReferenceChangeKind.CreateUnit, name: new string('b', Unit.MaxNameLength + 1)),
            CancellationToken.None);

        Assert.Equal(ReferenceChangeResolutionKind.Invalid, resolution.Kind);
        Assert.Equal("invalid_name", resolution.Code);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceChangeResolverTests"`
Expected: FAIL to compile - `ReferenceChangeResolver`, `ReferenceChangeResolution`, and `ReferenceChangeResolutionKind` do not exist.

- [ ] **Step 3: Write the resolver**

Create `src/MultiChannelAgent.Application/Inventories/ReferenceChangeResolver.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceChangeResolverTests"`
Expected: PASS, 19 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ReferenceChangeResolver.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceChangeResolverTests.cs
git commit -m "feat(inventories): resolve every reference change exactly against current state for #33"
```

---

## Task 9: Define the atomic reference administration store seam

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/IReferenceAdministrationStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryReferenceAdministrationStore.cs`

Why: the service and the confirmation path both need one writer whose contract is "all of it, or none of it, including the audits, the ledger, the proposal consumption, and the invalidation retiring causes". Naming that contract before implementing it is what lets Tasks 10, 11, and 12 be written and tested without SQL.

- [ ] **Step 1: Define the seam**

Create `src/MultiChannelAgent.Application/Inventories/IReferenceAdministrationStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How a reference change set was settled by the store.</summary>
public enum ReferenceAdministrationStoreOutcome
{
    /// <summary>Every change was applied, and the state changes, audit facts, ledger, proposal consumption, and any retirement-driven invalidation committed together.</summary>
    Applied,

    /// <summary>This operation identity had already been applied; the recorded changes are returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>Current state no longer matches what was proposed, or the proposal was already consumed, or a Retire is now blocked. Nothing at all was applied.</summary>
    Conflict,
}

/// <summary>
/// What one change actually did. Deliberately semantic: no row versions, concurrency stamps, audit
/// identities, or SQL detail ever appear here.
/// </summary>
public sealed record RecordedReferenceChange(
    int Order,
    ReferenceChangeKind Kind,
    ReferenceKind ReferenceKind,
    Guid ReferenceId,
    string Name)
{
    /// <summary>The exact new display name a rename applied, or null for every other kind.</summary>
    public string? NewName { get; init; }

    /// <summary>The single alias an alias add established or an alias removal ended, or null for every other kind.</summary>
    public string? Alias { get; init; }

    /// <summary>The initial aliases a Unit creation established, in order; empty for every other kind.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Everything a retry of one applied change set must be able to re-report without touching reference data again.</summary>
public sealed record RecordedReferenceChangeSet(
    ReferenceOperationId OperationId, ProposalId? ProposalId, IReadOnlyList<RecordedReferenceChange> Changes);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="ReferenceAdministrationStoreOutcome.Conflict"/>.</summary>
public sealed record ReferenceAdministrationStoreResult(
    ReferenceAdministrationStoreOutcome Outcome, RecordedReferenceChangeSet? Recorded);

/// <summary>
/// One fully decided set of administration changes, ready to apply. Everything is already resolved:
/// each <see cref="ProposedReferenceChange"/> names its exact identity, names, and terms, and the
/// expected versions and term absences say exactly what current state must still look like.
/// </summary>
public sealed record ReferenceChangeSetCommand
{
    /// <summary>The retry-stable identity this execution is recorded under; the reference ledger is keyed by it.</summary>
    public required ReferenceOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose role authorized this; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    /// <summary>
    /// The Turn that caused this execution. Recorded and uniquely indexed per Inventory, so a Turn
    /// re-driven after a crash finds what its own first attempt did without needing the proposal -
    /// which, by then, has been consumed.
    /// </summary>
    public required TurnId ConfirmedByTurnId { get; init; }

    /// <summary>The proposal to consume in the very same transaction, or null for an immediate change that needed none.</summary>
    public ProposalId? ConsumesProposalId { get; init; }

    public required IReadOnlyList<ProposedReferenceChange> Changes { get; init; }

    public required IReadOnlyList<ExpectedReferenceVersion> ExpectedVersions { get; init; }

    public required IReadOnlyList<ExpectedTermAbsence> ExpectedTermAbsences { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The single atomic writer for one or many reference administration changes. One call must, in one
/// transaction:
///
/// <list type="number">
/// <item>refuse if this operation identity was already applied, returning what it did;</item>
/// <item>consume the proposal it names, refusing if something already did;</item>
/// <item>refuse if any touched Unit or Location no longer carries its expected version;</item>
/// <item>refuse if any expected term absence has since been filled;</item>
/// <item><b>re-check every Retire against current Stock Entries</b> - this, not the plan-time check, is what "confirmed Retire fails for currently referenced data" means;</item>
/// <item>apply every change, preserving the identity of everything it retires;</item>
/// <item>append one minimal semantic audit fact per change;</item>
/// <item>settle every <em>other</em> pending proposal - stock proposals included - that references an identity this set retired;</item>
/// <item>and record the ledger.</item>
/// </list>
///
/// Partial application is never acceptable. A caller that sees
/// <see cref="ReferenceAdministrationStoreOutcome.Conflict"/> must be able to rely on nothing at all
/// having happened, which is exactly what "a failed atomic batch changes nothing" means.
/// </summary>
public interface IReferenceAdministrationStore
{
    /// <summary>What this operation identity already did in this Inventory, or null when it has never been applied there.</summary>
    Task<RecordedReferenceChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, ReferenceOperationId operationId, CancellationToken cancellationToken);

    /// <summary>
    /// What this Turn already did in this Inventory, or null when it did nothing. This is the replay
    /// lookup, keyed by the Turn rather than by the operation identity, because a confirmation
    /// consumes its proposal and a re-driven Turn can no longer derive that identity.
    /// </summary>
    Task<RecordedReferenceChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken);

    Task<ReferenceAdministrationStoreResult> ApplyAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the double**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryReferenceAdministrationStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IReferenceAdministrationStore"/> for Application-layer unit tests. It
/// honours exactly the contract the SQL store must: replay by identity and by Turn, single-use
/// proposal consumption, expected versions, expected term absences, the authoritative retire
/// re-check, and settling every other pending proposal that referenced a retired identity.
/// </summary>
public sealed class InMemoryReferenceAdministrationStore(InMemoryConfirmationProposalStore? proposalStore = null)
    : IReferenceAdministrationStore
{
    private readonly Dictionary<(InventoryId, ReferenceOperationId), RecordedReferenceChangeSet> _byOperation = [];
    private readonly Dictionary<(InventoryId, TurnId), RecordedReferenceChangeSet> _byTurn = [];
    private readonly Dictionary<(ReferenceKind, Guid), Guid> _versions = [];
    private readonly HashSet<(ReferenceKind, string)> _takenTerms = [];
    private readonly Dictionary<(ReferenceKind, Guid), int> _stockReferences = [];

    /// <summary>Every audit fact this store appended, in order - the same minimal facts the SQL store writes.</summary>
    public List<AuditFact> Audits { get; } = [];

    /// <summary>Forces the next apply to see a different version for this reference, exactly as a competing writer would.</summary>
    public void SetVersion(ReferenceKind kind, Guid referenceId, Guid concurrencyStamp) =>
        _versions[(kind, referenceId)] = concurrencyStamp;

    /// <summary>Marks a normalized term as taken, so an expected absence for it becomes a conflict.</summary>
    public void TakeTerm(ReferenceKind kind, string normalizedTerm) => _takenTerms.Add((kind, normalizedTerm));

    /// <summary>Sets how many Stock Entries reference something at execution time, which is what a Retire is re-checked against.</summary>
    public void SetStockReferences(ReferenceKind kind, Guid referenceId, int count) =>
        _stockReferences[(kind, referenceId)] = count;

    public Task<RecordedReferenceChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, ReferenceOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(_byOperation.GetValueOrDefault((inventoryId, operationId)));

    public Task<RecordedReferenceChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken) =>
        Task.FromResult(_byTurn.GetValueOrDefault((inventoryId, turnId)));

    public async Task<ReferenceAdministrationStoreResult> ApplyAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_byOperation.TryGetValue((command.InventoryId, command.OperationId), out var already))
        {
            return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, already);
        }

        if (command.ConsumesProposalId is { } proposalId
            && proposalStore is not null
            && !await proposalStore.SettleAsync(proposalId, ProposalStatus.Confirmed, command.Now, cancellationToken))
        {
            return Conflict();
        }

        foreach (var expected in command.ExpectedVersions)
        {
            if (_versions.TryGetValue((expected.Kind, expected.ReferenceId), out var current)
                && current != expected.ConcurrencyStamp)
            {
                return Conflict();
            }
        }

        foreach (var absence in command.ExpectedTermAbsences)
        {
            if (_takenTerms.Contains((absence.Kind, absence.NormalizedTerm)))
            {
                return Conflict();
            }
        }

        // The authoritative retire check: current at execution, not at proposal.
        foreach (var change in command.Changes.Where(change => change.RetiresReference))
        {
            if (_stockReferences.GetValueOrDefault((change.Target.Kind, change.Target.ReferenceId)) > 0)
            {
                return Conflict();
            }
        }

        var recorded = new List<RecordedReferenceChange>(command.Changes.Count);
        foreach (var change in command.Changes.OrderBy(change => change.Order))
        {
            recorded.Add(new RecordedReferenceChange(
                change.Order,
                change.Kind,
                change.Target.Kind,
                change.Target.ReferenceId,
                change.Target.Name)
            {
                NewName = change.NewName,
                Alias = change.Term?.Term,
                Aliases = [.. change.Terms.Where(term => !term.IsCanonical).Select(term => term.Term)],
            });

            Audits.Add(AuditFact.Create(
                ReferenceAdministrationFacts.EventTypeFor(change.Kind),
                AuditActorKind.Participant,
                command.ActorId.ToString(),
                command.InventoryId,
                subjectParticipantId: null,
                ReferenceAdministrationFacts.OutcomeCodeFor(change.Kind),
                command.Now));

            // Applying a change moves the reference's version, exactly as a fresh stamp does in SQL.
            _versions[(change.Target.Kind, change.Target.ReferenceId)] = Guid.NewGuid();

            if (change.RetiresReference && proposalStore is not null)
            {
                await proposalStore.InvalidateReferencingAsync(
                    command.InventoryId, change.Target.Kind, change.Target.ReferenceId, command.Now, cancellationToken);
            }
        }

        var result = new RecordedReferenceChangeSet(command.OperationId, command.ConsumesProposalId, recorded);
        _byOperation[(command.InventoryId, command.OperationId)] = result;
        _byTurn[(command.InventoryId, command.ConfirmedByTurnId)] = result;

        return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Applied, result);
    }

    private static ReferenceAdministrationStoreResult Conflict() =>
        new(ReferenceAdministrationStoreOutcome.Conflict, null);
}
```

- [ ] **Step 3: Widen the proposal double to match**

In `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryConfirmationProposalStore.cs`, add the reference-driven invalidation the interface will gain in Task 14, so the administration double above compiles and behaves like SQL. It is written against the double's existing `_rows` dictionary and its existing guarded `SettleAsync`, so only Pending rows can move:

```csharp
    /// <summary>
    /// Settles every pending proposal that references this Unit or Location, as retiring it must -
    /// including a stock proposal, which could otherwise create or move stock at a reference that no
    /// longer exists.
    /// </summary>
    public async Task<int> InvalidateReferencingAsync(
        InventoryId inventoryId,
        ReferenceKind kind,
        Guid referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var affected = _rows.Values
            .Where(row => row.Status == ProposalStatus.Pending
                && row.Proposal.InventoryId == inventoryId
                && (kind == ReferenceKind.Unit
                    ? row.Proposal.ReferencedUnitIds.Contains(new UnitId(referenceId))
                    : row.Proposal.ReferencedLocationIds.Contains(new LocationId(referenceId))))
            .Select(row => row.Proposal.Id)
            .ToList();

        var settled = 0;
        foreach (var proposalId in affected)
        {
            if (await SettleAsync(proposalId, ProposalStatus.Conflicted, now, cancellationToken))
            {
                settled++;
            }
        }

        return settled;
    }
```

The list is materialized before settling because `SettleAsync` replaces entries in the very dictionary being enumerated.

- [ ] **Step 4: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - the seam and the doubles compile and no shipped behavior changed. There is nothing behavioral to assert here yet; Task 10 is where the doubles start earning their keep, and Task 15 is where the real store is proven against SQL.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IReferenceAdministrationStore.cs \
        tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories
git commit -m "feat(inventories): define the atomic reference administration writer for #33"
```

---

## Task 10: Apply a lone non-destructive change, or propose everything else

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ReferenceAdministrationService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceAdministrationServiceTests.cs`

Why: this is where the role matrix, the replay rule, the atomic-refusal rule, and the confirmation policy are enforced - each in one expression, so none of them can drift.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceAdministrationServiceTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceAdministrationServiceTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly TurnId _turnId = new(Guid.NewGuid());
    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryConfirmationProposalStore _proposals = new();
    private readonly InMemoryReferenceAdministrationStore _administration;

    private const string ConversationId = "web-conversation-1";

    public ReferenceAdministrationServiceTests() => _administration = new InMemoryReferenceAdministrationStore(_proposals);

    private ReferenceAdministrationService Service() => new(
        new ReferenceChangeResolver(_catalog, _references),
        _administration,
        _proposals,
        new InventoryAuthorizationService(_inventories, _audits));

    private void GrantRole(MembershipRole role) =>
        _inventories.GrantMembership(_inventoryId, _participantId, role, DateTimeOffset.UnixEpoch);

    private UnitId SeedUnit(string canonicalName, params string[] aliases)
    {
        var unitId = _catalog.AddUnit(_inventoryId, canonicalName, aliases);
        _references.AddUnit(_inventoryId, unitId, [canonicalName, .. aliases]);

        return unitId;
    }

    private LocationId SeedLocation(string name)
    {
        var locationId = _catalog.AddLocation(_inventoryId, name);
        _references.AddLocation(_inventoryId, locationId, name);

        return locationId;
    }

    private static ReferenceChangeRequest Create(string name, int order = 1) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.CreateLocation,
        Name = name,
    };

    private Task<ReferenceAdministrationResult> ApplyAsync(params ReferenceChangeRequest[] requests) =>
        Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "create_locations", 0),
            requests,
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

    [Fact]
    public async Task A_non_member_is_told_nothing_that_would_reveal_the_Inventory_exists()
    {
        var result = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.NotFound, result.Kind);
        Assert.Equal("not_found", result.Code);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:NotAMember");
    }

    [Fact]
    public async Task A_Viewer_may_not_create_reference_data_and_the_denial_is_audited()
    {
        GrantRole(MembershipRole.Viewer);

        var result = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
    }

    [Fact]
    public async Task An_Editor_creating_one_Location_applies_immediately()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.Completed, result.Kind);
        var change = Assert.Single(result.Applied!.Changes);
        Assert.Equal("create_location", change.Operation);
        Assert.Equal("Shelf A", change.Name);
        Assert.Equal("Location:Created", Assert.Single(_administration.Audits).OutcomeCode);
        Assert.Null(await _proposals.FindPendingAsync(_participantId, ConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task An_Editor_may_not_Retire_and_the_denial_is_audited()
    {
        GrantRole(MembershipRole.Editor);
        SeedUnit("Cardboard Box");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "retire_units", 0),
            [new ReferenceChangeRequest { Order = 1, Kind = ReferenceChangeKind.RetireUnit, Reference = "Cardboard Box" }],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.Forbidden, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
        Assert.Empty(_administration.Audits);
    }

    [Fact]
    public async Task An_Owner_retiring_an_unused_Unit_is_asked_first_and_nothing_is_applied_yet()
    {
        GrantRole(MembershipRole.Owner);
        var boxId = SeedUnit("Cardboard Box");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "retire_units", 0),
            [new ReferenceChangeRequest { Order = 1, Kind = ReferenceChangeKind.RetireUnit, Reference = "Cardboard Box" }],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal("confirmation_required", result.Code);
        Assert.Equal(ConfirmationToken.TextLength, result.Proposal!.Token.Length);
        Assert.Equal("retire_unit", Assert.Single(result.Proposal.Changes).Operation);
        Assert.Empty(_administration.Audits);

        var pending = await _proposals.FindPendingAsync(_participantId, ConversationId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(ProposalKind.ReferenceAdministration, pending!.Kind);
        Assert.Equal(MembershipRole.Owner, pending.RequiredRole);
        Assert.Equal([new UnitId(boxId.Value)], pending.ReferencedUnitIds);
    }

    [Fact]
    public async Task Every_batch_of_more_than_one_change_is_proposed_rather_than_applied()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync(Create("Shelf A"), Create("Shelf B", order: 2));

        Assert.Equal(ReferenceAdministrationResultKind.ConfirmationRequired, result.Kind);
        Assert.Equal(2, result.Proposal!.Changes.Count);
        Assert.Empty(_administration.Audits);
    }

    [Fact]
    public async Task One_refusal_refuses_the_whole_set_and_nothing_is_applied()
    {
        GrantRole(MembershipRole.Editor);
        SeedLocation("Shelf B");

        var result = await ApplyAsync(Create("Shelf A"), Create("SHELF B", order: 2));

        Assert.Equal(ReferenceAdministrationResultKind.Conflict, result.Kind);
        Assert.Equal("name_in_use", result.Code);
        Assert.Empty(_administration.Audits);
        Assert.Null(await _proposals.FindPendingAsync(_participantId, ConversationId, CancellationToken.None));
    }

    [Fact]
    public async Task Two_changes_claiming_one_term_are_refused_rather_than_left_to_the_index()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync(Create("Shelf A"), Create("shelf a", order: 2));

        Assert.Equal(ReferenceAdministrationResultKind.Invalid, result.Kind);
        Assert.Equal("conflicting_changes", result.Code);
    }

    [Fact]
    public async Task Two_changes_acting_on_one_reference_are_refused()
    {
        GrantRole(MembershipRole.Editor);
        SeedUnit("Cardboard Box");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "add_unit_aliases", 0),
            [
                new ReferenceChangeRequest
                {
                    Order = 1, Kind = ReferenceChangeKind.AddUnitAlias, Reference = "Cardboard Box", Alias = "cartons",
                },
                new ReferenceChangeRequest
                {
                    Order = 2, Kind = ReferenceChangeKind.AddUnitAlias, Reference = "Cardboard Box", Alias = "kartons",
                },
            ],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.Invalid, result.Kind);
        Assert.Equal("conflicting_changes", result.Code);
    }

    [Fact]
    public async Task An_unknown_reference_answers_reference_not_found_with_bounded_suggestions()
    {
        GrantRole(MembershipRole.Editor);
        SeedUnit("Box Large");

        var result = await Service().ApplyAsync(
            _participantId,
            _inventoryId,
            _turnId,
            ReferenceOperationId.Derive(_turnId, "add_unit_aliases", 0),
            [new ReferenceChangeRequest { Order = 1, Kind = ReferenceChangeKind.AddUnitAlias, Reference = "box", Alias = "bx" }],
            ConversationId,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationResultKind.ReferenceNotFound, result.Kind);
        Assert.Equal("reference_not_found", result.Code);
        Assert.Equal(ReferenceKind.Unit, result.UnresolvedReference);
        Assert.Equal(["Box Large"], result.Suggestions);
    }

    [Fact]
    public async Task A_Turn_that_already_applied_its_changes_re_reports_them_instead_of_re_planning()
    {
        GrantRole(MembershipRole.Editor);

        var first = await ApplyAsync(Create("Shelf A"));
        var replay = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.Completed, first.Kind);
        Assert.Equal(ReferenceAdministrationResultKind.Completed, replay.Kind);
        Assert.Equal(first.Applied!.Changes[0].ReferenceId, replay.Applied!.Changes[0].ReferenceId);
        Assert.Single(_administration.Audits);
    }

    [Fact]
    public async Task A_replay_is_answered_only_after_authorization_so_it_discloses_nothing()
    {
        GrantRole(MembershipRole.Editor);
        await ApplyAsync(Create("Shelf A"));

        _inventories.RevokeMembership(_inventoryId, _participantId);
        var replay = await ApplyAsync(Create("Shelf A"));

        Assert.Equal(ReferenceAdministrationResultKind.NotFound, replay.Kind);
    }

    [Fact]
    public async Task An_empty_change_set_is_invalid()
    {
        GrantRole(MembershipRole.Editor);

        var result = await ApplyAsync();

        Assert.Equal(ReferenceAdministrationResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_changes", result.Code);
    }
}
```

`InMemoryInventoryStore.GrantMembership` and `RevokeMembership` are the shipped test-only helpers, and `InMemoryInventoryAuthorizationAuditStore.RecordedFacts` is where its audits land. If any of the three has drifted, copy the calls the shipped `InventoryAuthorizationServiceTests` and `StockChangeSetServiceTests` in this same folder make instead of inventing members.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceAdministrationServiceTests"`
Expected: FAIL to compile - `ReferenceAdministrationService`, `ReferenceAdministrationResult`, and the view types do not exist.

- [ ] **Step 3: Write the service**

Create `src/MultiChannelAgent.Application/Inventories/ReferenceAdministrationService.cs`:

```csharp
using System.Globalization;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

// System.Globalization is used for exactly one thing here: rendering a proposal's expiry as
// culture-invariant round-trip text, so a client in any locale reads the same instant.

/// <summary>Semantic outcome shape for one reference administration change set.</summary>
public enum ReferenceAdministrationResultKind
{
    Completed,

    /// <summary>The changes are understood and authorized but too consequential to apply unasked; an exact proposal is stored.</summary>
    ConfirmationRequired,

    /// <summary>No accessible Inventory - identical whether it does not exist or is simply not authorized.</summary>
    NotFound,

    /// <summary>A named Unit or Location does not exist here, or is retired. Bounded deterministic suggestions accompany it.</summary>
    ReferenceNotFound,

    Forbidden,
    Conflict,
    Invalid,
}

/// <summary>
/// One administration change, exactly as proposed or exactly as applied. Every field is a semantic
/// fact: no versions, no audit identities, no SQL detail.
/// </summary>
public sealed record ReferenceChangeView(
    int Order,
    string Operation,
    string Reference,
    string ReferenceId,
    string Name,
    string? NewName,
    string? Alias,
    IReadOnlyList<string> Aliases);

/// <summary>What one applied administration change set did.</summary>
public sealed record ReferenceChangeSetView(IReadOnlyList<ReferenceChangeView> Changes);

/// <summary>
/// An exact stored administration proposal, as shown to the Participant. <see cref="Token"/> is the
/// plaintext confirmation code; the proposal itself keeps only its hash. See
/// <see cref="ConfirmationToken"/> for exactly where it lives and for how long.
/// </summary>
public sealed record ReferenceProposalView(string Token, string ExpiresAt, IReadOnlyList<ReferenceChangeView> Changes);

/// <summary>The semantic result of an administration request. Never SQL detail, row versions, audit identities, or unauthorized existence.</summary>
public sealed record ReferenceAdministrationResult(
    ReferenceAdministrationResultKind Kind,
    string Code,
    ReferenceChangeSetView? Applied = null,
    ReferenceProposalView? Proposal = null,
    ReferenceKind? UnresolvedReference = null,
    IReadOnlyList<string>? Suggestions = null);

/// <summary>
/// The deterministic authority for one set of Unit and Location changes: authorize the role the
/// changes actually demand, answer a replay, resolve every change against current state, and then
/// either apply one non-destructive change immediately or store an exact proposal and hand back its
/// one-time token.
///
/// Three rules live here and nowhere else, each in one expression:
/// <list type="bullet">
/// <item>the required role is Owner when any change retires, and Editor otherwise;</item>
/// <item>confirmation is required when there is more than one change, or any change retires;</item>
/// <item>a set refuses whole - one refusal, one reference touched twice, or one term claimed twice.</item>
/// </list>
///
/// Callers only ever supply an InventoryId already scoped by trusted context, and an unauthorized
/// Inventory stays indistinguishable from one that does not exist.
/// </summary>
public sealed class ReferenceAdministrationService(
    ReferenceChangeResolver resolver,
    IReferenceAdministrationStore administrationStore,
    IConfirmationProposalStore proposalStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<ReferenceAdministrationResult> ApplyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        ReferenceOperationId operationId,
        IReadOnlyList<ReferenceChangeRequest> requests,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        // The role the *requested* changes demand, decided before anything is resolved, so an Editor
        // asking to Retire is refused without ever learning whether the target exists.
        var requiredRole = requests.Any(request => ReferenceAdministrationFacts.RequiredRole(request.Kind) == MembershipRole.Owner)
            ? MembershipRole.Owner
            : MembershipRole.Editor;

        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, requiredRole, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.NotFound, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Forbidden, "forbidden");
        }

        // Answered from the ledger before anything is resolved or re-planned, because a replayed Turn
        // meets a catalog its own first attempt already changed - re-planning would report the Unit it
        // created as a collision. Deliberately after authorization, so a Viewer or a non-member learns
        // nothing from a replay they could not learn from a first attempt.
        if (await administrationStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyRecorded)
        {
            return Applied(alreadyRecorded);
        }

        if (requests.Count == 0)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Invalid, "invalid_changes");
        }

        if (requests.Count > ConfirmationProposal.MaxChanges)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Invalid, "too_many_changes");
        }

        var changes = new List<ProposedReferenceChange>(requests.Count);
        var versions = new Dictionary<(ReferenceKind, Guid), ExpectedReferenceVersion>();
        var absences = new List<ExpectedTermAbsence>();
        var touched = new HashSet<(ReferenceKind, Guid)>();
        var claimedTerms = new HashSet<ExpectedTermAbsence>();

        foreach (var request in requests.OrderBy(request => request.Order))
        {
            var resolution = await resolver.ResolveAsync(inventoryId, request, cancellationToken);
            if (resolution.Kind != ReferenceChangeResolutionKind.Resolved)
            {
                // One refusal refuses the whole set. A batch is atomic, so answering "these two worked
                // and that one did not" would be describing a state that never exists.
                return Refused(resolution);
            }

            var change = resolution.Change!;

            // Every change in a set is resolved against the state the set started from, so two changes
            // to one reference would each be planned as if the other had not happened. Refusing is the
            // only answer that cannot silently apply something nobody asked for.
            if (!change.CreatesReference && !touched.Add((change.Target.Kind, change.Target.ReferenceId)))
            {
                return Invalid("conflicting_changes");
            }

            // Two changes can also collide without sharing a reference, by both claiming one term.
            // Each was resolved against a state in which that term was free, so left to execution they
            // would violate the filtered uniqueness index halfway through a transaction.
            foreach (var absence in resolution.ExpectedAbsences ?? [])
            {
                if (!claimedTerms.Add(absence))
                {
                    return Invalid("conflicting_changes");
                }

                absences.Add(absence);
            }

            changes.Add(change);

            foreach (var version in resolution.ExpectedVersions ?? [])
            {
                versions[(version.Kind, version.ReferenceId)] = version;
            }
        }

        var requiresConfirmation = changes.Count > 1
            || changes.Any(change => ReferenceAdministrationFacts.RequiresConfirmation(change.Kind));

        return requiresConfirmation
            ? await ProposeAsync(
                participantId, inventoryId, turnId, channelConversationId, changes, versions.Values.ToList(), absences, now, cancellationToken)
            : await ApplyImmediatelyAsync(
                participantId, inventoryId, turnId, operationId, changes, versions.Values.ToList(), absences, now, cancellationToken);
    }

    private async Task<ReferenceAdministrationResult> ProposeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string channelConversationId,
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Issued here, hashed into the proposal, and returned in the answer. The proposal row itself
        // never holds anything that could approve it.
        var token = ConfirmationToken.Issue();

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(token),
            participantId,
            channelConversationId,
            inventoryId,
            turnId,
            changes,
            versions,
            absences,
            now);

        // Storing supersedes whatever was pending in this conversation - a stock proposal just as much
        // as an administration one - atomically, so a confirmation arriving now can only ever mean
        // this proposal.
        await proposalStore.StoreAsync(proposal, now, cancellationToken);

        return new ReferenceAdministrationResult(
            ReferenceAdministrationResultKind.ConfirmationRequired,
            "confirmation_required",
            Proposal: new ReferenceProposalView(
                token,
                proposal.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                proposal.ReferenceChanges.Select(ToChangeView).ToList()));
    }

    private async Task<ReferenceAdministrationResult> ApplyImmediatelyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        ReferenceOperationId operationId,
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await administrationStore.ApplyAsync(
            new ReferenceChangeSetCommand
            {
                OperationId = operationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = null,
                Changes = changes,
                ExpectedVersions = versions,
                ExpectedTermAbsences = absences,
                Now = now,
            },
            cancellationToken);

        return stored.Outcome == ReferenceAdministrationStoreOutcome.Conflict
            ? new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Conflict, "state_changed")
            : Applied(stored.Recorded!);
    }

    /// <summary>
    /// The one place an applied administration change set becomes an answer, so a replay served from
    /// the ledger, a store that converged on an already-applied operation, and a first attempt that
    /// has just written are literally indistinguishable to a Participant.
    /// </summary>
    internal static ReferenceAdministrationResult Applied(RecordedReferenceChangeSet recorded) => new(
        ReferenceAdministrationResultKind.Completed,
        "completed",
        new ReferenceChangeSetView(recorded.Changes.Select(ToChangeView).ToList()));

    internal static ReferenceChangeView ToChangeView(RecordedReferenceChange change) => new(
        change.Order,
        ReferenceAdministrationFacts.ToMachineText(change.Kind),
        change.ReferenceKind.ToString().ToLowerInvariant(),
        change.ReferenceId.ToString(),
        change.Name,
        change.NewName,
        change.Alias,
        change.Aliases);

    internal static ReferenceChangeView ToChangeView(ProposedReferenceChange change) => new(
        change.Order,
        ReferenceAdministrationFacts.ToMachineText(change.Kind),
        change.Target.Kind.ToString().ToLowerInvariant(),
        change.Target.ReferenceId.ToString(),
        change.Target.Name,
        change.NewName,
        change.Term?.Term,
        [.. change.Terms.Where(term => !term.IsCanonical).Select(term => term.Term)]);

    private static ReferenceAdministrationResult Refused(ReferenceChangeResolution resolution) => resolution.Kind switch
    {
        ReferenceChangeResolutionKind.NotFound => new(ReferenceAdministrationResultKind.NotFound, resolution.Code),
        ReferenceChangeResolutionKind.ReferenceNotFound => new(
            ReferenceAdministrationResultKind.ReferenceNotFound,
            resolution.Code,
            UnresolvedReference: resolution.UnresolvedReference,
            Suggestions: resolution.Suggestions),
        ReferenceChangeResolutionKind.Conflict => new(ReferenceAdministrationResultKind.Conflict, resolution.Code),
        _ => new(ReferenceAdministrationResultKind.Invalid, resolution.Code),
    };

    private static ReferenceAdministrationResult Invalid(string code) =>
        new(ReferenceAdministrationResultKind.Invalid, code);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceAdministrationServiceTests"`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ReferenceAdministrationService.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceAdministrationServiceTests.cs
git commit -m "feat(inventories): apply a lone reference change or propose everything else for #33"
```

---

## Task 11: List active Units and Locations for a Viewer

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ReferenceListingService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceListingServiceTests.cs`

Why: `list_units` and `list_locations` are the only two administration tools a Viewer may call, and they are also what the web workspace projects. One service, one authorization seam, one ordering - so the conversation and the workspace can never disagree about what exists.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceListingServiceTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceListingServiceTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();

    private const string ConversationId = "web-conversation-1";

    private ReferenceListingService Service() =>
        new(_catalog, new InventoryAuthorizationService(_inventories, _audits));

    private Task<UnitListResult> ListUnitsAsync(int? pageSize = null, string? cursor = null) =>
        Service().ListUnitsAsync(
            _participantId, _inventoryId, pageSize, cursor, ConversationId, DateTimeOffset.UnixEpoch, CancellationToken.None);

    private Task<LocationListResult> ListLocationsAsync(int? pageSize = null, string? cursor = null) =>
        Service().ListLocationsAsync(
            _participantId, _inventoryId, pageSize, cursor, ConversationId, DateTimeOffset.UnixEpoch, CancellationToken.None);

    [Fact]
    public async Task A_non_member_is_told_nothing_that_would_reveal_the_Inventory_exists()
    {
        var result = await ListUnitsAsync();

        Assert.Equal(ReferenceListResultKind.NotFound, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:NotAMember");
    }

    [Fact]
    public async Task A_Viewer_may_list_active_Units_with_their_aliases()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddUnit(_inventoryId, "each", ["piece", "pieces", "pc", "pcs"], isReserved: true);
        _catalog.AddUnit(_inventoryId, "Cardboard Box", ["boxes"]);
        _catalog.AddUnit(_inventoryId, "Pallet", [], retired: true);

        var result = await ListUnitsAsync();

        Assert.Equal(ReferenceListResultKind.Completed, result.Kind);
        Assert.Equal(["Cardboard Box", "each"], result.View!.Units.Select(unit => unit.Name));
        Assert.Equal(["boxes"], result.View.Units[0].Aliases);
        Assert.False(result.View.HasMore);
        Assert.Null(result.View.NextCursor);
    }

    [Fact]
    public async Task A_Viewer_may_list_active_Locations()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddLocation(_inventoryId, "Shelf B");
        _catalog.AddLocation(_inventoryId, "Shelf A");
        _catalog.AddLocation(_inventoryId, "Old Bay", retired: true);

        var result = await ListLocationsAsync();

        Assert.Equal(["Shelf A", "Shelf B"], result.View!.Locations.Select(location => location.Name));
    }

    [Fact]
    public async Task A_bounded_page_reports_that_more_remain_and_hands_back_a_resumable_cursor()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddLocation(_inventoryId, "Shelf A");
        _catalog.AddLocation(_inventoryId, "Shelf B");
        _catalog.AddLocation(_inventoryId, "Shelf C");

        var first = await ListLocationsAsync(pageSize: 2);

        Assert.True(first.View!.HasMore);
        Assert.Equal(["Shelf A", "Shelf B"], first.View.Locations.Select(location => location.Name));

        var second = await ListLocationsAsync(pageSize: 2, first.View.NextCursor);

        Assert.False(second.View!.HasMore);
        Assert.Equal(["Shelf C"], second.View.Locations.Select(location => location.Name));
    }

    [Fact]
    public async Task A_page_size_outside_the_bound_is_answered_by_its_own_code()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);

        var result = await ListUnitsAsync(pageSize: ReferenceListQuery.MaxPageSize + 1);

        Assert.Equal(ReferenceListResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_page_size", result.Code);
    }

    [Fact]
    public async Task A_cursor_issued_for_the_other_list_is_refused()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        var cursor = new ReferenceListCursor(
            ReferenceKind.Location, new ReferenceOrderKey("shelf a", Guid.NewGuid().ToString("D"))).Encode();

        var result = await ListUnitsAsync(cursor: cursor);

        Assert.Equal(ReferenceListResultKind.Invalid, result.Kind);
        Assert.Equal("invalid_cursor", result.Code);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceListingServiceTests"`
Expected: FAIL to compile - `ReferenceListingService`, `UnitListResult`, `LocationListResult`, and the view types do not exist.

- [ ] **Step 3: Write the service**

Create `src/MultiChannelAgent.Application/Inventories/ReferenceListingService.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for an authorized catalog read.</summary>
public enum ReferenceListResultKind
{
    Completed,
    Forbidden,
    NotFound,
    Invalid,
}

/// <summary>One active Unit as exposed at the application boundary: its stable identity, its canonical name, and its active aliases in order.</summary>
public sealed record UnitView(string Id, string Name, IReadOnlyList<string> Aliases);

/// <summary>One active Location. Flat and alias-free by design; unlocated stock is the absence of a reference and never appears here.</summary>
public sealed record LocationView(string Id, string Name);

/// <summary>One authorized page of active Units, plus the opaque cursor to resume from when <see cref="HasMore"/> is true.</summary>
public sealed record UnitListView(IReadOnlyList<UnitView> Units, string? NextCursor, bool HasMore);

/// <summary>One authorized page of active Locations. See <see cref="UnitListView"/>.</summary>
public sealed record LocationListView(IReadOnlyList<LocationView> Locations, string? NextCursor, bool HasMore);

/// <summary>The semantic result of a Unit list. Never SQL detail, versions, reserved flags, or unauthorized existence.</summary>
public sealed record UnitListResult(ReferenceListResultKind Kind, string Code, UnitListView? View = null);

/// <summary>The semantic result of a Location list.</summary>
public sealed record LocationListResult(ReferenceListResultKind Kind, string Code, LocationListView? View = null);

/// <summary>
/// Lists the active Units and Locations of one Inventory: bounded, in the stable deterministic
/// display order both catalog reads share, retired references excluded. Viewer is enough - listing
/// reference data mutates nothing - and authorization always flows through
/// <see cref="InventoryAuthorizationService"/> so an unauthorized Inventory is indistinguishable
/// from one that does not exist.
///
/// This is the one service behind both the conversational <c>list_units</c>/<c>list_locations</c>
/// tools and the web workspace projections, so the conversation and the workspace can never disagree
/// about what exists.
/// </summary>
public sealed class ReferenceListingService(
    IReferenceCatalogStore catalogStore, InventoryAuthorizationService authorizationService)
{
    public async Task<UnitListResult> ListUnitsAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        int? pageSize,
        string? cursor,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return new UnitListResult(refusal.Kind, refusal.Code);
        }

        ReferenceListQuery query;
        try
        {
            query = ReferenceListQuery.Create(inventoryId, ReferenceKind.Unit, pageSize, cursor);
        }
        catch (ArgumentException invalid)
        {
            return new UnitListResult(ReferenceListResultKind.Invalid, InvalidQueryCode(invalid.ParamName));
        }

        var page = await catalogStore.ListUnitsAsync(query, cancellationToken);
        var hasMore = page.Count > query.PageSize;
        var rows = page.Take(query.PageSize).ToList();

        var nextCursor = hasMore
            ? new ReferenceListCursor(
                ReferenceKind.Unit,
                new ReferenceOrderKey(rows[^1].NormalizedCanonicalName, rows[^1].Id.Value.ToString("D"))).Encode()
            : null;

        return new UnitListResult(
            ReferenceListResultKind.Completed,
            "completed",
            new UnitListView(
                rows.Select(row => new UnitView(row.Id.ToString(), row.CanonicalName, row.Aliases)).ToList(),
                nextCursor,
                hasMore));
    }

    public async Task<LocationListResult> ListLocationsAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        int? pageSize,
        string? cursor,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeAsync(participantId, inventoryId, channelConversationId, now, cancellationToken);
        if (authorization is { } refusal)
        {
            return new LocationListResult(refusal.Kind, refusal.Code);
        }

        ReferenceListQuery query;
        try
        {
            query = ReferenceListQuery.Create(inventoryId, ReferenceKind.Location, pageSize, cursor);
        }
        catch (ArgumentException invalid)
        {
            return new LocationListResult(ReferenceListResultKind.Invalid, InvalidQueryCode(invalid.ParamName));
        }

        var page = await catalogStore.ListLocationsAsync(query, cancellationToken);
        var hasMore = page.Count > query.PageSize;
        var rows = page.Take(query.PageSize).ToList();

        var nextCursor = hasMore
            ? new ReferenceListCursor(
                ReferenceKind.Location,
                new ReferenceOrderKey(rows[^1].NormalizedName, rows[^1].Id.Value.ToString("D"))).Encode()
            : null;

        return new LocationListResult(
            ReferenceListResultKind.Completed,
            "completed",
            new LocationListView(
                rows.Select(row => new LocationView(row.Id.ToString(), row.Name)).ToList(),
                nextCursor,
                hasMore));
    }

    private async Task<(ReferenceListResultKind Kind, string Code)?> AuthorizeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Viewer, channelConversationId, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.NotFound => (ReferenceListResultKind.NotFound, "not_found"),
            InventoryAuthorizationOutcome.Forbidden => (ReferenceListResultKind.Forbidden, "forbidden"),
            _ => null,
        };
    }

    /// <summary>The machine code naming the bound a rejected request violated.</summary>
    internal static string InvalidQueryCode(string? parameterName) => parameterName switch
    {
        "pageSize" => "invalid_page_size",
        "cursor" => "invalid_cursor",
        _ => "invalid_query",
    };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceListingServiceTests"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ReferenceListingService.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceListingServiceTests.cs
git commit -m "feat(inventories): list active Units and Locations for a Viewer for #33"
```

---

## Task 12: Confirm a stock or a reference proposal under the role each demands

**Files:**
- Rename: `src/MultiChannelAgent.Application/Inventories/StockConfirmationService.cs` -> `src/MultiChannelAgent.Application/Inventories/InventoryConfirmationService.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Rename: `tests/MultiChannelAgent.Application.Tests/Inventories/StockConfirmationServiceTests.cs` -> `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryConfirmationServiceTests.cs`
- Modify: `tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs`

Why: there is exactly one pending proposal per Participant and ChannelConversation, so there must be exactly one thing that confirms it. A second confirmation service would need its own copy of the authorization preamble, the replay lookup, the evidence rule, the binding check, the expiry check, and the token check - six chances to drift on the most safety-critical path in the application. The class keeps every rule it has and gains exactly two: ask both ledgers for a replay, and require Owner before executing a proposal that retires.

- [ ] **Step 1: Rename the file, the class, and its result types**

```bash
git mv src/MultiChannelAgent.Application/Inventories/StockConfirmationService.cs \
       src/MultiChannelAgent.Application/Inventories/InventoryConfirmationService.cs
git mv tests/MultiChannelAgent.Application.Tests/Inventories/StockConfirmationServiceTests.cs \
       tests/MultiChannelAgent.Application.Tests/Inventories/InventoryConfirmationServiceTests.cs

for file in src/MultiChannelAgent.Application/Inventories/InventoryConfirmationService.cs \
            src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs \
            src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
            tests/MultiChannelAgent.Application.Tests/Inventories/InventoryConfirmationServiceTests.cs \
            tests/MultiChannelAgent.Application.Tests/Inventories/StockToolDispatcherTests.cs; do
  sed -i \
    -e 's/StockConfirmationServiceTests/InventoryConfirmationServiceTests/g' \
    -e 's/StockConfirmationService/InventoryConfirmationService/g' \
    -e 's/StockConfirmationResultKind/InventoryConfirmationResultKind/g' \
    -e 's/StockConfirmationResult/InventoryConfirmationResult/g' \
    "$file"
done

grep -rn "StockConfirmation" src tests --include=*.cs | grep -v "/bin/\|/obj/" || echo "no stragglers"
```

Expected: `no stragglers`.

- [ ] **Step 2: Write the failing test**

Append to `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryConfirmationServiceTests.cs`, inside the renamed class. The shipped class builds everything through a static `CreateHarness()` returning a `Harness` record and identifies Participants through static readonly fields (`Editor`, `Viewer`, `SomeInventory`, `Now`, `Conversation`); these tests follow that exactly and add the two things it lacks - an Owner, and the administration store.

First add the Owner identity beside the shipped ones:

```csharp
    private static readonly ParticipantId Owner = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
```

Then add the reference harness and its helpers:

```csharp
    private sealed record ReferenceHarness(
        InventoryConfirmationService Confirmations,
        InMemoryConfirmationProposalStore ProposalStore,
        InMemoryReferenceAdministrationStore AdministrationStore,
        InMemoryInventoryAuthorizationAuditStore AuditStore);

    /// <summary>
    /// The same shape as <see cref="CreateHarness"/>, plus an Owner and the reference administration
    /// store. It deliberately reuses the shipped identities and instant so a reference case and a
    /// stock case are directly comparable.
    /// </summary>
    private static ReferenceHarness CreateReferenceHarness(MembershipRole role)
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Owner, role, Now);

        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);

        var stockStore = new InMemoryStockStore();
        var proposalStore = new InMemoryConfirmationProposalStore();
        var changeSetStore = new InMemoryStockChangeSetStore(stockStore, proposalStore);
        var administrationStore = new InMemoryReferenceAdministrationStore(proposalStore);

        return new ReferenceHarness(
            new InventoryConfirmationService(proposalStore, changeSetStore, administrationStore, authorizationService),
            proposalStore,
            administrationStore,
            auditStore);
    }

    /// <summary>Stores one pending Owner-only Retire proposal and returns it, its plaintext token, and the Unit it would retire.</summary>
    private static async Task<(ConfirmationProposal Proposal, string Token, Guid UnitId)> StoreRetireProposalAsync(
        ReferenceHarness harness)
    {
        var token = ConfirmationToken.Issue();
        var unitId = Guid.NewGuid();

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(token),
            Owner,
            Conversation,
            SomeInventory,
            new TurnId(Guid.NewGuid()),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RetireUnit,
                    Target = new ProposedReferenceState(
                        ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", Reserved: false),
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, unitId, Guid.NewGuid())],
            [],
            Now);

        await harness.ProposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        return (proposal, token, unitId);
    }

    private static Task<InventoryConfirmationResult> ConfirmAsync(
        ReferenceHarness harness, TurnId turnId, string? token, DirectConfirmationEvidence evidence) =>
        harness.Confirmations.ConfirmAsync(
            Owner, SomeInventory, turnId, token, evidence, Conversation, Now, CancellationToken.None);

    [Fact]
    public async Task An_Owner_confirming_a_Retire_executes_it_exactly_once()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, unitId) = await StoreRetireProposalAsync(harness);
        var turnId = new TurnId(Guid.NewGuid());

        var result = await ConfirmAsync(harness, turnId, token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Completed, result.Kind);
        var change = Assert.Single(result.AppliedReferences!.Changes);
        Assert.Equal("retire_unit", change.Operation);
        Assert.Equal(unitId.ToString(), change.ReferenceId);
        Assert.Equal("Unit:Retired", Assert.Single(harness.AdministrationStore.Audits).OutcomeCode);
        Assert.Equal(
            ProposalStatus.Confirmed,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_Editor_may_not_confirm_a_Retire_and_the_denial_is_audited()
    {
        var harness = CreateReferenceHarness(MembershipRole.Editor);
        var (proposal, token, _) = await StoreRetireProposalAsync(harness);

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Forbidden, result.Kind);
        Assert.Equal("forbidden", result.Code);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Contains(harness.AuditStore.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");

        // A denied confirmation must not burn the Participant's own pending work: lookup is
        // per-Participant, nobody else can reach it, and it expires on its own in ten minutes.
        Assert.Equal(
            ProposalStatus.Pending,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_reference_proposal_still_needs_the_Participants_own_direct_confirmation()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, _) = await StoreRetireProposalAsync(harness);

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.None);

        Assert.Equal(InventoryConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("confirmation_evidence_missing", result.Code);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Equal(
            ProposalStatus.Pending,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_reference_proposal_whose_reference_moved_underneath_it_conflicts_and_is_settled()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, unitId) = await StoreRetireProposalAsync(harness);
        harness.AdministrationStore.SetVersion(ReferenceKind.Unit, unitId, Guid.NewGuid());

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Equal(
            ProposalStatus.Conflicted,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_confirmed_Retire_that_stock_now_references_changes_nothing()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, unitId) = await StoreRetireProposalAsync(harness);

        // Stock was created against this Unit after the proposal was reviewed. Current state at
        // execution is what decides, so the confirmation must fail rather than retire a used Unit.
        harness.AdministrationStore.SetStockReferences(ReferenceKind.Unit, unitId, 1);

        var result = await ConfirmAsync(harness, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Conflict, result.Kind);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Equal(
            ProposalStatus.Conflicted,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_Turn_that_already_executed_a_reference_proposal_re_reports_it()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (_, token, _) = await StoreRetireProposalAsync(harness);
        var turnId = new TurnId(Guid.NewGuid());

        var first = await ConfirmAsync(harness, turnId, token, DirectConfirmationEvidence.Confirmed);
        var replay = await ConfirmAsync(harness, turnId, token, DirectConfirmationEvidence.Confirmed);

        Assert.Equal(InventoryConfirmationResultKind.Completed, first.Kind);
        Assert.Equal(InventoryConfirmationResultKind.Completed, replay.Kind);
        Assert.Equal(
            first.AppliedReferences!.Changes[0].ReferenceId,
            replay.AppliedReferences!.Changes[0].ReferenceId);
        Assert.Single(harness.AdministrationStore.Audits);
    }

    [Fact]
    public async Task Rejecting_a_reference_proposal_changes_nothing_at_all()
    {
        var harness = CreateReferenceHarness(MembershipRole.Owner);
        var (proposal, token, _) = await StoreRetireProposalAsync(harness);

        var result = await harness.Confirmations.RejectAsync(
            Owner, SomeInventory, new TurnId(Guid.NewGuid()), token, DirectConfirmationEvidence.Rejected,
            Conversation, Now, CancellationToken.None);

        Assert.Equal(InventoryConfirmationResultKind.Rejected, result.Kind);
        Assert.Empty(harness.AdministrationStore.Audits);
        Assert.Equal(
            ProposalStatus.Rejected,
            await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
```

Also widen the shipped `CreateHarness` so it keeps compiling against the new constructor - it gains one argument and nothing else:

```csharp
            new InventoryConfirmationService(
                proposalStore, changeSetStore, new InMemoryReferenceAdministrationStore(proposalStore), authorizationService),
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~InventoryConfirmationServiceTests"`
Expected: FAIL to compile - the constructor takes three dependencies, not four, and `InventoryConfirmationResult` has no `AppliedReferences`.

- [ ] **Step 4: Widen the service**

In `src/MultiChannelAgent.Application/Inventories/InventoryConfirmationService.cs`:

Widen the result record:

```csharp
/// <summary>The semantic result of a confirmation or rejection. Never names a Stock Entry, a Unit, a Location, an Inventory, or another Participant on a refusal.</summary>
public sealed record InventoryConfirmationResult(
    InventoryConfirmationResultKind Kind,
    string Code,
    StockChangeSetView? Applied = null,
    ReferenceChangeSetView? AppliedReferences = null);
```

Widen the constructor:

```csharp
public sealed class InventoryConfirmationService(
    IConfirmationProposalStore proposalStore,
    IStockChangeSetStore changeSetStore,
    IReferenceAdministrationStore administrationStore,
    InventoryAuthorizationService authorizationService)
```

Replace the replay lookup inside `ConfirmAsync` (the block guarded by `FindRecordedByTurnAsync`) with:

```csharp
        // Asked first, and asked of both ledgers rather than of the proposal: a confirmation consumes
        // its proposal, so a Turn re-driven after a crash between the mutation transaction and the
        // Outcome transaction has nothing pending left to find - and by then its kind is no longer
        // knowable either. Both lookups are single indexed reads scoped to this Inventory, and at most
        // one of them can answer.
        if (await changeSetStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyExecuted)
        {
            return Completed(alreadyExecuted);
        }

        if (await administrationStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyAdministered)
        {
            return Completed(alreadyAdministered);
        }
```

Immediately after the token check (`if (!ConfirmationToken.Matches(pending.TokenHash, presentedToken))`) and before execution, insert the one new authorization step and the routing:

```csharp
        // A proposal that retires a Unit or a Location is Owner-only, and the role is rechecked here
        // rather than trusted from when the proposal was made - Membership can change inside ten
        // minutes. Every stock proposal reports Editor, so this is a no-op for the shipped path.
        if (pending.RequiredRole == MembershipRole.Owner)
        {
            var elevated = await authorizationService.AuthorizeAsync(
                participantId, inventoryId, MembershipRole.Owner, channelConversationId, now, cancellationToken);

            if (elevated.Outcome != InventoryAuthorizationOutcome.Authorized)
            {
                // Deliberately left Pending. Lookup is per-Participant, so nobody else can reach it,
                // and it settles itself in ten minutes; destroying a Participant's own reviewed work
                // because their role changed mid-conversation would be the worse failure.
                return new InventoryConfirmationResult(InventoryConfirmationResultKind.Forbidden, "forbidden");
            }
        }

        return pending.Kind == ProposalKind.ReferenceAdministration
            ? await ExecuteReferencesAsync(participantId, inventoryId, turnId, pending, now, cancellationToken)
            : await ExecuteStockAsync(participantId, inventoryId, turnId, pending, now, cancellationToken);
```

Move the shipped `changeSetStore.ApplyAsync(...)` block and its conflict handling verbatim into a new private method, changing nothing about it:

```csharp
    private async Task<InventoryConfirmationResult> ExecuteStockAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        ConfirmationProposal pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
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
            return new InventoryConfirmationResult(InventoryConfirmationResultKind.Conflict, "state_changed");
        }

        return Completed(stored.Recorded!);
    }

    private async Task<InventoryConfirmationResult> ExecuteReferencesAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        ConfirmationProposal pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await administrationStore.ApplyAsync(
            new ReferenceChangeSetCommand
            {
                OperationId = pending.ReferenceExecutionOperationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = pending.Id,
                Changes = pending.ReferenceChanges,
                ExpectedVersions = pending.ExpectedReferenceVersions,
                ExpectedTermAbsences = pending.ExpectedTermAbsences,
                Now = now,
            },
            cancellationToken);

        if (stored.Outcome == ReferenceAdministrationStoreOutcome.Conflict)
        {
            // A version that moved, a term that was claimed, or - the case this ticket exists for - a
            // Stock Entry that now references what this would retire. Nothing was applied, and the
            // proposal can never commit now, so it is settled.
            await proposalStore.SettleAsync(pending.Id, ProposalStatus.Conflicted, now, cancellationToken);
            return new InventoryConfirmationResult(InventoryConfirmationResultKind.Conflict, "state_changed");
        }

        return Completed(stored.Recorded!);
    }
```

Finally add the second `Completed` overload beside the shipped one:

```csharp
    private static InventoryConfirmationResult Completed(RecordedReferenceChangeSet recorded) => new(
        InventoryConfirmationResultKind.Completed,
        "completed",
        AppliedReferences: new ReferenceChangeSetView(
            recorded.Changes.Select(ReferenceAdministrationService.ToChangeView).ToList()));
```

- [ ] **Step 5: Keep the dispatcher and DI compiling**

In `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`, `ToDecision` now receives a result that may carry either payload. Replace its `Completed` arm with:

```csharp
        InventoryConfirmationResultKind.Completed when result.Applied is { } applied => Completed(
            "completed",
            SummarizeChanges(applied),
            JsonSerializer.Serialize(new StockChangesPayload(1, "stock_changes", applied.Changes), PayloadOptions)),

        // A confirmed administration proposal is answered by the reference dispatcher's own shaping,
        // reached through the router; a confirmation that executed one is reported here only as the
        // completed fact it is, because this dispatcher owns no reference vocabulary.
        InventoryConfirmationResultKind.Completed => Semantic(
            OutcomeCategory.Completed, "completed", "That change was applied."),
```

That arm is a deliberate placeholder for exactly one task: `ReferenceToolDispatcher` does not exist yet, so the reference vocabulary cannot be shaped here. Task 16 Step 6 replaces it with the real delegation. Until then it keeps this dispatcher total over its own result type rather than throwing on a shape it can legitimately be handed - the two confirmation tools stay exactly where they have always been, on this dispatcher, because the confirmation protocol is conversation-wide and moving it would be churn this ticket does not need.

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, the `sed` in Step 1 already renamed the registration. Confirm it now reads:

```csharp
        services.AddScoped<InventoryConfirmationService>();
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - every shipped confirmation test unchanged, plus the seven new reference cases.

- [ ] **Step 7: Commit**

```bash
git add -A src/MultiChannelAgent.Application/Inventories \
           src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
           tests/MultiChannelAgent.Application.Tests/Inventories
git commit -m "feat(inventories): confirm stock or reference proposals under one protocol for #33"
```

---

## Task 13: Persist retirement, versions, reserved terms, and the ledger

**Files:**
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/UnitEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/UnitTermEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/LocationEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ConfirmationProposalEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ConfirmationProposalReferenceEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ReferenceOperationEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ReferenceEffectEntity.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/UnitEntityConfiguration.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/UnitTermEntityConfiguration.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/LocationEntityConfiguration.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ConfirmationProposalEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ConfirmationProposalReferenceEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ReferenceOperationEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ReferenceEffectEntityConfiguration.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceRelationalModelTests.cs`

Why: the two filtered unique indexes *are* the shared namespace rule and the flat Location rule. Written in code they would be advisory; written in the schema they hold against every race, every replica, and every future caller. This task also adds the versions the executor pins, the per-term reserved flag, the reference ledger, and the proposal reference index retirement invalidates through.

- [ ] **Step 1: Write the failing model test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceRelationalModelTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free assertions on the compiled EF Core model for the rules Unit and Location
/// administration rests on: the shared Unit term namespace and flat Location names are unique over
/// <em>active</em> rows only, and the proposal reference index has exactly one cascade path (SQL
/// Server rejects a model with two, as the shipped <see cref="UnitTermRelationalModelTests"/>
/// records from a real CI failure).
/// </summary>
public sealed class ReferenceRelationalModelTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var context = new MultiChannelAgentDbContext(options);
        return context.Model;
    }

    [Fact]
    public void Unit_terms_are_unique_across_active_terms_only()
    {
        var index = BuildModel()
            .FindEntityType(typeof(UnitTermEntity))!
            .GetIndexes()
            .Single(i => i.Properties
                .Select(p => p.Name)
                .SequenceEqual([nameof(UnitTermEntity.InventoryId), nameof(UnitTermEntity.NormalizedTerm)]));

        Assert.True(index.IsUnique);
        Assert.Equal("RetiredAt IS NULL", index.GetFilter());
    }

    [Fact]
    public void Location_names_are_unique_across_active_Locations_only()
    {
        var index = BuildModel()
            .FindEntityType(typeof(LocationEntity))!
            .GetIndexes()
            .Single(i => i.Properties
                .Select(p => p.Name)
                .SequenceEqual([nameof(LocationEntity.InventoryId), nameof(LocationEntity.NormalizedName)]));

        Assert.True(index.IsUnique);
        Assert.Equal("RetiredAt IS NULL", index.GetFilter());
    }

    [Fact]
    public void A_proposal_reference_cascades_only_from_its_proposal()
    {
        var foreignKeys = BuildModel()
            .FindEntityType(typeof(ConfirmationProposalReferenceEntity))!
            .GetForeignKeys()
            .ToList();

        var foreignKey = Assert.Single(foreignKeys);
        Assert.Equal(typeof(ConfirmationProposalEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void A_reference_effect_cascades_only_from_its_ledger_header()
    {
        var foreignKeys = BuildModel()
            .FindEntityType(typeof(ReferenceEffectEntity))!
            .GetForeignKeys()
            .ToList();

        var foreignKey = Assert.Single(foreignKeys);
        Assert.Equal(typeof(ReferenceOperationEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void One_Turn_records_at_most_one_reference_operation_per_Inventory()
    {
        var index = BuildModel()
            .FindEntityType(typeof(ReferenceOperationEntity))!
            .GetIndexes()
            .Single(i => i.Properties
                .Select(p => p.Name)
                .SequenceEqual([nameof(ReferenceOperationEntity.InventoryId), nameof(ReferenceOperationEntity.ConfirmedByTurnId)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void One_proposal_can_be_consumed_by_at_most_one_reference_operation()
    {
        var index = BuildModel()
            .FindEntityType(typeof(ReferenceOperationEntity))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(ReferenceOperationEntity.ProposalId)]));

        Assert.True(index.IsUnique);
        Assert.Equal("ProposalId IS NOT NULL", index.GetFilter());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceRelationalModelTests"`
Expected: FAIL to compile - the new entities and columns do not exist. (This test needs no Docker; it inspects the compiled model.)

- [ ] **Step 3: Widen the reference entities**

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/UnitEntity.cs`, add after `IsReserved`:

```csharp
    /// <summary>
    /// Regenerated on every administrative write. It is what an <c>ExpectedReferenceVersion</c> pins,
    /// so a proposal decided against a Unit nobody holds any more can never land. It is deliberately
    /// not an EF concurrency token: every write to it goes through a guarded ExecuteUpdate rather than
    /// the change tracker.
    /// </summary>
    public Guid ConcurrencyStamp { get; set; }

    /// <summary>When this Unit was withdrawn from matching and assignment, or null while it is active. The row - and the identity - always remain.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/UnitTermEntity.cs`, add after `IsCanonical`:

```csharp
    /// <summary>
    /// True for the five terms the reserved `each` Unit is born with. Per-term rather than derived
    /// from the Unit, so a fixed alias can never be removed while an alias a Participant later teaches
    /// `each` stays removable.
    /// </summary>
    public bool IsReserved { get; set; }

    /// <summary>
    /// When this term left the active namespace, or null while it is active. Set for every term of a
    /// Unit when that Unit is retired, and for one term when an alias is removed - the row remains
    /// either way, so the audit trail and prior meaning survive.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/LocationEntity.cs`, add after `NormalizedName`:

```csharp
    /// <summary>Regenerated on every administrative write; what an <c>ExpectedReferenceVersion</c> pins. See <c>UnitEntity.ConcurrencyStamp</c>.</summary>
    public Guid ConcurrencyStamp { get; set; }

    /// <summary>When this Location was withdrawn from matching and assignment, or null while it is active.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ConfirmationProposalEntity.cs`, add after `Status`:

```csharp
    /// <summary>The <c>ProposalKind</c> as text: which of the two disjoint payloads this row carries.</summary>
    public required string Kind { get; set; }

    /// <summary>The exact proposed administration changes, serialized; null for a stock proposal.</summary>
    public string? ReferenceChangesJson { get; set; }

    /// <summary>The expected Unit and Location versions, serialized; null for a stock proposal.</summary>
    public string? ExpectedReferenceVersionsJson { get; set; }

    /// <summary>The normalized terms this proposal expects to still be free, serialized; null for a stock proposal.</summary>
    public string? ExpectedTermAbsencesJson { get; set; }
```

- [ ] **Step 4: Add the three new entities**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ConfirmationProposalReferenceEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One Unit or Location that one stored proposal depends on, written when the proposal is stored.
///
/// This exists so retiring a reference can settle exactly the pending proposals that reference it -
/// including stock mutation proposals, which would otherwise create or move stock at a Unit or
/// Location that no longer exists. Scanning the serialized proposal for a Guid would work by
/// accident; a keyed, indexed table works by construction.
/// </summary>
public sealed class ConfirmationProposalReferenceEntity
{
    public Guid ProposalId { get; set; }

    /// <summary>The <c>ReferenceKind</c> as text, so the row is readable and the index is provider-neutral.</summary>
    public required string ReferenceKind { get; set; }

    public Guid ReferenceId { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ReferenceOperationEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger header for one applied reference administration change set. Its whole purpose
/// is retry safety: a re-driven Turn finds its own row here and re-reports exactly what it did, so
/// one confirmed Retire can never be applied twice by a redelivery, a restart, or a competing
/// replica.
///
/// It is a separate table from the stock change-set ledger because the two record different work and
/// their identities are derived from different material; nothing about one can ever be mistaken for
/// the other.
/// </summary>
public sealed class ReferenceOperationEntity
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

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ReferenceEffectEntity.cs`:

```csharp
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
```

- [ ] **Step 5: Configure them**

In `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/UnitEntityConfiguration.cs`, add before the alternate key:

```csharp
        // Supports the active-only catalog reads and the retire-driven filters.
        builder.HasIndex(e => new { e.InventoryId, e.RetiredAt });
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/UnitTermEntityConfiguration.cs`, replace the unique index with the filtered one:

```csharp
        // Unit canonical names and aliases share one collision-free namespace within an Inventory: a
        // term identifies at most one *active* Unit. Retiring a Unit retires its terms, which returns
        // their names to the namespace while the rows - and the identity - remain, so the constraint
        // is written over active rows only. The filter is plain unquoted SQL text, valid on both SQL
        // Server and SQLite, exactly like the shipped Equivalent Stock filters.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedTerm })
            .IsUnique()
            .HasFilter("RetiredAt IS NULL");

        // Supports reading one Unit's own terms, which every alias change and every rename needs.
        builder.HasIndex(e => new { e.InventoryId, e.UnitId, e.RetiredAt });
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/LocationEntityConfiguration.cs`, replace the unique index with:

```csharp
        // Enforces "unique case-insensitively within an Inventory" against the already-normalized
        // name, over active Locations only - retiring one returns its name to the Inventory while its
        // identity remains for prior Stock Entry references and audits.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedName })
            .IsUnique()
            .HasFilter("RetiredAt IS NULL");

        builder.HasIndex(e => new { e.InventoryId, e.RetiredAt });
```

In `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ConfirmationProposalEntityConfiguration.cs`, add beside the existing `Status` bound:

```csharp
        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ConfirmationProposalReferenceEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ConfirmationProposalReferenceEntityConfiguration
    : IEntityTypeConfiguration<ConfirmationProposalReferenceEntity>
{
    public void Configure(EntityTypeBuilder<ConfirmationProposalReferenceEntity> builder)
    {
        builder.ToTable("ConfirmationProposalReferences");

        // The triple is the identity: a proposal names each reference it depends on exactly once.
        builder.HasKey(e => new { e.ProposalId, e.ReferenceKind, e.ReferenceId });

        builder.Property(e => e.ReferenceKind).HasMaxLength(16).IsRequired();

        // Deliberately the *only* foreign key here. A direct Inventory FK would add a second cascade
        // path (Inventory -> ConfirmationProposals -> here, and Inventory -> here), which SQL Server
        // rejects outright - the same failure the shipped UnitTerm model records.
        builder.HasOne<ConfirmationProposalEntity>()
            .WithMany()
            .HasForeignKey(e => e.ProposalId)
            .OnDelete(DeleteBehavior.Cascade);

        // The lookup retiring a reference performs: "which pending proposals depend on this one".
        builder.HasIndex(e => new { e.ReferenceKind, e.ReferenceId });
    }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ReferenceOperationEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ReferenceOperationEntityConfiguration : IEntityTypeConfiguration<ReferenceOperationEntity>
{
    public void Configure(EntityTypeBuilder<ReferenceOperationEntity> builder)
    {
        builder.ToTable("ReferenceOperations");

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

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ReferenceEffectEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ReferenceEffectEntityConfiguration : IEntityTypeConfiguration<ReferenceEffectEntity>
{
    public void Configure(EntityTypeBuilder<ReferenceEffectEntity> builder)
    {
        builder.ToTable("ReferenceEffects");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ReferenceKind).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(Location.MaxNameLength).IsRequired();
        builder.Property(e => e.NewName).HasMaxLength(Location.MaxNameLength);
        builder.Property(e => e.Alias).HasMaxLength(Unit.MaxNameLength);

        // Unbounded on purpose: the contents are bounded by the number of aliases a create may carry,
        // not by a character count, and nvarchar(n) cannot express that ceiling.
        builder.Property(e => e.AliasesJson);

        // The only foreign key, so there is exactly one cascade path
        // (Inventory -> ReferenceOperations -> here).
        builder.HasOne<ReferenceOperationEntity>()
            .WithMany()
            .HasForeignKey(e => e.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.OperationId, e.Order }).IsUnique();
    }
}
```

`Location.MaxNameLength` (200) is used for `Name`/`NewName` because a Location name is the longer of the two kinds this column carries; a Unit name is bounded to 100 by the domain before it ever gets here.

- [ ] **Step 6: Register the new sets**

In `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`, add beside the shipped `DbSet` properties:

```csharp
    public DbSet<ConfirmationProposalReferenceEntity> ConfirmationProposalReferences => Set<ConfirmationProposalReferenceEntity>();

    public DbSet<ReferenceOperationEntity> ReferenceOperations => Set<ReferenceOperationEntity>();

    public DbSet<ReferenceEffectEntity> ReferenceEffects => Set<ReferenceEffectEntity>();
```

- [ ] **Step 7: Seed the new columns when an Inventory is created**

In `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryStore.cs`, replace the `Units.Add` block and the two `UnitTerms.Add` blocks with one that uses the Unit's own term set, so the reserved flag is per-term and the version starts non-empty:

```csharp
        db.Units.Add(new UnitEntity
        {
            Id = reservedEachUnit.Id.Value,
            InventoryId = reservedEachUnit.InventoryId.Value,
            CanonicalName = reservedEachUnit.CanonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(reservedEachUnit.CanonicalName),
            IsReserved = reservedEachUnit.IsReserved,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = reservedEachUnit.CreatedAt,
            RetiredAt = null,
        });

        // The Unit's own term set, canonical first: exactly the five terms every Inventory starts
        // with, each marked reserved so none of them can ever be removed or reassigned.
        foreach (var term in reservedEachUnit.Terms())
        {
            db.UnitTerms.Add(new UnitTermEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = reservedEachUnit.InventoryId.Value,
                UnitId = reservedEachUnit.Id.Value,
                Term = term.Term,
                NormalizedTerm = term.NormalizedTerm,
                IsCanonical = term.IsCanonical,
                IsReserved = true,
                CreatedAt = reservedEachUnit.CreatedAt,
                RetiredAt = null,
            });
        }
```

- [ ] **Step 8: Generate the migration**

```bash
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddReferenceAdministration \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --output-dir Persistence/Migrations
```

Confirm the generated migration:

- adds `ConcurrencyStamp` and `RetiredAt` to `Units`, `IsReserved` and `RetiredAt` to `UnitTerms`, `ConcurrencyStamp` and `RetiredAt` to `Locations`, and `Kind`, `ReferenceChangesJson`, `ExpectedReferenceVersionsJson`, and `ExpectedTermAbsencesJson` to `ConfirmationProposals`;
- drops and recreates `IX_UnitTerms_InventoryId_NormalizedTerm` and `IX_Locations_InventoryId_NormalizedName` **with** the `RetiredAt IS NULL` filter;
- creates `ConfirmationProposalReferences`, `ReferenceOperations`, and `ReferenceEffects` with the foreign keys and indexes configured above;
- and nothing else.

Then add the three exact backfills by hand, at the **end** of the generated `Up` method. EF cannot write them, and without them existing rows would carry an empty version and no reserved marking:

```csharp
            // Every existing Unit and Location gets a real starting version. An empty Guid would still
            // work - the version only has to change on write - but a distinct one makes an accidental
            // "expected version was never read" bug visible instead of silently passing.
            migrationBuilder.Sql("UPDATE Units SET ConcurrencyStamp = NEWID() WHERE ConcurrencyStamp = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql("UPDATE Locations SET ConcurrencyStamp = NEWID() WHERE ConcurrencyStamp = '00000000-0000-0000-0000-000000000000';");

            // Every term that exists today belongs to a reserved `each` Unit - nothing else has ever
            // been able to create one - so this marks exactly the five fixed terms per Inventory.
            migrationBuilder.Sql(
                "UPDATE UnitTerms SET IsReserved = 1 WHERE UnitId IN (SELECT Id FROM Units WHERE IsReserved = 1);");

            // Every proposal that exists today is a stock proposal; nothing else has ever been stored.
            migrationBuilder.Sql("UPDATE ConfirmationProposals SET Kind = 'Stock' WHERE Kind = '';");
```

- [ ] **Step 9: Run the model test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceRelationalModelTests|FullyQualifiedName~UnitTermRelationalModelTests"`
Expected: PASS, 9 tests. The shipped `UnitTermRelationalModelTests` must still pass unchanged - the composite FK and the single cascade path into `UnitTerms` are untouched.

- [ ] **Step 10: Verify the catalog store against a real database**

This is where Task 6's SQL test finally becomes runnable.

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlReferenceCatalogStoreTests"`
Expected: PASS, 8 tests. If Docker is genuinely unavailable, run without the environment variable, confirm they report as skipped rather than failed, and say so plainly in the commit message.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 11: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Persistence \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryStore.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceRelationalModelTests.cs
git commit -m "feat(infrastructure): make the Unit and Location namespaces unique over active rows for #33"
```

---

## Task 14: Store a reference proposal, and the references every proposal touches

**Files:**
- Modify: `src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/IConfirmationProposalStore.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Inventories/SqlConfirmationProposalStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs`

Why: a reference proposal has to round-trip exactly - what the Participant reviewed is what commits - and the reference index has to be written in the *same transaction* as the proposal, or a retire could miss a proposal that was stored a microsecond earlier.

- [ ] **Step 1: Write the failing test**

Append to `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs`, inside the existing class, reusing whatever seeding helpers it already has for a Participant, an Inventory, and a conversation id:

```csharp
    [SkippableFact]
    public async Task A_reference_proposal_round_trips_every_exact_change_it_carries()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal round-trip.");

        var (participantId, inventoryId) = await SeedAsync();
        var unitId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        using var scope = Factory!.Services.CreateScope();
        var store = new SqlConfirmationProposalStore(Db(scope));

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(Guid.NewGuid()),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.CreateUnit,
                    Target = new ProposedReferenceState(ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", false),
                    Terms =
                    [
                        UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false),
                        UnitTerm.Create("boxes", isCanonical: false, isReserved: false),
                    ],
                },
                new ProposedReferenceChange
                {
                    Order = 2,
                    Kind = ReferenceChangeKind.RetireLocation,
                    Target = new ProposedReferenceState(ReferenceKind.Location, locationId, "Old Bay", "old bay", false),
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Location, locationId, Guid.NewGuid())],
            [new ExpectedTermAbsence(ReferenceKind.Unit, "cardboard box"), new ExpectedTermAbsence(ReferenceKind.Unit, "boxes")],
            DateTimeOffset.UnixEpoch);

        await store.StoreAsync(proposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var read = await store.FindPendingAsync(new ParticipantId(participantId), "web-conversation-1", CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(ProposalKind.ReferenceAdministration, read!.Kind);
        Assert.Empty(read.Changes);
        Assert.Equal(2, read.ReferenceChanges.Count);
        Assert.Equal(ReferenceChangeKind.CreateUnit, read.ReferenceChanges[0].Kind);
        Assert.Equal(unitId, read.ReferenceChanges[0].Target.ReferenceId);
        Assert.Equal(["Cardboard Box", "boxes"], read.ReferenceChanges[0].Terms.Select(term => term.Term));
        Assert.True(read.ReferenceChanges[0].Terms[0].IsCanonical);
        Assert.Equal(ReferenceChangeKind.RetireLocation, read.ReferenceChanges[1].Kind);
        Assert.Equal(MembershipRole.Owner, read.RequiredRole);
        Assert.Equal(proposal.ExpectedReferenceVersions[0].ConcurrencyStamp, read.ExpectedReferenceVersions[0].ConcurrencyStamp);
        Assert.Equal(["cardboard box", "boxes"], read.ExpectedTermAbsences.Select(absence => absence.NormalizedTerm));
    }

    [SkippableFact]
    public async Task Storing_a_proposal_records_every_reference_it_depends_on()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed proposal reference index.");

        var (participantId, inventoryId) = await SeedAsync();
        var locationId = Guid.NewGuid();
        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);
        var store = new SqlConfirmationProposalStore(db);

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(Guid.NewGuid()),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RetireLocation,
                    Target = new ProposedReferenceState(ReferenceKind.Location, locationId, "Old Bay", "old bay", false),
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Location, locationId, Guid.NewGuid())],
            [],
            DateTimeOffset.UnixEpoch);

        await store.StoreAsync(proposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var recorded = await db.ConfirmationProposalReferences
            .AsNoTracking()
            .Where(r => r.ProposalId == proposal.Id.Value)
            .ToListAsync();

        var reference = Assert.Single(recorded);
        Assert.Equal(nameof(ReferenceKind.Location), reference.ReferenceKind);
        Assert.Equal(locationId, reference.ReferenceId);
    }

    [SkippableFact]
    public async Task Retiring_a_reference_settles_every_pending_proposal_that_depends_on_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed invalidation.");

        var (participantId, inventoryId) = await SeedAsync();
        var locationId = Guid.NewGuid();
        using var scope = Factory!.Services.CreateScope();
        var store = new SqlConfirmationProposalStore(Db(scope));

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(Guid.NewGuid()),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RenameLocation,
                    Target = new ProposedReferenceState(ReferenceKind.Location, locationId, "Old Bay", "old bay", false),
                    NewName = "New Bay",
                    NewNormalizedName = "new bay",
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Location, locationId, Guid.NewGuid())],
            [],
            DateTimeOffset.UnixEpoch);

        await store.StoreAsync(proposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var settled = await store.InvalidateReferencingAsync(
            new InventoryId(inventoryId), ReferenceKind.Location, locationId, DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(1, settled);
        Assert.Equal(ProposalStatus.Conflicted, await store.FindStatusAsync(proposal.Id, CancellationToken.None));

        // Idempotent: a second retire of the same identity finds nothing pending to settle.
        Assert.Equal(0, await store.InvalidateReferencingAsync(
            new InventoryId(inventoryId), ReferenceKind.Location, locationId, DateTimeOffset.UnixEpoch, CancellationToken.None));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlConfirmationProposalStoreTests"`
Expected: FAIL to compile - `InvalidateReferencingAsync` does not exist and the mapper does not carry a reference payload.

- [ ] **Step 3: Widen the seam**

In `src/MultiChannelAgent.Application/Inventories/IConfirmationProposalStore.cs`, add to the interface:

```csharp
    /// <summary>
    /// Settles every Pending proposal in this Inventory that depends on this Unit or Location,
    /// returning how many moved. Retiring a reference must invalidate them - including stock
    /// mutation proposals, which would otherwise create or move stock at a reference that no longer
    /// exists - and it must happen in the same transaction that applied the retirement.
    /// </summary>
    Task<int> InvalidateReferencingAsync(
        InventoryId inventoryId,
        ReferenceKind kind,
        Guid referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
```

- [ ] **Step 4: Serialize both payloads**

In `src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs`, bump the schema version and add the reference DTOs beside the shipped ones:

```csharp
    /// <summary>
    /// Bumped by #33, which added the reference administration payload. A row this process cannot
    /// read is refused, not guessed at - a proposal is only ever ten minutes old, so an unreadable
    /// shape is a deployment mistake rather than a migration case.
    /// </summary>
    public const int SchemaVersion = 2;

    private sealed record UnitTermDto(string Term, string NormalizedTerm, bool IsCanonical, bool IsReserved);

    private sealed record ReferenceStateDto(string Kind, Guid ReferenceId, string Name, string NormalizedName, bool Reserved);

    private sealed record ReferenceChangeDto(
        int Order,
        string Kind,
        ReferenceStateDto Target,
        string? NewName,
        string? NewNormalizedName,
        UnitTermDto? Term,
        IReadOnlyList<UnitTermDto> Terms);

    private sealed record ReferenceVersionDto(string Kind, Guid ReferenceId, Guid ConcurrencyStamp);

    private sealed record TermAbsenceDto(string Kind, string NormalizedTerm);

    private sealed record ReferenceChangesEnvelope(int Version, IReadOnlyList<ReferenceChangeDto> Changes);
```

Replace `ToEntity` with one that writes whichever payload the proposal actually carries:

```csharp
    public static ConfirmationProposalEntity ToEntity(ConfirmationProposal proposal)
    {
        var isReferenceProposal = proposal.Kind == ProposalKind.ReferenceAdministration;

        return new ConfirmationProposalEntity
        {
            ProposalId = proposal.Id.Value,
            TokenHash = proposal.TokenHash.Value,
            ParticipantId = proposal.ParticipantId.Value,
            ChannelConversationId = proposal.ChannelConversationId,
            InventoryId = proposal.InventoryId.Value,
            ProposedInTurnId = proposal.ProposedInTurnId.Value,
            Status = nameof(ProposalStatus.Pending),
            Kind = proposal.Kind.ToString(),
            ChangesJson = JsonSerializer.Serialize(
                new ChangesEnvelope(SchemaVersion, proposal.Changes.Select(ToDto).ToList()), Options),
            ExpectedVersionsJson = JsonSerializer.Serialize(
                proposal.ExpectedVersions.Select(v => new VersionDto(v.StockEntryId.Value, v.ConcurrencyStamp)).ToList(), Options),
            ExpectedAbsencesJson = JsonSerializer.Serialize(
                proposal.ExpectedAbsences.Select(a => new AbsenceDto(a.NormalizedName, a.UnitId.Value, a.LocationId?.Value)).ToList(), Options),
            ReferenceChangesJson = isReferenceProposal
                ? JsonSerializer.Serialize(
                    new ReferenceChangesEnvelope(SchemaVersion, proposal.ReferenceChanges.Select(ToDto).ToList()), Options)
                : null,
            ExpectedReferenceVersionsJson = isReferenceProposal
                ? JsonSerializer.Serialize(
                    proposal.ExpectedReferenceVersions
                        .Select(v => new ReferenceVersionDto(v.Kind.ToString(), v.ReferenceId, v.ConcurrencyStamp))
                        .ToList(),
                    Options)
                : null,
            ExpectedTermAbsencesJson = isReferenceProposal
                ? JsonSerializer.Serialize(
                    proposal.ExpectedTermAbsences
                        .Select(a => new TermAbsenceDto(a.Kind.ToString(), a.NormalizedTerm))
                        .ToList(),
                    Options)
                : null,
            CreatedAt = proposal.CreatedAt,
            ExpiresAt = proposal.ExpiresAt,
            ExpiresAtTicks = proposal.ExpiresAt.UtcTicks,
            SettledAt = null,
            SettledAtTicks = null,
        };
    }
```

Replace the temporary `Kind = ProposalKind.Stock` line added in Task 5 with the real read, and read the reference payload:

```csharp
    public static ConfirmationProposal ToDomain(ConfirmationProposalEntity entity)
    {
        if (!Enum.TryParse<ProposalKind>(entity.Kind, ignoreCase: false, out var kind))
        {
            throw new InvalidOperationException("A stored proposal carried an unreadable kind.");
        }

        var envelope = JsonSerializer.Deserialize<ChangesEnvelope>(entity.ChangesJson, Options)
            ?? throw new InvalidOperationException("A stored proposal carried no changes.");

        if (envelope.Version != SchemaVersion)
        {
            throw new InvalidOperationException($"A stored proposal uses unsupported schema version {envelope.Version}.");
        }

        var versions = JsonSerializer.Deserialize<List<VersionDto>>(entity.ExpectedVersionsJson, Options) ?? [];
        var absences = JsonSerializer.Deserialize<List<AbsenceDto>>(entity.ExpectedAbsencesJson, Options) ?? [];

        var referenceChanges = entity.ReferenceChangesJson is { } referenceJson
            ? (JsonSerializer.Deserialize<ReferenceChangesEnvelope>(referenceJson, Options)
                ?? throw new InvalidOperationException("A stored proposal carried an unreadable reference payload."))
                .Changes.Select(ToDomain).ToList()
            : [];

        var referenceVersions = entity.ExpectedReferenceVersionsJson is { } versionJson
            ? (JsonSerializer.Deserialize<List<ReferenceVersionDto>>(versionJson, Options) ?? [])
                .Select(v => new ExpectedReferenceVersion(ParseReferenceKind(v.Kind), v.ReferenceId, v.ConcurrencyStamp))
                .ToList()
            : [];

        var termAbsences = entity.ExpectedTermAbsencesJson is { } absenceJson
            ? (JsonSerializer.Deserialize<List<TermAbsenceDto>>(absenceJson, Options) ?? [])
                .Select(a => new ExpectedTermAbsence(ParseReferenceKind(a.Kind), a.NormalizedTerm))
                .ToList()
            : [];

        return new ConfirmationProposal
        {
            Id = new ProposalId(entity.ProposalId),
            TokenHash = new ConfirmationTokenHash(entity.TokenHash),
            ParticipantId = new ParticipantId(entity.ParticipantId),
            ChannelConversationId = entity.ChannelConversationId,
            InventoryId = new InventoryId(entity.InventoryId),
            ProposedInTurnId = new TurnId(entity.ProposedInTurnId),
            Kind = kind,
            Changes = envelope.Changes.Select(ToDomain).ToList(),
            ExpectedVersions = versions
                .Select(v => new ExpectedEntryVersion(new StockEntryId(v.StockEntryId), v.ConcurrencyStamp))
                .ToList(),
            ExpectedAbsences = absences
                .Select(a => new ExpectedEquivalentStockAbsence(
                    a.NormalizedName, new UnitId(a.UnitId), a.LocationId is { } id ? new LocationId(id) : null))
                .ToList(),
            ReferenceChanges = referenceChanges,
            ExpectedReferenceVersions = referenceVersions,
            ExpectedTermAbsences = termAbsences,
            CreatedAt = entity.CreatedAt,
        };
    }

    private static ReferenceChangeDto ToDto(ProposedReferenceChange change) => new(
        change.Order,
        ReferenceAdministrationFacts.ToMachineText(change.Kind),
        new ReferenceStateDto(
            change.Target.Kind.ToString(),
            change.Target.ReferenceId,
            change.Target.Name,
            change.Target.NormalizedName,
            change.Target.Reserved),
        change.NewName,
        change.NewNormalizedName,
        change.Term is null ? null : ToDto(change.Term),
        change.Terms.Select(ToDto).ToList());

    private static UnitTermDto ToDto(UnitTerm term) =>
        new(term.Term, term.NormalizedTerm, term.IsCanonical, term.IsReserved);

    private static ProposedReferenceChange ToDomain(ReferenceChangeDto dto)
    {
        if (!ReferenceAdministrationFacts.TryParse(dto.Kind, out var kind))
        {
            throw new InvalidOperationException("A stored proposal carried an unreadable reference change kind.");
        }

        return new ProposedReferenceChange
        {
            Order = dto.Order,
            Kind = kind,
            Target = new ProposedReferenceState(
                ParseReferenceKind(dto.Target.Kind),
                dto.Target.ReferenceId,
                dto.Target.Name,
                dto.Target.NormalizedName,
                dto.Target.Reserved),
            NewName = dto.NewName,
            NewNormalizedName = dto.NewNormalizedName,
            Term = dto.Term is null ? null : ToDomain(dto.Term),
            Terms = dto.Terms.Select(ToDomain).ToList(),
        };
    }

    private static UnitTerm ToDomain(UnitTermDto dto) => new()
    {
        Term = dto.Term,
        NormalizedTerm = dto.NormalizedTerm,
        IsCanonical = dto.IsCanonical,
        IsReserved = dto.IsReserved,
    };

    private static ReferenceKind ParseReferenceKind(string text) =>
        Enum.TryParse<ReferenceKind>(text, ignoreCase: false, out var kind)
            ? kind
            : throw new InvalidOperationException("A stored proposal carried an unreadable reference kind.");
```

`ToDomain` reads `UnitTerm` through its object initializer rather than `UnitTerm.Create`, deliberately: the stored term is already tidied and already normalized, and re-deriving it here would let a later change to the tidying rules silently rewrite what a Participant reviewed.

- [ ] **Step 5: Write the index and the invalidation**

In `src/MultiChannelAgent.Infrastructure/Inventories/SqlConfirmationProposalStore.cs`, add the reference rows inside the existing `StoreAsync` transaction, immediately after `db.ConfirmationProposals.Add(...)`:

```csharp
            // Written in the same transaction as the proposal itself. A retire that ran between the
            // two would otherwise miss a proposal that already existed, and that proposal would go on
            // to be confirmable against a reference that no longer exists.
            foreach (var unitId in proposal.ReferencedUnitIds)
            {
                db.ConfirmationProposalReferences.Add(new ConfirmationProposalReferenceEntity
                {
                    ProposalId = proposal.Id.Value,
                    ReferenceKind = nameof(ReferenceKind.Unit),
                    ReferenceId = unitId.Value,
                });
            }

            foreach (var locationId in proposal.ReferencedLocationIds)
            {
                db.ConfirmationProposalReferences.Add(new ConfirmationProposalReferenceEntity
                {
                    ProposalId = proposal.Id.Value,
                    ReferenceKind = nameof(ReferenceKind.Location),
                    ReferenceId = locationId.Value,
                });
            }
```

and add the invalidation method:

```csharp
    public async Task<int> InvalidateReferencingAsync(
        InventoryId inventoryId,
        ReferenceKind kind,
        Guid referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var kindText = kind.ToString();

        // Selected first and then updated by identity: the two-statement shape is the portable one
        // this store already uses for its sweeps, and it keeps the update a plain keyed statement
        // rather than one carrying a correlated subquery every provider must translate.
        var affected = await db.ConfirmationProposalReferences
            .AsNoTracking()
            .Where(r => r.ReferenceKind == kindText && r.ReferenceId == referenceId)
            .Join(
                db.ConfirmationProposals.AsNoTracking()
                    .Where(p => p.InventoryId == inventoryId.Value && p.Status == PendingStatus),
                reference => reference.ProposalId,
                proposal => proposal.ProposalId,
                (_, proposal) => proposal.ProposalId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (affected.Count == 0)
        {
            return 0;
        }

        return await db.ConfirmationProposals
            .Where(p => affected.Contains(p.ProposalId) && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Status, nameof(ProposalStatus.Conflicted))
                    .SetProperty(p => p.SettledAt, now)
                    .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
                cancellationToken);
    }
```

- [ ] **Step 6: Update the in-memory store to match the widened seam**

`InMemoryConfirmationProposalStore` already gained `InvalidateReferencingAsync` in Task 9; it now satisfies the interface rather than merely offering the method. No further change is needed - but re-read its signature against the interface and fix any mismatch, because a mismatch here compiles as "does not implement" rather than as a subtle bug.

- [ ] **Step 7: Verify**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlConfirmationProposalStore"`
Expected: PASS - every shipped case plus the three new ones. The shipped `SqlConfirmationProposalStoreChangeTrackerIsolationTests` must also still pass: `StoreAsync` still stages everything inside one transaction and still abandons the scope on failure, so the new reference rows can never be left Added on a failed store.

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IConfirmationProposalStore.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/ConfirmationProposalMapper.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlConfirmationProposalStore.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlConfirmationProposalStoreTests.cs
git commit -m "feat(infrastructure): store reference proposals and the references they depend on for #33"
```

---

## Task 15: Apply a reference change set atomically, or change nothing

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlReferenceAdministrationStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreConcurrencyTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreChangeTrackerIsolationTests.cs`

Why: this is the transaction the whole ticket rests on. Six things must commit together or not at all - the proposal consumption, the version checks, the authoritative Retire re-check, the state changes, the audits, and the invalidation of every pending proposal that referenced a retired identity - and a Rename must be provably unable to touch a single Stock Entry.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreTests.cs`. Reuse the seeding helpers from `SqlReferenceCatalogStoreTests` verbatim (`SeedInventoryAsync`, `SeedUnitAsync`, `SeedLocationAsync`, `SeedStockAsync`, `Db`) - copy them into this class rather than sharing, exactly as the shipped SQL store test classes each carry their own:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The one transaction Unit and Location administration rests on: apply everything with its audits,
/// its ledger, its proposal consumption, and its retirement-driven invalidation - or change nothing
/// at all. It also proves the claim every Participant depends on: a Rename never touches a single
/// Stock Entry.
/// </summary>
public sealed class SqlReferenceAdministrationStoreTests : SqlIntegrationTestBase
{
    // Copy SeedInventoryAsync, SeedUnitAsync, SeedLocationAsync, SeedStockAsync, and Db from
    // SqlReferenceCatalogStoreTests unchanged.

    private SqlReferenceAdministrationStore Store(IServiceScope scope) =>
        new(Db(scope), new SqlConfirmationProposalStore(Db(scope)));

    private static ReferenceChangeSetCommand Command(
        Guid inventoryId,
        Guid participantId,
        Guid turnId,
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences,
        Guid? proposalId = null) => new()
        {
            OperationId = ReferenceOperationId.Derive(new TurnId(turnId), "reference_tool", 0),
            InventoryId = new InventoryId(inventoryId),
            ActorId = new ParticipantId(participantId),
            ConfirmedByTurnId = new TurnId(turnId),
            ConsumesProposalId = proposalId is { } id ? new ProposalId(id) : null,
            Changes = changes,
            ExpectedVersions = versions,
            ExpectedTermAbsences = absences,
            Now = DateTimeOffset.UnixEpoch,
        };

    private async Task<Guid> ParticipantIdAsync(Guid inventoryId)
    {
        using var scope = Factory!.Services.CreateScope();

        return await Db(scope).Memberships
            .AsNoTracking()
            .Where(m => m.InventoryId == inventoryId)
            .Select(m => m.ParticipantId)
            .FirstAsync();
    }

    private async Task<(Guid Stamp, DateTimeOffset? RetiredAt)> UnitStateAsync(Guid unitId)
    {
        using var scope = Factory!.Services.CreateScope();
        var row = await Db(scope).Units.AsNoTracking().FirstAsync(u => u.Id == unitId);

        return (row.ConcurrencyStamp, row.RetiredAt);
    }

    private async Task<int> CountAuditsAsync(Guid inventoryId, string eventType)
    {
        using var scope = Factory!.Services.CreateScope();

        return await Db(scope).InventoryAudits
            .AsNoTracking()
            .CountAsync(a => a.InventoryId == inventoryId && a.EventType == eventType);
    }

    [SkippableFact]
    public async Task Creating_a_Unit_writes_it_its_terms_its_audit_and_its_ledger_together()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var unitId = Guid.NewGuid();
        using var scope = Factory!.Services.CreateScope();

        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, unitId, "Cardboard Box", "cardboard box", false),
                        Terms =
                        [
                            UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false),
                            UnitTerm.Create("boxes", isCanonical: false, isReserved: false),
                        ],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "cardboard box"), new ExpectedTermAbsence(ReferenceKind.Unit, "boxes")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == unitId);
        Assert.Equal("Cardboard Box", unit.CanonicalName);
        Assert.False(unit.IsReserved);
        Assert.Null(unit.RetiredAt);
        Assert.NotEqual(Guid.Empty, unit.ConcurrencyStamp);

        var terms = await db.UnitTerms.AsNoTracking().Where(t => t.UnitId == unitId).ToListAsync();
        Assert.Equal(2, terms.Count);
        Assert.Single(terms, term => term.IsCanonical && term.NormalizedTerm == "cardboard box");
        Assert.All(terms, term => Assert.False(term.IsReserved));

        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitCreated)));
        Assert.Equal(1, await db.ReferenceOperations.AsNoTracking().CountAsync(o => o.InventoryId == inventoryId));
        Assert.Equal(1, await db.ReferenceEffects.AsNoTracking().CountAsync());
    }

    [SkippableFact]
    public async Task Renaming_a_Unit_preserves_every_identity_and_rewrites_no_Stock_Entry_at_all()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedStockAsync(inventoryId, boxId, shelfId, "Steel Bolts");
        await SeedStockAsync(inventoryId, eachUnitId, null, "Brass Rivets");

        List<StockEntryEntity> before;
        Guid stampBefore;
        using (var snapshotScope = Factory!.Services.CreateScope())
        {
            before = await Db(snapshotScope).StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
            stampBefore = (await Db(snapshotScope).Units.AsNoTracking().FirstAsync(u => u.Id == boxId)).ConcurrencyStamp;
        }

        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RenameUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        NewName = "Carton",
                        NewNormalizedName = "carton",
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == boxId);
        Assert.Equal(boxId, unit.Id);
        Assert.Equal("Carton", unit.CanonicalName);
        Assert.Equal("carton", unit.NormalizedCanonicalName);

        var canonical = await db.UnitTerms.AsNoTracking().FirstAsync(t => t.UnitId == boxId && t.IsCanonical);
        Assert.Equal("Carton", canonical.Term);
        Assert.Equal("carton", canonical.NormalizedTerm);
        Assert.Single(await db.UnitTerms.AsNoTracking().Where(t => t.UnitId == boxId && !t.IsCanonical).ToListAsync());

        // The claim every Participant depends on: nothing in StockEntries moved - not a name, not a
        // Unit, not a Location, not a Quantity, and not a concurrency stamp. Equivalent Stock is keyed
        // by UnitId, which never changed, so it cannot have changed either.
        var after = await db.StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(e => (e.Id, e.UnitId, e.LocationId, e.Name, e.NormalizedName, e.Quantity, e.ConcurrencyStamp)),
            after.Select(e => (e.Id, e.UnitId, e.LocationId, e.Name, e.NormalizedName, e.Quantity, e.ConcurrencyStamp)));

        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRenamed)));
    }

    [SkippableFact]
    public async Task Renaming_a_Location_preserves_its_identity_and_rewrites_no_Stock_Entry()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        await SeedStockAsync(inventoryId, eachUnitId, shelfId, "Steel Bolts");

        Guid stampBefore;
        List<StockEntryEntity> before;
        using (var snapshotScope = Factory!.Services.CreateScope())
        {
            stampBefore = (await Db(snapshotScope).Locations.AsNoTracking().FirstAsync(l => l.Id == shelfId)).ConcurrencyStamp;
            before = await Db(snapshotScope).StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        }

        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RenameLocation,
                        Target = new ProposedReferenceState(ReferenceKind.Location, shelfId, "Shelf A", "shelf a", false),
                        NewName = "Aisle 3",
                        NewNormalizedName = "aisle 3",
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Location, shelfId, stampBefore)],
                [new ExpectedTermAbsence(ReferenceKind.Location, "aisle 3")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var location = await db.Locations.AsNoTracking().FirstAsync(l => l.Id == shelfId);
        Assert.Equal("Aisle 3", location.Name);
        Assert.Equal("aisle 3", location.NormalizedName);

        var after = await db.StockEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(
            before.Select(e => (e.Id, e.LocationId, e.ConcurrencyStamp)),
            after.Select(e => (e.Id, e.LocationId, e.ConcurrencyStamp)));
    }

    [SkippableFact]
    public async Task Retiring_a_Unit_keeps_its_identity_and_frees_every_one_of_its_terms()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", ["boxes"]);
        var (stampBefore, _) = await UnitStateAsync(boxId);

        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RetireUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                []),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        var db = Db(readScope);

        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == boxId);
        Assert.Equal(boxId, unit.Id);
        Assert.Equal("Cardboard Box", unit.CanonicalName);
        Assert.NotNull(unit.RetiredAt);

        var terms = await db.UnitTerms.AsNoTracking().Where(t => t.UnitId == boxId).ToListAsync();
        Assert.Equal(2, terms.Count);
        Assert.All(terms, term => Assert.NotNull(term.RetiredAt));

        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));
    }

    [SkippableFact]
    public async Task A_freed_term_can_be_claimed_again_by_a_new_Unit()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stampBefore, _) = await UnitStateAsync(boxId);

        using (var retireScope = Factory!.Services.CreateScope())
        {
            await Store(retireScope).ApplyAsync(
                Command(
                    inventoryId,
                    participantId,
                    Guid.NewGuid(),
                    [
                        new ProposedReferenceChange
                        {
                            Order = 1,
                            Kind = ReferenceChangeKind.RetireUnit,
                            Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        },
                    ],
                    [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                    []),
                CancellationToken.None);
        }

        var replacementId = Guid.NewGuid();
        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(
                            ReferenceKind.Unit, replacementId, "Cardboard Box", "cardboard box", false),
                        Terms = [UnitTerm.Create("Cardboard Box", isCanonical: true, isReserved: false)],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "cardboard box")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal(2, await Db(readScope).Units.AsNoTracking().CountAsync(u => u.NormalizedCanonicalName == "cardboard box"));
    }

    [SkippableFact]
    public async Task A_Retire_that_Stock_now_references_changes_nothing_even_though_the_proposal_was_clean()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stampBefore, _) = await UnitStateAsync(boxId);

        // Decided when nothing referenced it; executed after something does.
        await SeedStockAsync(inventoryId, boxId, null, "Steel Bolts");

        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RetireUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
                []),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);

        var (_, retiredAt) = await UnitStateAsync(boxId);
        Assert.Null(retiredAt);
        Assert.Equal(0, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal(0, await Db(readScope).ReferenceOperations.AsNoTracking().CountAsync());
    }

    [SkippableFact]
    public async Task A_change_set_whose_expected_version_moved_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);

        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RenameUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        NewName = "Carton",
                        NewNormalizedName = "carton",
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, Guid.NewGuid())],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal("Cardboard Box", (await Db(readScope).Units.AsNoTracking().FirstAsync(u => u.Id == boxId)).CanonicalName);
        Assert.Equal(0, await Db(readScope).ReferenceEffects.AsNoTracking().CountAsync());
    }

    [SkippableFact]
    public async Task A_change_set_whose_term_was_claimed_meanwhile_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        await SeedUnitAsync(inventoryId, "Carton", []);

        using var scope = Factory!.Services.CreateScope();
        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, Guid.NewGuid(), "Carton", "carton", false),
                        Terms = [UnitTerm.Create("Carton", isCanonical: true, isReserved: false)],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);

        using var readScope = Factory!.Services.CreateScope();
        Assert.Equal(1, await Db(readScope).Units.AsNoTracking().CountAsync(u => u.NormalizedCanonicalName == "carton"));
    }

    [SkippableFact]
    public async Task Consuming_a_proposal_and_applying_it_happen_in_one_transaction()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stampBefore, _) = await UnitStateAsync(boxId);
        var turnId = Guid.NewGuid();

        using var scope = Factory!.Services.CreateScope();
        var proposalStore = new SqlConfirmationProposalStore(Db(scope));

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(turnId),
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.RetireUnit,
                    Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                },
            ],
            [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stampBefore)],
            [],
            DateTimeOffset.UnixEpoch);

        await proposalStore.StoreAsync(proposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var first = await Store(scope).ApplyAsync(
            Command(inventoryId, participantId, turnId, proposal.ReferenceChanges, proposal.ExpectedReferenceVersions, [], proposal.Id.Value),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, first.Outcome);
        Assert.Equal(ProposalStatus.Confirmed, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));

        // The proposal is single use: a second execution under a *different* operation identity loses
        // on the guarded consume and changes nothing.
        using var secondScope = Factory!.Services.CreateScope();
        var second = await Store(secondScope).ApplyAsync(
            Command(inventoryId, participantId, Guid.NewGuid(), proposal.ReferenceChanges, proposal.ExpectedReferenceVersions, [], proposal.Id.Value),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, second.Outcome);
        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));
    }

    [SkippableFact]
    public async Task Retiring_a_Location_settles_a_pending_stock_proposal_that_depended_on_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, eachUnitId) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var shelfId = await SeedLocationAsync(inventoryId, "Shelf A");
        var stockEntryId = await SeedStockAsync(inventoryId, eachUnitId, null, "Steel Bolts");

        Guid locationStamp;
        Guid entryStamp;
        using (var snapshotScope = Factory!.Services.CreateScope())
        {
            locationStamp = (await Db(snapshotScope).Locations.AsNoTracking().FirstAsync(l => l.Id == shelfId)).ConcurrencyStamp;
            entryStamp = (await Db(snapshotScope).StockEntries.AsNoTracking().FirstAsync(e => e.Id == stockEntryId)).ConcurrencyStamp;
        }

        using var scope = Factory!.Services.CreateScope();
        var proposalStore = new SqlConfirmationProposalStore(Db(scope));

        // An ordinary pending stock proposal that would place Stock in Shelf A.
        var stockProposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            new ParticipantId(participantId),
            "web-conversation-1",
            new InventoryId(inventoryId),
            new TurnId(Guid.NewGuid()),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Move,
                    Effect = StockChangeEffectKind.Placed,
                    Source = new ProposedEntryState(
                        new StockEntryId(stockEntryId), "Steel Bolts", "steel bolts", new UnitId(eachUnitId), "each",
                        null, null, null, Quantity.Create(1m), Quantity.Create(1m), false),
                    Destination = new ProposedEntryState(
                        new StockEntryId(stockEntryId), "Steel Bolts", "steel bolts", new UnitId(eachUnitId), "each",
                        new LocationId(shelfId), "Shelf A", null, Quantity.Create(1m), Quantity.Create(1m), false),
                },
            ],
            [new ExpectedEntryVersion(new StockEntryId(stockEntryId), entryStamp)],
            [],
            DateTimeOffset.UnixEpoch);

        await proposalStore.StoreAsync(stockProposal, DateTimeOffset.UnixEpoch, CancellationToken.None);

        var result = await Store(scope).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.RetireLocation,
                        Target = new ProposedReferenceState(ReferenceKind.Location, shelfId, "Shelf A", "shelf a", false),
                    },
                ],
                [new ExpectedReferenceVersion(ReferenceKind.Location, shelfId, locationStamp)],
                []),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, result.Outcome);
        Assert.Equal(ProposalStatus.Conflicted, await proposalStore.FindStatusAsync(stockProposal.Id, CancellationToken.None));
        Assert.Null(await proposalStore.FindPendingAsync(new ParticipantId(participantId), "web-conversation-1", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_instead_of_doing_it_twice()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration write.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var turnId = Guid.NewGuid();
        var command = Command(
            inventoryId,
            participantId,
            turnId,
            [
                new ProposedReferenceChange
                {
                    Order = 1,
                    Kind = ReferenceChangeKind.CreateLocation,
                    Target = new ProposedReferenceState(ReferenceKind.Location, Guid.NewGuid(), "Shelf A", "shelf a", false),
                },
            ],
            [],
            [new ExpectedTermAbsence(ReferenceKind.Location, "shelf a")]);

        using var scope = Factory!.Services.CreateScope();
        var store = Store(scope);

        var first = await store.ApplyAsync(command, CancellationToken.None);
        var replay = await store.ApplyAsync(command, CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Applied, first.Outcome);
        Assert.Equal(ReferenceAdministrationStoreOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(first.Recorded!.Changes[0].ReferenceId, replay.Recorded!.Changes[0].ReferenceId);
        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.LocationCreated)));

        using var readScope = Factory!.Services.CreateScope();
        var byTurn = await Store(readScope).FindRecordedByTurnAsync(
            new InventoryId(inventoryId), new TurnId(turnId), CancellationToken.None);

        Assert.NotNull(byTurn);
        Assert.Equal(first.Recorded.Changes[0].ReferenceId, byTurn!.Changes[0].ReferenceId);
    }
}
```

`SeedStockAsync` must return the created `Guid` for the invalidation test; change the copied helper's signature to `Task<Guid>` and return the identity it inserted.

- [ ] **Step 2: Write the concurrency test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreConcurrencyTests.cs`, copying the same seeding helpers:

```csharp
    [SkippableFact]
    public async Task Only_one_of_two_concurrent_Retires_of_one_Unit_can_win()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stamp, _) = await UnitStateAsync(boxId);

        async Task<ReferenceAdministrationStoreOutcome> RetireAsync()
        {
            using var scope = Factory!.Services.CreateScope();
            var result = await Store(scope).ApplyAsync(
                Command(
                    inventoryId,
                    participantId,
                    Guid.NewGuid(),
                    [
                        new ProposedReferenceChange
                        {
                            Order = 1,
                            Kind = ReferenceChangeKind.RetireUnit,
                            Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        },
                    ],
                    [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stamp)],
                    []),
                CancellationToken.None);

            return result.Outcome;
        }

        var outcomes = await Task.WhenAll(RetireAsync(), RetireAsync());

        Assert.Single(outcomes, outcome => outcome == ReferenceAdministrationStoreOutcome.Applied);
        Assert.Single(outcomes, outcome => outcome == ReferenceAdministrationStoreOutcome.Conflict);
        Assert.Equal(1, await CountAuditsAsync(inventoryId, nameof(AuditEventType.UnitRetired)));
    }

    [SkippableFact]
    public async Task A_Retire_racing_a_Stock_write_never_leaves_a_retired_Unit_with_Stock_referencing_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        var boxId = await SeedUnitAsync(inventoryId, "Cardboard Box", []);
        var (stamp, _) = await UnitStateAsync(boxId);

        async Task RetireAsync()
        {
            using var scope = Factory!.Services.CreateScope();
            await Store(scope).ApplyAsync(
                Command(
                    inventoryId,
                    participantId,
                    Guid.NewGuid(),
                    [
                        new ProposedReferenceChange
                        {
                            Order = 1,
                            Kind = ReferenceChangeKind.RetireUnit,
                            Target = new ProposedReferenceState(ReferenceKind.Unit, boxId, "Cardboard Box", "cardboard box", false),
                        },
                    ],
                    [new ExpectedReferenceVersion(ReferenceKind.Unit, boxId, stamp)],
                    []),
                CancellationToken.None);
        }

        async Task AddStockAsync()
        {
            try
            {
                await SeedStockAsync(inventoryId, boxId, null, "Steel Bolts");
            }
            catch (DbUpdateException)
            {
                // Losing the race is a legitimate outcome; the invariant below is what matters.
            }
        }

        await Task.WhenAll(RetireAsync(), AddStockAsync());

        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);
        var unit = await db.Units.AsNoTracking().FirstAsync(u => u.Id == boxId);
        var stockCount = await db.StockEntries.AsNoTracking().CountAsync(e => e.UnitId == boxId);

        // Either the Unit is still active, or nothing references it. A retired Unit with Stock
        // referencing it is the one state that must be unreachable, whichever way the race went.
        Assert.True(unit.RetiredAt is null || stockCount == 0);
    }
```

- [ ] **Step 3: Write the change-tracker isolation test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreChangeTrackerIsolationTests.cs`, mirroring the shipped `SqlStockChangeSetStoreChangeTrackerIsolationTests` exactly - same structure, same assertions, this store:

```csharp
    [SkippableFact]
    public async Task A_failed_change_set_leaves_nothing_staged_for_an_unrelated_write_to_commit()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed isolation proof.");

        var (inventoryId, _) = await SeedInventoryAsync();
        var participantId = await ParticipantIdAsync(inventoryId);
        await SeedUnitAsync(inventoryId, "Carton", []);

        using var scope = Factory!.Services.CreateScope();
        var db = Db(scope);

        // A create whose term was already claimed: it must fail, and it must leave the DbContext this
        // whole batch of Turns shares completely clean.
        var result = await new SqlReferenceAdministrationStore(db, new SqlConfirmationProposalStore(db)).ApplyAsync(
            Command(
                inventoryId,
                participantId,
                Guid.NewGuid(),
                [
                    new ProposedReferenceChange
                    {
                        Order = 1,
                        Kind = ReferenceChangeKind.CreateUnit,
                        Target = new ProposedReferenceState(ReferenceKind.Unit, Guid.NewGuid(), "Carton", "carton", false),
                        Terms = [UnitTerm.Create("Carton", isCanonical: true, isReserved: false)],
                    },
                ],
                [],
                [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);
        Assert.Empty(db.ChangeTracker.Entries());

        // An unrelated save on the very same scope must not commit anything the failed set staged.
        db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Units.AsNoTracking().CountAsync(u => u.NormalizedCanonicalName == "carton"));
        Assert.Equal(0, await db.ReferenceOperations.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.InventoryAudits.AsNoTracking().CountAsync(a => a.EventType == nameof(AuditEventType.UnitCreated)));
    }
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlReferenceAdministrationStore"`
Expected: FAIL to compile - `SqlReferenceAdministrationStore` does not exist.

- [ ] **Step 5: Write the store**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlReferenceAdministrationStore.cs`:

```csharp
using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IReferenceAdministrationStore"/>: the one transaction Unit and
/// Location administration rests on.
///
/// One <see cref="ApplyAsync"/> call consumes the proposal, verifies and locks every touched
/// reference, verifies every term it means to claim, <em>re-checks every Retire against current
/// Stock Entries</em>, applies every change, appends one minimal semantic audit fact per change,
/// settles every other pending proposal that referenced a retired identity, and writes the ledger -
/// all inside one explicit transaction. Any failure rolls the whole thing back, so a caller that
/// sees <see cref="ReferenceAdministrationStoreOutcome.Conflict"/> can rely on nothing at all having
/// happened, including the proposal still being pending.
///
/// Three things here are deliberate rather than incidental:
///
/// <list type="bullet">
/// <item><b>Locking order.</b> The verify pass touches every row the set will write to in one
/// globally agreed order - Units before Locations, then the ordinal text of the identity - with a
/// single guarded UPDATE per row that both takes the row's exclusive lock and checks its expected
/// version. Two concurrent sets over overlapping references therefore contend in the same order and
/// one simply loses, instead of deadlocking halfway through.</item>
/// <item><b>Serializable for a Retire.</b> Under read-committed, a Stock Entry could be decided
/// against an active Unit and inserted just after this transaction commits, leaving a retired Unit
/// with stock referencing it. A Retire's conflict check is a range query, so serializable isolation
/// makes the two serialize. It is scoped to sets that carry a Retire because nothing else needs
/// it.</item>
/// <item><b>Nothing is ever called a conflict that cannot be established.</b> A fault this store
/// cannot attribute to a version, a claimed term, or a blocked Retire propagates as the real fault
/// it is - the Turn then ends as a transient failure the Participant can simply ask again, which is
/// safe precisely because nothing was applied.</item>
/// </list>
///
/// The ledger commits with the state change rather than after it, exactly as
/// <see cref="SqlStockChangeSetStore"/> does: the terminal Outcome is written later, in its own
/// atomic write, and if the process dies in between, the Turn is reprocessed, finds its ledger row
/// through <see cref="FindRecordedByTurnAsync"/>, and re-reports instead of re-applying.
/// </summary>
public sealed class SqlReferenceAdministrationStore(
    MultiChannelAgentDbContext db, IConfirmationProposalStore proposalStore) : IReferenceAdministrationStore
{
    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);

    private static readonly JsonSerializerOptions AliasOptions = new();

    public async Task<RecordedReferenceChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, ReferenceOperationId operationId, CancellationToken cancellationToken)
    {
        var header = await db.ReferenceOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<RecordedReferenceChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken)
    {
        var header = await db.ReferenceOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.InventoryId == inventoryId.Value && o.ConfirmedByTurnId == turnId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<ReferenceAdministrationStoreResult> ApplyAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, already);
        }

        var retires = command.Changes.Where(change => change.RetiresReference).ToList();

        // Serializable only where it is actually needed: a Retire's "is anything still referencing
        // this" is a range query, and under read-committed a concurrent Stock insert could commit
        // just after this transaction does. Everything else is fully protected by the guarded
        // version checks and the filtered uniqueness indexes.
        await using var transaction = retires.Count > 0
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Consume the proposal, guarded. Doing this first means a losing confirmation stops
            //    here, before it has touched any reference data at all.
            if (command.ConsumesProposalId is { } proposalId)
            {
                var consumed = await db.ConfirmationProposals
                    .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(p => p.Status, nameof(ProposalStatus.Confirmed))
                            .SetProperty(p => p.SettledAt, command.Now)
                            .SetProperty(p => p.SettledAtTicks, command.Now.UtcTicks),
                        cancellationToken);

                if (consumed != 1)
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 2. Lock and verify every touched reference, in one globally agreed order. Each statement
            //    both takes the row's exclusive lock and asserts the version the proposal was decided
            //    against, so a reference that moved since stops the whole set here.
            foreach (var expected in command.ExpectedVersions
                .OrderBy(version => version.Kind)
                .ThenBy(version => version.ReferenceId.ToString("D"), StringComparer.Ordinal))
            {
                if (!await LockAndVerifyAsync(command.InventoryId, expected, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 3. Verify every term this set means to claim is still free. The filtered unique indexes
            //    are the real guarantee; this check turns the common case into a clean conflict rather
            //    than a caught index violation.
            foreach (var absence in command.ExpectedTermAbsences)
            {
                if (await TermIsTakenAsync(command.InventoryId, absence, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 4. The authoritative Retire check. The plan-time check told the Participant before they
            //    were asked; this one decides. Stock created in between makes the Retire fail, which
            //    is exactly what "confirmed Retire fails for currently referenced data" means.
            foreach (var retire in retires)
            {
                if (await AnyStockReferencesAsync(command.InventoryId, retire.Target, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction);
                }
            }

            // 5. Apply the changes in the order the Participant reviewed.
            var recorded = new List<RecordedReferenceChange>(command.Changes.Count);
            foreach (var change in command.Changes.OrderBy(change => change.Order))
            {
                var applied = await ApplyChangeAsync(command, change, cancellationToken);
                if (applied is null)
                {
                    return await RolledBackConflictAsync(transaction);
                }

                recorded.Add(applied);
            }

            // 6. Settle every *other* pending proposal that depended on something this set retired -
            //    stock proposals included. The proposal being confirmed right now cannot be caught by
            //    this: step 1 already moved it out of Pending.
            foreach (var retire in retires)
            {
                await proposalStore.InvalidateReferencingAsync(
                    command.InventoryId, retire.Target.Kind, retire.Target.ReferenceId, command.Now, cancellationToken);
            }

            // 7. Ledger, effects, and one minimal semantic audit fact per change.
            db.ReferenceOperations.Add(new ReferenceOperationEntity
            {
                OperationId = command.OperationId.Value,
                InventoryId = command.InventoryId.Value,
                ConfirmedByTurnId = command.ConfirmedByTurnId.Value,
                ProposalId = command.ConsumesProposalId?.Value,
                AppliedAt = command.Now,
            });

            foreach (var change in recorded)
            {
                db.ReferenceEffects.Add(ToEntity(command.OperationId, change));

                db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
                    ReferenceAdministrationFacts.EventTypeFor(change.Kind),
                    AuditActorKind.Participant,
                    command.ActorId.ToString(),
                    command.InventoryId,
                    subjectParticipantId: null,
                    ReferenceAdministrationFacts.OutcomeCodeFor(change.Kind),
                    command.Now)));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ReferenceAdministrationStoreResult(
                ReferenceAdministrationStoreOutcome.Applied,
                new RecordedReferenceChangeSet(command.OperationId, command.ConsumesProposalId, recorded));
        }
        catch (DbUpdateException exception)
        {
            await db.AbandonAsync(transaction);

            // A competing writer may have been this very operation, applied by another replica. Its
            // ledger row is the authoritative record of what happened, so converge on re-reporting it
            // rather than claiming a conflict against ourselves.
            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, converged);
            }

            if (exception is DbUpdateConcurrencyException || await AnyClaimedTermTakenAsync(command, cancellationToken))
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Conflict, null);
            }

            throw;
        }
        catch (DbException)
        {
            // A guarded ExecuteUpdate that violates a filtered unique index raises the provider's own
            // exception rather than a DbUpdateException, and serializable isolation can additionally
            // produce a deadlock victim. Classify only what can actually be established; anything else
            // propagates as the fault it is, which the Turn reports as a transient failure - safe,
            // because nothing was applied.
            await db.AbandonAsync(transaction);

            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.AlreadyApplied, converged);
            }

            if (await AnyClaimedTermTakenAsync(command, cancellationToken))
            {
                return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Conflict, null);
            }

            throw;
        }
        catch
        {
            // Every other fault leaves the same debris, and several are reachable - a cancellation
            // between staging an insert and saving it, for one. The transaction would roll back on
            // dispose either way, but the ChangeTracker would not, and this DbContext serves a whole
            // batch of Turns.
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// Applies exactly one decided change, or returns null when a guarded statement touched no row -
    /// which cannot happen while the verify pass holds every row, and so must fail the whole set
    /// rather than be applied partially.
    ///
    /// Inserts are staged and flushed at <c>SaveChangesAsync</c> while updates run immediately, which
    /// is safe here because a change set can never touch one reference twice (the service refuses
    /// that outright), so no change in a set can depend on another's write being visible.
    /// </summary>
    private async Task<RecordedReferenceChange?> ApplyChangeAsync(
        ReferenceChangeSetCommand command, ProposedReferenceChange change, CancellationToken cancellationToken)
    {
        var inventoryId = command.InventoryId.Value;
        var referenceId = change.Target.ReferenceId;

        switch (change.Kind)
        {
            case ReferenceChangeKind.CreateUnit:
            {
                db.Units.Add(new UnitEntity
                {
                    Id = referenceId,
                    InventoryId = inventoryId,
                    CanonicalName = change.Target.Name,
                    NormalizedCanonicalName = change.Target.NormalizedName,
                    IsReserved = false,
                    ConcurrencyStamp = Guid.NewGuid(),
                    CreatedAt = command.Now,
                    RetiredAt = null,
                });

                foreach (var term in change.Terms)
                {
                    db.UnitTerms.Add(new UnitTermEntity
                    {
                        Id = Guid.NewGuid(),
                        InventoryId = inventoryId,
                        UnitId = referenceId,
                        Term = term.Term,
                        NormalizedTerm = term.NormalizedTerm,
                        IsCanonical = term.IsCanonical,

                        // Only the reserved `each` Unit's original five terms are fixed, and nothing
                        // here can ever create that Unit.
                        IsReserved = false,
                        CreatedAt = command.Now,
                        RetiredAt = null,
                    });
                }

                return Recorded(change) with
                {
                    Aliases = [.. change.Terms.Where(term => !term.IsCanonical).Select(term => term.Term)],
                };
            }

            case ReferenceChangeKind.RenameUnit:
            {
                var newName = change.NewName!;
                var newNormalizedName = change.NewNormalizedName!;

                var renamed = await db.Units
                    .Where(u => u.Id == referenceId && u.InventoryId == inventoryId && u.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(u => u.CanonicalName, newName)
                            .SetProperty(u => u.NormalizedCanonicalName, newNormalizedName)
                            .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                // The canonical term moves with the Unit's name; its aliases do not. StockEntries is
                // neither read nor written, so Equivalent Stock - keyed by UnitId - cannot change.
                var retermed = await db.UnitTerms
                    .Where(t => t.UnitId == referenceId && t.InventoryId == inventoryId && t.IsCanonical && t.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(t => t.Term, newName)
                            .SetProperty(t => t.NormalizedTerm, newNormalizedName),
                        cancellationToken);

                return renamed == 1 && retermed == 1 ? Recorded(change) with { NewName = newName } : null;
            }

            case ReferenceChangeKind.AddUnitAlias:
            {
                var term = change.Term!;

                db.UnitTerms.Add(new UnitTermEntity
                {
                    Id = Guid.NewGuid(),
                    InventoryId = inventoryId,
                    UnitId = referenceId,
                    Term = term.Term,
                    NormalizedTerm = term.NormalizedTerm,
                    IsCanonical = false,
                    IsReserved = false,
                    CreatedAt = command.Now,
                    RetiredAt = null,
                });

                return await BumpUnitAsync(inventoryId, referenceId, cancellationToken)
                    ? Recorded(change) with { Alias = term.Term }
                    : null;
            }

            case ReferenceChangeKind.RemoveUnitAlias:
            {
                var term = change.Term!;
                var normalized = term.NormalizedTerm;

                // Retired rather than deleted: the row - and what it used to mean - remains, which is
                // what keeps prior audits and prior proposals readable. Guarded on both protections so
                // a canonical or fixed term can never be removed even if a caller asked.
                var removed = await db.UnitTerms
                    .Where(t => t.UnitId == referenceId
                        && t.InventoryId == inventoryId
                        && t.NormalizedTerm == normalized
                        && !t.IsCanonical
                        && !t.IsReserved
                        && t.RetiredAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RetiredAt, command.Now), cancellationToken);

                if (removed != 1)
                {
                    return null;
                }

                return await BumpUnitAsync(inventoryId, referenceId, cancellationToken)
                    ? Recorded(change) with { Alias = term.Term }
                    : null;
            }

            case ReferenceChangeKind.RetireUnit:
            {
                // Guarded on IsReserved as well as on the Unit still being active: the reserved `each`
                // Unit can never be retired, and that must hold in the database rather than only in
                // the planner.
                var retired = await db.Units
                    .Where(u => u.Id == referenceId && u.InventoryId == inventoryId && u.RetiredAt == null && !u.IsReserved)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(u => u.RetiredAt, command.Now)
                            .SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                if (retired != 1)
                {
                    return null;
                }

                // Every one of its terms leaves the active namespace with it, which is what returns
                // those names to the Inventory. The rows remain, so the identity does too.
                await db.UnitTerms
                    .Where(t => t.UnitId == referenceId && t.InventoryId == inventoryId && t.RetiredAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RetiredAt, command.Now), cancellationToken);

                return Recorded(change);
            }

            case ReferenceChangeKind.CreateLocation:
            {
                db.Locations.Add(new LocationEntity
                {
                    Id = referenceId,
                    InventoryId = inventoryId,
                    Name = change.Target.Name,
                    NormalizedName = change.Target.NormalizedName,
                    ConcurrencyStamp = Guid.NewGuid(),
                    CreatedAt = command.Now,
                    RetiredAt = null,
                });

                return Recorded(change);
            }

            case ReferenceChangeKind.RenameLocation:
            {
                var newName = change.NewName!;
                var newNormalizedName = change.NewNormalizedName!;

                var renamed = await db.Locations
                    .Where(l => l.Id == referenceId && l.InventoryId == inventoryId && l.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(l => l.Name, newName)
                            .SetProperty(l => l.NormalizedName, newNormalizedName)
                            .SetProperty(l => l.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                return renamed == 1 ? Recorded(change) with { NewName = newName } : null;
            }

            case ReferenceChangeKind.RetireLocation:
            {
                var retired = await db.Locations
                    .Where(l => l.Id == referenceId && l.InventoryId == inventoryId && l.RetiredAt == null)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(l => l.RetiredAt, command.Now)
                            .SetProperty(l => l.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                return retired == 1 ? Recorded(change) : null;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "Unhandled reference change kind.");
        }
    }

    /// <summary>Moves a Unit's version because one of its terms changed, so a proposal decided against the old term set cannot still land.</summary>
    private async Task<bool> BumpUnitAsync(Guid inventoryId, Guid unitId, CancellationToken cancellationToken) =>
        await db.Units
            .Where(u => u.Id == unitId && u.InventoryId == inventoryId && u.RetiredAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid()), cancellationToken) == 1;

    /// <summary>Takes one reference's exclusive lock and asserts the version this set was decided against, in one statement.</summary>
    private async Task<bool> LockAndVerifyAsync(
        InventoryId inventoryId, ExpectedReferenceVersion expected, CancellationToken cancellationToken)
    {
        var freshStamp = Guid.NewGuid();

        var locked = expected.Kind == ReferenceKind.Unit
            ? await db.Units
                .Where(u => u.Id == expected.ReferenceId
                    && u.InventoryId == inventoryId.Value
                    && u.RetiredAt == null
                    && u.ConcurrencyStamp == expected.ConcurrencyStamp)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ConcurrencyStamp, freshStamp), cancellationToken)
            : await db.Locations
                .Where(l => l.Id == expected.ReferenceId
                    && l.InventoryId == inventoryId.Value
                    && l.RetiredAt == null
                    && l.ConcurrencyStamp == expected.ConcurrencyStamp)
                .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.ConcurrencyStamp, freshStamp), cancellationToken);

        return locked == 1;
    }

    private async Task<bool> TermIsTakenAsync(
        InventoryId inventoryId, ExpectedTermAbsence absence, CancellationToken cancellationToken) =>
        absence.Kind == ReferenceKind.Unit
            ? await db.UnitTerms
                .AsNoTracking()
                .AnyAsync(
                    t => t.InventoryId == inventoryId.Value && t.NormalizedTerm == absence.NormalizedTerm && t.RetiredAt == null,
                    cancellationToken)
            : await db.Locations
                .AsNoTracking()
                .AnyAsync(
                    l => l.InventoryId == inventoryId.Value && l.NormalizedName == absence.NormalizedTerm && l.RetiredAt == null,
                    cancellationToken);

    private async Task<bool> AnyClaimedTermTakenAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken)
    {
        foreach (var absence in command.ExpectedTermAbsences)
        {
            if (await TermIsTakenAsync(command.InventoryId, absence, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any Stock Entry still references what a Retire would withdraw. Administration never rewrites stock; it refuses.</summary>
    private async Task<bool> AnyStockReferencesAsync(
        InventoryId inventoryId, ProposedReferenceState target, CancellationToken cancellationToken)
    {
        var entries = db.StockEntries.AsNoTracking().Where(e => e.InventoryId == inventoryId.Value);

        entries = target.Kind == ReferenceKind.Unit
            ? entries.Where(e => e.UnitId == target.ReferenceId)
            : entries.Where(e => e.LocationId == target.ReferenceId);

        return await entries.AnyAsync(cancellationToken);
    }

    private async Task<ReferenceAdministrationStoreResult> RolledBackConflictAsync(IDbContextTransaction transaction)
    {
        await db.AbandonAsync(transaction);

        return new ReferenceAdministrationStoreResult(ReferenceAdministrationStoreOutcome.Conflict, null);
    }

    private async Task<RecordedReferenceChangeSet> ReadRecordedAsync(
        ReferenceOperationEntity header, CancellationToken cancellationToken)
    {
        var rows = await db.ReferenceEffects
            .AsNoTracking()
            .Where(e => e.OperationId == header.OperationId)
            .OrderBy(e => e.Order)
            .ToListAsync(cancellationToken);

        return new RecordedReferenceChangeSet(
            new ReferenceOperationId(header.OperationId),
            header.ProposalId is { } proposalId ? new ProposalId(proposalId) : null,
            rows.Select(ToRecorded).ToList());
    }

    private static RecordedReferenceChange ToRecorded(ReferenceEffectEntity row)
    {
        if (!ReferenceAdministrationFacts.TryParse(row.Kind, out var kind)
            || !Enum.TryParse<ReferenceKind>(row.ReferenceKind, ignoreCase: false, out var referenceKind))
        {
            throw new InvalidOperationException("A recorded reference change carried an unreadable kind.");
        }

        return new RecordedReferenceChange(row.Order, kind, referenceKind, row.ReferenceId, row.Name)
        {
            NewName = row.NewName,
            Alias = row.Alias,
            Aliases = row.AliasesJson is { } json
                ? JsonSerializer.Deserialize<List<string>>(json, AliasOptions) ?? []
                : [],
        };
    }

    private static ReferenceEffectEntity ToEntity(ReferenceOperationId operationId, RecordedReferenceChange change) => new()
    {
        Id = Guid.NewGuid(),
        OperationId = operationId.Value,
        Order = change.Order,
        Kind = ReferenceAdministrationFacts.ToMachineText(change.Kind),
        ReferenceKind = change.ReferenceKind.ToString(),
        ReferenceId = change.ReferenceId,
        Name = change.Name,
        NewName = change.NewName,
        Alias = change.Alias,
        AliasesJson = change.Aliases.Count == 0 ? null : JsonSerializer.Serialize(change.Aliases, AliasOptions),
    };

    /// <summary>
    /// The recorded form of one proposed change. The proposal's own state is exact - it is what the
    /// Participant reviewed - so nothing is re-read to build this.
    /// </summary>
    private static RecordedReferenceChange Recorded(ProposedReferenceChange change) =>
        new(change.Order, change.Kind, change.Target.Kind, change.Target.ReferenceId, change.Target.Name);
}
```

- [ ] **Step 6: Register everything**

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, add beside the shipped registrations:

```csharp
        services.AddScoped<IReferenceCatalogStore, SqlReferenceCatalogStore>();
        services.AddScoped<IReferenceAdministrationStore, SqlReferenceAdministrationStore>();
        services.AddScoped<ReferenceChangeResolver>();
        services.AddScoped<ReferenceAdministrationService>();
        services.AddScoped<ReferenceListingService>();
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlReferenceAdministrationStore"`
Expected: PASS - 11 behavior tests, 2 concurrency tests, 1 isolation test. If Docker is genuinely unavailable, run without the environment variable, confirm they report as skipped rather than failed, and say so plainly in the commit message.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Inventories/SqlReferenceAdministrationStore.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreConcurrencyTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlReferenceAdministrationStoreChangeTrackerIsolationTests.cs
git commit -m "feat(infrastructure): apply a reference change set atomically or change nothing for #33"
```

---

## Task 16: Dispatch the ten Unit and Location tools

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ReferenceToolDispatcher.cs`
- Create: `src/MultiChannelAgent.Application/Inventories/InventoryToolRouter.cs`
- Modify: `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceToolDispatcherTests.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryToolRouterTests.cs`

Why: the ten tools become executable here, under trusted context and never under model-supplied identity. `StockToolDispatcher` is already 700 lines and owns a complete stock vocabulary; a second complete vocabulary belongs in its own file, and one small router decides which one a tool name means.

- [ ] **Step 1: Write the failing dispatcher test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceToolDispatcherTests.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ReferenceToolDispatcherTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly TurnId _turnId = new(Guid.NewGuid());
    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryConfirmationProposalStore _proposals = new();

    private ReferenceToolDispatcher Dispatcher()
    {
        var authorization = new InventoryAuthorizationService(_inventories, _audits);

        return new ReferenceToolDispatcher(
            new ReferenceListingService(_catalog, authorization),
            new ReferenceAdministrationService(
                new ReferenceChangeResolver(_catalog, _references),
                new InMemoryReferenceAdministrationStore(_proposals),
                _proposals,
                authorization));
    }

    private TurnExecutionContext Context(InventoryId? activeInventoryId = null) => new(
        _turnId,
        _participantId,
        new ChannelConversationId("web-conversation-1"),
        new FoundryConversationId(Guid.NewGuid()),
        1,
        activeInventoryId ?? _inventoryId,
        TraceId: null);

    private Task<ModelDecision> DispatchAsync(string toolName, Dictionary<string, string> args, TurnExecutionContext? context = null) =>
        Dispatcher().DispatchAsync(
            new ToolCallProposal(toolName, args), context ?? Context(), DateTimeOffset.UnixEpoch, CancellationToken.None);

    [Fact]
    public async Task Without_an_Active_Inventory_the_answer_is_guidance_not_a_failure()
    {
        var decision = await DispatchAsync(
            ReferenceToolDispatcher.ListUnitsToolName,
            [],
            new TurnExecutionContext(
                _turnId,
                _participantId,
                new ChannelConversationId("web-conversation-1"),
                new FoundryConversationId(Guid.NewGuid()),
                1,
                null,
                TraceId: null));

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("no_active_inventory", decision.Code);
    }

    [Fact]
    public async Task Listing_Units_answers_a_typed_payload_a_Viewer_may_see()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddUnit(_inventoryId, "each", ["piece"], isReserved: true);
        _catalog.AddUnit(_inventoryId, "Cardboard Box", ["boxes"]);

        var decision = await DispatchAsync(ReferenceToolDispatcher.ListUnitsToolName, []);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("unit_list", payload.GetProperty("kind").GetString());
        Assert.Equal(2, payload.GetProperty("units").GetArrayLength());
        Assert.Equal("Cardboard Box", payload.GetProperty("units")[0].GetProperty("name").GetString());
        Assert.Equal("boxes", payload.GetProperty("units")[0].GetProperty("aliases")[0].GetString());
    }

    [Fact]
    public async Task Listing_Locations_answers_its_own_typed_payload()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);
        _catalog.AddLocation(_inventoryId, "Shelf A");

        var decision = await DispatchAsync(ReferenceToolDispatcher.ListLocationsToolName, []);

        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("location_list", payload.GetProperty("kind").GetString());
        Assert.Equal("Shelf A", payload.GetProperty("locations")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_Viewer_creating_a_Location_is_forbidden()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.CreateLocationsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"name":"Shelf A"}]""" });

        Assert.Equal(OutcomeCategory.Forbidden, decision.Category);
        Assert.Equal("forbidden", decision.Code);
    }

    [Fact]
    public async Task An_Editor_creating_one_Location_completes_with_a_typed_payload()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.CreateLocationsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"name":"Shelf A"}]""" });

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("reference_changes", payload.GetProperty("kind").GetString());
        Assert.Equal("create_location", payload.GetProperty("changes")[0].GetProperty("operation").GetString());
    }

    [Fact]
    public async Task An_Owner_retiring_a_Unit_is_asked_first_and_the_answer_carries_the_code()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Owner, DateTimeOffset.UnixEpoch);
        var unitId = _catalog.AddUnit(_inventoryId, "Cardboard Box", []);
        _references.AddUnit(_inventoryId, unitId, "Cardboard Box");

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.RetireUnitsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"unit":"Cardboard Box"}]""" });

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Equal("confirmation_required", decision.Code);

        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("reference_proposal", payload.GetProperty("kind").GetString());
        Assert.Equal(ConfirmationToken.TextLength, payload.GetProperty("token").GetString()!.Length);

        // The token is a bearer secret: it belongs in the payload the Participant is shown, never in
        // the Outcome's permanent summary column.
        Assert.DoesNotContain(payload.GetProperty("token").GetString()!, decision.Summary);
        Assert.Equal(TimeSpan.FromMinutes(ConfirmationProposal.LifetimeMinutes), decision.PayloadRetention);
    }

    [Fact]
    public async Task An_unknown_reference_answers_not_found_with_its_bounded_suggestions()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, DateTimeOffset.UnixEpoch);
        var unitId = _catalog.AddUnit(_inventoryId, "Box Large", []);
        _references.AddUnit(_inventoryId, unitId, "Box Large");

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.AddUnitAliasesToolName,
            new Dictionary<string, string> { ["changes"] = """[{"unit":"box","alias":"bx"}]""" });

        Assert.Equal(OutcomeCategory.NotFound, decision.Category);
        Assert.Equal("reference_not_found", decision.Code);

        var payload = JsonDocument.Parse(decision.Payload!).RootElement;
        Assert.Equal("reference_suggestions", payload.GetProperty("kind").GetString());
        Assert.Equal("unit", payload.GetProperty("reference").GetString());
        Assert.Equal("Box Large", payload.GetProperty("suggestions")[0].GetString());
        Assert.Contains("Box Large", decision.Summary);
    }

    [Fact]
    public async Task A_malformed_change_array_is_invalid_and_names_the_bound_it_violated()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.CreateUnitsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"name":"Box","location":"Shelf A"}]""" });

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("invalid_changes", decision.Code);
    }

    [Fact]
    public async Task The_reserved_Unit_is_answered_as_a_typed_conflict()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Owner, DateTimeOffset.UnixEpoch);
        var eachId = _catalog.AddUnit(_inventoryId, "each", ["piece", "pieces", "pc", "pcs"], isReserved: true);
        _references.AddUnit(_inventoryId, eachId, "each", "piece", "pieces", "pc", "pcs");

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.RenameUnitsToolName,
            new Dictionary<string, string> { ["changes"] = """[{"unit":"each","newName":"item"}]""" });

        Assert.Equal(OutcomeCategory.Conflict, decision.Category);
        Assert.Equal("reserved_unit", decision.Code);
    }

    [Fact]
    public async Task A_page_size_outside_the_bound_is_answered_by_its_own_code()
    {
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Viewer, DateTimeOffset.UnixEpoch);

        var decision = await DispatchAsync(
            ReferenceToolDispatcher.ListUnitsToolName,
            new Dictionary<string, string> { ["pageSize"] = "9999" });

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("invalid_page_size", decision.Code);
    }

    [Fact]
    public void The_dispatcher_names_exactly_the_ten_tools_the_specification_lists() =>
        Assert.Equal(
            [
                "list_units",
                "create_units",
                "rename_units",
                "add_unit_aliases",
                "remove_unit_aliases",
                "retire_units",
                "list_locations",
                "create_locations",
                "rename_locations",
                "retire_locations",
            ],
            ReferenceToolDispatcher.ToolNames);
}
```

- [ ] **Step 2: Write the failing router test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/InventoryToolRouterTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryToolRouterTests
{
    private sealed class RecordingDispatcher(string answer) : IToolDispatcher
    {
        public List<string> Dispatched { get; } = [];

        public Task<ModelDecision> DispatchAsync(
            ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Dispatched.Add(proposal.ToolName);

            return Task.FromResult(new ModelDecision
            {
                Category = OutcomeCategory.Completed,
                Code = answer,
                Summary = answer,
            });
        }
    }

    private static TurnExecutionContext Context() => new(
        new TurnId(Guid.NewGuid()),
        new ParticipantId(Guid.NewGuid()),
        new ChannelConversationId("web-conversation-1"),
        new FoundryConversationId(Guid.NewGuid()),
        1,
        new InventoryId(Guid.NewGuid()),
        TraceId: null);

    [Theory]
    [InlineData("list_stock")]
    [InlineData("add_stock")]
    [InlineData("apply_stock_changes")]
    [InlineData("confirm_inventory_operation")]
    [InlineData("reject_inventory_operation")]
    public async Task Every_stock_and_confirmation_tool_reaches_the_stock_dispatcher(string toolName)
    {
        var stock = new RecordingDispatcher("stock");
        var reference = new RecordingDispatcher("reference");

        var decision = await new InventoryToolRouter(stock, reference).DispatchAsync(
            new ToolCallProposal(toolName, new Dictionary<string, string>()),
            Context(),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal("stock", decision.Code);
        Assert.Equal([toolName], stock.Dispatched);
        Assert.Empty(reference.Dispatched);
    }

    [Theory]
    [InlineData("list_units")]
    [InlineData("create_units")]
    [InlineData("rename_units")]
    [InlineData("add_unit_aliases")]
    [InlineData("remove_unit_aliases")]
    [InlineData("retire_units")]
    [InlineData("list_locations")]
    [InlineData("create_locations")]
    [InlineData("rename_locations")]
    [InlineData("retire_locations")]
    public async Task Every_reference_tool_reaches_the_reference_dispatcher(string toolName)
    {
        var stock = new RecordingDispatcher("stock");
        var reference = new RecordingDispatcher("reference");

        var decision = await new InventoryToolRouter(stock, reference).DispatchAsync(
            new ToolCallProposal(toolName, new Dictionary<string, string>()),
            Context(),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal("reference", decision.Code);
        Assert.Equal([toolName], reference.Dispatched);
        Assert.Empty(stock.Dispatched);
    }

    [Fact]
    public async Task An_unrecognized_tool_reaches_nobody_and_is_reported_as_a_system_failure()
    {
        var stock = new RecordingDispatcher("stock");
        var reference = new RecordingDispatcher("reference");

        var decision = await new InventoryToolRouter(stock, reference).DispatchAsync(
            new ToolCallProposal("drop_database", new Dictionary<string, string>()),
            Context(),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.TransientFailure, decision.Category);
        Assert.Equal("unknown_tool", decision.Code);
        Assert.Empty(stock.Dispatched);
        Assert.Empty(reference.Dispatched);
    }
}
```

- [ ] **Step 3: Run both to verify they fail**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ReferenceToolDispatcherTests|FullyQualifiedName~InventoryToolRouterTests"`
Expected: FAIL to compile - `ReferenceToolDispatcher` and `InventoryToolRouter` do not exist.

- [ ] **Step 4: Write the reference dispatcher**

Create `src/MultiChannelAgent.Application/Inventories/ReferenceToolDispatcher.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Executes the ten Unit and Location administration tool calls the specification names -
/// list_units/create_units/rename_units/add_unit_aliases/remove_unit_aliases/retire_units and
/// list_locations/create_locations/rename_locations/retire_locations - always under the trusted
/// <see cref="TurnExecutionContext"/>, never the proposal's own untrusted arguments.
///
/// Those arguments are only ever a bounded page size, an opaque cursor, and one <c>changes</c> array
/// whose element shape is fixed by the tool that was called. None of them is identity: the
/// Participant, the Inventory, the conversation, the Turn, and the role all come from trusted context
/// alone, so a malicious or buggy proposal cannot widen what a Turn may touch.
/// </summary>
public sealed class ReferenceToolDispatcher(
    ReferenceListingService listingService, ReferenceAdministrationService administrationService) : IToolDispatcher
{
    public const string ListUnitsToolName = "list_units";
    public const string CreateUnitsToolName = "create_units";
    public const string RenameUnitsToolName = "rename_units";
    public const string AddUnitAliasesToolName = "add_unit_aliases";
    public const string RemoveUnitAliasesToolName = "remove_unit_aliases";
    public const string RetireUnitsToolName = "retire_units";
    public const string ListLocationsToolName = "list_locations";
    public const string CreateLocationsToolName = "create_locations";
    public const string RenameLocationsToolName = "rename_locations";
    public const string RetireLocationsToolName = "retire_locations";

    /// <summary>The exact ten tools this dispatcher owns, in the order the specification lists them.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
    [
        ListUnitsToolName,
        CreateUnitsToolName,
        RenameUnitsToolName,
        AddUnitAliasesToolName,
        RemoveUnitAliasesToolName,
        RetireUnitsToolName,
        ListLocationsToolName,
        CreateLocationsToolName,
        RenameLocationsToolName,
        RetireLocationsToolName,
    ];

    /// <summary>The one mutating tool name to change kind mapping. The tool fixes the kind, which is what makes a change array homogeneous by construction.</summary>
    private static readonly Dictionary<string, ReferenceChangeKind> MutatingTools = new(StringComparer.Ordinal)
    {
        [CreateUnitsToolName] = ReferenceChangeKind.CreateUnit,
        [RenameUnitsToolName] = ReferenceChangeKind.RenameUnit,
        [AddUnitAliasesToolName] = ReferenceChangeKind.AddUnitAlias,
        [RemoveUnitAliasesToolName] = ReferenceChangeKind.RemoveUnitAlias,
        [RetireUnitsToolName] = ReferenceChangeKind.RetireUnit,
        [CreateLocationsToolName] = ReferenceChangeKind.CreateLocation,
        [RenameLocationsToolName] = ReferenceChangeKind.RenameLocation,
        [RetireLocationsToolName] = ReferenceChangeKind.RetireLocation,
    };

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActiveInventoryId is not { } inventoryId)
        {
            // Guidance, not a failure: the Participant simply has no Inventory selected for this
            // conversation yet, and the answer tells them how to proceed.
            return Semantic(OutcomeCategory.Invalid, "no_active_inventory", "Select an Inventory in this conversation first.");
        }

        if (proposal.ToolName == ListUnitsToolName)
        {
            return await DispatchListUnitsAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken);
        }

        if (proposal.ToolName == ListLocationsToolName)
        {
            return await DispatchListLocationsAsync(proposal.UntrustedArgs, context, inventoryId, now, cancellationToken);
        }

        if (MutatingTools.TryGetValue(proposal.ToolName, out var kind))
        {
            return await DispatchChangesAsync(kind, proposal, context, inventoryId, now, cancellationToken);
        }

        // Unreachable through the router, which only sends names in ToolNames; kept so this dispatcher
        // is total over its own input rather than throwing on one.
        return SystemFailure("unknown_tool", $"'{proposal.ToolName}' is not a recognized tool.");
    }

    private async Task<ModelDecision> DispatchListUnitsAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await listingService.ListUnitsAsync(
            context.ParticipantId,
            inventoryId,
            ParsePageSize(untrustedArgs),
            untrustedArgs.GetValueOrDefault("cursor"),
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return result.Kind switch
        {
            ReferenceListResultKind.Completed => Answered(
                OutcomeCategory.Completed,
                "completed",
                SummarizeUnits(result.View!),
                JsonSerializer.Serialize(
                    new UnitListPayload(1, "unit_list", result.View!.Units, result.View.NextCursor, result.View.HasMore),
                    PayloadOptions)),
            ReferenceListResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No accessible Inventory is selected."),
            ReferenceListResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidListSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchListLocationsAsync(
        IReadOnlyDictionary<string, string> untrustedArgs,
        TurnExecutionContext context,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = await listingService.ListLocationsAsync(
            context.ParticipantId,
            inventoryId,
            ParsePageSize(untrustedArgs),
            untrustedArgs.GetValueOrDefault("cursor"),
            context.ChannelConversationId.Value,
            now,
            cancellationToken);

        return result.Kind switch
        {
            ReferenceListResultKind.Completed => Answered(
                OutcomeCategory.Completed,
                "completed",
                SummarizeLocations(result.View!),
                JsonSerializer.Serialize(
                    new LocationListPayload(1, "location_list", result.View!.Locations, result.View.NextCursor, result.View.HasMore),
                    PayloadOptions)),
            ReferenceListResultKind.NotFound => Semantic(OutcomeCategory.NotFound, "not_found", "No accessible Inventory is selected."),
            ReferenceListResultKind.Invalid => Semantic(OutcomeCategory.Invalid, result.Code, InvalidListSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    private async Task<ModelDecision> DispatchChangesAsync(
        ReferenceChangeKind kind,
        ToolCallProposal proposal,
        TurnExecutionContext context,
        InventoryId inventoryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ReferenceChangeSetParser.TryParse(kind, proposal.UntrustedArgs.GetValueOrDefault("changes"), out var requests, out var code))
        {
            return Semantic(OutcomeCategory.Invalid, code, InvalidChangesSummary(code));
        }

        // Derived from the durably accepted Turn and the tool being executed - both trusted, both
        // stable across retries - so replaying this Turn re-reports the recorded effect instead of
        // applying a second one. Nothing the model proposes contributes to it.
        var operationId = ReferenceOperationId.Derive(context.TurnId, proposal.ToolName, sequence: 0);

        var result = await administrationService.ApplyAsync(
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
            ReferenceAdministrationResultKind.Completed => AppliedChanges(result.Applied!),
            ReferenceAdministrationResultKind.ConfirmationRequired => ConfirmationRequired(result.Proposal!),
            ReferenceAdministrationResultKind.ReferenceNotFound => ReferenceNotFound(result),
            ReferenceAdministrationResultKind.NotFound => Semantic(
                OutcomeCategory.NotFound, result.Code, NotFoundSummary(result.Code)),
            ReferenceAdministrationResultKind.Conflict => Semantic(
                OutcomeCategory.Conflict, result.Code, ConflictSummary(result.Code)),
            ReferenceAdministrationResultKind.Invalid => Semantic(
                OutcomeCategory.Invalid, result.Code, InvalidChangesSummary(result.Code)),
            _ => Semantic(OutcomeCategory.Forbidden, "forbidden", "That request could not be completed."),
        };
    }

    /// <summary>
    /// The typed read-back one applied administration change set leaves behind. Shared with
    /// <see cref="StockToolDispatcher"/>, which reaches it when a confirmation it dispatched turns
    /// out to have executed an administration proposal - so the reference vocabulary is shaped in
    /// exactly one place.
    /// </summary>
    internal static ModelDecision AppliedChanges(ReferenceChangeSetView applied) => Answered(
        OutcomeCategory.Completed,
        "completed",
        SummarizeChanges(applied.Changes),
        JsonSerializer.Serialize(new ReferenceChangesPayload(1, "reference_changes", applied.Changes), PayloadOptions));

    private static ModelDecision ConfirmationRequired(ReferenceProposalView proposal)
    {
        var payload = JsonSerializer.Serialize(
            new ReferenceProposalPayload(1, "reference_proposal", proposal.Token, proposal.ExpiresAt, proposal.Changes),
            PayloadOptions);

        return new ModelDecision
        {
            Category = OutcomeCategory.ConfirmationRequired,
            Code = "confirmation_required",
            Summary = SummarizeProposal(proposal),
            Payload = payload,

            // The token is a bearer secret with a ten-minute life, retained for exactly that window
            // rather than the ordinary payload retention - once the proposal expires the token means
            // nothing, and keeping it readable for a day would buy nothing.
            PayloadRetention = TimeSpan.FromMinutes(ConfirmationProposal.LifetimeMinutes),
            Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, payload)],
        };
    }

    private static ModelDecision ReferenceNotFound(ReferenceAdministrationResult result)
    {
        var noun = result.UnresolvedReference == ReferenceKind.Location ? "Location" : "Unit";
        var suggestions = result.Suggestions ?? [];

        var summary = suggestions.Count == 0
            ? $"That {noun} does not exist in this Inventory."
            : $"That {noun} does not exist in this Inventory. This Inventory has {string.Join(", ", suggestions)}.";

        return Answered(
            OutcomeCategory.NotFound,
            "reference_not_found",
            summary,
            JsonSerializer.Serialize(
                new ReferenceSuggestionsPayload(
                    1, "reference_suggestions", noun.ToLowerInvariant(), suggestions),
                PayloadOptions));
    }

    /// <summary>
    /// States exactly what will happen and what it costs, then asks. It names no Inventory and no
    /// other Participant - and deliberately no token: the payload beside it carries the code, and
    /// repeating a bearer secret into the Outcome's permanent summary column would keep it long after
    /// the payload it belongs to has been discarded.
    /// </summary>
    private static string SummarizeProposal(ReferenceProposalView proposal)
    {
        var lines = proposal.Changes.Select(DescribeChange).ToList();
        var opening = lines.Count == 1
            ? $"This needs your confirmation: {lines[0]}"
            : $"These {lines.Count} changes apply together, or not at all: {string.Join(" ", lines)}";

        return $"{opening} Reply with \"confirm\" followed by the confirmation code shown with this "
            + "answer to apply it, or \"reject\" to leave everything as it is.";
    }

    private static string SummarizeChanges(IReadOnlyList<ReferenceChangeView> changes) =>
        string.Join(" ", changes.Select(DescribeChange));

    /// <summary>One administration change in plain words, always naming what it acts on and what it leaves behind.</summary>
    private static string DescribeChange(ReferenceChangeView change) => change.Operation switch
    {
        "create_unit" => change.Aliases.Count == 0
            ? $"Create the Unit {change.Name}."
            : $"Create the Unit {change.Name} with the aliases {string.Join(", ", change.Aliases)}.",
        "rename_unit" => $"Rename the Unit {change.Name} to {change.NewName}. Stock keeps its Unit and its Quantity.",
        "add_unit_alias" => $"Let {change.Alias} also mean the Unit {change.Name}.",
        "remove_unit_alias" => $"Stop {change.Alias} meaning the Unit {change.Name}.",
        "retire_unit" => $"Retire the Unit {change.Name}. It stops being usable, and no Stock is changed.",
        "create_location" => $"Create the Location {change.Name}.",
        "rename_location" => $"Rename the Location {change.Name} to {change.NewName}. Stock stays exactly where it is.",
        "retire_location" => $"Retire the Location {change.Name}. It stops being usable, and no Stock is changed.",
        _ => $"Change {change.Name}.",
    };

    private static string SummarizeUnits(UnitListView view) => view.Units.Count switch
    {
        0 => "No Units found.",
        1 => "1 Unit found.",
        var n => view.HasMore ? $"{n} Units shown; more remain." : $"{n} Units found.",
    };

    private static string SummarizeLocations(LocationListView view) => view.Locations.Count switch
    {
        0 => "No Locations found.",
        1 => "1 Location found.",
        var n => view.HasMore ? $"{n} Locations shown; more remain." : $"{n} Locations found.",
    };

    /// <summary>Names the current-state conflict a refused change ran into, without disclosing anything else about it.</summary>
    private static string ConflictSummary(string code) => code switch
    {
        "term_in_use" => "That name or alias already means something else in this Inventory, so nothing was changed.",
        "name_in_use" => "A Location here already has that name, so nothing was changed.",
        "reserved_unit" => "The reserved each Unit cannot be renamed or retired.",
        "reserved_term" => "That is one of the reserved each Unit's fixed aliases, so it cannot be removed.",
        "canonical_term" => "That is the Unit's own name, not one of its aliases, so it cannot be removed as one.",
        "reference_in_use" => "Stock still uses that, so it cannot be retired. Move or remove that Stock first.",
        "no_change" => "That would leave everything exactly as it is, so nothing was changed.",
        "state_changed" => "That changed while this request was being prepared, so nothing was changed. Ask again.",
        _ => "That request conflicts with this Inventory's reference data, so nothing was changed.",
    };

    private static string NotFoundSummary(string code) => code switch
    {
        "alias_not_found" => "That Unit does not have that alias, so there was nothing to remove.",
        _ => "No accessible Inventory is selected.",
    };

    /// <summary>Names the bound a rejected request violated, rather than only that it was rejected.</summary>
    private static string InvalidChangesSummary(string code) => code switch
    {
        "invalid_changes" => "State each change plainly - what to create, rename, alias, or retire.",
        "too_many_changes" => $"Ask for at most {ConfirmationProposal.MaxChanges} changes at a time.",
        "conflicting_changes" => "Two of those changes act on the same Unit or Location, or ask for the same name. Ask for them one at a time.",
        "invalid_name" =>
            $"A Unit name or alias must be 1 to {Unit.MaxNameLength} characters, and a Location name 1 to {Location.MaxNameLength}.",
        "invalid_reference" => "Name the Unit or Location to change.",
        _ => "That request could not be understood.",
    };

    private static string InvalidListSummary(string code) => code switch
    {
        "invalid_page_size" => $"Ask for between 1 and {ReferenceListQuery.MaxPageSize} at a time.",
        "invalid_cursor" => "That page marker belongs to a different request; start the list again.",
        _ => "That request could not be understood.",
    };

    /// <summary>
    /// Reads an untrusted page size. An unparseable value is treated as "not asked for" (the bounded
    /// default applies); a parseable but out-of-range one is passed through so the request is
    /// answered as invalid rather than silently widened or narrowed.
    /// </summary>
    private static int? ParsePageSize(IReadOnlyDictionary<string, string> untrustedArgs) =>
        untrustedArgs.TryGetValue("pageSize", out var raw) && int.TryParse(raw, out var parsed) ? parsed : null;

    private static ModelDecision Answered(OutcomeCategory category, string code, string summary, string payload) => new()
    {
        Category = category,
        Code = code,
        Summary = summary,
        Payload = payload,
        Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, payload)],
    };

    private static ModelDecision Semantic(OutcomeCategory category, string code, string summary) => new()
    {
        Category = category,
        Code = code,
        Summary = summary,
        Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, summary)],
    };

    private static ModelDecision SystemFailure(string code, string summary) => new()
    {
        Category = OutcomeCategory.TransientFailure,
        Code = code,
        Summary = summary,
    };

    private sealed record UnitListPayload(
        int Version, string Kind, IReadOnlyList<UnitView> Units, string? NextCursor, bool HasMore);

    private sealed record LocationListPayload(
        int Version, string Kind, IReadOnlyList<LocationView> Locations, string? NextCursor, bool HasMore);

    private sealed record ReferenceChangesPayload(int Version, string Kind, IReadOnlyList<ReferenceChangeView> Changes);

    private sealed record ReferenceProposalPayload(
        int Version, string Kind, string Token, string ExpiresAt, IReadOnlyList<ReferenceChangeView> Changes);

    /// <summary>The bounded deterministic alternatives an unknown reference offers. Never a nearest-match guess.</summary>
    private sealed record ReferenceSuggestionsPayload(
        int Version, string Kind, string Reference, IReadOnlyList<string> Suggestions);
}
```

- [ ] **Step 5: Write the router**

Create `src/MultiChannelAgent.Application/Inventories/InventoryToolRouter.cs`:

```csharp
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The single registered <see cref="IToolDispatcher"/>. It owns one decision and nothing else: which
/// dispatcher a tool name belongs to.
///
/// The set is closed and explicit. A name in neither set never reaches any dispatcher and is reported
/// as the model/system failure it is - a proposal this application cannot execute - rather than being
/// silently ignored or passed on to whichever dispatcher happened to be asked first.
/// </summary>
public sealed class InventoryToolRouter : IToolDispatcher
{
    private readonly IToolDispatcher _stockDispatcher;
    private readonly IToolDispatcher _referenceDispatcher;

    private static readonly HashSet<string> StockTools = new(StockToolDispatcher.ToolNames, StringComparer.Ordinal);

    private static readonly HashSet<string> ReferenceTools = new(ReferenceToolDispatcher.ToolNames, StringComparer.Ordinal);

    /// <summary>
    /// Takes the two dispatcher contracts rather than the concrete classes, so a test can supply
    /// recording doubles and prove routing without standing up either real dispatcher. Production
    /// resolution passes the concrete ones through the explicit factory registration.
    /// </summary>
    public InventoryToolRouter(IToolDispatcher stockDispatcher, IToolDispatcher referenceDispatcher)
    {
        _stockDispatcher = stockDispatcher;
        _referenceDispatcher = referenceDispatcher;
    }

    public async Task<ModelDecision> DispatchAsync(
        ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        if (StockTools.Contains(proposal.ToolName))
        {
            return await _stockDispatcher.DispatchAsync(proposal, context, now, cancellationToken);
        }

        if (ReferenceTools.Contains(proposal.ToolName))
        {
            return await _referenceDispatcher.DispatchAsync(proposal, context, now, cancellationToken);
        }

        // An unrecognized tool name is the model proposing something this application cannot execute -
        // a model/system failure, not an answer to the Participant's request.
        return new ModelDecision
        {
            Category = OutcomeCategory.TransientFailure,
            Code = "unknown_tool",
            Summary = $"'{proposal.ToolName}' is not a recognized tool.",
        };
    }
}
```

The primary constructor was deliberately not used: a type with a primary constructor may only declare additional constructors that chain to it with `this(...)`, and this router needs exactly one constructor taking the two contracts. The DI registration in Step 7 passes the concrete dispatchers into it.

- [ ] **Step 6: Name the stock tools and finish the confirmation shaping**

In `src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs`, add the tool-name set beside the shipped constants:

```csharp
    /// <summary>The exact tools this dispatcher owns, including the two conversation-wide confirmation tools it has always executed.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
    [
        ListStockToolName,
        FindStockToolName,
        AddStockToolName,
        RemoveStockToolName,
        SetStockToolName,
        MoveStockToolName,
        RenameStockToolName,
        ForgetStockToolName,
        ApplyStockChangesToolName,
        ConfirmToolName,
        RejectToolName,
    ];
```

and replace the placeholder arm Task 12 added to `ToDecision` with the real shaping, now that the reference vocabulary exists:

```csharp
        InventoryConfirmationResultKind.Completed when result.Applied is { } applied => Completed(
            "completed",
            SummarizeChanges(applied),
            JsonSerializer.Serialize(new StockChangesPayload(1, "stock_changes", applied.Changes), PayloadOptions)),

        // The Participant confirmed the one thing pending in this conversation, and it turned out to
        // be an administration proposal. The answer is shaped by the dispatcher that owns that
        // vocabulary, so `reference_changes` is built in exactly one place.
        InventoryConfirmationResultKind.Completed when result.AppliedReferences is { } appliedReferences =>
            ReferenceToolDispatcher.AppliedChanges(appliedReferences),

        InventoryConfirmationResultKind.Completed => Semantic(
            OutcomeCategory.Completed, "completed", "That change was applied."),
```

- [ ] **Step 7: Register the router**

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, replace

```csharp
        services.AddScoped<IToolDispatcher, StockToolDispatcher>();
```

with

```csharp
        services.AddScoped<StockToolDispatcher>();
        services.AddScoped<ReferenceToolDispatcher>();

        // One registered dispatcher, which routes by an explicit closed set of tool names.
        services.AddScoped<IToolDispatcher>(sp => new InventoryToolRouter(
            sp.GetRequiredService<StockToolDispatcher>(), sp.GetRequiredService<ReferenceToolDispatcher>()));
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - 11 dispatcher tests, 16 router tests, and every shipped `StockToolDispatcherTests` case unchanged (it still constructs `StockToolDispatcher` directly).

- [ ] **Step 9: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ReferenceToolDispatcher.cs \
        src/MultiChannelAgent.Application/Inventories/InventoryToolRouter.cs \
        src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ReferenceToolDispatcherTests.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/InventoryToolRouterTests.cs
git commit -m "feat(inventories): dispatch the ten Unit and Location tools under trusted context for #33"
```

---

## Task 17: Recognize the administration commands in the scripted grammar

**Files:**
- Modify: `src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs`
- Modify: `src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs`

Why: the deterministic model double is how every scenario, adapter conformance run, and end-to-end proof reaches a tool. Without these commands, nothing above can be driven through the real application boundary.

- [ ] **Step 1: Write the failing test**

Append to `tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs`, inside the existing class, using whatever helper it already has for invoking the boundary and reading the proposed tool call:

```csharp
    [Fact]
    public async Task List_units_proposes_the_bounded_read()
    {
        var proposal = await ProposeAsync("list units page size 5");

        Assert.Equal("list_units", proposal.ToolCall!.ToolName);
        Assert.Equal("5", proposal.ToolCall.UntrustedArgs["pageSize"]);
    }

    [Fact]
    public async Task List_locations_proposes_the_bounded_read()
    {
        var proposal = await ProposeAsync("list locations");

        Assert.Equal("list_locations", proposal.ToolCall!.ToolName);
        Assert.Empty(proposal.ToolCall.UntrustedArgs);
    }

    [Fact]
    public async Task Create_unit_proposes_one_homogeneous_change_with_its_initial_aliases()
    {
        var proposal = await ProposeAsync("create unit Cardboard Box aliases boxes, bx");

        Assert.Equal("create_units", proposal.ToolCall!.ToolName);
        Assert.Equal(
            """[{"name":"Cardboard Box","aliases":"boxes, bx"}]""",
            proposal.ToolCall.UntrustedArgs["changes"]);
    }

    [Fact]
    public async Task Create_location_proposes_one_homogeneous_change()
    {
        var proposal = await ProposeAsync("create location Shelf A");

        Assert.Equal("create_locations", proposal.ToolCall!.ToolName);
        Assert.Equal("""[{"name":"Shelf A"}]""", proposal.ToolCall.UntrustedArgs["changes"]);
    }

    [Fact]
    public async Task Rename_unit_carries_the_reference_and_the_new_name()
    {
        var proposal = await ProposeAsync("rename unit boxes to Carton");

        Assert.Equal("rename_units", proposal.ToolCall!.ToolName);
        Assert.Equal("""[{"unit":"boxes","newName":"Carton"}]""", proposal.ToolCall.UntrustedArgs["changes"]);
    }

    [Fact]
    public async Task Rename_location_carries_the_reference_and_the_new_name()
    {
        var proposal = await ProposeAsync("rename location Shelf A to Aisle 3");

        Assert.Equal("rename_locations", proposal.ToolCall!.ToolName);
        Assert.Equal("""[{"location":"Shelf A","newName":"Aisle 3"}]""", proposal.ToolCall.UntrustedArgs["changes"]);
    }

    [Fact]
    public async Task Adding_and_removing_an_alias_each_carry_one_alias()
    {
        var added = await ProposeAsync("add alias cartons to unit Cardboard Box");
        var removed = await ProposeAsync("remove alias cartons from unit Cardboard Box");

        Assert.Equal("add_unit_aliases", added.ToolCall!.ToolName);
        Assert.Equal("""[{"unit":"Cardboard Box","alias":"cartons"}]""", added.ToolCall.UntrustedArgs["changes"]);
        Assert.Equal("remove_unit_aliases", removed.ToolCall!.ToolName);
        Assert.Equal("""[{"unit":"Cardboard Box","alias":"cartons"}]""", removed.ToolCall.UntrustedArgs["changes"]);
    }

    [Fact]
    public async Task Retiring_a_Unit_and_a_Location_each_name_only_the_reference()
    {
        var unit = await ProposeAsync("retire unit Cardboard Box");
        var location = await ProposeAsync("retire location Shelf A");

        Assert.Equal("retire_units", unit.ToolCall!.ToolName);
        Assert.Equal("""[{"unit":"Cardboard Box"}]""", unit.ToolCall.UntrustedArgs["changes"]);
        Assert.Equal("retire_locations", location.ToolCall!.ToolName);
        Assert.Equal("""[{"location":"Shelf A"}]""", location.ToolCall.UntrustedArgs["changes"]);
    }

    [Fact]
    public async Task An_administration_command_never_swallows_a_stock_command()
    {
        var proposal = await ProposeAsync("rename stock Steel Bolts to Brass Rivets");

        Assert.Equal("rename_stock", proposal.ToolCall!.ToolName);
    }

    [Fact]
    public async Task An_administration_command_with_nothing_to_act_on_falls_back_to_the_echo()
    {
        var proposal = await ProposeAsync("retire unit");

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
    }
```

If the class has no `ProposeAsync` helper, call `ScriptedModelBoundary.ProposeAsync` exactly the way its neighbouring shipped tests do and keep that shape.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ScriptedModelBoundaryTests"`
Expected: FAIL - each new command falls through to the echo, so `ToolCall` is null.

- [ ] **Step 3: Extend the clause grammar**

In `src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs`, add the three new clause words. `aliases` must precede `alias` in the alternation so the longer word wins, exactly as `to unlocated` precedes `to`:

```csharp
    [GeneratedRegex(
        @"\b(including zero|to unlocated|unlocated|named|unit|in|page size|after|quantity|note|aliases|alias|from|to|all)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClauseScanner { get; }
```

`aliases`, `alias`, and `from` are all value clauses, so `FlagClauses` is unchanged.

- [ ] **Step 4: Recognize the ten commands**

In `src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs`, add the command constants beside the shipped ones:

```csharp
    private const string ListUnitsCommand = "list units";
    private const string ListLocationsCommand = "list locations";
    private const string CreateUnitCommand = "create unit";
    private const string CreateLocationCommand = "create location";
    private const string RenameUnitCommand = "rename unit";
    private const string RenameLocationCommand = "rename location";
    private const string AddAliasCommand = "add alias";
    private const string RemoveAliasCommand = "remove alias";
    private const string RetireUnitCommand = "retire unit";
    private const string RetireLocationCommand = "retire location";
```

Add the dispatch attempts to `ProposeAsync`, **before** the shipped mutation loop, so `rename unit ...` is never read as `rename stock ...` (they share no prefix, but ordering makes the intent explicit and the "never swallows" test proves it):

```csharp
        if (TryProposeReferenceRead(content, out var referenceReadProposal))
        {
            return Task.FromResult(referenceReadProposal!);
        }

        if (TryProposeReferenceAdministration(content, out var administrationProposal))
        {
            return Task.FromResult(administrationProposal!);
        }
```

Then add the two parsers:

```csharp
    /// <summary>Parses <c>list units [page size N] [after CURSOR]</c> and its Location twin.</summary>
    private static bool TryProposeReferenceRead(string content, out ModelProposal? proposal)
    {
        proposal = null;

        foreach (var (command, toolName) in ((string, string)[])
                 [(ListUnitsCommand, "list_units"), (ListLocationsCommand, "list_locations")])
        {
            if (!StartsWithCommand(content, command, out var remainder)
                || !ConversationalClauses.TryParse(remainder, out var clauses))
            {
                continue;
            }

            var args = new Dictionary<string, string>();
            CopyValue(clauses, "page size", args, "pageSize");
            CopyValue(clauses, "after", args, "cursor");

            proposal = ModelProposal.Tool(toolName, args);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses the eight mutating administration commands into one homogeneous single-element
    /// <c>changes</c> array. Every value is untrusted text: the tool fixes the kind, and the
    /// deterministic services resolve and bound everything else. Identity never comes from here.
    /// </summary>
    private static bool TryProposeReferenceAdministration(string content, out ModelProposal? proposal)
    {
        proposal = null;

        // Longest command words first, so "create location" is never read as "create" plus a
        // reference that happens to start with "location".
        foreach (var (command, toolName, build) in AdministrationCommands)
        {
            if (!StartsWithCommand(content, command, out var remainder) || remainder.Length == 0)
            {
                continue;
            }

            var subject = remainder;
            IReadOnlyDictionary<string, string> clauses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var clauseStart = FindFirstClauseIndex(remainder);
            if (clauseStart >= 0)
            {
                subject = remainder[..clauseStart].Trim();
                if (!ConversationalClauses.TryParse(remainder[clauseStart..], out clauses))
                {
                    return false;
                }
            }

            if (subject.Length == 0)
            {
                return false;
            }

            if (build(subject, clauses) is not { } element)
            {
                return false;
            }

            proposal = ModelProposal.Tool(toolName, new Dictionary<string, string> { ["changes"] = $"[{element}]" });
            return true;
        }

        return false;
    }

    /// <summary>The eight mutating commands, each with the tool it proposes and how to shape its one element.</summary>
    private static readonly (string Command, string ToolName, Func<string, IReadOnlyDictionary<string, string>, string?> Build)[]
        AdministrationCommands =
    [
        (CreateLocationCommand, "create_locations", static (subject, _) => Element(("name", subject))),
        (CreateUnitCommand, "create_units", static (subject, clauses) => clauses.TryGetValue("aliases", out var aliases)
            ? Element(("name", subject), ("aliases", aliases))
            : Element(("name", subject))),
        (RenameLocationCommand, "rename_locations", static (subject, clauses) => clauses.TryGetValue("to", out var newName)
            ? Element(("location", subject), ("newName", newName))
            : null),
        (RenameUnitCommand, "rename_units", static (subject, clauses) => clauses.TryGetValue("to", out var newName)
            ? Element(("unit", subject), ("newName", newName))
            : null),

        // "add alias cartons to unit Cardboard Box": the subject is the alias, and the "unit" clause
        // names the Unit it belongs to.
        (AddAliasCommand, "add_unit_aliases", static (subject, clauses) => clauses.TryGetValue("unit", out var unit)
            ? Element(("unit", unit), ("alias", subject))
            : null),

        // "remove alias cartons from unit Cardboard Box": "from" swallows the word "unit", so the
        // Unit is read from whichever of the two clauses carried it.
        (RemoveAliasCommand, "remove_unit_aliases", static (subject, clauses) => UnitOf(clauses) is { } unit
            ? Element(("unit", unit), ("alias", subject))
            : null),
        (RetireLocationCommand, "retire_locations", static (subject, _) => Element(("location", subject))),
        (RetireUnitCommand, "retire_units", static (subject, _) => Element(("unit", subject))),
    ];

    /// <summary>Reads the Unit a removal names, whether it arrived as "unit X" or as "from unit X".</summary>
    private static string? UnitOf(IReadOnlyDictionary<string, string> clauses)
    {
        if (clauses.TryGetValue("unit", out var unit))
        {
            return unit;
        }

        if (!clauses.TryGetValue("from", out var from))
        {
            return null;
        }

        var trimmed = from.Trim();

        return trimmed.StartsWith("unit ", StringComparison.OrdinalIgnoreCase) ? trimmed["unit ".Length..].Trim() : trimmed;
    }

    /// <summary>Builds one JSON object of untrusted string values, in the order given.</summary>
    private static string Element(params (string Name, string Value)[] properties) =>
        $"{{{string.Join(",", properties.Select(p => $"{JsonSerializer.Serialize(p.Name)}:{JsonSerializer.Serialize(p.Value)}"))}}}";
```

Finally, extend `FindFirstClauseIndex`'s keyword list so the new clauses end a subject:

```csharp
        foreach (var clause in (string[])
                 [" unit ", " in ", " unlocated", " quantity ", " note ", " to unlocated", " to ", " all", " aliases ", " alias ", " from "])
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ScriptedModelBoundaryTests"`
Expected: PASS - every shipped case unchanged plus the ten new ones. If a shipped stock case regresses, the clause list ordering is the cause: `aliases` must precede `alias`, and neither may precede `unit`.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Turns/ConversationalClauses.cs \
        src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs \
        tests/MultiChannelAgent.Application.Tests/ScriptedModelBoundaryTests.cs
git commit -m "feat(turns): recognize the ten Unit and Location administration commands for #33"
```

---

## Task 18: Show active Units and Locations, and what administration did

**Files:**
- Create: `src/MultiChannelAgent.Host/Endpoints/ReferenceEndpoints.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Create: `src/web/src/referenceApi.ts`
- Create: `src/web/src/ReferenceWorkspace.tsx`
- Modify: `src/web/src/turnsApi.ts`
- Modify: `src/web/src/TurnTracer.tsx`
- Modify: `src/web/src/App.tsx`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceEndpointsHttpTests.cs`

Why: the Inventory workspace is an authoritative read projection, and after this ticket the set of usable Units and Locations changes conversationally. A workspace that kept showing a retired Location would be showing something a Participant can no longer use.

- [ ] **Step 1: Write the failing HTTP test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceEndpointsHttpTests.cs`. It follows the shipped `StockEndpointsHttpTests` exactly: the SQLite factory, a cookie-free `HttpClient` with a `CookieJar`, and the `/api/test/sign-in` plus `/api/session/bootstrap` pair. Copy that class's `InitializeAsync`, `DisposeAsync`, `SignInAndBootstrapAsync`, and its Inventory-creation helper verbatim, then add:

```csharp
    [Fact]
    public async Task An_authorized_Participant_reads_the_active_Units_and_Locations()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Catalog Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Catalog Warehouse");

        var units = await GetJsonAsync(jar, $"/api/inventories/{inventoryId}/units");
        var locations = await GetJsonAsync(jar, $"/api/inventories/{inventoryId}/locations");

        Assert.Equal(1, units.GetProperty("units").GetArrayLength());
        Assert.Equal("each", units.GetProperty("units")[0].GetProperty("name").GetString());
        Assert.Equal(4, units.GetProperty("units")[0].GetProperty("aliases").GetArrayLength());
        Assert.False(units.GetProperty("hasMore").GetBoolean());
        Assert.Equal(0, locations.GetProperty("locations").GetArrayLength());
    }

    [Fact]
    public async Task An_Inventory_the_Participant_may_not_see_is_indistinguishable_from_one_that_does_not_exist()
    {
        var (jar, _) = await SignInAndBootstrapAsync("Stranger");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{Guid.NewGuid()}/units");
        jar.Apply(request);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_page_size_outside_the_bound_is_answered_with_a_problem_naming_it()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Catalog Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Catalog Warehouse");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/locations?pageSize=9999");
        jar.Apply(request);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pageSize", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<JsonElement> GetJsonAsync(CookieJar jar, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        jar.Apply(request);
        var response = await _client.SendAsync(request);
        jar.Capture(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
```

If `StockEndpointsHttpTests` has no reusable Inventory-creation helper, add `CreateInventoryAsync(CookieJar jar, string csrfToken, string name)` to this class by copying the `POST /api/inventories` call that class already makes inline, returning the created `Guid`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceEndpointsHttpTests"`
Expected: FAIL - both endpoints return 404 because they are not mapped.

- [ ] **Step 3: Map the endpoints**

Create `src/MultiChannelAgent.Host/Endpoints/ReferenceEndpoints.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the two authorized reference projections the Inventory workspace refetches after a terminal
/// Outcome arrives - the same authorized reads the conversational list_units and list_locations tool
/// calls use, resolved by the same service, so the workspace and the conversation can never disagree
/// about which Units and Locations exist.
///
/// Both are Viewer-authorized reads that expose only semantic facts: identities, names, and active
/// aliases. Never a version, never a reserved flag, never a retired row.
/// </summary>
public static class ReferenceEndpoints
{
    public static IEndpointRouteBuilder MapReferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventories/{inventoryId:guid}/units", async (
            Guid inventoryId,
            string? pageSize,
            string? cursor,
            ClaimsPrincipal user,
            ReferenceListingService listingService,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!TryParsePageSize(pageSize, out var parsedPageSize))
            {
                return InvalidPageSize();
            }

            var result = await listingService.ListUnitsAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                parsedPageSize,
                cursor,
                WebConversationCookie.EnsureId(httpContext),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Kind switch
            {
                ReferenceListResultKind.Completed => Results.Ok(result.View),

                // Whether the Inventory does not exist or simply is not authorized for this
                // Participant, the response must be identical: a plain 404, never a distinct signal.
                ReferenceListResultKind.NotFound or ReferenceListResultKind.Forbidden => Results.NotFound(),
                _ => InvalidRequest(result.Code),
            };
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        endpoints.MapGet("/api/inventories/{inventoryId:guid}/locations", async (
            Guid inventoryId,
            string? pageSize,
            string? cursor,
            ClaimsPrincipal user,
            ReferenceListingService listingService,
            HttpContext httpContext,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!TryParsePageSize(pageSize, out var parsedPageSize))
            {
                return InvalidPageSize();
            }

            var result = await listingService.ListLocationsAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                parsedPageSize,
                cursor,
                WebConversationCookie.EnsureId(httpContext),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Kind switch
            {
                ReferenceListResultKind.Completed => Results.Ok(result.View),
                ReferenceListResultKind.NotFound or ReferenceListResultKind.Forbidden => Results.NotFound(),
                _ => InvalidRequest(result.Code),
            };
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        return endpoints;
    }

    /// <summary>A blank page size means "not asked for" (the bounded default applies); anything non-numeric is rejected.</summary>
    private static bool TryParsePageSize(string? pageSize, out int? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(pageSize))
        {
            return true;
        }

        if (!int.TryParse(pageSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        parsed = value;
        return true;
    }

    private static IResult InvalidRequest(string code) => code switch
    {
        "invalid_cursor" => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["cursor"] = ["cursor is not valid here, or was issued for a different list."] }),
        _ => InvalidPageSize(),
    };

    private static IResult InvalidPageSize() => Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["pageSize"] = [$"pageSize must be a whole number between 1 and {ReferenceListQuery.MaxPageSize}."],
        });
}
```

In `src/MultiChannelAgent.Host/Program.cs`, add the mapping beside the shipped `MapStockEndpoints()` call:

```csharp
app.MapReferenceEndpoints();
```

- [ ] **Step 4: Type the new payloads on the client**

In `src/web/src/turnsApi.ts`, add beside the shipped payload interfaces:

```typescript
/** One active Unit: its stable identity, its canonical name, and its active aliases in order. */
export interface UnitView {
  id: string;
  name: string;
  aliases: string[];
}

/** One active Location. Flat and alias-free; unlocated stock is the absence of a reference and never appears here. */
export interface LocationView {
  id: string;
  name: string;
}

export interface UnitListPayload {
  version: number;
  kind: 'unit_list';
  units: UnitView[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface LocationListPayload {
  version: number;
  kind: 'location_list';
  locations: LocationView[];
  nextCursor: string | null;
  hasMore: boolean;
}

/** One Unit or Location administration change, exactly as proposed or exactly as applied. */
export interface ReferenceChangeView {
  order: number;
  operation:
    | 'create_unit'
    | 'rename_unit'
    | 'add_unit_alias'
    | 'remove_unit_alias'
    | 'retire_unit'
    | 'create_location'
    | 'rename_location'
    | 'retire_location';
  reference: 'unit' | 'location';
  /** The reference's stable identity. It never changes - not when renamed, and not when retired. */
  referenceId: string;
  name: string;
  newName: string | null;
  alias: string | null;
  aliases: string[];
}

/**
 * An exact set of reference changes awaiting explicit confirmation. `token` is the same short-lived
 * single-use confirmation code a stock proposal carries: render it, do not log it, and do not
 * persist it separately.
 */
export interface ReferenceProposalPayload {
  version: number;
  kind: 'reference_proposal';
  token: string;
  expiresAt: string;
  changes: ReferenceChangeView[];
}

/** What one applied administration change set did. */
export interface ReferenceChangesPayload {
  version: number;
  kind: 'reference_changes';
  changes: ReferenceChangeView[];
}

/**
 * The bounded, deterministic alternatives an unknown reference offers - active names sharing the
 * requested prefix, or else what this Inventory actually has. Never a nearest-match guess.
 */
export interface ReferenceSuggestionsPayload {
  version: number;
  kind: 'reference_suggestions';
  reference: 'unit' | 'location';
  suggestions: string[];
}
```

and widen the union:

```typescript
export type TurnOutcomePayload =
  | StockListPayload
  | StockFindPayload
  | StockMutationPayload
  | StockProposalPayload
  | StockChangesPayload
  | UnitListPayload
  | LocationListPayload
  | ReferenceProposalPayload
  | ReferenceChangesPayload
  | ReferenceSuggestionsPayload;
```

- [ ] **Step 5: Render them**

In `src/web/src/TurnTracer.tsx`, add the components beside the shipped `StockChanges`/`StockProposal` ones:

```tsx
function ReferenceChangeRows({ changes }: { changes: ReferenceChangeView[] }) {
  return (
    <table>
      <thead>
        <tr>
          <th>Change</th>
          <th>Reference</th>
          <th>Name</th>
          <th>Result</th>
        </tr>
      </thead>
      <tbody>
        {changes.map((change) => (
          <tr key={change.order}>
            <td>{change.operation.replaceAll('_', ' ')}</td>
            <td>{change.reference}</td>
            <td>{change.name}</td>
            <td>
              {change.newName ?? change.alias ?? (change.aliases.length > 0 ? change.aliases.join(', ') : '—')}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function ReferenceProposal({
  payload,
  onCommand,
}: {
  payload: ReferenceProposalPayload;
  onCommand: (command: string) => void;
}) {
  return (
    <section>
      <h3>Confirm this change to Units and Locations</h3>
      <ReferenceChangeRows changes={payload.changes} />
      <p>Expires at {new Date(payload.expiresAt).toLocaleTimeString()}</p>
      <button type="button" onClick={() => onCommand(`confirm ${payload.token}`)}>
        Confirm
      </button>
      <button type="button" onClick={() => onCommand('reject')}>
        Reject
      </button>
    </section>
  );
}

function ReferenceChanges({ payload }: { payload: ReferenceChangesPayload }) {
  return (
    <section>
      <h3>Applied</h3>
      <ReferenceChangeRows changes={payload.changes} />
    </section>
  );
}

function ReferenceSuggestions({ payload }: { payload: ReferenceSuggestionsPayload }) {
  return (
    <section>
      <h3>No such {payload.reference}</h3>
      {payload.suggestions.length === 0 ? (
        <p>This Inventory has no {payload.reference}s yet.</p>
      ) : (
        <ul>
          {payload.suggestions.map((suggestion) => (
            <li key={suggestion}>{suggestion}</li>
          ))}
        </ul>
      )}
    </section>
  );
}
```

and the five render arms beside the shipped ones:

```tsx
          {outcome.payload?.kind === 'unit_list' && (
            <section>
              <h3>Units</h3>
              <ul>
                {outcome.payload.units.map((unit) => (
                  <li key={unit.id}>
                    {unit.name}
                    {unit.aliases.length > 0 && ` (${unit.aliases.join(', ')})`}
                  </li>
                ))}
              </ul>
              {outcome.payload.hasMore && <p>More Units are available.</p>}
            </section>
          )}

          {outcome.payload?.kind === 'location_list' && (
            <section>
              <h3>Locations</h3>
              <ul>
                {outcome.payload.locations.map((location) => (
                  <li key={location.id}>{location.name}</li>
                ))}
              </ul>
              {outcome.payload.hasMore && <p>More Locations are available.</p>}
            </section>
          )}

          {outcome.payload?.kind === 'reference_proposal' && (
            <ReferenceProposal payload={outcome.payload} onCommand={setContentText} />
          )}

          {outcome.payload?.kind === 'reference_changes' && <ReferenceChanges payload={outcome.payload} />}

          {outcome.payload?.kind === 'reference_suggestions' && <ReferenceSuggestions payload={outcome.payload} />}
```

Import the five new types from `./turnsApi` alongside the shipped imports.

- [ ] **Step 6: Project the catalog in the workspace**

Create `src/web/src/referenceApi.ts`:

```typescript
import type { LocationView, UnitView } from './turnsApi';

export interface UnitListView {
  units: UnitView[];
  nextCursor: string | null;
  hasMore: boolean;
}

export interface LocationListView {
  locations: LocationView[];
  nextCursor: string | null;
  hasMore: boolean;
}

/**
 * Fetches the authoritative active Unit catalog for one Inventory - the same authorized read the
 * conversational list_units tool call uses. Returns null when the Inventory is not authorized for
 * the current Participant (or does not exist - the two are indistinguishable by design).
 */
export async function fetchUnits(inventoryId: string): Promise<UnitListView | null> {
  return fetchCatalog<UnitListView>(`/api/inventories/${inventoryId}/units`, 'Unit');
}

/** Fetches the authoritative active Location catalog. See {@link fetchUnits}. */
export async function fetchLocations(inventoryId: string): Promise<LocationListView | null> {
  return fetchCatalog<LocationListView>(`/api/inventories/${inventoryId}/locations`, 'Location');
}

async function fetchCatalog<T>(url: string, noun: string): Promise<T | null> {
  const response = await fetch(url, { credentials: 'include' });

  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(`Reading the ${noun} projection failed with status ${response.status}.`);
  }

  return (await response.json()) as T;
}
```

Create `src/web/src/ReferenceWorkspace.tsx`, following `StockWorkspace.tsx`'s shape exactly - including its `void (async () => { ... })()` effect wrapper and the comment explaining why it is written that way:

```tsx
import { useCallback, useEffect, useState } from 'react';
import { fetchLocations, fetchUnits, type LocationListView, type UnitListView } from './referenceApi';

interface ReferenceWorkspaceProps {
  inventoryId: string;
  /** Bumped by the parent whenever a terminal Outcome arrives, to trigger a refetch. */
  refetchToken: number;
}

/**
 * The Inventory workspace's authoritative reference projection: the active Units (with their
 * aliases) and the active Locations, refetched whenever the parent signals a terminal Outcome
 * arrived. Retired references are excluded server-side, so a Unit or Location that has just been
 * retired conversationally stops being offered here in the same breath.
 */
function ReferenceWorkspace({ inventoryId, refetchToken }: ReferenceWorkspaceProps) {
  const [units, setUnits] = useState<UnitListView | null>(null);
  const [locations, setLocations] = useState<LocationListView | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setUnits(await fetchUnits(inventoryId));
      setLocations(await fetchLocations(inventoryId));

      // A refetch that succeeded is the authoritative view, so an earlier failure must stop being
      // shown: leaving it would keep the workspace stuck on a stale error for the rest of the
      // session, hiding the very catalog it just loaded.
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [inventoryId]);

  useEffect(() => {
    // oxlint(react/set-state-in-effect) only recognizes an inline async IIFE's await boundary, not
    // one behind a named function reference - even though load's setState calls already happen after
    // its own internal awaits. See StockWorkspace.tsx for the same pattern.
    void (async () => {
      await load();
    })();
    // refetchToken deliberately participates in this effect's dependency list purely to trigger a
    // refetch when it changes - its value itself is never read.
  }, [load, refetchToken]);

  if (error) {
    return (
      <section role="alert">
        <h2>Units and Locations</h2>
        <p>{error}</p>
      </section>
    );
  }

  return (
    <section>
      <h2>Units and Locations</h2>
      <h3>Units</h3>
      {!units || units.units.length === 0 ? (
        <p>No Units yet.</p>
      ) : (
        <ul>
          {units.units.map((unit) => (
            <li key={unit.id}>
              {unit.name}
              {unit.aliases.length > 0 && ` (${unit.aliases.join(', ')})`}
            </li>
          ))}
        </ul>
      )}
      <h3>Locations</h3>
      {!locations || locations.locations.length === 0 ? (
        <p>No Locations yet. Stock with no Location is unlocated.</p>
      ) : (
        <ul>
          {locations.locations.map((location) => (
            <li key={location.id}>{location.name}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

export default ReferenceWorkspace;
```

In `src/web/src/App.tsx`, import it and mount it immediately after `<StockWorkspace ... />`, passing the same two props the Stock workspace already receives:

```tsx
        <ReferenceWorkspace inventoryId={activeInventoryId} refetchToken={stockRefetchToken} />
```

Use whatever names `App.tsx` already uses for the active Inventory identity and the refetch token; do not rename them.

- [ ] **Step 7: Verify**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceEndpointsHttpTests"`
Expected: PASS, 3 tests. These use the SQLite factory, so no Docker is needed.

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed. The build is the type check: an unhandled member of the widened `TurnOutcomePayload` union would fail here.

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Host/Endpoints/ReferenceEndpoints.cs \
        src/MultiChannelAgent.Host/Program.cs \
        src/web/src \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceEndpointsHttpTests.cs
git commit -m "feat(web): project active Units and Locations and render what administration did for #33"
```

---

## Task 19: Prove Unit and Location administration end to end

**Files:**
- Create: `tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationScenario.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationSqliteTests.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceAdministrationSqlScenarioTests.cs`

Why: the highest required correctness seam in this repository is one SQL-backed application-boundary suite. Every acceptance criterion of #33 has to be observable from outside: submit a normalized Turn, read the terminal Outcome, and check durable state.

- [ ] **Step 1: Write the scenario**

Create `tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationScenario.cs`, following `ConfirmedStockMutationScenario.cs` exactly in structure and helper style (`CompleteAsync`, `OutcomeAsync`, `TokenOf`, the projection assertions, the audit counters):

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The whole Unit and Location administration protocol through the real HTTP application boundary:
/// a Viewer who may only list, an Editor who may create/rename/alias, an Owner who alone may Retire
/// and only after confirming, the shared collision-free term namespace, the immutable reserved
/// `each` Unit, flat unique Locations, renames that never touch Stock, a Retire refused while Stock
/// references it, a confirmed Retire that keeps the identity and invalidates a pending proposal that
/// referenced it, bounded deterministic suggestions, minimal semantic audits for outcomes and
/// denials, and retries that return the recorded Outcome without ever re-planning.
///
/// Shared by the SQL Server-backed scenario and its Docker-free SQLite twin so both prove the
/// identical externally observable behavior.
/// </summary>
internal static class ReferenceAdministrationScenario
{
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Administering Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Reference Warehouse");

        // 1. Every Inventory starts with the reserved `each` Unit and its four fixed aliases, and no
        //    Locations at all - unlocated is the absence of a reference, never a row.
        var initialUnits = await CompleteAsync(factory, owner, "ref-list-1", "list units");
        var initialUnitsPayload = PayloadOf(initialUnits, "unit_list");
        Assert.Equal(1, initialUnitsPayload.GetProperty("units").GetArrayLength());
        Assert.Equal("each", initialUnitsPayload.GetProperty("units")[0].GetProperty("name").GetString());
        Assert.Equal(4, initialUnitsPayload.GetProperty("units")[0].GetProperty("aliases").GetArrayLength());

        var initialLocations = await CompleteAsync(factory, owner, "ref-list-2", "list locations");
        Assert.Equal(0, PayloadOf(initialLocations, "location_list").GetProperty("locations").GetArrayLength());

        // 2. An Editor-level create applies immediately and is audited once.
        var createdUnit = await CompleteAsync(factory, owner, "ref-create-1", "create unit Cardboard Box aliases boxes, bx");
        var createdUnitChange = SingleChange(createdUnit, "reference_changes");
        Assert.Equal("create_unit", createdUnitChange.GetProperty("operation").GetString());
        var boxUnitId = createdUnitChange.GetProperty("referenceId").GetString()!;
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitCreated)));

        await CompleteAsync(factory, owner, "ref-create-2", "create location Shelf A");
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.LocationCreated)));

        // 3. Unit names and aliases share one namespace: a term that already means something is refused.
        var collision = await OutcomeAsync(factory, owner, "ref-create-3", "create unit BOXES");
        Assert.Equal("conflict", collision.GetProperty("category").GetString());
        Assert.Equal("term_in_use", collision.GetProperty("code").GetString());

        // A Location name that is already taken is refused by its own code, and Locations have no
        // aliases to collide with.
        var locationCollision = await OutcomeAsync(factory, owner, "ref-create-4", "create location SHELF A");
        Assert.Equal("name_in_use", locationCollision.GetProperty("code").GetString());

        // 4. The reserved Unit and its fixed aliases cannot be renamed, retired, removed, or reassigned.
        foreach (var (nativeId, command, expected) in ((string, string, string)[])
                 [
                     ("ref-reserved-1", "rename unit each to item", "reserved_unit"),
                     ("ref-reserved-2", "retire unit pcs", "reserved_unit"),
                     ("ref-reserved-3", "remove alias pcs from unit each", "reserved_term"),
                     ("ref-reserved-4", "add alias piece to unit Cardboard Box", "term_in_use"),
                     ("ref-reserved-5", "remove alias each from unit each", "canonical_term"),
                 ])
        {
            var refused = await OutcomeAsync(factory, owner, nativeId, command);
            Assert.Equal("conflict", refused.GetProperty("category").GetString());
            Assert.Equal(expected, refused.GetProperty("code").GetString());
        }

        // A non-reserved alias may still be taught to `each`, and removed again.
        await CompleteAsync(factory, owner, "ref-alias-1", "add alias stuks to unit each");
        await CompleteAsync(factory, owner, "ref-alias-2", "remove alias stuks from unit each");
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitAliasAdded)));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitAliasRemoved)));

        // 5. Stock can be added against the new Unit and Location, using an alias.
        await CompleteAsync(factory, owner, "ref-stock-1", "add stock Steel Bolts quantity 10 unit bx in Shelf A");
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Cardboard Box", "Shelf A");

        // 6. Rename preserves identity and does not rewrite Stock Entries or alter Equivalent Stock.
        var stockBefore = await StockRowsAsync(factory, inventoryId);
        var renamedUnit = await CompleteAsync(factory, owner, "ref-rename-1", "rename unit boxes to Carton");
        Assert.Equal(boxUnitId, SingleChange(renamedUnit, "reference_changes").GetProperty("referenceId").GetString());
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Carton", "Shelf A");
        Assert.Equal(stockBefore, await StockRowsAsync(factory, inventoryId));

        var renamedLocation = await CompleteAsync(factory, owner, "ref-rename-2", "rename location Shelf A to Aisle 3");
        Assert.Equal("rename_location", SingleChange(renamedLocation, "reference_changes").GetProperty("operation").GetString());
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Carton", "Aisle 3");
        Assert.Equal(stockBefore, await StockRowsAsync(factory, inventoryId));

        // The old alias no longer resolves; the new canonical name and the surviving alias do.
        var goneAlias = await OutcomeAsync(factory, owner, "ref-alias-3", "add alias kartons to unit boxes");
        Assert.Equal("not_found", goneAlias.GetProperty("category").GetString());
        Assert.Equal("reference_not_found", goneAlias.GetProperty("code").GetString());
        await CompleteAsync(factory, owner, "ref-alias-4", "add alias kartons to unit bx");

        // 7. Retire is refused while Stock references it - and refused before anyone is asked to confirm.
        var blockedUnit = await OutcomeAsync(factory, owner, "ref-retire-1", "retire unit Carton");
        Assert.Equal("conflict", blockedUnit.GetProperty("category").GetString());
        Assert.Equal("reference_in_use", blockedUnit.GetProperty("code").GetString());

        var blockedLocation = await OutcomeAsync(factory, owner, "ref-retire-2", "retire location Aisle 3");
        Assert.Equal("reference_in_use", blockedLocation.GetProperty("code").GetString());

        // 8. Once the Stock is gone, Retire needs the Owner's explicit confirmation.
        await CompleteAsync(factory, owner, "ref-stock-2", "set stock Steel Bolts quantity 0");
        var clearing = await OutcomeAsync(factory, owner, "ref-stock-3", "set stock Steel Bolts quantity 0");
        await CompleteAsync(factory, owner, "ref-stock-4", $"confirm {TokenOf(clearing)}");
        var forget = await OutcomeAsync(factory, owner, "ref-stock-5", "forget stock Steel Bolts");
        await CompleteAsync(factory, owner, "ref-stock-6", $"confirm {TokenOf(forget)}");

        var proposedRetire = await OutcomeAsync(factory, owner, "ref-retire-3", "retire unit Carton");
        Assert.Equal("confirmation_required", proposedRetire.GetProperty("category").GetString());
        var proposedChange = SingleChange(proposedRetire, "reference_proposal");
        Assert.Equal("retire_unit", proposedChange.GetProperty("operation").GetString());
        Assert.Equal(boxUnitId, proposedChange.GetProperty("referenceId").GetString());

        // Nothing has happened yet: it still lists and still resolves.
        Assert.Contains("Carton", await UnitNamesAsync(factory, owner, "ref-list-3"));
        Assert.Equal(0, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitRetired)));

        // 9. Rejecting changes nothing at all.
        await CompleteAsync(factory, owner, "ref-reject-1", "reject");
        Assert.Contains("Carton", await UnitNamesAsync(factory, owner, "ref-list-6"));
        Assert.Equal(0, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitRetired)));

        // 10. A confirmed Retire preserves the identity, stops the Unit resolving, and audits once.
        var reproposed = await OutcomeAsync(factory, owner, "ref-retire-4", "retire unit Carton");
        var retired = await CompleteAsync(factory, owner, "ref-confirm-1", $"confirm {TokenOf(reproposed)}");
        Assert.Equal(boxUnitId, SingleChange(retired, "reference_changes").GetProperty("referenceId").GetString());
        Assert.DoesNotContain("Carton", await UnitNamesAsync(factory, owner, "ref-list-7"));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.UnitRetired)));
        Assert.Equal(boxUnitId, (await RetiredUnitIdAsync(factory, inventoryId)).ToString());

        // The token is single use.
        var reused = await OutcomeAsync(factory, owner, "ref-confirm-2", $"confirm {TokenOf(reproposed)}");
        Assert.Equal("proposal_not_found", reused.GetProperty("code").GetString());

        // 11. A retired reference is exactly as unknown as one that never existed - and its terms are
        //     free again, so a fresh Unit may claim them.
        var unknownNow = await OutcomeAsync(factory, owner, "ref-retire-5", "retire unit Carton");
        Assert.Equal("reference_not_found", unknownNow.GetProperty("code").GetString());
        await CompleteAsync(factory, owner, "ref-create-5", "create unit Carton");

        // 12. Unknown references offer bounded deterministic suggestions.
        var suggested = await OutcomeAsync(factory, owner, "ref-suggest-1", "retire unit Cart");
        Assert.Equal("reference_not_found", suggested.GetProperty("code").GetString());
        var suggestionPayload = PayloadOf(suggested, "reference_suggestions");
        Assert.Equal("unit", suggestionPayload.GetProperty("reference").GetString());
        Assert.Equal("Carton", suggestionPayload.GetProperty("suggestions")[0].GetString());
        Assert.True(suggestionPayload.GetProperty("suggestions").GetArrayLength() <= 5);

        // 13. A pending stock proposal that depends on a Location is invalidated when it is retired.
        await CompleteAsync(factory, owner, "ref-create-6", "create location Bay 7");
        await CompleteAsync(factory, owner, "ref-stock-7", "add stock Brass Rivets quantity 4 in Bay 7");
        await CompleteAsync(factory, owner, "ref-stock-8", "move stock Brass Rivets all to unlocated");

        var editor = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Second Editor");
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");
        await editor.SelectInventoryAsync(inventoryId);

        var editorProposal = await OutcomeAsync(factory, editor, "ref-editor-1", "move stock Brass Rivets all to Bay 7");
        Assert.Equal("confirmation_required", editorProposal.GetProperty("category").GetString());

        var bayRetire = await OutcomeAsync(factory, owner, "ref-retire-6", "retire location Bay 7");
        await CompleteAsync(factory, owner, "ref-confirm-3", $"confirm {TokenOf(bayRetire)}");

        var strandedConfirm = await OutcomeAsync(factory, editor, "ref-editor-2", $"confirm {TokenOf(editorProposal)}");
        Assert.Equal("not_found", strandedConfirm.GetProperty("category").GetString());
        Assert.Equal("proposal_not_found", strandedConfirm.GetProperty("code").GetString());

        // 14. Only the Owner may Retire; an Editor is forbidden and the denial is audited.
        await CompleteAsync(factory, owner, "ref-create-7", "create location Bay 8");
        var editorRetire = await OutcomeAsync(factory, editor, "ref-editor-3", "retire location Bay 8");
        Assert.Equal("forbidden", editorRetire.GetProperty("category").GetString());
        Assert.True(await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.AccessDenied)) > 0);

        // 15. A Viewer may list, and may not create.
        var viewer = await ConversationTestClient.SignInAsync(ConversationTestClient.CreateHttpsClient(factory), "Third Viewer");
        await owner.GrantMembershipAsync(inventoryId, viewer.ParticipantIdentifier, "Viewer");
        await viewer.SelectInventoryAsync(inventoryId);

        var viewerList = await CompleteAsync(factory, viewer, "ref-viewer-1", "list locations");
        Assert.True(PayloadOf(viewerList, "location_list").GetProperty("locations").GetArrayLength() > 0);

        var viewerCreate = await OutcomeAsync(factory, viewer, "ref-viewer-2", "create location Bay 9");
        Assert.Equal("forbidden", viewerCreate.GetProperty("category").GetString());

        // 16. A retry of an accepted Turn returns the recorded Outcome and never re-plans.
        var firstCreate = await CompleteAsync(factory, owner, "ref-retry-1", "create location Bay 10");
        var retried = await CompleteAsync(factory, owner, "ref-retry-1", "create location Bay 10");
        Assert.Equal(
            SingleChange(firstCreate, "reference_changes").GetProperty("referenceId").GetString(),
            SingleChange(retried, "reference_changes").GetProperty("referenceId").GetString());
        Assert.Equal(1, await CountLocationsNamedAsync(factory, inventoryId, "bay 10"));
    }
}
```

Implement the helpers this scenario names using the shipped ones as the model:

- `CompleteAsync` / `OutcomeAsync` / `TokenOf` / `SingleChange`: copy from `ConfirmedStockMutationScenario`, generalizing `SingleChange` to take the payload kind it should assert (it already does).
- `PayloadOf(outcome, kind)`: parse the Outcome's `payload` property, assert `kind`, and return the root element.
- `AssertStockAsync`: the shipped `AssertProjectionAsync`, extended to also assert the row's `unit`.
- `StockRowsAsync(factory, inventoryId)`: read every `StockEntries` row through a scoped `MultiChannelAgentDbContext` and project `(Id, UnitId, LocationId, Name, NormalizedName, Quantity, ConcurrencyStamp)` into an ordered list, so equality means "nothing about Stock changed at all".
- `UnitNamesAsync(factory, participant, nativeId)`: run `list units` and return the payload's unit names as a list.
- `CountAuditsAsync`: the shipped one.
- `RetiredUnitIdAsync`: the single `Units` row whose `RetiredAt` is not null.
- `CountLocationsNamedAsync`: count `Locations` rows with that normalized name, including retired ones.
- `GrantMembershipAsync(Guid inventoryId, string targetIdentifier, string role)`, `SelectInventoryAsync(Guid inventoryId)`, and `ParticipantIdentifier` are the shipped `ConversationTestClient` members this scenario uses; nothing new is needed on it.

- [ ] **Step 2: Write both runners**

Create `tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationSqliteTests.cs`:

```csharp
namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The Docker-free twin of the SQL Server-backed administration scenario. It boots the real Host
/// against an in-memory SQLite database and runs the identical externally observable protocol, so a
/// regression is caught locally in seconds rather than only in CI.
/// </summary>
public sealed class ReferenceAdministrationSqliteTests
{
    [Fact]
    public async Task Unit_and_Location_administration_works_end_to_end()
    {
        await using var factory = new SqliteWebApplicationFactory();

        await ReferenceAdministrationScenario.RunAsync(factory);
    }
}
```

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceAdministrationSqlScenarioTests.cs`:

```csharp
namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The same administration protocol against a real SQL Server instance with production migrations
/// applied - the repository's highest required correctness seam. Its SQLite twin proves the same
/// behavior without Docker; this one additionally proves the production schema, its filtered unique
/// indexes, and its isolation behavior.
/// </summary>
public sealed class ReferenceAdministrationSqlScenarioTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Unit_and_Location_administration_works_end_to_end()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed administration scenario.");

        await ReferenceAdministrationScenario.RunAsync(Factory!);
    }
}
```

- [ ] **Step 3: Run the Docker-free twin to verify it fails, then passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceAdministrationSqliteTests"`
Expected: FAIL first (the scenario file does not compile until its helpers exist), then PASS once the helpers are written.

Work through the scenario one numbered step at a time: run it, read the first failing assertion, fix the cause, and run again. A failure here is almost always one of three things - a scripted command the grammar does not recognize (Task 17), a payload property named differently than the scenario expects (Task 16), or a `ConversationTestClient` helper that does not exist yet.

- [ ] **Step 4: Run the SQL Server-backed scenario**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ReferenceAdministrationSqlScenarioTests"`
Expected: PASS. If Docker is genuinely unavailable, run without the environment variable, confirm it reports as skipped rather than failed, and say so plainly in the commit message.

- [ ] **Step 5: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationScenario.cs \
        tests/MultiChannelAgent.IntegrationTests/ReferenceAdministrationSqliteTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ReferenceAdministrationSqlScenarioTests.cs
git commit -m "test(integration): administer Units and Locations through a web conversation end to end for #33"
```

---

## Task 20: Whole-suite verification

**Files:** none created; this task fixes whatever it finds.

- [ ] **Step 1: Build exactly as CI does**

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings. `TreatWarningsAsErrors` is on, so any warning is a failure - including a switch that is no longer exhaustive over the widened `AuditEventType`.

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
grep -c "ReferenceOperations\|ReferenceEffects\|ConfirmationProposalReferences\|RetiredAt" ./migrations-check.sql
grep -c "RetiredAt IS NULL" ./migrations-check.sql
rm ./migrations-check.sql
```
Expected: the script generates, the first grep finds the new objects and columns, and the second finds the two filtered unique indexes.

- [ ] **Step 4: Build and lint the web client**

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed.

- [ ] **Step 5: Confirm the architecture boundaries still hold**

Run: `dotnet test tests/MultiChannelAgent.ArchitectureTests/MultiChannelAgent.ArchitectureTests.csproj`
Expected: PASS. `ReferenceChangePlan`, `ReferenceAdministrationFacts`, `ReferenceOperationId`, `ReferenceListQuery`, and `ReferenceListCursor` live in Domain and reference only Domain; `ReferenceChangeResolver`, `ReferenceAdministrationService`, `ReferenceListingService`, `ReferenceToolDispatcher`, `InventoryToolRouter`, and both new store seams live in Application and must reference nothing from Infrastructure.

- [ ] **Step 6: Confirm the ten tools are exactly the ten**

Run:
```bash
grep -o '"list_units"\|"create_units"\|"rename_units"\|"add_unit_aliases"\|"remove_unit_aliases"\|"retire_units"\|"list_locations"\|"create_locations"\|"rename_locations"\|"retire_locations"' \
  src/MultiChannelAgent.Application/Inventories/ReferenceToolDispatcher.cs | sort -u | wc -l
```
Expected: `10`. If it is anything else, a tool was invented or omitted - fix it against issue #26's list before going further.

- [ ] **Step 7: Confirm no budget or spend policy crept in**

Run:
```bash
git diff origin/main...HEAD -- src tests | grep -niE "budget|spend|chargeback|cost ceiling|quota purchase" || echo "clean"
```
Expected: `clean`. The parent spec puts every one of these out of scope; a "safety" limit added here would be behavior nobody asked for.

- [ ] **Step 8: Scan the diff for anything unfinished**

Run:
```bash
git diff --stat origin/main...HEAD
git diff origin/main...HEAD | grep -nE "TODO|FIXME|XXX|NotImplementedException|placeholder" || echo "clean"
```
Expected: `clean`. Any hit is a task that was not actually finished; finish it rather than deleting the marker.

Replace `origin/main` with whatever base branch this work actually targets if it differs.

- [ ] **Step 9: Commit any fixes**

```bash
git add -A
git commit -m "fix(inventories): settle the whole suite for Unit and Location administration for #33"
```

If nothing needed fixing, skip this commit rather than creating an empty one.

---

## Acceptance criteria coverage

| Acceptance criterion | Where it is implemented | Where it is proven |
| --- | --- | --- |
| Viewer can list active Units and Locations | Task 11 (`ReferenceListingService` authorizes Viewer), Task 16 (`list_units`/`list_locations`) | `ReferenceListingServiceTests.A_Viewer_may_list_active_Units_with_their_aliases`, `...may_list_active_Locations`, `ReferenceToolDispatcherTests.Listing_Units_answers_a_typed_payload_a_Viewer_may_see`, scenario step 15 |
| Owner and Editor can create, rename, and manage Unit aliases | Task 1 (`RequiredRole`), Task 10 (role decided from the requested kinds) | `ReferenceAdministrationFactsTests.Only_Retire_demands_the_Owner`, `ReferenceAdministrationServiceTests.An_Editor_creating_one_Location_applies_immediately`, `...A_Viewer_may_not_create_reference_data...`, scenario steps 2 and 15 |
| Only Owner can Retire | Task 1, Task 10 (pre-resolution role check), Task 12 (Owner rechecked at confirmation) | `ReferenceAdministrationServiceTests.An_Editor_may_not_Retire_and_the_denial_is_audited`, `InventoryConfirmationServiceTests.An_Editor_may_not_confirm_a_Retire...`, scenario step 14 |
| All ten specified tools exist - none invented, none omitted | Task 16 (`ReferenceToolDispatcher.ToolNames`) | `ReferenceToolDispatcherTests.The_dispatcher_names_exactly_the_ten_tools_the_specification_lists`, `InventoryToolRouterTests.Every_reference_tool_reaches_the_reference_dispatcher`, Task 20 step 6 |
| Tools use trusted Inventory context, never model-supplied identity | Task 16 (`TurnExecutionContext` only; args are page size, cursor, `changes`) | `ReferenceToolDispatcherTests.Without_an_Active_Inventory_the_answer_is_guidance_not_a_failure`, and every dispatcher test passing identity only through context |
| Tools accept exact IDs or names | Task 6 (active-only exact resolution), Task 8 (`FindUnitAsync`/`FindLocationAsync`) | `ReferenceChangeResolverTests.The_reserved_Unit_is_refused_by_name_or_by_identity`, `...Renaming_a_Unit_pins_the_version...` (by alias), `SqlReferenceCatalogStoreTests` |
| Homogeneous atomic change arrays | Task 7 (kind fixed by the tool; closed property set), Task 10 (one refusal refuses the set) | `ReferenceChangeSetParserTests.A_kind_property_is_itself_unknown...`, `...An_element_carrying_a_property_this_kind_does_not_have...`, `ReferenceAdministrationServiceTests.One_refusal_refuses_the_whole_set...` |
| Existing typed statuses only | Task 8 (plan-to-status mapping), Task 16 (`OutcomeCategory` arms) | `ReferenceChangeResolverTests` (every refusal), `ReferenceToolDispatcherTests` (`Completed`, `ConfirmationRequired`, `NotFound`, `Forbidden`, `Conflict`, `Invalid`) |
| Unit names and aliases share one collision-free normalized namespace | Task 3 (`ForCreateUnit`/`ForRenameUnit`/`ForAddUnitAlias`), Task 13 (filtered unique index) | `ReferenceChangePlanTests` collision cases, `ReferenceRelationalModelTests.Unit_terms_are_unique_across_active_terms_only`, `SqlReferenceAdministrationStoreTests.A_change_set_whose_term_was_claimed_meanwhile_changes_nothing`, scenario step 3 |
| The reserved `each` Unit and fixed aliases cannot be renamed, retired, removed, or reassigned | Task 2 (`IsReservedEachTerm`), Task 3 (four refusals), Task 13 (`IsReserved` per term; guarded SQL) | `ReferenceChangePlanTests.The_reserved_Unit_can_never_be_renamed`/`..._retired`, `...A_fixed_alias_..._can_never_be_removed`, `...A_reserved_term_can_never_be_reassigned...`, `ReferenceChangeResolverTests`, scenario step 4 |
| Location names remain flat, unique, and alias-free | Task 3 (`ForCreateLocation`/`ForRenameLocation`), Task 7 (no alias property for any Location kind), Task 13 (filtered unique index) | `ReferenceChangeSetParserTests.An_element_carrying_a_property_this_kind_does_not_have...`, `ReferenceRelationalModelTests.Location_names_are_unique_across_active_Locations_only`, scenario step 3 |
| Unlocated remains absence of a reference | Decision 5; no Location tool or plan ever names it, and `Locations` never holds a row for it | `ReferenceListingServiceTests` (empty Location list on a fresh Inventory), scenario step 1 and step 13's move to unlocated |
| Rename preserves stable identities | Task 3 (identity untouched), Task 15 (`RenameUnit`/`RenameLocation` write only names) | `SqlReferenceAdministrationStoreTests.Renaming_a_Unit_preserves_every_identity...`, `ReferenceAdministrationServiceTests`, scenario step 6 |
| Rename does not rewrite Stock Entries or alter Equivalent Stock | Task 15 (`StockEntries` is neither read nor written by a rename) | `SqlReferenceAdministrationStoreTests.Renaming_a_Unit_preserves_every_identity_and_rewrites_no_Stock_Entry_at_all`, `...Renaming_a_Location_...`, scenario step 6's full-row snapshot equality |
| Confirmed Retire fails for currently referenced data | Task 8 (plan-time check), Task 15 (authoritative re-check inside the transaction, under `Serializable`) | `ReferenceChangeResolverTests.A_Unit_a_Stock_Entry_still_references...`, `SqlReferenceAdministrationStoreTests.A_Retire_that_Stock_now_references_changes_nothing...`, `InventoryConfirmationServiceTests.A_confirmed_Retire_that_stock_now_references_changes_nothing`, `SqlReferenceAdministrationStoreConcurrencyTests`, scenario step 7 |
| Confirmed Retire preserves the retired identity | Task 15 (`RetiredAt` set; nothing deleted) | `SqlReferenceAdministrationStoreTests.Retiring_a_Unit_keeps_its_identity_and_frees_every_one_of_its_terms`, scenario step 10 |
| Confirmed Retire invalidates pending proposals referencing it | Task 5 (`ReferencedUnitIds`/`ReferencedLocationIds`), Task 13 (`ConfirmationProposalReferences`), Task 14 (`InvalidateReferencingAsync`), Task 15 (inside the transaction) | `SqlConfirmationProposalStoreTests.Retiring_a_reference_settles_every_pending_proposal_that_depends_on_it`, `SqlReferenceAdministrationStoreTests.Retiring_a_Location_settles_a_pending_stock_proposal_that_depended_on_it`, scenario step 13 |
| Retire is always confirmed, and every multi-change batch is too | Task 1 (`RequiresConfirmation`), Task 10 (`changes.Count > 1 \|\| any`) | `ReferenceAdministrationFactsTests`, `ReferenceAdministrationServiceTests.An_Owner_retiring_an_unused_Unit_is_asked_first...`, `...Every_batch_of_more_than_one_change_is_proposed...`, scenario step 8 |
| One pending proposal per Participant and ChannelConversation, across stock and administration | Task 5 (one aggregate, two payloads), Task 14 (shipped filtered unique index untouched) | `SqlConfirmationProposalStoreTests` (shipped one-pending test still passes with reference proposals), scenario step 13 |
| Administration outcomes produce the specified semantic audit facts | Task 1 (event type and outcome code per kind), Task 15 (one fact per change, in the same transaction) | `ReferenceAdministrationFactsTests.Every_kind_audits_one_minimal_fact`, `SqlReferenceAdministrationStoreTests` audit counts, scenario audit counters in steps 2, 4, 10 |
| Denials produce the specified semantic audit facts | Decision 11 (shipped `AccessDenied`), Task 10 and Task 12 (both authorize through `InventoryAuthorizationService`) | `ReferenceAdministrationServiceTests.A_non_member_is_told_nothing...`, `...A_Viewer_may_not_create...`, `...An_Editor_may_not_Retire...`, `InventoryConfirmationServiceTests.An_Editor_may_not_confirm_a_Retire...`, scenario step 14 |
| Unknown references provide bounded deterministic suggestions | Task 6 (`SuggestAsync`, prefix-then-fallback, capped at 5), Task 8, Task 16 (`reference_suggestions`) | `SqlReferenceCatalogStoreTests.Suggestions_are_bounded_deterministic_and_never_fuzzy`, `ReferenceChangeResolverTests.An_unknown_Unit_answers_reference_not_found_with_bounded_deterministic_suggestions`, `ReferenceToolDispatcherTests`, scenario step 12 |
| Retired references are excluded from matching and ordinary lists | Task 6 (active-only resolution and catalog reads) | `ReferenceChangeResolverTests.A_retired_reference_is_exactly_as_unknown_as_one_that_never_existed`, `SqlReferenceCatalogStoreTests.A_retired_reference_is_not_found_for_administration_either`, scenario step 11 |
| Semantic no-ops return typed conflicts | Task 3 (`NoChange`), Task 8 (`no_change`) | `ReferenceChangePlanTests` no-op cases, `ReferenceChangeResolverTests.Renaming_a_Location_to_exactly_what_it_is_called_is_a_typed_no_op` |
| Operation-identity replay returns recorded success | Task 1 (`ReferenceOperationId`), Task 9/15 (ledger + `FindRecordedByTurnAsync`), Tasks 10 and 12 (replay asked first) | `ReferenceAdministrationServiceTests.A_Turn_that_already_applied...`, `InventoryConfirmationServiceTests.A_Turn_that_already_executed_a_reference_proposal_re_reports_it`, `SqlReferenceAdministrationStoreTests.Applying_the_same_operation_identity_again...`, scenario step 16 |
| A failed atomic change set changes nothing | Task 15 (explicit transaction, rollback on every guard, abandoned scope) | `SqlReferenceAdministrationStoreTests` conflict tests, `SqlReferenceAdministrationStoreChangeTrackerIsolationTests` |
| Non-disclosure preserved | Task 10 and Task 11 (authorize before anything, `not_found` for both cases), Task 16 (generic summaries) | `ReferenceAdministrationServiceTests.A_non_member_is_told_nothing...`, `...A_replay_is_answered_only_after_authorization...`, `ReferenceEndpointsHttpTests.An_Inventory_the_Participant_may_not_see...` |
| Concurrency and races handled | Task 13 (filtered unique indexes), Task 15 (ordered lock-and-verify, `Serializable` for a Retire, conflict classification) | `SqlReferenceAdministrationStoreConcurrencyTests.Only_one_of_two_concurrent_Retires...`, `...A_Retire_racing_a_Stock_write_never_leaves_a_retired_Unit_with_Stock_referencing_it` |
| Migrations, indexes, and cascade paths | Task 13 (one migration, backfills, single cascade path per new table) | `ReferenceRelationalModelTests`, shipped `UnitTermRelationalModelTests`, `ReferenceAdministrationSqlScenarioTests` (real migrations on a fresh database), Task 20 step 3 |
| Web semantic rendering and projection refresh | Task 18 (two endpoints, five payload kinds, `ReferenceWorkspace` on the same refetch token) | `ReferenceEndpointsHttpTests`, `npm run build` type-checking the widened payload union, scenario steps 1 and 10 through the same authorized reads |
| No monetary budgets | No task adds one; Task 20 step 7 enforces it | Task 20 step 7 |
| No import or channel work | Out of scope above; no task touches CSV, Teams, email, Graph, or voice | Task 20 step 8's diff review |

---

## Deliberate design decisions worth knowing

- **One vocabulary, five jobs.** `ReferenceChangeKind` is the tool argument, the ledger record, the audit fact, the confirmation policy, and the role matrix. A separate "effect" enum whose members mapped one-to-one would be a second thing to keep in step for no expressiveness, so there is not one.
- **The tool fixes the kind.** A `changes` array is homogeneous because the tool that carried it says which kind it holds, not because anything checks. An element naming its own `kind` is an unknown property, so a mixed batch cannot even be expressed.
- **Creating a Unit establishes its whole term set.** A Unit's initial aliases arrive with it, exactly as the reserved `each` Unit's four do; afterwards aliases are managed one at a time, which is what makes each alias change one auditable fact.
- **Retirement frees the name.** Retiring sets `RetiredAt` on the Unit and on every one of its terms, and the uniqueness indexes are filtered to active rows. The identity, the rows, and every prior audit survive; the name returns to the Inventory. Restore is out of scope, so a freed name can never collide with a revived reference.
- **`IsReserved` is per term, not per Unit.** That is the only way "the fixed aliases cannot be removed" and "a Participant may still teach `each` a local word" can both be true.
- **Rename never reads `StockEntries`.** Not "does not write it" - does not touch it. Equivalent Stock is keyed by `UnitId` and `LocationId`, neither of which a rename changes, so the claim is structural and the SQL test proves it by whole-row snapshot equality.
- **The Retire check happens twice, and the second one decides.** The plan-time check exists so a Participant is told before being asked to confirm. The in-transaction check is the authoritative one, because "confirmed Retire fails for **currently** referenced data" is a statement about execution time.
- **Serializable, but only for a Retire.** The conflict check is a range query, and read-committed would let a Stock insert commit just after the retirement. It is one line, scoped to the one change kind that needs it, and everything else stays on the default isolation the rest of the application uses.
- **Retire-driven invalidation is keyed, not searched.** `ConfirmationProposalReferences` is written in the same transaction as the proposal. Scanning serialized JSON for a Guid would work by accident; a keyed, indexed table works by construction - and it catches stock proposals, which are the ones a retirement would most quietly strand.
- **One proposal type, two payloads.** The single pending slot, the ten-minute single-use token, the binding predicate, and the whole status state machine are shared. The stock factory is untouched; the reference factory enforces the exact parallel rules on its own inputs, so nothing about stock is relaxed to make room.
- **One confirmation service, two executions.** There is exactly one pending proposal per conversation, so there is exactly one thing that confirms it. A second service would need its own copy of the authorization preamble, the replay lookup, the evidence rule, the binding check, the expiry check, and the token check - six chances to drift on the most safety-critical path in the application.
- **The Owner recheck is at confirmation, not at proposal.** Membership can change inside ten minutes, and the role that matters is the one held when the change actually commits.
- **A denied confirmation leaves the proposal pending.** Lookup is per-Participant, so nobody else can reach it, and it settles itself in ten minutes. Destroying a Participant's own reviewed work because their role changed mid-conversation would be the worse failure.
- **Suggestions are prefix-and-order, never fuzzy.** Exact-prefix first, then "here is what this Inventory has", both bounded to five and both in one deterministic order. #26 puts fuzzy matching out of scope, and nothing here approaches it.
- **`ambiguous` is unreachable for references.** A reference resolves to exactly one identity or to none, so no code path produces that status - which is why the reference dispatcher has no arm for it.
- **Two ledgers, disjoint by construction.** `ReferenceOperations` is its own table with its own identity type, hashed from differently shaped material. A Turn dispatches one tool call, so it writes to at most one ledger, and the confirmation service asks both because by replay time the proposal that would have said which is gone.
- **Nothing is called a conflict that cannot be established.** A fault the store cannot attribute to a version, a claimed term, or a blocked Retire propagates as the fault it is. The Turn then ends as a transient failure the Participant can simply ask again - which is safe precisely because nothing was applied.
- **No budgets.** Nothing in this plan adds a cost ceiling, spend threshold, or quota check; the parent spec puts all of them out of scope, and a "safety" limit added here would be a behavior nobody asked for.
