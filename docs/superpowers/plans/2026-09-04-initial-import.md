# Initial Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #34 by giving Owners and Editors one signed-in web Initial Import workflow that accepts a bounded UTF-8 RFC 4180 five-column CSV for an Inventory with no Stock Entries, validates the whole file and reports every actionable row and column error together, resolves only active Units and Locations without ever creating one, merges Equivalent rows by summing Quantity when their Notes are compatible, shows an exact normalized preview backed by a ten-minute single-use proposal bound to the Participant, the Inventory, the file digest, the exact rows and the empty-state version, and on confirmation creates every Stock Entry atomically and idempotently - or none - without reparsing, then discards the raw CSV and retains only minimal 90-day semantic facts.

**Architecture:** Initial Import is a **signed-in HTTP workflow, not a conversational tool**, so it does not go through `InboundTurn -> TurnProcessingCoordinator -> IToolDispatcher` and it does not compete for the one conversational pending-proposal slot. It reuses what genuinely aligns - `ConfirmationToken` for the single-use hashed token, `InventoryAuthorizationService` for role checks and denial audits, `IInventoryReferenceStore` for active-only reference resolution, `AssignedReferenceLocks` and the reference-then-proposal-then-stock lock order from #33, `AbandonedWrites.AbandonAsync` for failed-write hygiene, and the `Quantity`/`StockEntry`/`NameNormalization` domain rules - and adds its own durable aggregate where the shipped one does not fit: `ConfirmationProposal` is bounded to 25 changes and keyed one-pending-per-ChannelConversation, while an import carries up to 5,000 rows and belongs to a browser session. Four pure Domain pieces carry the rules: a `CsvImportDocument` reader that is the only thing that understands bytes, an `ImportRow` model the merge and the executor share, an `ImportMergePlan` that decides equivalence and Note compatibility from rows alone, and an `ImportProposal` aggregate carrying the token, the digest, the exact normalized rows and the empty-state version. Two Application services compose them - `InitialImportService` (eligibility, validate, preview) and `ImportConfirmationService` (confirm, reject, replay) - over two new store seams, `IImportProposalStore` and `IImportExecutionStore`. `SqlImportExecutionStore` is the one atomic writer: under `Serializable` it holds every referenced Unit and Location, consumes the proposal, re-asserts that the Inventory still has no Stock Entries at all, inserts every entry, appends one minimal audit fact and its ledger row, and discards the raw upload - all in one transaction, or nothing.

**Tech Stack:** C#/.NET 10, EF Core 10 (SQL Server in production, SQLite for Docker-free relational tests), ASP.NET Core minimal APIs with multipart form upload and the shipped `AntiforgeryEndpointFilter`, xUnit 2.9, `Xunit.SkippableFact`, `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers `MsSql`, React 19 + TypeScript + Vite + oxlint.

---

## Scope and non-goals

In scope - issue #34's acceptance criteria, verbatim:

1. Owner and Editor can open Initial Import only when the Inventory has no Stock Entries, including zero-quantity entries.
2. The parser accepts the specified UTF-8 RFC 4180-style five-column contract and rejects unknown, duplicate, oversized, or invalid input.
3. Validation resolves active Unit names or aliases and active Location names without creating references.
4. Equivalent rows merge by summing Quantity only when Notes are compatible, and all actionable row/column errors are returned together.
5. A successful validation creates an exact normalized preview and ten-minute proposal bound to actor, Inventory, file digest, rows, and empty-state version.
6. Confirmation creates all entries atomically and idempotently or none, without reparsing.
7. Raw CSV is discarded after completion or expiry and only the specified 90-day semantic facts remain.

Preserved from #28/#30/#31/#32/#33 and never regressed: trusted context, non-disclosing refusals, minimal semantic audits, optimistic concurrency, the operation-ledger replay rule, active-only reference resolution, the conversational one-pending-proposal slot, and the shared reference-then-proposal-then-stock lock order.

Explicitly **out of scope**:

- **Monetary budgets, spend thresholds, chargeback, cost ceilings, and quota purchase.** The parent spec (#26) puts every one of these out of scope under "Bounds and graceful degradation": *"Enforce no monetary budgets, spend thresholds, chargeback, or cost-triggered shutdown."* **No task in this plan may add a cost check, a spend ceiling, or a budget policy of any kind.**
- Import through any channel other than the signed-in web SPA. #26: *"Provide one signed-in web workflow"* and *"Initial Import is the only file-driven bulk path."* There is no import tool, no import Turn, and no email or Teams attachment path.
- Import into an Inventory that already holds Stock. There is no merge-into-existing, no upsert, and no second import.
- Creating Units or Locations. An unknown reference is an error, never a create - that is #33's job and it is conversational.
- Any file format other than the five-column CSV. No XLSX, no JSON, no column mapping UI, no delimiter detection.
- Partial import, resumable import, or per-row skip. The whole file validates or nothing is written.
- Undo, re-import, or import history beyond the minimal audit fact.
- Blob or file-system storage. The bounded upload lives in SQL for its ten minutes so there is exactly one durable store and one cleanup path.

---

## File responsibility map

### Domain (`src/MultiChannelAgent.Domain/`)

| File | Responsibility |
| --- | --- |
| `Inventories/ImportContract.cs` (create) | The file contract as data: `ImportColumn` (the five headers in their fixed order), `ImportLimits` (2 MiB, 5,000 source rows, 5,000 normalized entries, 500 reported errors), `ImportErrorCode` (the closed set of actionable errors), and `ImportFacts` mapping each code to its machine text. One vocabulary, so the parser, the merge, the service, and the client cannot drift. |
| `Inventories/CsvImportDocument.cs` (create) | The only thing in the system that understands CSV bytes: strict UTF-8 decoding, optional BOM, RFC 4180 quoting, CRLF/LF/CR records, the exact five-header check, and the per-record field split. Produces `CsvImportDocument` (header-validated records with their source line numbers) or a list of column errors. Knows nothing about Inventories, Units, or Quantity. |
| `Inventories/ImportRow.cs` (create) | One parsed, bounded, still-unresolved row: source line number, display name, normalized name, raw Unit term, raw Location name, Note, and exact `Quantity`. Plus `ImportRowError` - one actionable error at one line and one column. |
| `Inventories/ImportMergePlan.cs` (create) | The pure merge: groups resolved rows by Equivalent Stock key, decides Note compatibility, sums Quantity, enforces the normalized-entry bound, and reports conflicts as row errors. Reads and writes nothing. |
| `Inventories/ImportProposal.cs` (create) | The aggregate: `ImportProposalId`, `ImportProposalStatus`, the ten-minute single-use hashed token, the binding (`ParticipantId`, `InventoryId`, `FileDigest`), the exact normalized rows, the `EmptyStateVersion`, and `ExecutionOperationId`. |
| `Inventories/ImportOperationId.cs` (create) | The retry-stable ledger identity of one import execution, derived from the proposal, hashed from material shaped so it can never equal a `StockOperationId` or a `ReferenceOperationId`. |
| `Inventories/FileDigest.cs` (create) | SHA-256 of the exact uploaded bytes as 64 lowercase hex characters, with a strict parser. Mirrors `ConfirmationTokenHash`'s shape so a digest can never be confused with a token. |
| `Inventories/AuditFact.cs` (modify) | `AuditEventType` gains `StockImported`. No new denial vocabulary: denials keep using the shipped `AccessDenied`. |

### Application (`src/MultiChannelAgent.Application/`)

| File | Responsibility |
| --- | --- |
| `Inventories/IImportProposalStore.cs` (create) | The pending-import seam: store (superseding any previous pending one for this Participant and Inventory) with its raw upload, find pending by Participant and Inventory, find by identity, settle guarded, expire, and sweep. |
| `Inventories/IImportExecutionStore.cs` (create) | The one atomic writer: `ImportExecutionCommand`, `RecordedImport`, `ImportExecutionOutcome`, `ImportExecutionResult`, and the replay lookup. |
| `Inventories/IStockEmptyStateReader.cs` (create) | The single authorized read behind eligibility: whether an Inventory holds *any* Stock Entry, zero-quantity included. |
| `Inventories/ImportReferenceResolver.cs` (create) | Turns each parsed row's raw Unit term and Location name into resolved identities using the shipped active-only `IInventoryReferenceStore`, caching per distinct term so a 5,000-row file is a handful of lookups, and reporting unknown references as row errors with bounded suggestions from `IReferenceCatalogStore`. |
| `Inventories/InitialImportService.cs` (create) | Eligibility, validation, and preview. Authorizes Editor, refuses when the Inventory is not empty, drives the parser, the resolver, and the merge, and on success stores the proposal and hands back its one-time token and exact preview. Owns `ImportEligibilityView`, `ImportPreviewView`, `ImportPreviewRowView`, `ImportErrorView`, and `ImportValidationResult`. |
| `Inventories/ImportConfirmationService.cs` (create) | Confirms or rejects the one pending import proposal. Re-authorizes Editor, answers a replay from the ledger before anything else, verifies the token, and executes the stored rows - never reparsing. |
| `Inventories/ImportCleanupCoordinator.cs` (create) | Expires pending import proposals, discards their raw uploads, deletes settled ones past retention, and sweeps audit facts past their 90 days. Leased, bounded, and driveable one-shot from tests. |
| `Inventories/IInventoryAuditRetentionStore.cs` (create) | The bounded delete behind the 90-day audit retention the spec requires and nothing currently enforces. |

### Infrastructure (`src/MultiChannelAgent.Infrastructure/`)

| File | Responsibility |
| --- | --- |
| `Persistence/Entities/ImportProposalEntity.cs` (create) | The pending import: identity, token hash, binding, digest, serialized rows, empty-state version, status, created/expires/settled with their tick mirrors. |
| `Persistence/Entities/ImportUploadEntity.cs` (create) | The raw bytes for the ten-minute window, one row per proposal, cascade-deleted with it. |
| `Persistence/Entities/ImportOperationEntity.cs` (create) | The ledger header: operation identity, Inventory, proposal, importing Participant, created-entry count, applied-at. |
| `Persistence/Configurations/ImportProposalEntityConfiguration.cs` (create) | Bounds, the filtered one-pending-per-Participant-and-Inventory unique index, the token-hash unique index, and the expiry sweep index. |
| `Persistence/Configurations/ImportUploadEntityConfiguration.cs` (create) | Key on the proposal, single cascade path from the proposal only. |
| `Persistence/Configurations/ImportOperationEntityConfiguration.cs` (create) | Operation identity as the key and the unique proposal index that makes replay exact. |
| `Persistence/Migrations/*_AddInitialImport.cs` (generate) | One migration: the three tables and their indexes. Nothing else. |
| `Inventories/ImportProposalMapper.cs` (create) | Serializes and reads the exact normalized rows, versioned, refusing an unreadable shape. |
| `Inventories/SqlImportProposalStore.cs` (create) | Stores the proposal and its upload in one transaction, superseding any previous pending one; guarded settle; bounded expiry and retention sweeps. |
| `Inventories/SqlImportExecutionStore.cs` (create) | The one atomic writer, including the `Serializable` empty-state re-assertion, the reference locks, the bulk insert, the audit, the ledger, and the raw discard. |
| `Inventories/SqlStockEmptyStateReader.cs` (create) | `AnyStockAsync`, counting zero-quantity entries too. |
| `Inventories/SqlInventoryAuditRetentionStore.cs` (create) | The bounded audit delete. |
| `ServiceCollectionExtensions.cs` (modify) | Registers the new stores, services, and coordinator. |

### Host (`src/MultiChannelAgent.Host/`)

| File | Responsibility |
| --- | --- |
| `Endpoints/ImportEndpoints.cs` (create) | `GET /api/inventories/{id}/import`, `POST /api/inventories/{id}/import/validate` (multipart, bounded, CSRF), `POST /api/inventories/{id}/import/confirm`, `POST /api/inventories/{id}/import/reject`. Maps typed results to typed HTTP statuses and RFC 7807 problems. |
| `Workers/ImportCleanupWorker.cs` (create) | Periodically drives `ImportCleanupCoordinator.SweepAsync`. |
| `Program.cs` (modify) | Maps the endpoints, registers the worker, and sets the request body limit for the import route. |

### Web (`src/web/src/`)

| File | Responsibility |
| --- | --- |
| `importApi.ts` (create) | Typed fetches for eligibility, validate (multipart), confirm, and reject. |
| `InitialImport.tsx` (create) | The workflow: eligibility gate, file picker, error report, normalized preview, confirm and cancel. |
| `App.tsx` (modify) | Mounts `InitialImport` beside the workspaces and bumps the refetch token after a completed import. |

### Tests

| File | Responsibility |
| --- | --- |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/CsvImportDocumentTests.cs` (create) | Encoding, BOM, newlines, quoting, headers, field counts, and bounds. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportRowTests.cs` (create) | Per-field parsing, blanks, and length bounds. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportMergePlanTests.cs` (create) | Equivalence, Note compatibility, summing, and the normalized bound. |
| `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportProposalTests.cs` (create) | Binding, expiry, single use, and the ledger identity. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ImportReferenceResolverTests.cs` (create) | Active-only resolution, blank defaults, unknown references, suggestions. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/InitialImportServiceTests.cs` (create) | Role matrix, the empty-state gate, error aggregation, and the stored proposal. |
| `tests/MultiChannelAgent.Application.Tests/Inventories/ImportConfirmationServiceTests.cs` (create) | Confirm, reject, replay, expiry, and the no-reparse guarantee. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryImportProposalStore.cs` (create) | The proposal double. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryImportExecutionStore.cs` (create) | The atomic writer double. |
| `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockEmptyStateReader.cs` (create) | The empty-state double. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportRelationalModelTests.cs` (create) | Docker-free model assertions for the filtered unique index and the single cascade path. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreTests.cs` (create) | Atomicity, idempotency, the empty-state guard, the raw discard, and the audit. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreConcurrencyTests.cs` (create) | An import racing a Stock write, and an import racing a Retire of a Unit it references. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreChangeTrackerIsolationTests.cs` (create) | A failed import leaves nothing staged. |
| `tests/MultiChannelAgent.IntegrationTests/InitialImportScenario.cs` (create) | The whole workflow through the real HTTP application boundary. |
| `tests/MultiChannelAgent.IntegrationTests/InitialImportSqliteTests.cs` (create) | The Docker-free twin. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/InitialImportSqlScenarioTests.cs` (create) | The SQL Server-backed run of the same scenario. |
| `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportCleanupCoordinatorTests.cs` (create) | Expiry, raw discard, settled retention, and the 90-day audit sweep. |

---

## Design decisions, settled here so no task re-decides them

### 1. Initial Import is not a conversational tool, and does not use `ConfirmationProposal`

#26 says *"Provide one signed-in web workflow for Owner/Editor"* and lists Initial Import outside the tool contracts. It therefore has no tool name, no `TurnExecutionContext`, and no `IToolDispatcher` arm.

It also gets its own aggregate rather than reusing `ConfirmationProposal`, for three reasons that are properties of the shipped type rather than preferences:

- `ConfirmationProposal.MaxChanges` is **25**. An import carries up to 5,000 rows.
- Its pending slot is unique per `(ParticipantId, ChannelConversationId)`. An import belongs to a signed-in browser session and an Inventory, and must not evict a pending stock confirmation or be evicted by one.
- Its payload is `ProposedChange`, which carries expected versions of *existing* Stock Entries. An import has none by definition: the Inventory is empty.

What *is* reused is everything where the contract genuinely aligns: `ConfirmationToken` (issue, hash, match, well-formedness), the ten-minute lifetime constant, the guarded `Pending -> terminal` settle, `InventoryAuthorizationService` and its denial audits, `IInventoryReferenceStore`'s active-only resolution, `AssignedReferenceLocks` and the reference-then-proposal-then-stock order, `AbandonedWrites.AbandonAsync`, and the `Quantity`/`StockEntry`/`NameNormalization` rules.

### 2. The file contract, exactly

- **Encoding.** UTF-8 only, decoded with `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)`. Invalid byte sequences are one column-level error (`invalid_encoding`), never replacement characters, because a silently mangled name would import wrong data.
- **BOM.** A leading UTF-8 BOM (`EF BB BF`) is accepted and stripped. No other BOM is accepted - UTF-16 would fail the strict decode anyway, and saying so explicitly is what makes the failure legible.
- **Newlines.** `CRLF`, `LF`, and bare `CR` all end a record. RFC 4180 specifies CRLF; real spreadsheet exports use all three, and accepting them costs nothing because inside quotes a newline is literal data either way. A trailing newline at end of file does not create an empty final row.
- **Quoting.** A field may be wrapped in `"`. Inside a quoted field, `""` is one literal `"`, and `,`/newlines are literal. A quote appearing inside an unquoted field is literal. An unterminated quoted field at end of file is `unterminated_quote`. A quoted field followed by anything other than `,` or a record end is `malformed_quote`.
- **Delimiter.** Comma only. No detection, no semicolon, no tab - a detected delimiter is a guess, and this file decides how much stock exists.
- **Headers.** Exactly five, in exactly this order: `Name`, `Quantity`, `Unit`, `Location`, `Note`. Compared after trimming surrounding whitespace, case-insensitively (`OrdinalIgnoreCase`), because spreadsheet software re-cases headers freely. Order is fixed rather than mapped: #26 says *"with exactly `Name`, `Quantity`, `Unit`, `Location`, and `Note`"*, and a fixed order removes a whole class of "which column was that" mistakes. A header that is not one of the five is `unknown_column`; the same header twice is `duplicate_column`; fewer or more than five is `wrong_column_count`. All header errors are reported together and no row is parsed, because row errors reported against misaligned columns would be noise.
- **Field count.** Every record must have exactly five fields. Fewer is `too_few_fields`; more is `too_many_fields`. Both name the line.
- **Required.** `Name` and `Quantity` must be non-blank. Blank `Unit` means the reserved `each` Unit; blank `Location` means unlocated; blank `Note` means no Note. "Blank" means empty or whitespace-only after unquoting.

### 3. Limits, and where each is enforced

| Limit | Value | Enforced |
| --- | --- | --- |
| Upload bytes | 2 MiB (`2 * 1024 * 1024`) | At the HTTP boundary by the file part's own length, under a per-endpoint body-size limit of the file bound plus 64 KiB of transport framing, **and** re-checked in `CsvImportDocument.Read` so the Domain rule holds regardless of transport |
| Source rows | 5,000 data records, header excluded | `CsvImportDocument.Read` |
| Normalized Stock Entries | 5,000 after merge | `ImportMergePlan.Create` |
| Reported errors | 500, with an exact `omittedErrorCount` | `ImportValidationResult` |
| Name | `StockEntry.MaxNameLength` (200) | `ImportRow.TryCreate` |
| Note | `StockEntry.MaxNoteLength` (500) | `ImportRow.TryCreate` |
| Unit term | `Unit.MaxNameLength` (100) | `ImportRow.TryCreate` |
| Location name | `Location.MaxNameLength` (200) | `ImportRow.TryCreate` |

The error bound deserves its own note. #34 requires *"all actionable row/column errors are returned together"*, and a 5,000-row file of nonsense would otherwise produce a 5,000-item response that is neither reviewable nor cheap. Reporting the first 500 in source order plus the exact number omitted keeps the promise that matters - fix the file once, not row by row - while staying bounded. The count is exact, not "many", so nobody is misled about how much work remains.

### 4. Quantity

Parsed with the shipped `Quantity.TryParseInvariant`, so import and the conversational tools agree by construction: invariant culture, optional leading sign, decimal point only, no thousands separators, no exponent, no currency. Negative is `invalid_quantity` because `Quantity.Create` refuses it. The bounds are the shipped ones - `MaxIntegerDigits` 18 and `MaxScale` 10.

Merging sums with `Quantity.TryAdd`, which enforces the same bounds; a sum that overflows them is `quantity_overflow` reported against the *first* line of the merged group, so the message points at something the Participant can find.

Zero is allowed. A zero-quantity row is a real Stock Entry that simply has none on hand, and #34's own eligibility rule ("including zero-quantity entries") exists precisely because zero-quantity entries are entries.

### 5. Equivalent Stock and Note compatibility

Two rows are equivalent when their **normalized name**, resolved **Unit**, and resolved **optional Location** are equal - the shipped `StockEntry.IsEquivalentTo` predicate, and the same key the database's uniqueness index enforces. Notes deliberately do not participate, exactly as `CONTEXT.md` says.

Notes are compared **ordinally and case-sensitively, after trimming**. Within one equivalence group:

- Every blank Note is compatible with anything, and contributes nothing.
- If the group's non-blank Notes are all the same string, they are compatible and that string survives.
- If the group has two different non-blank Notes, that is `conflicting_notes`, reported against every line in the group after the first, naming the `Note` column.

Case-sensitivity is the deliberate choice: a Note is free text a person wrote to record a distinction, so folding `Blue box` into `blue box` would silently erase one. Refusing and asking is the safe direction, and it is stated in the error message.

The merged entry keeps the **first** line's display name and the first line's resolved references; the surviving Note is the single distinct non-blank one or null. The preview reports the merged result and every source line that contributed to it, so a Participant can see exactly which rows collapsed.

### 6. References are resolved, never created

Each distinct raw Unit term and Location name is resolved once through `IInventoryReferenceStore`, which is **active-only** since #33 - a retired Unit, a retired alias, and a retired Location all resolve to nothing, indistinguishable from one that never existed. A blank Unit resolves the reserved term `each`.

An unresolved reference is `unknown_unit` or `unknown_location`, reported against the row and column, and carries up to `IReferenceCatalogStore.MaxSuggestions` (5) bounded deterministic suggestions from the shipped `SuggestAsync`. Nothing is ever created: #26 says *"unknown Units and Locations reported instead of created implicitly"*, and creating them here would be an unreviewed reference-administration act by a workflow that never asked for one.

Resolution is cached per distinct term for the life of one validation, so a 5,000-row file with three Units performs three lookups. The cache lives inside one `ImportReferenceResolver` instance and never outlives the request.

### 7. Eligibility, the empty-state version, and phantom protection

Import is offered only when **`Owner` or `Editor`** and the Inventory holds **no Stock Entry at all**, zero-quantity included. `IStockEmptyStateReader.AnyStockAsync` is a plain `AnyAsync` over `StockEntries` with no quantity filter, which is exactly what "including zero-quantity entries" means.

The empty-state version is `EmptyStateVersion.Empty`, whose only legal content is *"this Inventory contained zero Stock Entries"*. It is deliberately **not** a row version, because there is no row to version: the thing being asserted is an absence, and an absence is a range, not a row. It is therefore enforced the way #33 enforces a Retire's absence-of-referencing-stock - the confirm transaction runs at `IsolationLevel.Serializable` and re-asserts `!await db.StockEntries.AnyAsync(e => e.InventoryId == id)` inside it, so the range lock makes a concurrent insert either lose or serialize before it. A preview decided against an empty Inventory can therefore never commit into a non-empty one, and the failure is a typed conflict, never a partial import.

### 8. The proposal, its binding, and its lifetime

`ImportProposal` carries the SHA-256 hash of a freshly issued `ConfirmationToken`, never the token; the plaintext exists only in the validate response. It is bound to `ParticipantId`, `InventoryId`, and `FileDigest`, and `BelongsTo(participantId, inventoryId)` is checked before anything else, so a token can never be replayed into another Participant's session or another Inventory.

Lifetime is ten minutes, from `ConfirmationProposal.LifetimeMinutes` so the two workflows cannot drift apart. Reading an expired proposal settles it `Expired` and answers as if it were not there.

At most one pending import proposal exists per `(ParticipantId, InventoryId)`, enforced by a filtered unique index rather than by agreement. Validating again supersedes the previous one - a Participant who fixes their CSV and re-uploads has replaced their own pending work, which is the only reasonable reading, and the superseded row's raw upload is deleted with it.

The digest is SHA-256 over the exact uploaded bytes, before BOM stripping, as 64 lowercase hex characters. It is recorded so the confirm response can state which file was applied and so an operator can tell two uploads apart in the ledger without retaining either.

### 9. The raw CSV lifecycle

The uploaded bytes are stored in `ImportUploads`, one row per pending proposal, cascade-deleted with it. They are kept for the ten-minute window for two concrete reasons: the error report cites source line numbers and a reload of the preview page must be able to show them again without a re-upload, and having the bytes in SQL means "discard the raw CSV" is one durable, testable fact rather than a claim about process memory.

They are deleted in the very transaction that confirms the import, in the guarded settle that rejects it, and by `ImportCleanupCoordinator` when a proposal expires or is superseded. After any of those, only the digest and the minimal audit fact remain. Nothing else in the system ever reads `ImportUploads`.

### 10. Confirmation: atomic, idempotent, no reparse

`ImportConfirmationService.ConfirmAsync` asks the ledger first, then verifies the token, then hands the **stored rows** to `IImportExecutionStore`. It never touches `ImportUploads` and never parses anything - #26: *"Confirm atomically and idempotently"* and #34: *"without reparsing"*. What the Participant previewed is what commits.

`SqlImportExecutionStore.ApplyAsync` runs one `Serializable` transaction in the shared order:

1. **References.** `AssignedReferenceLocks.TryHoldActiveAsync` over every distinct Unit and Location the stored rows reference, Units before Locations, ordinal by identity - so a Retire cannot slip underneath an import the way #33 proved it could underneath a stock write.
2. **Proposal.** The guarded `Pending -> Confirmed` update. Zero rows affected means somebody else already settled it: conflict, nothing applied.
3. **Empty state.** The re-assertion described in decision 7.
4. **Entries.** Every Stock Entry inserted through the `StockEntry.Create` domain factory, so persistence never sees a name or Note the domain would refuse.
5. **Ledger and audit.** One `ImportOperationEntity` and exactly one `AuditFact` (`StockImported` / `Import:Completed`).
6. **Raw discard.** The `ImportUploads` row deleted.

Any failure abandons the transaction through `AbandonedWrites.AbandonAsync`, which clears the `ChangeTracker` as well as rolling back - the `DbContext` serves a whole batch of requests. Replay is by `ImportOperationId.DeriveForProposal(proposalId)`, uniquely indexed on `(InventoryId, ProposalId)`, so a re-driven confirm re-reports instead of importing twice.

### 11. Audits and the 90 days

One fact per completed import: `AuditEventType.StockImported`, `ActorKind.Participant`, the actor, the Inventory, outcome code `Import:Completed`, and the instant. It carries **no** file name, no digest, no row count, no names - the same minimality every other audit in this codebase keeps.

Denials reuse the shipped `AccessDenied` with `Denied:NotAMember` or `Denied:InsufficientRole`, written for free by `InventoryAuthorizationService`. No new denial vocabulary is invented, because #26 specifies none.

`AuditFact.RetentionDays` is already 90, but **nothing currently enforces it** - the shipped cleanup workers cover confirmation proposals and outcome payloads only. #34 requires that *"only the specified 90-day semantic facts remain"*, so `ImportCleanupCoordinator` also sweeps `InventoryAudits` older than `AuditFact.RetentionDays`, bounded per pass and under its own lease. That closes a gap the whole system has, not just import.

### 12. Typed HTTP statuses

| Situation | Status | Body |
| --- | --- | --- |
| Eligibility read | `200` | `{ eligible, reason }` |
| Validation succeeded | `200` | preview + token |
| Validation found errors | `400` | RFC 7807 with `errors[]`, `omittedErrorCount` |
| Upload larger than 2 MiB | `413` | RFC 7807 naming the limit |
| Missing or non-CSV part | `400` | RFC 7807 naming `file` |
| Not a member, or not Owner/Editor | `404` | empty - identical either way, so membership is never disclosed |
| Inventory already has Stock | `409` | RFC 7807, code `inventory_not_empty` |
| Confirm with unknown, expired, or foreign token | `404` | RFC 7807, code `proposal_not_found` |
| Confirm when state changed | `409` | RFC 7807, code `state_changed` |
| Confirm that already ran | `200` | the recorded summary |
| Import applied | `200` | `{ createdEntryCount, digest }` |

`404` for both "no such Inventory" and "not authorized" is the shipped non-disclosure rule from `StockEndpoints` and `ReferenceEndpoints`, and import follows it exactly.

### 13. Upload transport

`POST /api/inventories/{id}/import/validate` accepts `multipart/form-data` with exactly one part named `file`. The endpoint sets a per-route request body limit of 2 MiB + 64 KiB, so an oversized upload is refused by the server before it is buffered, and `CsvImportDocument.Read` re-checks the decoded byte count so the Domain rule stands on its own. Route-local form options set both the multipart body limit and the in-memory buffering threshold to that same request bound. ASP.NET Core therefore either buffers the accepted request in memory or refuses it before its file section can spill to a temporary file; nothing is written to disk.

The 64 KiB is transport framing margin, not import capacity. Multipart part headers and boundaries cost a few hundred bytes, but a body forwarded through an intermediary that re-chunks it arrives framed in ways this route never sees the shape of, and a margin measured in single kibibytes would turn a perfectly valid maximum-sized import into a 413 somewhere in the middle. What may be imported stays exactly 2 MiB, checked independently against the file part's own length, so raising the framing allowance never raises the file bound.

A body the route cannot read as multipart at all - no boundary, or a declared boundary the body ends before reaching, which is what a connection cut mid-upload leaves behind - is malformed input rather than a server fault, and is answered exactly like a request with no file part: 400 naming `file`. ASP.NET Core reports truncation as an `IOException` rather than an `InvalidDataException`, so both are caught; the server's own refusal is not, because `BadHttpRequestException` is an `IOException` too and a body over the route's bound has to stay the 413 the server made it.

CSRF is the shipped `AntiforgeryEndpointFilter` on all three mutating routes, exactly as `InventoryEndpoints` and `TurnEndpoints` use it. All four routes require `AuthorizationPolicies.ActiveTenantMember`.

---

## Task 1: Name the import contract once

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ImportContract.cs`
- Create: `src/MultiChannelAgent.Domain/Inventories/FileDigest.cs`
- Modify: `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportContractTests.cs`

Why: the five headers, the limits, the closed set of error codes, and the shape of one reported error are quoted by the parser, the merge, the service, the endpoint, and the React client. Written once they cannot drift; written five times they will.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportContractTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportContractTests
{
    [Fact]
    public void The_file_contract_is_exactly_five_columns_in_one_fixed_order() =>
        Assert.Equal(["Name", "Quantity", "Unit", "Location", "Note"], ImportContract.Headers);

    [Fact]
    public void Every_bound_the_specification_states_is_stated_here()
    {
        Assert.Equal(2 * 1024 * 1024, ImportContract.MaxUploadBytes);
        Assert.Equal(5_000, ImportContract.MaxSourceRows);
        Assert.Equal(5_000, ImportContract.MaxNormalizedEntries);
        Assert.Equal(500, ImportContract.MaxReportedErrors);
    }

    [Theory]
    [InlineData(ImportErrorCode.UnknownColumn, "unknown_column")]
    [InlineData(ImportErrorCode.DuplicateColumn, "duplicate_column")]
    [InlineData(ImportErrorCode.WrongColumnCount, "wrong_column_count")]
    [InlineData(ImportErrorCode.InvalidEncoding, "invalid_encoding")]
    [InlineData(ImportErrorCode.UnterminatedQuote, "unterminated_quote")]
    [InlineData(ImportErrorCode.MalformedQuote, "malformed_quote")]
    [InlineData(ImportErrorCode.TooFewFields, "too_few_fields")]
    [InlineData(ImportErrorCode.TooManyFields, "too_many_fields")]
    [InlineData(ImportErrorCode.MissingName, "missing_name")]
    [InlineData(ImportErrorCode.MissingQuantity, "missing_quantity")]
    [InlineData(ImportErrorCode.InvalidQuantity, "invalid_quantity")]
    [InlineData(ImportErrorCode.QuantityOverflow, "quantity_overflow")]
    [InlineData(ImportErrorCode.NameTooLong, "name_too_long")]
    [InlineData(ImportErrorCode.NoteTooLong, "note_too_long")]
    [InlineData(ImportErrorCode.UnitTooLong, "unit_too_long")]
    [InlineData(ImportErrorCode.LocationTooLong, "location_too_long")]
    [InlineData(ImportErrorCode.UnknownUnit, "unknown_unit")]
    [InlineData(ImportErrorCode.UnknownLocation, "unknown_location")]
    [InlineData(ImportErrorCode.ConflictingNotes, "conflicting_notes")]
    [InlineData(ImportErrorCode.FileTooLarge, "file_too_large")]
    [InlineData(ImportErrorCode.TooManyRows, "too_many_rows")]
    [InlineData(ImportErrorCode.TooManyEntries, "too_many_entries")]
    [InlineData(ImportErrorCode.EmptyFile, "empty_file")]
    public void Every_error_has_stable_machine_text_that_round_trips(ImportErrorCode code, string text)
    {
        Assert.Equal(text, ImportFacts.ToMachineText(code));
        Assert.True(ImportFacts.TryParse(text, out var parsed));
        Assert.Equal(code, parsed);
    }

    [Fact]
    public void Machine_text_is_exact_and_case_sensitive()
    {
        Assert.False(ImportFacts.TryParse("Unknown_Column", out _));
        Assert.False(ImportFacts.TryParse("unknown", out _));
        Assert.False(ImportFacts.TryParse(null, out _));
    }

    [Fact]
    public void A_digest_is_sixty_four_lowercase_hexadecimal_characters()
    {
        var digest = FileDigest.Of([1, 2, 3]);

        Assert.Equal(64, digest.Value.Length);
        Assert.Equal(digest.Value, digest.Value.ToLowerInvariant());
        Assert.Equal(digest, FileDigest.Of([1, 2, 3]));
        Assert.NotEqual(digest, FileDigest.Of([1, 2, 4]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ZZ00000000000000000000000000000000000000000000000000000000000000")]
    public void A_malformed_digest_is_refused(string value) => Assert.False(FileDigest.TryParse(value, out _));

    [Fact]
    public void A_well_formed_digest_round_trips_through_its_text()
    {
        var digest = FileDigest.Of("Name,Quantity,Unit,Location,Note\n"u8.ToArray());

        Assert.True(FileDigest.TryParse(digest.Value, out var parsed));
        Assert.Equal(digest, parsed);
    }

    [Fact]
    public void An_error_names_a_line_and_optionally_a_column()
    {
        var rowError = new ImportRowError(ImportErrorCode.MissingName, 7, ImportContract.NameColumn);
        var fileError = new ImportRowError(ImportErrorCode.FileTooLarge, 0, null);

        Assert.Equal(7, rowError.LineNumber);
        Assert.Equal(0, rowError.ColumnIndex);
        Assert.Equal(0, fileError.LineNumber);
        Assert.Null(fileError.ColumnIndex);
    }

    [Fact]
    public void An_import_audits_one_minimal_fact() =>
        Assert.Equal("Import:Completed", ImportFacts.CompletedOutcomeCode);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportContractTests"`
Expected: FAIL to compile - `ImportContract`, `ImportErrorCode`, `ImportFacts`, and `FileDigest` do not exist.

- [ ] **Step 3: Add the contract**

Create `src/MultiChannelAgent.Domain/Inventories/ImportContract.cs`:

```csharp
namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The Initial Import file contract, as data. Every bound and every column name the specification
/// states lives here exactly once, because the parser, the merge, the application service, the HTTP
/// endpoint, and the web client all quote them.
/// </summary>
public static class ImportContract
{
    /// <summary>The five headers, in the one fixed order a file must present them in.</summary>
    public static readonly IReadOnlyList<string> Headers = ["Name", "Quantity", "Unit", "Location", "Note"];

    /// <summary>Two mebibytes of uploaded bytes.</summary>
    public const int MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>Data records, header excluded.</summary>
    public const int MaxSourceRows = 5_000;

    /// <summary>Stock Entries after equivalent rows have been merged.</summary>
    public const int MaxNormalizedEntries = 5_000;

    /// <summary>
    /// How many errors one answer carries. The promise is that a Participant can fix the file once,
    /// not that every one of five thousand broken rows is enumerated: beyond this the exact number
    /// omitted is reported instead, so nobody is misled about how much is left.
    /// </summary>
    public const int MaxReportedErrors = 500;

    /// <summary>The zero-based index of each column, so a row error can name the column it is about.</summary>
    public const int NameColumn = 0;
    public const int QuantityColumn = 1;
    public const int UnitColumn = 2;
    public const int LocationColumn = 3;
    public const int NoteColumn = 4;
}

/// <summary>
/// Every actionable thing that can be wrong with an import, as a closed set. There is deliberately no
/// free-text error: a Participant fixes a file by knowing which line, which column, and which rule.
/// </summary>
public enum ImportErrorCode
{
    /// <summary>A header that is not one of the five.</summary>
    UnknownColumn,

    /// <summary>The same header twice.</summary>
    DuplicateColumn,

    /// <summary>Fewer or more than five headers.</summary>
    WrongColumnCount,

    /// <summary>The bytes are not valid UTF-8.</summary>
    InvalidEncoding,

    /// <summary>A quoted field never closed before end of file.</summary>
    UnterminatedQuote,

    /// <summary>A closing quote followed by something other than a comma or a record end.</summary>
    MalformedQuote,

    TooFewFields,
    TooManyFields,
    MissingName,
    MissingQuantity,

    /// <summary>Not an invariant non-negative decimal within the shipped Quantity bounds.</summary>
    InvalidQuantity,

    /// <summary>Summing an equivalent group left the shipped Quantity bounds.</summary>
    QuantityOverflow,

    NameTooLong,
    NoteTooLong,
    UnitTooLong,
    LocationTooLong,

    /// <summary>No active Unit here answers to that term.</summary>
    UnknownUnit,

    /// <summary>No active Location here carries that name.</summary>
    UnknownLocation,

    /// <summary>Equivalent rows carried two different non-blank Notes.</summary>
    ConflictingNotes,

    FileTooLarge,
    TooManyRows,
    TooManyEntries,

    /// <summary>No data records at all - a header alone imports nothing and is almost certainly a mistake.</summary>
    EmptyFile,
}

/// <summary>
/// One actionable error, at one place. <see cref="LineNumber"/> is the 1-based source line - the
/// header is line 1 - and is 0 for a whole-file failure that belongs to no line.
/// <see cref="ColumnIndex"/> is the zero-based column from <see cref="ImportContract"/>, or null when
/// the error is about the record rather than one field.
///
/// Deliberately free of prose: the client renders a message from the code, so the same failure reads
/// the same way everywhere and nothing here has to be translated or kept in step with a UI string.
/// </summary>
public sealed record ImportRowError(ImportErrorCode Code, int LineNumber, int? ColumnIndex);

/// <summary>The one mapping from an import error to its machine text, and the one audit outcome code an import writes.</summary>
public static class ImportFacts
{
    /// <summary>The coarse outcome code a completed import is audited under. Never a file name, a digest, or a count.</summary>
    public const string CompletedOutcomeCode = "Import:Completed";

    public static string ToMachineText(ImportErrorCode code) => code switch
    {
        ImportErrorCode.UnknownColumn => "unknown_column",
        ImportErrorCode.DuplicateColumn => "duplicate_column",
        ImportErrorCode.WrongColumnCount => "wrong_column_count",
        ImportErrorCode.InvalidEncoding => "invalid_encoding",
        ImportErrorCode.UnterminatedQuote => "unterminated_quote",
        ImportErrorCode.MalformedQuote => "malformed_quote",
        ImportErrorCode.TooFewFields => "too_few_fields",
        ImportErrorCode.TooManyFields => "too_many_fields",
        ImportErrorCode.MissingName => "missing_name",
        ImportErrorCode.MissingQuantity => "missing_quantity",
        ImportErrorCode.InvalidQuantity => "invalid_quantity",
        ImportErrorCode.QuantityOverflow => "quantity_overflow",
        ImportErrorCode.NameTooLong => "name_too_long",
        ImportErrorCode.NoteTooLong => "note_too_long",
        ImportErrorCode.UnitTooLong => "unit_too_long",
        ImportErrorCode.LocationTooLong => "location_too_long",
        ImportErrorCode.UnknownUnit => "unknown_unit",
        ImportErrorCode.UnknownLocation => "unknown_location",
        ImportErrorCode.ConflictingNotes => "conflicting_notes",
        ImportErrorCode.FileTooLarge => "file_too_large",
        ImportErrorCode.TooManyRows => "too_many_rows",
        ImportErrorCode.TooManyEntries => "too_many_entries",
        ImportErrorCode.EmptyFile => "empty_file",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unhandled import error code."),
    };

    /// <summary>Reads machine text back. Exact and case-sensitive: text spelled differently is unreadable, not a near miss.</summary>
    public static bool TryParse(string? text, out ImportErrorCode code)
    {
        foreach (var candidate in Enum.GetValues<ImportErrorCode>())
        {
            if (string.Equals(ToMachineText(candidate), text, StringComparison.Ordinal))
            {
                code = candidate;
                return true;
            }
        }

        code = default;
        return false;
    }
}
```

- [ ] **Step 4: Add the digest**

Create `src/MultiChannelAgent.Domain/Inventories/FileDigest.cs`:

```csharp
using System.Security.Cryptography;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The SHA-256 of an uploaded file's exact bytes, as 64 lowercase hexadecimal characters.
///
/// It binds a stored proposal to the file that produced it, so a confirmation can state which file it
/// applied without the system retaining that file - and so two uploads are distinguishable in the
/// ledger by something that reveals nothing about their contents. It is computed over the bytes as
/// received, before any BOM is stripped, because the digest identifies the upload rather than the
/// text the parser derived from it.
///
/// A distinct type from <see cref="ConfirmationTokenHash"/> on purpose: both are 64 hex characters,
/// and one is a secret while the other is not.
/// </summary>
public readonly record struct FileDigest
{
    private FileDigest(string value) => Value = value;

    public string Value { get; }

    public static FileDigest Of(ReadOnlySpan<byte> content) => new(Convert.ToHexStringLower(SHA256.HashData(content)));

    public static bool TryParse(string? value, out FileDigest digest)
    {
        digest = default;

        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        digest = new FileDigest(value);
        return true;
    }

    public override string ToString() => Value;
}
```

- [ ] **Step 5: Add the audit event**

In `src/MultiChannelAgent.Domain/Inventories/AuditFact.cs`, add one member to `AuditEventType`, immediately after `LocationRetired`:

```csharp
    /// <summary>An empty Inventory's starting Stock Entries were created by a confirmed Initial Import. The fact records that it happened, never what was imported.</summary>
    StockImported,
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportContractTests"`
Expected: PASS - every case in the class.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings. `TreatWarningsAsErrors` is on, so a switch that is no longer exhaustive over `AuditEventType` surfaces here.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ImportContract.cs \
        src/MultiChannelAgent.Domain/Inventories/FileDigest.cs \
        src/MultiChannelAgent.Domain/Inventories/AuditFact.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ImportContractTests.cs
git commit -m "feat(inventories): name the Initial Import file contract once for #34"
```

---

## Task 2: Read the CSV, and nothing else

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/CsvImportDocument.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/CsvImportDocumentTests.cs`

Why: exactly one type in the system understands bytes, quoting, and newlines, and it understands nothing about Inventories. That is what lets every encoding and quoting rule be decided and tested on its own, and what stops CSV concerns leaking into the merge or the service.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/CsvImportDocumentTests.cs`:

```csharp
using System.Text;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class CsvImportDocumentTests
{
    private const string Header = "Name,Quantity,Unit,Location,Note";

    private static CsvImportReadResult Read(string text) => CsvImportDocument.Read(Encoding.UTF8.GetBytes(text));

    private static CsvImportReadResult ReadBytes(byte[] bytes) => CsvImportDocument.Read(bytes);

    [Fact]
    public void A_well_formed_file_yields_one_record_per_data_line_with_its_source_line_number()
    {
        var result = Read($"{Header}\r\nSteel Bolts,10,each,Shelf A,Blue box\r\nBrass Rivets,2,,,\r\n");

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Document!.Records.Count);
        Assert.Equal(2, result.Document.Records[0].LineNumber);
        Assert.Equal(["Steel Bolts", "10", "each", "Shelf A", "Blue box"], result.Document.Records[0].Fields);
        Assert.Equal(3, result.Document.Records[1].LineNumber);
        Assert.Equal(["Brass Rivets", "2", "", "", ""], result.Document.Records[1].Fields);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void Every_newline_a_spreadsheet_might_write_ends_a_record(string newline)
    {
        var result = Read($"{Header}{newline}Steel Bolts,10,each,Shelf A,{newline}");

        Assert.Empty(result.Errors);
        Assert.Single(result.Document!.Records);
    }

    [Fact]
    public void A_trailing_newline_does_not_invent_an_empty_final_record()
    {
        Assert.Single(Read($"{Header}\nSteel Bolts,10,each,,\n").Document!.Records);
        Assert.Single(Read($"{Header}\nSteel Bolts,10,each,,").Document!.Records);
    }

    [Fact]
    public void A_leading_byte_order_mark_is_accepted_and_stripped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes($"{Header}\nA,1,,,")).ToArray();

        var result = ReadBytes(bytes);

        Assert.Empty(result.Errors);
        Assert.Single(result.Document!.Records);
    }

    [Fact]
    public void Bytes_that_are_not_valid_UTF8_are_one_legible_error_rather_than_mangled_text()
    {
        var result = ReadBytes([0xFF, 0xFE, 0x41, 0x00]);

        Assert.Equal(ImportErrorCode.InvalidEncoding, Assert.Single(result.Errors).Code);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_quoted_field_may_carry_commas_newlines_and_escaped_quotes()
    {
        var result = Read($"{Header}\n\"Bolts, 5mm\",10,each,,\"He said \"\"hi\"\"\nsecond line\"\n");

        Assert.Empty(result.Errors);
        var record = Assert.Single(result.Document!.Records);
        Assert.Equal("Bolts, 5mm", record.Fields[0]);
        Assert.Equal("He said \"hi\"\nsecond line", record.Fields[4]);
    }

    [Fact]
    public void A_quote_inside_an_unquoted_field_is_literal_data()
    {
        var result = Read($"{Header}\n5\" pipe,10,each,,\n");

        Assert.Empty(result.Errors);
        Assert.Equal("5\" pipe", result.Document!.Records[0].Fields[0]);
    }

    [Fact]
    public void A_quoted_field_that_never_closes_is_refused()
    {
        var result = Read($"{Header}\n\"Steel Bolts,10,each,,\n");

        Assert.Equal(ImportErrorCode.UnterminatedQuote, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void A_closing_quote_followed_by_stray_text_is_refused()
    {
        var result = Read($"{Header}\n\"Steel\" Bolts,10,each,,\n");

        Assert.Equal(ImportErrorCode.MalformedQuote, Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData("name,quantity,unit,location,note")]
    [InlineData("NAME,QUANTITY,UNIT,LOCATION,NOTE")]
    [InlineData(" Name , Quantity , Unit , Location , Note ")]
    public void Headers_are_matched_without_case_or_surrounding_whitespace(string header)
    {
        var result = Read($"{header}\nA,1,,,\n");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_header_that_is_not_one_of_the_five_is_named()
    {
        var result = Read("Name,Quantity,Unit,Location,Colour\nA,1,,,\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownColumn, error.Code);
        Assert.Equal(1, error.LineNumber);
        Assert.Equal(4, error.ColumnIndex);
    }

    [Fact]
    public void A_repeated_header_is_refused()
    {
        var result = Read("Name,Quantity,Unit,Note,Note\nA,1,,,\n");

        Assert.Contains(result.Errors, error => error.Code == ImportErrorCode.DuplicateColumn);
    }

    [Theory]
    [InlineData("Name,Quantity,Unit,Location")]
    [InlineData("Name,Quantity,Unit,Location,Note,Extra")]
    public void A_file_without_exactly_five_headers_is_refused(string header)
    {
        var result = Read($"{header}\nA,1,,,\n");

        Assert.Equal(ImportErrorCode.WrongColumnCount, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void A_header_failure_stops_before_a_single_row_is_read()
    {
        var result = Read("Name,Quantity,Unit,Location,Colour\n,,,,\n,,,,\n");

        Assert.Single(result.Errors);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_row_with_the_wrong_number_of_fields_names_its_line()
    {
        var result = Read($"{Header}\nA,1,,\nB,1,,,,\n");

        Assert.Equal(2, result.Errors.Count);
        Assert.Equal(ImportErrorCode.TooFewFields, result.Errors[0].Code);
        Assert.Equal(2, result.Errors[0].LineNumber);
        Assert.Equal(ImportErrorCode.TooManyFields, result.Errors[1].Code);
        Assert.Equal(3, result.Errors[1].LineNumber);
    }

    [Fact]
    public void A_file_with_only_a_header_imports_nothing_and_says_so()
    {
        Assert.Equal(ImportErrorCode.EmptyFile, Assert.Single(Read($"{Header}\n").Errors).Code);
        Assert.Equal(ImportErrorCode.EmptyFile, Assert.Single(Read(string.Empty).Errors).Code);
    }

    [Fact]
    public void A_file_beyond_the_upload_bound_is_refused_by_the_domain_too()
    {
        var oversized = new byte[ImportContract.MaxUploadBytes + 1];

        Assert.Equal(ImportErrorCode.FileTooLarge, Assert.Single(ReadBytes(oversized).Errors).Code);
    }

    [Fact]
    public void A_file_beyond_the_source_row_bound_is_refused()
    {
        var builder = new StringBuilder(Header).Append('\n');
        for (var row = 0; row < ImportContract.MaxSourceRows + 1; row++)
        {
            builder.Append("A,1,,,\n");
        }

        Assert.Equal(ImportErrorCode.TooManyRows, Assert.Single(Read(builder.ToString()).Errors).Code);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~CsvImportDocumentTests"`
Expected: FAIL to compile - `CsvImportDocument`, `CsvImportReadResult`, and `CsvImportRecord` do not exist.

- [ ] **Step 3: Write the reader**

Create `src/MultiChannelAgent.Domain/Inventories/CsvImportDocument.cs`:

```csharp
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>One data record, with the source line it came from so every later error can point at it.</summary>
public sealed record CsvImportRecord(int LineNumber, IReadOnlyList<string> Fields);

/// <summary>A header-validated file: the records, in file order.</summary>
public sealed record CsvImportDocumentContent(IReadOnlyList<CsvImportRecord> Records);

/// <summary>
/// The outcome of reading bytes. Exactly one of the two is present: a document when the file's
/// envelope is sound, or the errors that stopped it. Row-level meaning is somebody else's job.
/// </summary>
public sealed record CsvImportReadResult(CsvImportDocumentContent? Document, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// The only thing in this system that understands CSV bytes.
///
/// It decodes strict UTF-8 (a BOM is accepted and stripped), splits records on CRLF, LF, or bare CR,
/// honours RFC 4180 quoting, checks that the five headers are present in their fixed order, and
/// splits each record into exactly five fields. It knows nothing about Inventories, Units, Locations,
/// or Quantity - which is what lets every encoding and quoting rule be reasoned about on its own.
///
/// Failures of the file's envelope - encoding, quoting, headers, bounds - stop the read, because rows
/// interpreted against a misaligned or unreadable file would be noise rather than help. Failures of a
/// single record's shape are collected and the read continues, because those are exactly the errors a
/// Participant wants to see all at once.
/// </summary>
public static class CsvImportDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static CsvImportReadResult Read(ReadOnlySpan<byte> content)
    {
        if (content.Length > ImportContract.MaxUploadBytes)
        {
            return Failed(ImportErrorCode.FileTooLarge, lineNumber: 0, columnIndex: null);
        }

        // The digest is taken over the bytes as received; the BOM is only in the way of the text.
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            content = content[3..];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return Failed(ImportErrorCode.InvalidEncoding, lineNumber: 0, columnIndex: null);
        }

        if (!TrySplitRecords(text, out var rawRecords, out var envelopeError))
        {
            return new CsvImportReadResult(null, [envelopeError!]);
        }

        if (rawRecords.Count == 0)
        {
            return Failed(ImportErrorCode.EmptyFile, lineNumber: 0, columnIndex: null);
        }

        var headerErrors = ValidateHeader(rawRecords[0]);
        if (headerErrors.Count > 0)
        {
            return new CsvImportReadResult(null, headerErrors);
        }

        if (rawRecords.Count == 1)
        {
            return Failed(ImportErrorCode.EmptyFile, lineNumber: 0, columnIndex: null);
        }

        if (rawRecords.Count - 1 > ImportContract.MaxSourceRows)
        {
            return Failed(ImportErrorCode.TooManyRows, lineNumber: 0, columnIndex: null);
        }

        var records = new List<CsvImportRecord>(rawRecords.Count - 1);
        var errors = new List<ImportRowError>();

        foreach (var raw in rawRecords.Skip(1))
        {
            if (raw.Fields.Count < ImportContract.Headers.Count)
            {
                errors.Add(new ImportRowError(ImportErrorCode.TooFewFields, raw.LineNumber, null));
                continue;
            }

            if (raw.Fields.Count > ImportContract.Headers.Count)
            {
                errors.Add(new ImportRowError(ImportErrorCode.TooManyFields, raw.LineNumber, null));
                continue;
            }

            records.Add(new CsvImportRecord(raw.LineNumber, raw.Fields));
        }

        return new CsvImportReadResult(new CsvImportDocumentContent(records), errors);
    }

    private static IReadOnlyList<ImportRowError> ValidateHeader(CsvImportRecord header)
    {
        if (header.Fields.Count != ImportContract.Headers.Count)
        {
            return [new ImportRowError(ImportErrorCode.WrongColumnCount, header.LineNumber, null)];
        }

        var errors = new List<ImportRowError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var column = 0; column < header.Fields.Count; column++)
        {
            var name = header.Fields[column].Trim();

            if (!seen.Add(name))
            {
                errors.Add(new ImportRowError(ImportErrorCode.DuplicateColumn, header.LineNumber, column));
                continue;
            }

            // The order is part of the contract, so a header is only right in its own position.
            if (!string.Equals(name, ImportContract.Headers[column], StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ImportRowError(ImportErrorCode.UnknownColumn, header.LineNumber, column));
            }
        }

        return errors;
    }

    /// <summary>
    /// Splits the whole text into records and fields in one pass, tracking the source line so a record
    /// spanning a quoted newline still reports the line it started on.
    /// </summary>
    private static bool TrySplitRecords(string text, out List<CsvImportRecord> records, out ImportRowError? error)
    {
        records = [];
        error = null;

        var fields = new List<string>(ImportContract.Headers.Count);
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var quoted = false;
        var closedQuote = false;
        var started = false;
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index += 2;
                        continue;
                    }

                    quoted = false;
                    closedQuote = true;
                    index++;
                    continue;
                }

                if (character == '\n')
                {
                    line++;
                }

                field.Append(character);
                index++;
                continue;
            }

            if (character == '"' && field.Length == 0 && !closedQuote)
            {
                quoted = true;
                started = true;
                index++;
                continue;
            }

            if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                closedQuote = false;
                started = true;
                index++;
                continue;
            }

            if (character is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(new CsvImportRecord(recordLine, fields));
                fields = new List<string>(ImportContract.Headers.Count);
                closedQuote = false;
                started = false;

                // CRLF is one record end, not two.
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                index++;
                line++;
                recordLine = line;
                continue;
            }

            if (closedQuote)
            {
                error = new ImportRowError(ImportErrorCode.MalformedQuote, recordLine, null);
                return false;
            }

            field.Append(character);
            started = true;
            index++;
        }

        if (quoted)
        {
            error = new ImportRowError(ImportErrorCode.UnterminatedQuote, recordLine, null);
            return false;
        }

        // A trailing newline already closed the last record; anything else still in hand is one more.
        if (started || field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(new CsvImportRecord(recordLine, fields));
        }

        return true;
    }

    private static CsvImportReadResult Failed(ImportErrorCode code, int lineNumber, int? columnIndex) =>
        new(null, [new ImportRowError(code, lineNumber, columnIndex)]);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~CsvImportDocumentTests"`
Expected: PASS - every case in the class. `ImportRowError` already exists from Task 1.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/CsvImportDocument.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/CsvImportDocumentTests.cs
git commit -m "feat(inventories): read a bounded RFC 4180 import file and nothing else for #34"
```

---

## Task 3: Turn one record into one bounded row

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ImportRow.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportRowTests.cs`

Why: field-level meaning - required, blank-means-default, length bounds, and exact Quantity - is decided once, from a record alone, with no store and no Inventory in sight.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportRowTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportRowTests
{
    private static CsvImportRecord Record(
        string name = "Steel Bolts",
        string quantity = "10",
        string unit = "each",
        string location = "Shelf A",
        string note = "Blue box",
        int lineNumber = 2) => new(lineNumber, [name, quantity, unit, location, note]);

    [Fact]
    public void A_complete_row_carries_its_tidy_display_text_and_its_normalized_name()
    {
        Assert.True(ImportRow.TryCreate(Record(name: "  Steel   Bolts  "), out var row, out var errors));

        Assert.Empty(errors);
        Assert.Equal(2, row!.LineNumber);
        Assert.Equal("Steel Bolts", row.Name);
        Assert.Equal("steel bolts", row.NormalizedName);
        Assert.Equal("10", row.Quantity.ToInvariantText());
        Assert.Equal("each", row.UnitTerm);
        Assert.Equal("Shelf A", row.LocationName);
        Assert.Equal("Blue box", row.Note);
    }

    [Fact]
    public void A_blank_Unit_means_the_reserved_each_Unit()
    {
        Assert.True(ImportRow.TryCreate(Record(unit: "   "), out var row, out _));

        Assert.Equal(Unit.ReservedEachCanonicalName, row!.UnitTerm);
    }

    [Fact]
    public void A_blank_Location_means_unlocated_and_a_blank_Note_means_no_Note()
    {
        Assert.True(ImportRow.TryCreate(Record(location: "", note: "   "), out var row, out _));

        Assert.Null(row!.LocationName);
        Assert.Null(row.Note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_row_without_a_Name_is_refused_at_the_Name_column(string name)
    {
        Assert.False(ImportRow.TryCreate(Record(name: name), out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(ImportErrorCode.MissingName, error.Code);
        Assert.Equal(ImportContract.NameColumn, error.ColumnIndex);
        Assert.Equal(2, error.LineNumber);
    }

    [Fact]
    public void A_row_without_a_Quantity_is_refused_at_the_Quantity_column()
    {
        Assert.False(ImportRow.TryCreate(Record(quantity: " "), out _, out var errors));

        Assert.Equal(ImportErrorCode.MissingQuantity, Assert.Single(errors).Code);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.2.3")]
    [InlineData("1,5")]
    [InlineData("1e3")]
    [InlineData("ten")]
    [InlineData("$5")]
    [InlineData("1 000")]
    public void A_Quantity_that_is_not_an_exact_invariant_non_negative_decimal_is_refused(string quantity)
    {
        Assert.False(ImportRow.TryCreate(Record(quantity: quantity), out _, out var errors));

        var error = Assert.Single(errors);
        Assert.Equal(ImportErrorCode.InvalidQuantity, error.Code);
        Assert.Equal(ImportContract.QuantityColumn, error.ColumnIndex);
    }

    [Fact]
    public void Zero_is_a_perfectly_good_starting_Quantity()
    {
        Assert.True(ImportRow.TryCreate(Record(quantity: "0"), out var row, out _));

        Assert.Equal(Quantity.Zero, row!.Quantity);
    }

    [Fact]
    public void Every_length_bound_is_reported_against_its_own_column()
    {
        Assert.False(ImportRow.TryCreate(Record(name: new string('a', StockEntry.MaxNameLength + 1)), out _, out var name));
        Assert.Equal(ImportErrorCode.NameTooLong, Assert.Single(name).Code);

        Assert.False(ImportRow.TryCreate(Record(note: new string('a', StockEntry.MaxNoteLength + 1)), out _, out var note));
        Assert.Equal(ImportErrorCode.NoteTooLong, Assert.Single(note).Code);

        Assert.False(ImportRow.TryCreate(Record(unit: new string('a', Unit.MaxNameLength + 1)), out _, out var unit));
        Assert.Equal(ImportErrorCode.UnitTooLong, Assert.Single(unit).Code);

        Assert.False(ImportRow.TryCreate(Record(location: new string('a', Location.MaxNameLength + 1)), out _, out var location));
        Assert.Equal(ImportErrorCode.LocationTooLong, Assert.Single(location).Code);
    }

    [Fact]
    public void Every_thing_wrong_with_one_row_is_reported_together()
    {
        Assert.False(ImportRow.TryCreate(Record(name: "", quantity: "nope"), out _, out var errors));

        Assert.Equal(
            [ImportErrorCode.MissingName, ImportErrorCode.InvalidQuantity],
            errors.Select(error => error.Code));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportRowTests"`
Expected: FAIL to compile - `ImportRow` does not exist.

- [ ] **Step 3: Write the row**

Create `src/MultiChannelAgent.Domain/Inventories/ImportRow.cs`:

```csharp
namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// One parsed, bounded import row: everything a record means on its own, before anything about this
/// Inventory is known. The Unit and Location are still raw terms here - resolving them needs a store,
/// and this type deliberately needs nothing.
///
/// Every rule it applies is a shipped one: <see cref="NameNormalization"/> for the display and
/// comparison forms, <see cref="Quantity.TryParseInvariant"/> for the amount, and the same length
/// bounds <see cref="StockEntry"/>, <see cref="Unit"/>, and <see cref="Location"/> already enforce -
/// so a file cannot describe stock the conversation could not.
/// </summary>
public sealed record ImportRow
{
    /// <summary>The 1-based source line this row came from, so an error can be found in the file.</summary>
    public required int LineNumber { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required Quantity Quantity { get; init; }

    /// <summary>The raw Unit term to resolve. Never blank: a blank Unit column means the reserved <c>each</c> Unit.</summary>
    public required string UnitTerm { get; init; }

    /// <summary>The raw Location name to resolve, or null for unlocated - which is the absence of a reference, not a name.</summary>
    public string? LocationName { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// Reads one record. Every problem with the row is collected rather than the first returned, so a
    /// Participant fixing a file sees everything wrong with a line at once.
    /// </summary>
    public static bool TryCreate(CsvImportRecord record, out ImportRow? row, out IReadOnlyList<ImportRowError> errors)
    {
        ArgumentNullException.ThrowIfNull(record);

        row = null;
        var found = new List<ImportRowError>();

        var name = Collapsed(record.Fields[ImportContract.NameColumn]);
        var quantityText = record.Fields[ImportContract.QuantityColumn].Trim();
        var unit = Collapsed(record.Fields[ImportContract.UnitColumn]);
        var location = Collapsed(record.Fields[ImportContract.LocationColumn]);
        var note = record.Fields[ImportContract.NoteColumn].Trim();

        if (name.Length == 0)
        {
            found.Add(Error(ImportErrorCode.MissingName, record, ImportContract.NameColumn));
        }
        else if (name.Length > StockEntry.MaxNameLength)
        {
            found.Add(Error(ImportErrorCode.NameTooLong, record, ImportContract.NameColumn));
        }

        var quantity = Quantity.Zero;
        if (quantityText.Length == 0)
        {
            found.Add(Error(ImportErrorCode.MissingQuantity, record, ImportContract.QuantityColumn));
        }
        else if (!Quantity.TryParseInvariant(quantityText, out quantity))
        {
            found.Add(Error(ImportErrorCode.InvalidQuantity, record, ImportContract.QuantityColumn));
        }

        if (unit.Length > Unit.MaxNameLength)
        {
            found.Add(Error(ImportErrorCode.UnitTooLong, record, ImportContract.UnitColumn));
        }

        if (location.Length > Location.MaxNameLength)
        {
            found.Add(Error(ImportErrorCode.LocationTooLong, record, ImportContract.LocationColumn));
        }

        if (note.Length > StockEntry.MaxNoteLength)
        {
            found.Add(Error(ImportErrorCode.NoteTooLong, record, ImportContract.NoteColumn));
        }

        if (found.Count > 0)
        {
            errors = found;
            return false;
        }

        errors = [];
        row = new ImportRow
        {
            LineNumber = record.LineNumber,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,

            // A blank Unit is not a missing Unit: the specification says it means `each`, and saying so
            // here means nothing downstream has to remember it.
            UnitTerm = unit.Length == 0 ? Unit.ReservedEachCanonicalName : unit,
            LocationName = location.Length == 0 ? null : location,
            Note = note.Length == 0 ? null : note,
        };

        return true;
    }

    private static string Collapsed(string value) => NameNormalization.Collapse(value);

    private static ImportRowError Error(ImportErrorCode code, CsvImportRecord record, int columnIndex) =>
        new(code, record.LineNumber, columnIndex);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportRowTests|FullyQualifiedName~CsvImportDocumentTests"`
Expected: PASS - both classes.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ImportRow.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ImportRowTests.cs
git commit -m "feat(inventories): read one import record as one bounded row for #34"
```

---

## Task 4: Merge equivalent rows, or say why they cannot merge

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ImportMergePlan.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportMergePlanTests.cs`

Why: "merge Equivalent Stock rows by summing Quantity when Notes are compatible; conflicting Notes are errors" is the subtlest rule in the ticket, and it is decidable from resolved rows alone. Deciding it in a pure type is what lets every case be enumerated.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportMergePlanTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportMergePlanTests
{
    private static readonly UnitId Each = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly UnitId Box = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly LocationId ShelfA = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static ResolvedImportRow Row(
        int lineNumber,
        string name = "Steel Bolts",
        string quantity = "1",
        UnitId? unitId = null,
        LocationId? locationId = null,
        string? note = null) => new()
        {
            LineNumber = lineNumber,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = ParseQuantity(quantity),
            UnitId = unitId ?? Each,
            UnitCanonicalName = "each",
            LocationId = locationId,
            LocationName = locationId is null ? null : "Shelf A",
            Note = note,
        };

    private static Quantity ParseQuantity(string text)
    {
        Assert.True(Quantity.TryParseInvariant(text, out var quantity));
        return quantity;
    }

    [Fact]
    public void Rows_that_are_not_equivalent_each_become_their_own_entry()
    {
        var plan = ImportMergePlan.Create(
        [
            Row(2, name: "Steel Bolts"),
            Row(3, name: "Brass Rivets"),
            Row(4, name: "Steel Bolts", unitId: Box),
            Row(5, name: "Steel Bolts", locationId: ShelfA),
        ]);

        Assert.Empty(plan.Errors);
        Assert.Equal(4, plan.Entries.Count);
    }

    [Fact]
    public void Equivalent_rows_sum_their_Quantity_and_report_every_line_that_contributed()
    {
        var plan = ImportMergePlan.Create([Row(2, quantity: "4"), Row(5, quantity: "6.5")]);

        Assert.Empty(plan.Errors);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal("10.5", entry.Quantity.ToInvariantText());
        Assert.Equal([2, 5], entry.SourceLineNumbers);
        Assert.Equal(2, entry.LineNumber);
    }

    [Fact]
    public void Equivalence_ignores_case_and_whitespace_exactly_as_the_domain_does()
    {
        var plan = ImportMergePlan.Create([Row(2, name: "Steel Bolts"), Row(3, name: "  STEEL   bolts ")]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal("2", entry.Quantity.ToInvariantText());

        // The first line's display text is what survives, so the preview shows what a person wrote.
        Assert.Equal("Steel Bolts", entry.Name);
    }

    [Fact]
    public void A_blank_Note_is_compatible_with_anything_and_the_written_one_survives()
    {
        var plan = ImportMergePlan.Create([Row(2, note: null), Row(3, note: "Blue box"), Row(4, note: null)]);

        Assert.Empty(plan.Errors);
        Assert.Equal("Blue box", Assert.Single(plan.Entries).Note);
    }

    [Fact]
    public void The_same_Note_written_twice_is_compatible()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "Blue box"), Row(3, note: "Blue box")]);

        Assert.Empty(plan.Errors);
        Assert.Equal("Blue box", Assert.Single(plan.Entries).Note);
    }

    [Fact]
    public void Two_different_Notes_on_equivalent_rows_are_refused_rather_than_guessed()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "Blue box"), Row(3, note: "Red box")]);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(ImportErrorCode.ConflictingNotes, error.Code);
        Assert.Equal(3, error.LineNumber);
        Assert.Equal(ImportContract.NoteColumn, error.ColumnIndex);
        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void Notes_differing_only_in_case_are_a_conflict_because_a_Note_is_what_someone_wrote()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "Blue box"), Row(3, note: "blue box")]);

        Assert.Equal(ImportErrorCode.ConflictingNotes, Assert.Single(plan.Errors).Code);
    }

    [Fact]
    public void Every_conflicting_line_after_the_first_is_named_so_one_pass_fixes_the_file()
    {
        var plan = ImportMergePlan.Create([Row(2, note: "A"), Row(3, note: "B"), Row(4, note: "C")]);

        Assert.Equal([3, 4], plan.Errors.Select(error => error.LineNumber));
    }

    [Fact]
    public void A_sum_that_leaves_the_Quantity_bounds_is_refused_against_the_first_line_of_the_group()
    {
        var huge = new string('9', Quantity.MaxIntegerDigits);
        var plan = ImportMergePlan.Create([Row(2, quantity: huge), Row(3, quantity: huge)]);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(ImportErrorCode.QuantityOverflow, error.Code);
        Assert.Equal(2, error.LineNumber);
    }

    [Fact]
    public void More_normalized_entries_than_the_bound_is_one_whole_file_error()
    {
        var rows = Enumerable
            .Range(0, ImportContract.MaxNormalizedEntries + 1)
            .Select(index => Row(index + 2, name: $"Item {index}"))
            .ToList();

        var plan = ImportMergePlan.Create(rows);

        var error = Assert.Single(plan.Errors);
        Assert.Equal(ImportErrorCode.TooManyEntries, error.Code);
        Assert.Equal(0, error.LineNumber);
        Assert.Empty(plan.Entries);
    }

    [Fact]
    public void Entries_come_back_in_the_order_their_first_line_appeared()
    {
        var plan = ImportMergePlan.Create([Row(4, name: "Zinc"), Row(2, name: "Alpha"), Row(3, name: "Zinc")]);

        Assert.Equal(["Zinc", "Alpha"], plan.Entries.Select(entry => entry.Name));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportMergePlanTests"`
Expected: FAIL to compile - `ResolvedImportRow`, `ImportMergePlan`, and `ImportEntry` do not exist.

- [ ] **Step 3: Write the merge**

Create `src/MultiChannelAgent.Domain/Inventories/ImportMergePlan.cs`:

```csharp
namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// One import row whose Unit and Location have been resolved to identities. The display names come
/// along so the preview can show what the Inventory actually calls them rather than what the file
/// happened to type.
/// </summary>
public sealed record ResolvedImportRow
{
    public required int LineNumber { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required Quantity Quantity { get; init; }

    public required UnitId UnitId { get; init; }

    public required string UnitCanonicalName { get; init; }

    public LocationId? LocationId { get; init; }

    public string? LocationName { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// One Stock Entry the import will create, and every source line that contributed to it - so a
/// Participant reviewing the preview can see exactly which rows collapsed into it.
/// </summary>
public sealed record ImportEntry
{
    /// <summary>The first source line of the group, which is also the line whose display text and references survive.</summary>
    public required int LineNumber { get; init; }

    public required IReadOnlyList<int> SourceLineNumbers { get; init; }

    public required string Name { get; init; }

    public required string NormalizedName { get; init; }

    public required Quantity Quantity { get; init; }

    public required UnitId UnitId { get; init; }

    public required string UnitCanonicalName { get; init; }

    public LocationId? LocationId { get; init; }

    public string? LocationName { get; init; }

    public string? Note { get; init; }
}

/// <summary>The merged result, or the reasons it could not be merged. Both are never partly true: entries are empty whenever anything failed.</summary>
public sealed record ImportMergeResult(IReadOnlyList<ImportEntry> Entries, IReadOnlyList<ImportRowError> Errors);

/// <summary>
/// The pure merge. Rows are equivalent exactly when the domain says they are - same normalized name,
/// same Unit, same optional Location, which is the key the database's Equivalent Stock index enforces
/// - and Notes deliberately do not participate, as <c>CONTEXT.md</c> states.
///
/// Notes are compared ordinally and case-sensitively after trimming. A Note is free text somebody
/// wrote to record a distinction, so folding "Blue box" into "blue box" would quietly erase one;
/// refusing and asking is the safe direction, and it is the direction a Participant can act on.
/// </summary>
public static class ImportMergePlan
{
    public static ImportMergeResult Create(IReadOnlyList<ResolvedImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var groups = new Dictionary<EquivalenceKey, List<ResolvedImportRow>>();
        var order = new List<EquivalenceKey>();

        foreach (var row in rows.OrderBy(row => row.LineNumber))
        {
            var key = new EquivalenceKey(row.NormalizedName, row.UnitId, row.LocationId);

            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add(key);
            }

            group.Add(row);
        }

        var errors = new List<ImportRowError>();
        var entries = new List<ImportEntry>(order.Count);

        foreach (var key in order)
        {
            var group = groups[key];
            var first = group[0];

            if (!TryMergeNotes(group, errors, out var note))
            {
                continue;
            }

            if (!TrySum(group, out var quantity))
            {
                errors.Add(new ImportRowError(ImportErrorCode.QuantityOverflow, first.LineNumber, ImportContract.QuantityColumn));
                continue;
            }

            entries.Add(new ImportEntry
            {
                LineNumber = first.LineNumber,
                SourceLineNumbers = [.. group.Select(row => row.LineNumber)],
                Name = first.Name,
                NormalizedName = first.NormalizedName,
                Quantity = quantity,
                UnitId = first.UnitId,
                UnitCanonicalName = first.UnitCanonicalName,
                LocationId = first.LocationId,
                LocationName = first.LocationName,
                Note = note,
            });
        }

        if (errors.Count > 0)
        {
            return new ImportMergeResult([], errors);
        }

        // Checked after merging, because the bound is on the Stock Entries this would create, not on
        // the rows that describe them - a file may legitimately carry more rows than entries.
        return entries.Count > ImportContract.MaxNormalizedEntries
            ? new ImportMergeResult([], [new ImportRowError(ImportErrorCode.TooManyEntries, 0, null)])
            : new ImportMergeResult(entries, []);
    }

    /// <summary>
    /// Decides the group's surviving Note. Blanks are compatible with anything and contribute nothing;
    /// one distinct non-blank Note survives; two are a conflict reported against every line after the
    /// first that introduced a different one, so one pass over the file fixes it.
    /// </summary>
    private static bool TryMergeNotes(List<ResolvedImportRow> group, List<ImportRowError> errors, out string? note)
    {
        note = null;
        var conflicted = false;

        foreach (var row in group)
        {
            if (row.Note is null)
            {
                continue;
            }

            if (note is null)
            {
                note = row.Note;
                continue;
            }

            if (!string.Equals(note, row.Note, StringComparison.Ordinal))
            {
                errors.Add(new ImportRowError(ImportErrorCode.ConflictingNotes, row.LineNumber, ImportContract.NoteColumn));
                conflicted = true;
            }
        }

        return !conflicted;
    }

    private static bool TrySum(List<ResolvedImportRow> group, out Quantity total)
    {
        total = Quantity.Zero;

        foreach (var row in group)
        {
            if (!total.TryAdd(row.Quantity, out total))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct EquivalenceKey(string NormalizedName, UnitId UnitId, LocationId? LocationId);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportMergePlanTests"`
Expected: PASS - every case in the class.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ImportMergePlan.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ImportMergePlanTests.cs
git commit -m "feat(inventories): merge equivalent import rows only when their Notes agree for #34"
```

---

## Task 5: Model the import proposal

**Files:**
- Create: `src/MultiChannelAgent.Domain/Inventories/ImportProposal.cs`
- Create: `src/MultiChannelAgent.Domain/Inventories/ImportOperationId.cs`
- Test: `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportProposalTests.cs`

Why: what the Participant reviewed is what must commit, so the exact rows, the binding, the digest, the empty-state version, and the single-use token have to live in one immutable aggregate that confirmation reads and never recomputes.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Domain.Tests/Inventories/ImportProposalTests.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ImportProposalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly InventoryId Inventory = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly UnitId Each = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private static ImportEntry Entry(string name = "Steel Bolts", int lineNumber = 2) => new()
    {
        LineNumber = lineNumber,
        SourceLineNumbers = [lineNumber],
        Name = name,
        NormalizedName = NameNormalization.Normalize(name),
        Quantity = Quantity.Create(4m),
        UnitId = Each,
        UnitCanonicalName = "each",
        LocationId = null,
        LocationName = null,
        Note = null,
    };

    private static ImportProposal Create(IReadOnlyList<ImportEntry>? entries = null) => ImportProposal.Create(
        ConfirmationToken.HashOf(ConfirmationToken.Issue()),
        Participant,
        Inventory,
        FileDigest.Of("Name,Quantity,Unit,Location,Note\n"u8.ToArray()),
        entries ?? [Entry()],
        EmptyStateVersion.Empty,
        Now);

    [Fact]
    public void A_proposal_carries_the_exact_entries_it_was_previewed_with()
    {
        var proposal = Create([Entry("Steel Bolts"), Entry("Brass Rivets", 3)]);

        Assert.Equal(["Steel Bolts", "Brass Rivets"], proposal.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void A_proposal_shares_the_shipped_ten_minute_single_use_lifetime()
    {
        var proposal = Create();

        Assert.Equal(ConfirmationProposal.LifetimeMinutes, ImportProposal.LifetimeMinutes);
        Assert.Equal(Now.AddMinutes(ImportProposal.LifetimeMinutes), proposal.ExpiresAt);
        Assert.False(proposal.IsExpired(proposal.ExpiresAt.AddTicks(-1)));
        Assert.True(proposal.IsExpired(proposal.ExpiresAt));
    }

    [Fact]
    public void A_proposal_belongs_to_exactly_one_Participant_and_one_Inventory()
    {
        var proposal = Create();

        Assert.True(proposal.BelongsTo(Participant, Inventory));
        Assert.False(proposal.BelongsTo(new ParticipantId(Guid.NewGuid()), Inventory));
        Assert.False(proposal.BelongsTo(Participant, new InventoryId(Guid.NewGuid())));
    }

    [Fact]
    public void A_proposal_must_carry_at_least_one_entry() =>
        Assert.Throws<ArgumentException>(() => Create([]));

    [Fact]
    public void A_proposal_must_not_exceed_the_normalized_entry_bound()
    {
        var entries = Enumerable
            .Range(0, ImportContract.MaxNormalizedEntries + 1)
            .Select(index => Entry($"Item {index}", index + 2))
            .ToList();

        Assert.Throws<ArgumentException>(() => Create(entries));
    }

    [Fact]
    public void The_empty_state_version_says_only_that_the_Inventory_held_nothing()
    {
        Assert.Equal(0, EmptyStateVersion.Empty.ExpectedStockEntryCount);
        Assert.Equal(EmptyStateVersion.Empty, Create().EmptyStateVersion);
    }

    [Fact]
    public void A_proposal_executes_under_its_own_ledger_identity_which_no_other_ledger_can_mint()
    {
        var proposal = Create();

        Assert.Equal(ImportOperationId.DeriveForProposal(proposal.Id), proposal.ExecutionOperationId);
        Assert.NotEqual(
            StockOperationId.DeriveForProposal(new ProposalId(proposal.Id.Value)).Value,
            proposal.ExecutionOperationId.Value);
        Assert.NotEqual(
            ReferenceOperationId.DeriveForProposal(new ProposalId(proposal.Id.Value)).Value,
            proposal.ExecutionOperationId.Value);
    }

    [Fact]
    public void A_proposal_names_every_reference_its_entries_depend_on()
    {
        var locationId = new LocationId(Guid.NewGuid());
        var located = Entry() with { LocationId = locationId, LocationName = "Shelf A" };

        var proposal = Create([Entry(), located]);

        Assert.Equal([Each], proposal.ReferencedUnitIds);
        Assert.Equal([locationId], proposal.ReferencedLocationIds);
    }

    [Fact]
    public void Two_proposals_never_share_an_identity() =>
        Assert.NotEqual(Create().Id, Create().Id);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj --filter "FullyQualifiedName~ImportProposalTests"`
Expected: FAIL to compile - `ImportProposal`, `ImportProposalId`, `EmptyStateVersion`, and `ImportOperationId` do not exist.

- [ ] **Step 3: Add the operation identity**

Create `src/MultiChannelAgent.Domain/Inventories/ImportOperationId.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// The stable identity of one Initial Import execution. Like <see cref="StockOperationId"/> and
/// <see cref="ReferenceOperationId"/> it is <em>derived</em> - never generated - so a confirmation
/// re-driven after a crash re-reports what it did instead of importing a second time.
///
/// Its hash material is shaped so it can never equal either of the others: three ledgers, three
/// identity spaces, and no way for "what did this operation do" to be ambiguous.
/// </summary>
public readonly record struct ImportOperationId(Guid Value)
{
    public static ImportOperationId DeriveForProposal(ImportProposalId proposalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"import-proposal|{proposalId.Value:D}"));

        return new ImportOperationId(new Guid(digest.AsSpan(0, 16)));
    }

    public override string ToString() => Value.ToString();
}
```

- [ ] **Step 4: Add the aggregate**

Create `src/MultiChannelAgent.Domain/Inventories/ImportProposal.cs`:

```csharp
namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Strongly typed identity of one stored import proposal.</summary>
public readonly record struct ImportProposalId(Guid Value)
{
    public static ImportProposalId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// Where a stored import proposal ended up. Every status other than <see cref="Pending"/> is
/// terminal, so an import can execute at most once however many times it is confirmed.
/// </summary>
public enum ImportProposalStatus
{
    Pending,

    /// <summary>Executed. Set inside the very transaction that created the Stock Entries.</summary>
    Confirmed,

    /// <summary>The Participant cancelled it.</summary>
    Rejected,

    /// <summary>A newer validation for the same Participant and Inventory replaced it.</summary>
    Superseded,

    /// <summary>Its ten minutes ran out.</summary>
    Expired,

    /// <summary>Execution found the Inventory no longer empty, so nothing was created.</summary>
    Conflicted,
}

/// <summary>
/// The state an import was decided against: an Inventory holding no Stock Entries at all,
/// zero-quantity ones included.
///
/// It is deliberately not a row version, because the thing being asserted is an <em>absence</em>, and
/// an absence is a range rather than a row. Its enforcement is therefore a range lock: the execution
/// transaction runs at serializable isolation and re-asserts the same emptiness inside itself, so a
/// Stock Entry created between preview and confirmation makes the import conflict rather than land
/// into an Inventory that is no longer the one that was reviewed.
/// </summary>
public readonly record struct EmptyStateVersion(int ExpectedStockEntryCount)
{
    public static EmptyStateVersion Empty { get; } = new(0);
}

/// <summary>
/// One exact, immutable, server-stored Initial Import awaiting confirmation, bound to the Participant
/// and Inventory that produced it, the digest of the file it came from, and the empty state it was
/// decided against, carrying the hash of its single-use token and the exact Stock Entries it will
/// create.
///
/// Nothing here can be re-derived at confirmation time, and that is the whole point: the file is
/// discarded, the rows are what commit, and #34 requires confirmation to apply the stored proposal
/// "without reparsing".
///
/// This is a separate aggregate from <see cref="ConfirmationProposal"/> on purpose. That one is
/// bounded to <see cref="ConfirmationProposal.MaxChanges"/> changes, keyed one-pending-per
/// ChannelConversation, and carries expected versions of existing Stock Entries. An import carries up
/// to <see cref="ImportContract.MaxNormalizedEntries"/> entries, belongs to a signed-in browser
/// session rather than a conversation, and by definition touches no existing entry at all. Sharing
/// the type would mean relaxing all three of those rules for everybody.
/// </summary>
public sealed record ImportProposal
{
    /// <summary>The same ten minutes a conversational confirmation gets, taken from it so the two can never drift apart.</summary>
    public const int LifetimeMinutes = ConfirmationProposal.LifetimeMinutes;

    public required ImportProposalId Id { get; init; }

    public required ConfirmationTokenHash TokenHash { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required InventoryId InventoryId { get; init; }

    public required FileDigest FileDigest { get; init; }

    public required IReadOnlyList<ImportEntry> Entries { get; init; }

    public required EmptyStateVersion EmptyStateVersion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt => CreatedAt.AddMinutes(LifetimeMinutes);

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>The ledger identity this execution is recorded under, fixed the moment the proposal exists.</summary>
    public ImportOperationId ExecutionOperationId => ImportOperationId.DeriveForProposal(Id);

    /// <summary>Every Unit the entries reference, so execution can hold them all before it writes.</summary>
    public IReadOnlyList<UnitId> ReferencedUnitIds => [.. Entries.Select(entry => entry.UnitId).Distinct()];

    /// <summary>Every Location the entries reference. Unlocated is the absence of one, so it contributes nothing.</summary>
    public IReadOnlyList<LocationId> ReferencedLocationIds =>
        [.. Entries.Select(entry => entry.LocationId).OfType<LocationId>().Distinct()];

    /// <summary>
    /// Whether this proposal belongs to exactly this Participant and Inventory. One that does not is
    /// treated as if it did not exist, so a token can never be replayed into another session or
    /// another Inventory.
    /// </summary>
    public bool BelongsTo(ParticipantId participantId, InventoryId inventoryId) =>
        ParticipantId == participantId && InventoryId == inventoryId;

    public static ImportProposal Create(
        ConfirmationTokenHash tokenHash,
        ParticipantId participantId,
        InventoryId inventoryId,
        FileDigest fileDigest,
        IReadOnlyList<ImportEntry> entries,
        EmptyStateVersion emptyStateVersion,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            throw new ArgumentException("An import proposal must carry at least one entry.", nameof(entries));
        }

        if (entries.Count > ImportContract.MaxNormalizedEntries)
        {
            throw new ArgumentException(
                $"An import proposal must not carry more than {ImportContract.MaxNormalizedEntries} entries.", nameof(entries));
        }

        return new ImportProposal
        {
            Id = ImportProposalId.NewId(),
            TokenHash = tokenHash,
            ParticipantId = participantId,
            InventoryId = inventoryId,
            FileDigest = fileDigest,
            Entries = entries,
            EmptyStateVersion = emptyStateVersion,
            CreatedAt = createdAt,
        };
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Domain.Tests/MultiChannelAgent.Domain.Tests.csproj`
Expected: PASS - every Domain test, including the shipped ones.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Domain/Inventories/ImportProposal.cs \
        src/MultiChannelAgent.Domain/Inventories/ImportOperationId.cs \
        tests/MultiChannelAgent.Domain.Tests/Inventories/ImportProposalTests.cs
git commit -m "feat(inventories): model the exact import proposal a confirmation applies for #34"
```

---

## Task 6: Define the import store seams

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/IImportProposalStore.cs`
- Create: `src/MultiChannelAgent.Application/Inventories/IImportExecutionStore.cs`
- Create: `src/MultiChannelAgent.Application/Inventories/IStockEmptyStateReader.cs`
- Create: `src/MultiChannelAgent.Application/Inventories/IInventoryAuditRetentionStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryImportProposalStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryImportExecutionStore.cs`
- Create: `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockEmptyStateReader.cs`

Why: naming the contracts before implementing them is what lets Tasks 7 and 8 be written and tested with no SQL at all, and it is where "all of it or none of it, including the audit, the ledger, the proposal, and the raw file" is stated as a promise a caller may rely on.

- [ ] **Step 1: Define the pending-import seam**

Create `src/MultiChannelAgent.Application/Inventories/IImportProposalStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The one pending Initial Import per Participant and Inventory, and the raw file it came from.
///
/// The raw bytes live here for the proposal's ten minutes and nowhere else. They are kept so a
/// reloaded preview can be shown again without a re-upload and so "the raw CSV is discarded" is one
/// durable, testable fact rather than a claim about process memory - and they are deleted by every
/// path out of Pending, so after confirmation, rejection, supersession, or expiry only the digest and
/// the minimal audit fact remain.
/// </summary>
public interface IImportProposalStore
{
    /// <summary>
    /// Stores <paramref name="proposal"/> with its raw upload, superseding any proposal this
    /// Participant already had pending for this Inventory - and discarding that one's upload - in the
    /// same transaction. Returns whether something was superseded, so the caller can say so.
    /// </summary>
    Task<bool> StoreAsync(
        ImportProposal proposal, ReadOnlyMemory<byte> rawContent, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The one pending proposal for this Participant and Inventory, or null. Never returns a settled one.</summary>
    Task<ImportProposal?> FindPendingAsync(
        ParticipantId participantId, InventoryId inventoryId, CancellationToken cancellationToken);

    /// <summary>The raw bytes of a pending proposal, or null once they have been discarded.</summary>
    Task<ReadOnlyMemory<byte>?> FindRawContentAsync(ImportProposalId proposalId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a proposal out of Pending, guarded, and discards its raw upload in the same transaction.
    /// Returns false when it was not Pending any more, which is how two callers racing to settle one
    /// proposal are resolved without either of them guessing.
    /// </summary>
    Task<bool> SettleAsync(
        ImportProposalId proposalId, ImportProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The status of a proposal, or null when there is no such row. For tests and diagnostics only.</summary>
    Task<ImportProposalStatus?> FindStatusAsync(ImportProposalId proposalId, CancellationToken cancellationToken);

    /// <summary>Settles every pending proposal whose ten minutes ran out before <paramref name="now"/>, bounded, discarding their uploads.</summary>
    Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken);

    /// <summary>Deletes settled proposals older than <paramref name="cutoff"/>, bounded.</summary>
    Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Define the atomic writer seam**

Create `src/MultiChannelAgent.Application/Inventories/IImportExecutionStore.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How an import execution was settled.</summary>
public enum ImportExecutionOutcome
{
    /// <summary>Every Stock Entry was created, with its audit fact, its ledger row, and the raw file discarded - all together.</summary>
    Applied,

    /// <summary>This operation identity had already been applied; the recorded facts are returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>The Inventory was no longer empty, or the proposal was no longer pending. Nothing at all was written.</summary>
    Conflict,
}

/// <summary>
/// The durable semantic facts of one applied import - exactly what a replay must be able to
/// re-report without touching Inventory state again. Deliberately semantic: no row versions, no audit
/// identities, no SQL detail, and no file contents.
/// </summary>
public sealed record RecordedImport(
    ImportOperationId OperationId,
    ImportProposalId ProposalId,
    ParticipantId ActorId,
    FileDigest FileDigest,
    int CreatedEntryCount);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="ImportExecutionOutcome.Conflict"/>.</summary>
public sealed record ImportExecutionResult(ImportExecutionOutcome Outcome, RecordedImport? Recorded);

/// <summary>One fully decided import, ready to apply. Everything in it was reviewed; nothing is recomputed.</summary>
public sealed record ImportExecutionCommand
{
    /// <summary>The retry-stable identity this execution is recorded under; the ledger is keyed by it.</summary>
    public required ImportOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Editor-or-better Membership authorized this; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    /// <summary>The proposal to consume in the very same transaction, and whose raw upload to discard.</summary>
    public required ImportProposalId ConsumesProposalId { get; init; }

    public required FileDigest FileDigest { get; init; }

    public required IReadOnlyList<ImportEntry> Entries { get; init; }

    /// <summary>The empty state this import was decided against, re-asserted inside the execution transaction.</summary>
    public required EmptyStateVersion EmptyStateVersion { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The one atomic writer behind Initial Import.
///
/// <see cref="ApplyAsync"/> holds every Unit and Location the entries reference, consumes the
/// proposal, re-asserts that the Inventory still holds no Stock Entry at all, creates every entry,
/// appends exactly one minimal semantic audit fact, writes its ledger row, and discards the raw
/// upload - in one transaction. A caller that sees <see cref="ImportExecutionOutcome.Conflict"/> may
/// rely on nothing at all having happened, including the proposal still being pending.
/// </summary>
public interface IImportExecutionStore
{
    Task<RecordedImport?> FindRecordedAsync(
        InventoryId inventoryId, ImportOperationId operationId, CancellationToken cancellationToken);

    Task<ImportExecutionResult> ApplyAsync(ImportExecutionCommand command, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Define the empty-state and retention seams**

Create `src/MultiChannelAgent.Application/Inventories/IStockEmptyStateReader.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The single authorized read behind Initial Import's eligibility gate: does this Inventory hold any
/// Stock Entry at all?
///
/// "At all" is the whole point. #34 says "including zero-quantity entries", so this deliberately does
/// not filter on Quantity: a zero-quantity Stock Entry is a Stock Entry, which is exactly why the
/// conversational Forget exists to remove one.
/// </summary>
public interface IStockEmptyStateReader
{
    Task<bool> AnyStockAsync(InventoryId inventoryId, CancellationToken cancellationToken);
}
```

Create `src/MultiChannelAgent.Application/Inventories/IInventoryAuditRetentionStore.cs`:

```csharp
namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The bounded delete behind the ninety-day audit retention the specification requires.
///
/// <c>AuditFact.RetentionDays</c> has said ninety since audits existed, but nothing enforced it: the
/// shipped cleanup covers confirmation proposals and outcome payloads only. #34 requires that "only
/// the specified 90-day semantic facts remain", so the sweep lives here and covers every audit fact,
/// not only the import one.
/// </summary>
public interface IInventoryAuditRetentionStore
{
    /// <summary>Deletes audit facts that occurred before <paramref name="cutoff"/>, at most <paramref name="maxRows"/> of them.</summary>
    Task<int> DeleteOccurredBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add the doubles**

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryImportProposalStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IImportProposalStore"/>. It honours exactly the contract the SQL store
/// must: one pending proposal per Participant and Inventory, a guarded settle only one caller can
/// win, and a raw upload that is discarded by every path out of Pending.
/// </summary>
public sealed class InMemoryImportProposalStore : IImportProposalStore
{
    private sealed record Row(ImportProposal Proposal, ImportProposalStatus Status, DateTimeOffset? SettledAt)
    {
        public ReadOnlyMemory<byte>? RawContent { get; init; }
    }

    private readonly Dictionary<ImportProposalId, Row> _rows = [];

    public Task<bool> StoreAsync(
        ImportProposal proposal, ReadOnlyMemory<byte> rawContent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var superseded = false;

        foreach (var existing in FindPendingRows(proposal.ParticipantId, proposal.InventoryId))
        {
            _rows[existing.Proposal.Id] = existing with
            {
                Status = ImportProposalStatus.Superseded,
                SettledAt = now,
                RawContent = null,
            };
            superseded = true;
        }

        _rows[proposal.Id] = new Row(proposal, ImportProposalStatus.Pending, null) { RawContent = rawContent };

        return Task.FromResult(superseded);
    }

    public Task<ImportProposal?> FindPendingAsync(
        ParticipantId participantId, InventoryId inventoryId, CancellationToken cancellationToken) =>
        Task.FromResult(FindPendingRows(participantId, inventoryId).SingleOrDefault()?.Proposal);

    public Task<ReadOnlyMemory<byte>?> FindRawContentAsync(ImportProposalId proposalId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(proposalId, out var row) ? row.RawContent : null);

    public Task<bool> SettleAsync(
        ImportProposalId proposalId, ImportProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(proposalId, out var row) || row.Status != ImportProposalStatus.Pending)
        {
            return Task.FromResult(false);
        }

        _rows[proposalId] = row with { Status = status, SettledAt = now, RawContent = null };

        return Task.FromResult(true);
    }

    public Task<ImportProposalStatus?> FindStatusAsync(ImportProposalId proposalId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(proposalId, out var row) ? row.Status : null);

    public Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var expiring = _rows.Values
            .Where(row => row.Status == ImportProposalStatus.Pending && row.Proposal.IsExpired(now))
            .Take(maxRows)
            .ToList();

        foreach (var row in expiring)
        {
            _rows[row.Proposal.Id] = row with
            {
                Status = ImportProposalStatus.Expired,
                SettledAt = now,
                RawContent = null,
            };
        }

        return Task.FromResult(expiring.Count);
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

    private List<Row> FindPendingRows(ParticipantId participantId, InventoryId inventoryId) =>
        [.. _rows.Values.Where(row =>
            row.Status == ImportProposalStatus.Pending
            && row.Proposal.BelongsTo(participantId, inventoryId))];
}
```

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryImportExecutionStore.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IImportExecutionStore"/>. It honours exactly the contract the SQL
/// store must: replay by operation identity, single-use proposal consumption, the authoritative
/// empty-state re-check, one audit fact, and nothing written when any of them refuses.
/// </summary>
public sealed class InMemoryImportExecutionStore(
    InMemoryImportProposalStore? proposalStore = null, InMemoryStockEmptyStateReader? emptyState = null)
    : IImportExecutionStore
{
    private readonly Dictionary<(InventoryId, ImportOperationId), RecordedImport> _recorded = [];

    /// <summary>Every audit fact this store appended, in order - the same minimal facts the SQL store writes.</summary>
    public List<AuditFact> Audits { get; } = [];

    /// <summary>Every entry this store created, so a test can assert exactly what an import produced.</summary>
    public List<ImportEntry> CreatedEntries { get; } = [];

    public Task<RecordedImport?> FindRecordedAsync(
        InventoryId inventoryId, ImportOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(_recorded.GetValueOrDefault((inventoryId, operationId)));

    public async Task<ImportExecutionResult> ApplyAsync(ImportExecutionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_recorded.TryGetValue((command.InventoryId, command.OperationId), out var already))
        {
            return new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, already);
        }

        // The SQL store does all of this in one transaction, so a conflict discovered after the
        // proposal was consumed still leaves it exactly as it was. This double has no transaction, so
        // it refuses before consuming rather than rolling back afterwards.
        if (emptyState is not null && await emptyState.AnyStockAsync(command.InventoryId, cancellationToken))
        {
            return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
        }

        if (proposalStore is not null
            && !await proposalStore.SettleAsync(
                command.ConsumesProposalId, ImportProposalStatus.Confirmed, command.Now, cancellationToken))
        {
            return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
        }

        CreatedEntries.AddRange(command.Entries);
        emptyState?.SetAnyStock(command.InventoryId, true);

        Audits.Add(AuditFact.Create(
            AuditEventType.StockImported,
            AuditActorKind.Participant,
            command.ActorId.ToString(),
            command.InventoryId,
            subjectParticipantId: null,
            ImportFacts.CompletedOutcomeCode,
            command.Now));

        var recorded = new RecordedImport(
            command.OperationId, command.ConsumesProposalId, command.ActorId, command.FileDigest, command.Entries.Count);
        _recorded[(command.InventoryId, command.OperationId)] = recorded;

        return new ImportExecutionResult(ImportExecutionOutcome.Applied, recorded);
    }
}
```

Create `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryStockEmptyStateReader.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>Minimal in-memory <see cref="IStockEmptyStateReader"/>: an Inventory holds Stock only when a test says so.</summary>
public sealed class InMemoryStockEmptyStateReader : IStockEmptyStateReader
{
    private readonly HashSet<InventoryId> _withStock = [];

    public void SetAnyStock(InventoryId inventoryId, bool anyStock)
    {
        if (anyStock)
        {
            _withStock.Add(inventoryId);
        }
        else
        {
            _withStock.Remove(inventoryId);
        }
    }

    public Task<bool> AnyStockAsync(InventoryId inventoryId, CancellationToken cancellationToken) =>
        Task.FromResult(_withStock.Contains(inventoryId));
}
```

- [ ] **Step 5: Verify**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - the seams and the doubles compile and no shipped behavior changed. There is nothing behavioral to assert yet; Tasks 7 and 8 are where the doubles start earning their keep.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/IImportProposalStore.cs \
        src/MultiChannelAgent.Application/Inventories/IImportExecutionStore.cs \
        src/MultiChannelAgent.Application/Inventories/IStockEmptyStateReader.cs \
        src/MultiChannelAgent.Application/Inventories/IInventoryAuditRetentionStore.cs \
        tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories
git commit -m "feat(inventories): define the Initial Import store seams for #34"
```

---

## Task 7: Resolve every row's references, active-only, without creating one

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ImportReferenceResolver.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ImportReferenceResolverTests.cs`

Why: "resolves active Unit names or aliases and active Location names without creating references" is an acceptance criterion of its own, and it is also where a 5,000-row file either performs five lookups or five thousand.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ImportReferenceResolverTests.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ImportReferenceResolverTests
{
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly UnitId _eachId = new(Guid.NewGuid());
    private readonly UnitId _boxId = new(Guid.NewGuid());
    private readonly LocationId _shelfId = new(Guid.NewGuid());
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();

    public ImportReferenceResolverTests()
    {
        _references.AddUnit(_inventoryId, _eachId, "each", "piece", "pieces", "pc", "pcs");
        _references.AddUnit(_inventoryId, _boxId, "Cardboard Box", "boxes", "bx");
        _references.AddLocation(_inventoryId, _shelfId, "Shelf A");
    }

    private ImportReferenceResolver Resolver() => new(_references, _catalog);

    private static ImportRow Row(
        int lineNumber = 2,
        string name = "Steel Bolts",
        string unitTerm = "each",
        string? locationName = null,
        string? note = null) => new()
        {
            LineNumber = lineNumber,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = Quantity.Create(4m),
            UnitTerm = unitTerm,
            LocationName = locationName,
            Note = note,
        };

    private Task<ImportResolutionResult> ResolveAsync(params ImportRow[] rows) =>
        Resolver().ResolveAsync(_inventoryId, rows, CancellationToken.None);

    [Fact]
    public async Task A_Unit_resolves_by_its_canonical_name_or_by_any_active_alias()
    {
        var result = await ResolveAsync(Row(2, unitTerm: "Cardboard Box"), Row(3, unitTerm: "bx"), Row(4, unitTerm: "BOXES"));

        Assert.Empty(result.Errors);
        Assert.All(result.Rows, row => Assert.Equal(_boxId, row.UnitId));

        // The Inventory's own canonical name is what the preview shows, not what the file typed.
        Assert.All(result.Rows, row => Assert.Equal("Cardboard Box", row.UnitCanonicalName));
    }

    [Fact]
    public async Task A_Location_resolves_by_its_exact_active_name_however_it_is_cased()
    {
        var result = await ResolveAsync(Row(locationName: "  shelf   a "));

        Assert.Empty(result.Errors);
        Assert.Equal(_shelfId, Assert.Single(result.Rows).LocationId);
        Assert.Equal("Shelf A", result.Rows[0].LocationName);
    }

    [Fact]
    public async Task An_absent_Location_stays_absent_because_unlocated_is_not_a_reference()
    {
        var result = await ResolveAsync(Row(locationName: null));

        Assert.Null(Assert.Single(result.Rows).LocationId);
        Assert.Null(result.Rows[0].LocationName);
    }

    [Fact]
    public async Task An_unknown_Unit_is_reported_at_its_own_column_and_never_created()
    {
        var result = await ResolveAsync(Row(unitTerm: "crate"));

        Assert.Empty(result.Rows);
        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownUnit, error.Code);
        Assert.Equal(2, error.LineNumber);
        Assert.Equal(ImportContract.UnitColumn, error.ColumnIndex);
    }

    [Fact]
    public async Task An_unknown_Location_is_reported_at_its_own_column()
    {
        var result = await ResolveAsync(Row(locationName: "Bay 9"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(ImportErrorCode.UnknownLocation, error.Code);
        Assert.Equal(ImportContract.LocationColumn, error.ColumnIndex);
    }

    [Fact]
    public async Task A_retired_reference_is_exactly_as_unknown_as_one_that_never_existed()
    {
        _references.RetireUnit(_inventoryId, _boxId);

        var result = await ResolveAsync(Row(unitTerm: "Cardboard Box"));

        Assert.Equal(ImportErrorCode.UnknownUnit, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task An_unknown_reference_carries_bounded_deterministic_suggestions()
    {
        _catalog.AddUnit(_inventoryId, "Crate Large", []);
        _catalog.AddUnit(_inventoryId, "Crate Small", []);

        var result = await ResolveAsync(Row(unitTerm: "crate"));

        var error = Assert.Single(result.Errors);
        Assert.Equal(["Crate Large", "Crate Small"], error.Suggestions);
        Assert.True(error.Suggestions.Count <= IReferenceCatalogStore.MaxSuggestions);
    }

    [Fact]
    public async Task Every_unresolvable_row_is_reported_so_one_pass_fixes_the_file()
    {
        var result = await ResolveAsync(Row(2, unitTerm: "crate"), Row(3, locationName: "Bay 9"), Row(4, unitTerm: "drum"));

        Assert.Equal(3, result.Errors.Count);
        Assert.Equal([2, 3, 4], result.Errors.Select(error => error.LineNumber));
    }

    [Fact]
    public async Task One_distinct_term_is_looked_up_once_however_many_rows_use_it()
    {
        var rows = Enumerable.Range(0, 50).Select(index => Row(index + 2, unitTerm: "bx")).ToArray();

        var result = await ResolveAsync(rows);

        Assert.Empty(result.Errors);
        Assert.Equal(50, result.Rows.Count);
        Assert.Equal(1, _references.UnitResolutionCount);
    }

    [Fact]
    public async Task A_row_that_resolves_carries_everything_the_merge_needs()
    {
        var result = await ResolveAsync(Row(7, name: "Steel Bolts", unitTerm: "bx", locationName: "Shelf A", note: "Blue box"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(7, row.LineNumber);
        Assert.Equal("Steel Bolts", row.Name);
        Assert.Equal("steel bolts", row.NormalizedName);
        Assert.Equal("4", row.Quantity.ToInvariantText());
        Assert.Equal(_boxId, row.UnitId);
        Assert.Equal(_shelfId, row.LocationId);
        Assert.Equal("Blue box", row.Note);
    }
}
```

- [ ] **Step 2: Extend the reference double to count lookups**

`InMemoryInventoryReferenceStore` already exists and already supports retirement (added by #33). Add one counter so the caching claim is testable rather than asserted. In `tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryInventoryReferenceStore.cs`, add:

```csharp
    /// <summary>How many times a Unit reference was resolved, so a caller's caching claim can be proven rather than trusted.</summary>
    public int UnitResolutionCount { get; private set; }
```

and increment it as the first statement of `ResolveUnitAsync`:

```csharp
        UnitResolutionCount++;
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ImportReferenceResolverTests"`
Expected: FAIL to compile - `ImportReferenceResolver`, `ImportResolutionResult`, and `ImportReferenceError` do not exist.

- [ ] **Step 4: Write the resolver**

Create `src/MultiChannelAgent.Application/Inventories/ImportReferenceResolver.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// One import error, plus the bounded suggestions an unknown reference offers. Suggestions are empty
/// for every error that is not about a reference.
/// </summary>
public sealed record ImportReferenceError(ImportRowError Error, IReadOnlyList<string> Suggestions)
{
    public ImportErrorCode Code => Error.Code;

    public int LineNumber => Error.LineNumber;

    public int? ColumnIndex => Error.ColumnIndex;
}

/// <summary>The rows whose references resolved, and the errors for those that did not. Rows are empty whenever anything failed.</summary>
public sealed record ImportResolutionResult(IReadOnlyList<ResolvedImportRow> Rows, IReadOnlyList<ImportReferenceError> Errors);

/// <summary>
/// Resolves every row's Unit term and Location name to identities, using the shipped active-only
/// <see cref="IInventoryReferenceStore"/>, and reports the ones that do not resolve.
///
/// Nothing is ever created. #26 is explicit - "unknown Units and Locations reported instead of created
/// implicitly" - and creating one here would be an unreviewed reference-administration act by a
/// workflow nobody asked to administer references.
///
/// Each distinct term is resolved once and cached for the life of one validation, so a five-thousand
/// row file with three Units performs three lookups. The cache never outlives the call, so it can
/// never serve a reference that was retired since.
/// </summary>
public sealed class ImportReferenceResolver(IInventoryReferenceStore references, IReferenceCatalogStore catalog)
{
    public async Task<ImportResolutionResult> ResolveAsync(
        InventoryId inventoryId, IReadOnlyList<ImportRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var units = new Dictionary<string, (UnitId Id, string CanonicalName)?>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<string, (LocationId Id, string Name)?>(StringComparer.OrdinalIgnoreCase);

        var resolved = new List<ResolvedImportRow>(rows.Count);
        var errors = new List<ImportReferenceError>();

        foreach (var row in rows)
        {
            var unit = await ResolveUnitAsync(inventoryId, row.UnitTerm, units, cancellationToken);
            if (unit is null)
            {
                errors.Add(await UnknownAsync(
                    inventoryId, ReferenceKind.Unit, row.UnitTerm, ImportErrorCode.UnknownUnit,
                    row.LineNumber, ImportContract.UnitColumn, cancellationToken));
                continue;
            }

            (LocationId Id, string Name)? location = null;
            if (row.LocationName is { } locationName)
            {
                location = await ResolveLocationAsync(inventoryId, locationName, locations, cancellationToken);
                if (location is null)
                {
                    errors.Add(await UnknownAsync(
                        inventoryId, ReferenceKind.Location, locationName, ImportErrorCode.UnknownLocation,
                        row.LineNumber, ImportContract.LocationColumn, cancellationToken));
                    continue;
                }
            }

            resolved.Add(new ResolvedImportRow
            {
                LineNumber = row.LineNumber,
                Name = row.Name,
                NormalizedName = row.NormalizedName,
                Quantity = row.Quantity,
                UnitId = unit.Value.Id,
                UnitCanonicalName = unit.Value.CanonicalName,
                LocationId = location?.Id,
                LocationName = location?.Name,
                Note = row.Note,
            });
        }

        return errors.Count > 0
            ? new ImportResolutionResult([], errors)
            : new ImportResolutionResult(resolved, []);
    }

    private async Task<(UnitId Id, string CanonicalName)?> ResolveUnitAsync(
        InventoryId inventoryId,
        string term,
        Dictionary<string, (UnitId Id, string CanonicalName)?> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(term, out var cached))
        {
            return cached;
        }

        (UnitId Id, string CanonicalName)? resolved = null;

        if (await references.ResolveUnitAsync(inventoryId, term, cancellationToken) is { } unitId
            && await references.FindUnitCanonicalNameAsync(inventoryId, unitId, cancellationToken) is { } canonicalName)
        {
            resolved = (unitId, canonicalName);
        }

        cache[term] = resolved;
        return resolved;
    }

    private async Task<(LocationId Id, string Name)?> ResolveLocationAsync(
        InventoryId inventoryId,
        string name,
        Dictionary<string, (LocationId Id, string Name)?> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        (LocationId Id, string Name)? resolved = null;

        if (await references.ResolveLocationAsync(inventoryId, name, cancellationToken) is { } locationId
            && await references.FindLocationNameAsync(inventoryId, locationId, cancellationToken) is { } displayName)
        {
            resolved = (locationId, displayName);
        }

        cache[name] = resolved;
        return resolved;
    }

    private async Task<ImportReferenceError> UnknownAsync(
        InventoryId inventoryId,
        ReferenceKind kind,
        string reference,
        ImportErrorCode code,
        int lineNumber,
        int columnIndex,
        CancellationToken cancellationToken)
    {
        // Only ever reached after the caller has been authorized for this Inventory, and only ever
        // naming references the caller could list anyway, so suggestions disclose nothing new.
        var suggestions = await catalog.SuggestAsync(inventoryId, kind, reference, cancellationToken);

        return new ImportReferenceError(new ImportRowError(code, lineNumber, columnIndex), suggestions);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ImportReferenceResolverTests"`
Expected: PASS - every case in the class.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ImportReferenceResolver.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ImportReferenceResolverTests.cs \
        tests/MultiChannelAgent.Application.Tests/TestDoubles/Inventories/InMemoryInventoryReferenceStore.cs
git commit -m "feat(inventories): resolve import references active-only and never create one for #34"
```

---

## Task 8: Validate a file and store the exact preview

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/InitialImportService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/InitialImportServiceTests.cs`

Why: this is where the role matrix, the empty-state gate, the whole-file error report, and the stored proposal come together - and where "never partially import" becomes true, because nothing is written until every row has passed.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/InitialImportServiceTests.cs`:

```csharp
using System.Text;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InitialImportServiceTests
{
    private const string Header = "Name,Quantity,Unit,Location,Note";
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly UnitId _eachId = new(Guid.NewGuid());
    private readonly LocationId _shelfId = new(Guid.NewGuid());

    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryInventoryReferenceStore _references = new();
    private readonly InMemoryReferenceCatalogStore _catalog = new();
    private readonly InMemoryImportProposalStore _proposals = new();
    private readonly InMemoryStockEmptyStateReader _emptyState = new();

    public InitialImportServiceTests()
    {
        _references.AddUnit(_inventoryId, _eachId, "each", "piece", "pieces", "pc", "pcs");
        _references.AddLocation(_inventoryId, _shelfId, "Shelf A");
    }

    private InitialImportService Service() => new(
        new InventoryAuthorizationService(_inventories, _audits),
        _emptyState,
        new ImportReferenceResolver(_references, _catalog),
        _proposals);

    private void GrantMembership(MembershipRole role) =>
        _inventories.GrantMembership(_inventoryId, _participantId, role, Now);

    private Task<ImportValidationResult> ValidateAsync(string csv) =>
        Service().ValidateAsync(_participantId, _inventoryId, Encoding.UTF8.GetBytes(csv), Now, CancellationToken.None);

    private Task<ImportEligibilityResult> EligibilityAsync() =>
        Service().ReadEligibilityAsync(_participantId, _inventoryId, Now, CancellationToken.None);

    [Fact]
    public async Task A_non_member_is_told_nothing_that_would_reveal_the_Inventory_exists()
    {
        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(ImportResultKind.NotFound, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:NotAMember");
    }

    [Fact]
    public async Task A_Viewer_may_not_import_and_the_denial_is_audited()
    {
        GrantMembership(MembershipRole.Viewer);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(ImportResultKind.Forbidden, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
    }

    [Theory]
    [InlineData(MembershipRole.Editor)]
    [InlineData(MembershipRole.Owner)]
    public async Task An_Editor_and_an_Owner_may_both_import(MembershipRole role)
    {
        GrantMembership(role);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(ImportResultKind.Completed, result.Kind);
    }

    [Fact]
    public async Task Import_is_offered_only_while_the_Inventory_holds_no_Stock_at_all()
    {
        GrantMembership(MembershipRole.Editor);
        Assert.True((await EligibilityAsync()).View!.Eligible);

        // A zero-quantity Stock Entry is still a Stock Entry, which is exactly why this is not filtered.
        _emptyState.SetAnyStock(_inventoryId, true);

        var eligibility = await EligibilityAsync();
        Assert.False(eligibility.View!.Eligible);
        Assert.Equal("inventory_not_empty", eligibility.View.Reason);

        var validation = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");
        Assert.Equal(ImportResultKind.NotEmpty, validation.Kind);
    }

    [Fact]
    public async Task A_Viewer_is_not_even_told_whether_the_Inventory_is_empty()
    {
        GrantMembership(MembershipRole.Viewer);

        Assert.Equal(ImportResultKind.Forbidden, (await EligibilityAsync()).Kind);
    }

    [Fact]
    public async Task A_valid_file_previews_the_exact_normalized_entries_it_would_create()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync(
            $"{Header}\nSteel Bolts,4,,Shelf A,Blue box\nBrass Rivets,2.5,piece,,\nSTEEL bolts,6,,Shelf A,\n");

        Assert.Equal(ImportResultKind.Completed, result.Kind);
        var preview = result.View!;
        Assert.Equal(3, preview.SourceRowCount);
        Assert.Equal(2, preview.Entries.Count);

        var bolts = preview.Entries[0];
        Assert.Equal("Steel Bolts", bolts.Name);
        Assert.Equal("10", bolts.Quantity);
        Assert.Equal("each", bolts.UnitCanonicalName);
        Assert.Equal("Shelf A", bolts.LocationName);
        Assert.Equal("Blue box", bolts.Note);
        Assert.Equal([2, 4], bolts.SourceLineNumbers);

        Assert.Equal("Brass Rivets", preview.Entries[1].Name);
        Assert.Null(preview.Entries[1].LocationName);
    }

    [Fact]
    public async Task A_successful_validation_stores_a_pending_proposal_bound_to_everything_that_decided_it()
    {
        GrantMembership(MembershipRole.Editor);
        var csv = $"{Header}\nSteel Bolts,4,,,\n";

        var result = await ValidateAsync(csv);

        var stored = await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(_participantId, stored!.ParticipantId);
        Assert.Equal(_inventoryId, stored.InventoryId);
        Assert.Equal(FileDigest.Of(Encoding.UTF8.GetBytes(csv)), stored.FileDigest);
        Assert.Equal(EmptyStateVersion.Empty, stored.EmptyStateVersion);
        Assert.Equal(Now.AddMinutes(ImportProposal.LifetimeMinutes), stored.ExpiresAt);
        Assert.Equal(stored.FileDigest.Value, result.View!.FileDigest);

        // The plaintext token exists only in this answer; the row carries its hash.
        Assert.True(ConfirmationToken.IsWellFormed(result.View.Token));
        Assert.True(ConfirmationToken.Matches(stored.TokenHash, result.View.Token));
    }

    [Fact]
    public async Task The_raw_file_is_kept_for_the_proposal_and_nowhere_else()
    {
        GrantMembership(MembershipRole.Editor);
        var csv = $"{Header}\nSteel Bolts,4,,,\n";

        await ValidateAsync(csv);

        var stored = await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None);
        var raw = await _proposals.FindRawContentAsync(stored!.Id, CancellationToken.None);
        Assert.Equal(Encoding.UTF8.GetBytes(csv), raw!.Value.ToArray());
    }

    [Fact]
    public async Task Validating_again_replaces_this_Participants_own_pending_import()
    {
        GrantMembership(MembershipRole.Editor);

        var first = await ValidateAsync($"{Header}\nSteel Bolts,4,,,\n");
        var second = await ValidateAsync($"{Header}\nBrass Rivets,1,,,\n");

        Assert.True(second.View!.SupersededPrevious);
        Assert.NotEqual(first.View!.Token, second.View.Token);

        var pending = await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None);
        Assert.Equal("Brass Rivets", Assert.Single(pending!.Entries).Name);
    }

    [Fact]
    public async Task Every_actionable_error_comes_back_together_and_nothing_is_stored()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync(
            $"{Header}\n,4,,,\nSteel Bolts,nope,,,\nBrass Rivets,1,crate,,\nZinc,1,,Bay 9,\n");

        Assert.Equal(ImportResultKind.Invalid, result.Kind);
        Assert.Equal(
            ["missing_name", "invalid_quantity", "unknown_unit", "unknown_location"],
            result.Errors.Select(error => error.Code));
        Assert.Equal([2, 3, 4, 5], result.Errors.Select(error => error.LineNumber));
        Assert.Null(await _proposals.FindPendingAsync(_participantId, _inventoryId, CancellationToken.None));
    }

    [Fact]
    public async Task Row_errors_are_reported_before_references_are_even_looked_up()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync($"{Header}\n,4,crate,,\n");

        // One line, one answer: the row is unreadable, so its unknown Unit is not piled on top.
        Assert.Equal("missing_name", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Conflicting_Notes_on_equivalent_rows_are_reported_as_errors()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,,,Blue box\nSteel Bolts,4,,,Red box\n");

        Assert.Equal("conflicting_notes", Assert.Single(result.Errors).Code);
        Assert.Equal(ImportResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task An_unknown_reference_error_carries_its_bounded_suggestions()
    {
        GrantMembership(MembershipRole.Editor);
        _catalog.AddUnit(_inventoryId, "Crate Large", []);

        var result = await ValidateAsync($"{Header}\nSteel Bolts,4,crate,,\n");

        Assert.Equal(["Crate Large"], Assert.Single(result.Errors).Suggestions);
    }

    [Fact]
    public async Task An_answer_never_carries_more_than_the_bounded_number_of_errors_and_says_how_many_it_omitted()
    {
        GrantMembership(MembershipRole.Editor);
        var builder = new StringBuilder(Header).Append('\n');
        for (var row = 0; row < ImportContract.MaxReportedErrors + 25; row++)
        {
            builder.Append(",1,,,\n");
        }

        var result = await ValidateAsync(builder.ToString());

        Assert.Equal(ImportContract.MaxReportedErrors, result.Errors.Count);
        Assert.Equal(25, result.OmittedErrorCount);
    }

    [Fact]
    public async Task A_file_whose_envelope_is_broken_is_reported_without_any_row_noise()
    {
        GrantMembership(MembershipRole.Editor);

        var result = await ValidateAsync("Name,Quantity,Unit,Location,Colour\n,1,,,\n,1,,,\n");

        Assert.Equal("unknown_column", Assert.Single(result.Errors).Code);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~InitialImportServiceTests"`
Expected: FAIL to compile - `InitialImportService`, `ImportResultKind`, `ImportValidationResult`, and `ImportEligibilityResult` do not exist.

- [ ] **Step 3: Write the service**

Create `src/MultiChannelAgent.Application/Inventories/InitialImportService.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>The semantic shape of an Initial Import answer. Only these; nothing here invents a status.</summary>
public enum ImportResultKind
{
    Completed,

    /// <summary>No accessible Inventory. Deliberately identical whether it does not exist or is not this Participant's.</summary>
    NotFound,

    /// <summary>A member, but not an Editor or Owner.</summary>
    Forbidden,

    /// <summary>The Inventory already holds Stock, so there is nothing initial to import.</summary>
    NotEmpty,

    /// <summary>The file could not be understood. <see cref="ImportValidationResult.Errors"/> says why, for every line at once.</summary>
    Invalid,
}

/// <summary>Whether Initial Import is available, and when it is not, the one machine code saying why.</summary>
public sealed record ImportEligibilityView(bool Eligible, string? Reason);

public sealed record ImportEligibilityResult(ImportResultKind Kind, ImportEligibilityView? View);

/// <summary>One entry the import would create, exactly as it will be created.</summary>
public sealed record ImportPreviewRowView(
    string Name,
    string Quantity,
    string UnitCanonicalName,
    string? LocationName,
    string? Note,
    IReadOnlyList<int> SourceLineNumbers);

/// <summary>
/// The exact normalized preview, plus the one-time token that confirms it. The token is the only
/// place the plaintext ever exists; the stored proposal keeps its hash.
/// </summary>
public sealed record ImportPreviewView(
    string Token,
    string FileDigest,
    int SourceRowCount,
    IReadOnlyList<ImportPreviewRowView> Entries,
    bool SupersededPrevious,
    DateTimeOffset ExpiresAt);

/// <summary>One reported problem: its machine code, where it is, and any bounded suggestions.</summary>
public sealed record ImportErrorView(string Code, int LineNumber, int? ColumnIndex, IReadOnlyList<string> Suggestions);

/// <summary>
/// The answer to a validation. Exactly one of <see cref="View"/> and <see cref="Errors"/> carries
/// anything: a file either previews cleanly or is reported, never both.
/// </summary>
public sealed record ImportValidationResult(
    ImportResultKind Kind,
    ImportPreviewView? View,
    IReadOnlyList<ImportErrorView> Errors,
    int OmittedErrorCount)
{
    public static ImportValidationResult Refused(ImportResultKind kind) => new(kind, null, [], 0);
}

/// <summary>
/// Initial Import's eligibility and validation half: authorize, gate on the empty Inventory, read the
/// file, resolve its references, merge its equivalent rows, and either report everything that is
/// wrong or store the exact proposal a confirmation will apply.
///
/// Nothing is written until every row has passed - #26 says "Validate the whole file and report all
/// actionable row/column errors. Never partially import." - and the phases are ordered so a
/// Participant is never told about an unknown Unit on a line whose Name is missing, because the line
/// they need to fix is the same line either way.
/// </summary>
public sealed class InitialImportService(
    InventoryAuthorizationService authorizationService,
    IStockEmptyStateReader emptyStateReader,
    ImportReferenceResolver resolver,
    IImportProposalStore proposalStore)
{
    public async Task<ImportEligibilityResult> ReadEligibilityAsync(
        ParticipantId participantId, InventoryId inventoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId: null, now, cancellationToken);

        if (RefusalFor(authorization.Outcome) is { } refusal)
        {
            return new ImportEligibilityResult(refusal, null);
        }

        return await emptyStateReader.AnyStockAsync(inventoryId, cancellationToken)
            ? new ImportEligibilityResult(ImportResultKind.Completed, new ImportEligibilityView(false, "inventory_not_empty"))
            : new ImportEligibilityResult(ImportResultKind.Completed, new ImportEligibilityView(true, null));
    }

    public async Task<ImportValidationResult> ValidateAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        ReadOnlyMemory<byte> content,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId: null, now, cancellationToken);

        if (RefusalFor(authorization.Outcome) is { } refusal)
        {
            return ImportValidationResult.Refused(refusal);
        }

        // Asked before the file is even read: importing into an Inventory that already holds Stock is
        // not a validation failure, it is a workflow that does not apply.
        if (await emptyStateReader.AnyStockAsync(inventoryId, cancellationToken))
        {
            return ImportValidationResult.Refused(ImportResultKind.NotEmpty);
        }

        var digest = FileDigest.Of(content.Span);

        // Phase 1: the envelope. A file whose headers or quoting are wrong produces no row errors at
        // all, because rows read against a broken envelope would be noise rather than help.
        var read = CsvImportDocument.Read(content.Span);
        if (read.Document is null)
        {
            return Invalid(read.Errors.Select(Plain));
        }

        // Phase 2: the rows, on their own terms.
        var rows = new List<ImportRow>(read.Document.Records.Count);
        var rowErrors = new List<ImportRowError>(read.Errors);

        foreach (var record in read.Document.Records)
        {
            if (ImportRow.TryCreate(record, out var row, out var errors))
            {
                rows.Add(row!);
            }
            else
            {
                rowErrors.AddRange(errors);
            }
        }

        if (rowErrors.Count > 0)
        {
            return Invalid(Ordered(rowErrors).Select(Plain));
        }

        // Phase 3: this Inventory's references.
        var resolution = await resolver.ResolveAsync(inventoryId, rows, cancellationToken);
        if (resolution.Errors.Count > 0)
        {
            return Invalid(resolution.Errors.Select(error => new ImportErrorView(
                ImportFacts.ToMachineText(error.Code), error.LineNumber, error.ColumnIndex, error.Suggestions)));
        }

        // Phase 4: equivalence and Notes.
        var merged = ImportMergePlan.Create(resolution.Rows);
        if (merged.Errors.Count > 0)
        {
            return Invalid(Ordered(merged.Errors).Select(Plain));
        }

        var token = ConfirmationToken.Issue();
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId,
            inventoryId,
            digest,
            merged.Entries,
            EmptyStateVersion.Empty,
            now);

        var superseded = await proposalStore.StoreAsync(proposal, content, now, cancellationToken);

        return new ImportValidationResult(
            ImportResultKind.Completed,
            new ImportPreviewView(
                token,
                digest.Value,
                read.Document.Records.Count,
                [.. merged.Entries.Select(ToPreviewRow)],
                superseded,
                proposal.ExpiresAt),
            [],
            0);
    }

    private static ImportPreviewRowView ToPreviewRow(ImportEntry entry) => new(
        entry.Name,
        entry.Quantity.ToInvariantText(),
        entry.UnitCanonicalName,
        entry.LocationName,
        entry.Note,
        entry.SourceLineNumbers);

    /// <summary>Source order, so a Participant reads the report the way they read the file.</summary>
    private static IEnumerable<ImportRowError> Ordered(IEnumerable<ImportRowError> errors) =>
        errors.OrderBy(error => error.LineNumber).ThenBy(error => error.ColumnIndex ?? -1);

    private static ImportErrorView Plain(ImportRowError error) =>
        new(ImportFacts.ToMachineText(error.Code), error.LineNumber, error.ColumnIndex, []);

    /// <summary>
    /// Bounds the report at <see cref="ImportContract.MaxReportedErrors"/> and states exactly how many
    /// were omitted. The promise is that a Participant can fix the file once, not that every one of
    /// five thousand broken rows is enumerated - and an exact count is what keeps that honest.
    /// </summary>
    private static ImportValidationResult Invalid(IEnumerable<ImportErrorView> errors)
    {
        var all = errors.ToList();
        var reported = all.Take(ImportContract.MaxReportedErrors).ToList();

        return new ImportValidationResult(ImportResultKind.Invalid, null, reported, all.Count - reported.Count);
    }

    private static ImportResultKind? RefusalFor(InventoryAuthorizationOutcome outcome) => outcome switch
    {
        InventoryAuthorizationOutcome.NotFound => ImportResultKind.NotFound,
        InventoryAuthorizationOutcome.Forbidden => ImportResultKind.Forbidden,
        _ => null,
    };
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~InitialImportServiceTests"`
Expected: PASS - every case in the class.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/InitialImportService.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/InitialImportServiceTests.cs
git commit -m "feat(inventories): validate a whole import file and store its exact preview for #34"
```

---

## Task 9: Confirm the stored proposal, and never reparse

> **Implementation note:** Task 9's final reviewed contract exposes the opaque `ProposalId` in the
> preview and requires it on confirm/reject. Confirmation authorizes first, then checks the derived
> ledger identity before looking for a pending proposal, so a lost-response retry re-reports the
> completed import. `RecordedImport` therefore carries `ActorId`, and replay returns `NotFound` when
> that actor does not match the current Participant. The remaining persistence and HTTP tasks below
> include this reviewed contract even where the original illustrative Task 9 snippets predate it.

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ImportConfirmationService.cs`
- Test: `tests/MultiChannelAgent.Application.Tests/Inventories/ImportConfirmationServiceTests.cs`

Why: "Confirmation creates all entries atomically and idempotently or none, without reparsing" is one acceptance criterion, and every part of it is decided here: the replay lookup comes first, the token is verified, the stored rows are handed over untouched, and the raw file is never read.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.Application.Tests/Inventories/ImportConfirmationServiceTests.cs`:

```csharp
using System.Text;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class ImportConfirmationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _participantId = new(Guid.NewGuid());
    private readonly UnitId _eachId = new(Guid.NewGuid());

    private readonly InMemoryInventoryStore _inventories = new(_ => "Participant");
    private readonly InMemoryInventoryAuthorizationAuditStore _audits = new();
    private readonly InMemoryImportProposalStore _proposals = new();
    private readonly InMemoryStockEmptyStateReader _emptyState = new();
    private readonly InMemoryImportExecutionStore _execution;

    public ImportConfirmationServiceTests()
    {
        _execution = new InMemoryImportExecutionStore(_proposals, _emptyState);
        _inventories.GrantMembership(_inventoryId, _participantId, MembershipRole.Editor, Now);
    }

    private ImportConfirmationService Service() => new(
        new InventoryAuthorizationService(_inventories, _audits), _proposals, _execution);

    private async Task<(ImportProposal Proposal, string Token)> StorePendingAsync(int entryCount = 1)
    {
        var token = ConfirmationToken.Issue();
        var entries = Enumerable.Range(0, entryCount).Select(index => new ImportEntry
        {
            LineNumber = index + 2,
            SourceLineNumbers = [index + 2],
            Name = $"Item {index}",
            NormalizedName = $"item {index}",
            Quantity = Quantity.Create(4m),
            UnitId = _eachId,
            UnitCanonicalName = "each",
            LocationId = null,
            LocationName = null,
            Note = null,
        }).ToList();

        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(token),
            _participantId,
            _inventoryId,
            FileDigest.Of(Encoding.UTF8.GetBytes("Name,Quantity,Unit,Location,Note\n")),
            entries,
            EmptyStateVersion.Empty,
            Now);

        await _proposals.StoreAsync(proposal, new byte[] { 1, 2, 3 }, Now, CancellationToken.None);

        return (proposal, token);
    }

    private Task<ImportConfirmationResult> ConfirmAsync(string? token, DateTimeOffset? at = null) =>
        Service().ConfirmAsync(_participantId, _inventoryId, token, at ?? Now, CancellationToken.None);

    [Fact]
    public async Task Confirming_creates_every_entry_exactly_once_and_audits_one_fact()
    {
        var (proposal, token) = await StorePendingAsync(entryCount: 3);

        var result = await ConfirmAsync(token);

        Assert.Equal(ImportConfirmationResultKind.Completed, result.Kind);
        Assert.Equal(3, result.View!.CreatedEntryCount);
        Assert.Equal(proposal.FileDigest.Value, result.View.FileDigest);
        Assert.Equal(3, _execution.CreatedEntries.Count);
        Assert.Equal("Import:Completed", Assert.Single(_execution.Audits).OutcomeCode);
        Assert.Equal(
            ImportProposalStatus.Confirmed,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Confirmation_applies_the_stored_rows_and_never_reads_the_file()
    {
        var (proposal, token) = await StorePendingAsync();

        await ConfirmAsync(token);

        // The raw upload is discarded by the settle, and nothing re-read it on the way there.
        Assert.Null(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Equal("Item 0", Assert.Single(_execution.CreatedEntries).Name);
    }

    [Fact]
    public async Task The_token_is_single_use()
    {
        var (_, token) = await StorePendingAsync();

        Assert.Equal(ImportConfirmationResultKind.Completed, (await ConfirmAsync(token)).Kind);

        var reused = await ConfirmAsync(token);
        Assert.Equal(ImportConfirmationResultKind.NotFound, reused.Kind);
        Assert.Single(_execution.CreatedEntries);
    }

    [Fact]
    public async Task A_wrong_token_leaves_the_proposal_pending_so_a_typo_destroys_nothing()
    {
        var (proposal, _) = await StorePendingAsync();

        var result = await ConfirmAsync(ConfirmationToken.Issue());

        Assert.Equal(ImportConfirmationResultKind.Invalid, result.Kind);
        Assert.Equal("proposal_token_mismatch", result.Code);
        Assert.Equal(
            ImportProposalStatus.Pending,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task An_expired_proposal_is_settled_and_answered_as_if_it_were_not_there()
    {
        var (proposal, token) = await StorePendingAsync();

        var result = await ConfirmAsync(token, Now.AddMinutes(ImportProposal.LifetimeMinutes));

        Assert.Equal(ImportConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("proposal_expired", result.Code);
        Assert.Equal(
            ImportProposalStatus.Expired,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task Confirming_into_an_Inventory_that_is_no_longer_empty_creates_nothing()
    {
        var (proposal, token) = await StorePendingAsync();
        _emptyState.SetAnyStock(_inventoryId, true);

        var result = await ConfirmAsync(token);

        Assert.Equal(ImportConfirmationResultKind.Conflict, result.Kind);
        Assert.Equal("state_changed", result.Code);
        Assert.Empty(_execution.CreatedEntries);
        Assert.Equal(
            ImportProposalStatus.Conflicted,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_replayed_confirmation_re_reports_what_it_did_instead_of_importing_twice()
    {
        var (proposal, token) = await StorePendingAsync(entryCount: 2);
        await ConfirmAsync(token);

        // The operation identity is derived from the proposal, so a re-driven confirmation finds it.
        var replay = await Service().ReplayAsync(
            _participantId, _inventoryId, proposal.Id, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Completed, replay.Kind);
        Assert.Equal(2, replay.View!.CreatedEntryCount);
        Assert.Equal(2, _execution.CreatedEntries.Count);
        Assert.Single(_execution.Audits);
    }

    [Fact]
    public async Task Replay_does_not_disclose_another_Participants_import()
    {
        var (proposal, token) = await StorePendingAsync();
        await ConfirmAsync(token);
        var otherEditor = new ParticipantId(Guid.NewGuid());
        _inventories.GrantMembership(_inventoryId, otherEditor, MembershipRole.Editor, Now);

        var replay = await Service().ReplayAsync(
            otherEditor, _inventoryId, proposal.Id, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.NotFound, replay.Kind);
    }

    [Fact]
    public async Task Rejecting_settles_the_proposal_discards_the_file_and_creates_nothing()
    {
        var (proposal, token) = await StorePendingAsync();

        var result = await Service().RejectAsync(_participantId, _inventoryId, token, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Rejected, result.Kind);
        Assert.Equal(
            ImportProposalStatus.Rejected,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task Rejecting_needs_no_token_because_declining_is_always_safe()
    {
        var (proposal, _) = await StorePendingAsync();

        var result = await Service().RejectAsync(_participantId, _inventoryId, null, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Rejected, result.Kind);
        Assert.Equal(
            ImportProposalStatus.Rejected,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Another_Participants_pending_import_is_unreachable()
    {
        var (_, token) = await StorePendingAsync();
        var stranger = new ParticipantId(Guid.NewGuid());
        _inventories.GrantMembership(_inventoryId, stranger, MembershipRole.Editor, Now);

        var result = await Service().ConfirmAsync(stranger, _inventoryId, token, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.NotFound, result.Kind);
        Assert.Empty(_execution.CreatedEntries);
    }

    [Fact]
    public async Task A_Viewer_may_not_confirm_and_the_denial_is_audited()
    {
        var (_, token) = await StorePendingAsync();
        var viewer = new ParticipantId(Guid.NewGuid());
        _inventories.GrantMembership(_inventoryId, viewer, MembershipRole.Viewer, Now);

        var result = await Service().ConfirmAsync(viewer, _inventoryId, token, Now, CancellationToken.None);

        Assert.Equal(ImportConfirmationResultKind.Forbidden, result.Kind);
        Assert.Contains(_audits.RecordedFacts, fact => fact.OutcomeCode == "Denied:InsufficientRole");
        Assert.Empty(_execution.CreatedEntries);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj --filter "FullyQualifiedName~ImportConfirmationServiceTests"`
Expected: FAIL to compile - `ImportConfirmationService`, `ImportConfirmationResultKind`, and `ImportConfirmationResult` do not exist.

- [ ] **Step 3: Write the service**

Create `src/MultiChannelAgent.Application/Inventories/ImportConfirmationService.cs`:

```csharp
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>The semantic shape of a confirmation or rejection.</summary>
public enum ImportConfirmationResultKind
{
    /// <summary>The import ran - or had already run under this identity.</summary>
    Completed,

    /// <summary>The Participant cancelled it, and nothing was created.</summary>
    Rejected,

    /// <summary>There is no import this Participant may act on here. Identical whether it never existed, belongs to someone else, or was already settled.</summary>
    NotFound,

    Forbidden,

    /// <summary>It expired, or the Inventory is no longer empty. Nothing was created.</summary>
    Conflict,

    /// <summary>The token did not match. The proposal is deliberately left pending.</summary>
    Invalid,
}

/// <summary>What a completed import did. Semantic only: a count and the digest of the file it came from.</summary>
public sealed record ImportConfirmationView(int CreatedEntryCount, string FileDigest);

public sealed record ImportConfirmationResult(ImportConfirmationResultKind Kind, string Code, ImportConfirmationView? View = null);

/// <summary>
/// Executes or cancels the one pending Initial Import a Participant has for one Inventory.
///
/// Three things must hold before anything is created, and each is an acceptance criterion: the
/// Participant is still an Editor or Owner of the Inventory; the presented token matches the stored
/// hash; and the proposal is still pending, still bound to this Participant and Inventory, and not
/// yet expired.
///
/// Execution then consumes the proposal and creates every entry in one transaction, so two
/// confirmations can never both run. Nothing is ever re-read or re-parsed: the stored rows are handed
/// to the writer exactly as they were previewed, which is what #34 means by "without reparsing".
/// </summary>
public sealed class ImportConfirmationService(
    InventoryAuthorizationService authorizationService,
    IImportProposalStore proposalStore,
    IImportExecutionStore executionStore)
{
    public async Task<ImportConfirmationResult> ConfirmAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? presentedToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(participantId, inventoryId, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var pending = await proposalStore.FindPendingAsync(participantId, inventoryId, cancellationToken);
        if (pending is null || !pending.BelongsTo(participantId, inventoryId))
        {
            return NotFound();
        }

        if (pending.IsExpired(now))
        {
            await proposalStore.SettleAsync(pending.Id, ImportProposalStatus.Expired, now, cancellationToken);
            return new ImportConfirmationResult(ImportConfirmationResultKind.Conflict, "proposal_expired");
        }

        // A wrong token deliberately leaves the proposal pending. The token is 256 bits, so there is no
        // brute-force attack to defend against by burning the Participant's own reviewed work - and a
        // mistyped confirmation should not destroy an import they still mean to run.
        if (!ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return new ImportConfirmationResult(ImportConfirmationResultKind.Invalid, "proposal_token_mismatch");
        }

        return await ExecuteAsync(participantId, inventoryId, pending, now, cancellationToken);
    }

    /// <summary>
    /// Re-reports what a confirmation already did, by the identity derived from its proposal. This is
    /// what makes a re-driven confirmation - a retried request, a replica that lost a race to its own
    /// twin - safe: the ledger, not the proposal, is the authoritative record, and by replay time the
    /// proposal has been consumed.
    /// </summary>
    public async Task<ImportConfirmationResult> ReplayAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        ImportProposalId proposalId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(participantId, inventoryId, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var recorded = await executionStore.FindRecordedAsync(
            inventoryId, ImportOperationId.DeriveForProposal(proposalId), cancellationToken);

        return recorded is null || recorded.ActorId != participantId ? NotFound() : Completed(recorded);
    }

    public async Task<ImportConfirmationResult> RejectAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? presentedToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await AuthorizeAsync(participantId, inventoryId, now, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var pending = await proposalStore.FindPendingAsync(participantId, inventoryId, cancellationToken);
        if (pending is null || !pending.BelongsTo(participantId, inventoryId))
        {
            return NotFound();
        }

        // A token is optional when cancelling: declining is always safe, and nobody should have to
        // quote a token to stop something happening. Presented, it must still be the right one, so a
        // stale cancel cannot settle the import that replaced it.
        if (presentedToken is not null && !ConfirmationToken.Matches(pending.TokenHash, presentedToken))
        {
            return new ImportConfirmationResult(ImportConfirmationResultKind.Invalid, "proposal_token_mismatch");
        }

        return await proposalStore.SettleAsync(pending.Id, ImportProposalStatus.Rejected, now, cancellationToken)
            ? new ImportConfirmationResult(ImportConfirmationResultKind.Rejected, "rejected")
            : NotFound();
    }

    private async Task<ImportConfirmationResult> ExecuteAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        ImportProposal pending,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await executionStore.ApplyAsync(
            new ImportExecutionCommand
            {
                OperationId = pending.ExecutionOperationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConsumesProposalId = pending.Id,
                FileDigest = pending.FileDigest,

                // Exactly what was previewed. Nothing is re-resolved, re-merged, or re-read.
                Entries = pending.Entries,
                EmptyStateVersion = pending.EmptyStateVersion,
                Now = now,
            },
            cancellationToken);

        if (stored.Outcome == ImportExecutionOutcome.Conflict)
        {
            // The Inventory is no longer the empty one that was reviewed, and nothing was created. The
            // proposal can never commit now, so it is settled rather than left to conflict again.
            await proposalStore.SettleAsync(pending.Id, ImportProposalStatus.Conflicted, now, cancellationToken);
            return new ImportConfirmationResult(ImportConfirmationResultKind.Conflict, "state_changed");
        }

        return Completed(stored.Recorded!);
    }

    private async Task<ImportConfirmationResult?> AuthorizeAsync(
        ParticipantId participantId, InventoryId inventoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId: null, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.NotFound => NotFound(),
            InventoryAuthorizationOutcome.Forbidden =>
                new ImportConfirmationResult(ImportConfirmationResultKind.Forbidden, "forbidden"),
            _ => null,
        };
    }

    private static ImportConfirmationResult Completed(RecordedImport recorded) => new(
        ImportConfirmationResultKind.Completed,
        "completed",
        new ImportConfirmationView(recorded.CreatedEntryCount, recorded.FileDigest.Value));

    private static ImportConfirmationResult NotFound() =>
        new(ImportConfirmationResultKind.NotFound, "proposal_not_found");
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.Application.Tests/MultiChannelAgent.Application.Tests.csproj`
Expected: PASS - every Application test, including the shipped ones.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ImportConfirmationService.cs \
        tests/MultiChannelAgent.Application.Tests/Inventories/ImportConfirmationServiceTests.cs
git commit -m "feat(inventories): confirm a stored import without ever reparsing it for #34"
```

---

## Task 10: Persist the import proposal, its file, and its ledger

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ImportProposalEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ImportUploadEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ImportOperationEntity.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ImportProposalEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ImportUploadEntityConfiguration.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ImportOperationEntityConfiguration.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`
- Generate: `src/MultiChannelAgent.Infrastructure/Persistence/Migrations/*_AddInitialImport.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportRelationalModelTests.cs`

Why: "one pending import per Participant and Inventory" and "the raw file goes when the proposal goes" are guarantees the schema can hold and code can only promise. This is also where the single cascade path is decided - SQL Server rejects a model with two.

- [ ] **Step 1: Write the failing model test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportRelationalModelTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free assertions on the compiled EF Core model for the rules Initial Import rests on:
/// one pending import per Participant and Inventory enforced by the database rather than by
/// agreement, a raw upload that cannot outlive its proposal, and exactly one cascade path into each
/// new table - SQL Server rejects a model with two, as the shipped
/// <see cref="UnitTermRelationalModelTests"/> records from a real CI failure.
/// </summary>
public sealed class ImportRelationalModelTests
{
    private static IModel BuildModel() =>
        new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlServer("Server=none").Options).Model;

    [Fact]
    public void One_import_may_be_pending_per_Participant_and_Inventory_and_the_database_says_so()
    {
        var index = BuildModel()
            .FindEntityType(typeof(ImportProposalEntity))!
            .GetIndexes()
            .Single(candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual(new[]
                {
                    nameof(ImportProposalEntity.ParticipantId),
                    nameof(ImportProposalEntity.InventoryId),
                }));

        Assert.True(index.IsUnique);
        Assert.Equal($"Status = '{nameof(ImportProposalStatus.Pending)}'", index.GetFilter());
    }

    [Fact]
    public void A_token_can_never_back_two_imports()
    {
        var index = BuildModel()
            .FindEntityType(typeof(ImportProposalEntity))!
            .GetIndexes()
            .Single(candidate => candidate.Properties.Single().Name == nameof(ImportProposalEntity.TokenHash));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void A_raw_upload_belongs_to_exactly_one_proposal_and_cannot_outlive_it()
    {
        var upload = BuildModel().FindEntityType(typeof(ImportUploadEntity))!;

        Assert.Equal([nameof(ImportUploadEntity.ProposalId)], upload.FindPrimaryKey()!.Properties.Select(p => p.Name));

        var foreignKey = Assert.Single(upload.GetForeignKeys());
        Assert.Equal(typeof(ImportProposalEntity), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void An_import_ledger_row_is_keyed_by_its_operation_and_unique_per_proposal()
    {
        var operation = BuildModel().FindEntityType(typeof(ImportOperationEntity))!;
        Assert.NotNull(operation.FindProperty(nameof(ImportOperationEntity.ActorId)));

        Assert.Equal(
            [nameof(ImportOperationEntity.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(property => property.Name));

        var index = operation.GetIndexes()
            .Single(candidate => candidate.Properties.Single().Name == nameof(ImportOperationEntity.ProposalId));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void The_import_ledger_survives_its_proposal_because_replay_must_outlive_it()
    {
        // The ledger is the authoritative record of what happened. A settled proposal is swept after a
        // day; the ledger row is not, so a re-driven confirmation can still be told what it did.
        var operation = BuildModel().FindEntityType(typeof(ImportOperationEntity))!;

        Assert.DoesNotContain(
            operation.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ImportProposalEntity));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ImportRelationalModelTests"`
Expected: FAIL to compile - the three entities do not exist. (This test needs no Docker; it inspects the compiled model.)

- [ ] **Step 3: Add the entities**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ImportProposalEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one pending Initial Import. It carries the hash of its single-use token -
/// never the token - the binding that makes it reachable by exactly one Participant in exactly one
/// Inventory, the digest of the file it came from, and the exact entries it will create.
///
/// Only <see cref="Status"/> ever changes after insert, and only ever from <c>Pending</c> to a
/// terminal value, which is what makes single use enforceable by a guarded update rather than by
/// hoping two callers do not race.
/// </summary>
public sealed class ImportProposalEntity
{
    public Guid ProposalId { get; set; }

    /// <summary>SHA-256 of the token, as 64 lowercase hexadecimal characters. Unique, so a token can never back two imports.</summary>
    public required string TokenHash { get; set; }

    public Guid ParticipantId { get; set; }

    public Guid InventoryId { get; set; }

    /// <summary>SHA-256 of the uploaded bytes, as 64 lowercase hexadecimal characters.</summary>
    public required string FileDigest { get; set; }

    /// <summary>The <c>ImportProposalStatus</c> as text, so the filtered unique index can be written in provider-neutral SQL.</summary>
    public required string Status { get; set; }

    /// <summary>The exact entries this import will create, serialized (see <c>ImportProposalMapper</c>).</summary>
    public required string EntriesJson { get; set; }

    /// <summary>The number of Stock Entries the Inventory held when this was decided. Always zero, and re-asserted at execution.</summary>
    public int ExpectedStockEntryCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// <see cref="ExpiresAt"/> as UTC ticks. The expiry sweep compares and orders by it, and a
    /// DateTimeOffset is not comparable on every relational provider this model runs on.
    /// </summary>
    public long ExpiresAtTicks { get; set; }

    /// <summary>When it left <c>Pending</c>; null while it is still pending.</summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary><see cref="SettledAt"/> as UTC ticks, for the same reason as <see cref="ExpiresAtTicks"/>.</summary>
    public long? SettledAtTicks { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ImportUploadEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The uploaded bytes, for exactly as long as the proposal they belong to is pending.
///
/// They are kept so a reloaded preview can be shown again without a re-upload, and so "the raw CSV is
/// discarded" is one durable, testable fact rather than a claim about process memory. Nothing else in
/// the system ever reads this table, and every path out of <c>Pending</c> deletes the row.
/// </summary>
public sealed class ImportUploadEntity
{
    public Guid ProposalId { get; set; }

    /// <summary>The exact bytes as received, bounded by <c>ImportContract.MaxUploadBytes</c> before they ever get here.</summary>
    public required byte[] Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Entities/ImportOperationEntity.cs`:

```csharp
namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable ledger header for one applied Initial Import. Its whole purpose is retry safety: a
/// re-driven confirmation finds its own row here and re-reports what it did, so one import can never
/// be applied twice by a retried request, a restart, or a competing replica.
///
/// It deliberately carries no foreign key to the proposal. Settled proposals are swept after a day
/// and the ledger must outlive them, because the ledger - not the proposal - is the authoritative
/// record of what happened.
/// </summary>
public sealed class ImportOperationEntity
{
    public Guid OperationId { get; set; }

    public Guid InventoryId { get; set; }

    /// <summary>The proposal this consumed. Unique, which is what makes the replay lookup exact.</summary>
    public Guid ProposalId { get; set; }

    /// <summary>The Participant who applied it, retained so replay cannot disclose another Editor's import.</summary>
    public Guid ActorId { get; set; }

    /// <summary>The digest of the file that produced it - never the file.</summary>
    public required string FileDigest { get; set; }

    public int CreatedEntryCount { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}
```

- [ ] **Step 4: Configure them**

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ImportProposalEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ImportProposalEntityConfiguration : IEntityTypeConfiguration<ImportProposalEntity>
{
    private const string PendingStatus = nameof(ImportProposalStatus.Pending);

    public void Configure(EntityTypeBuilder<ImportProposalEntity> builder)
    {
        builder.ToTable("ImportProposals");
        builder.HasKey(e => e.ProposalId);

        builder.Property(e => e.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.FileDigest).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();
        builder.Property(e => e.EntriesJson).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // The Participant is referenced without a cascade: deleting a Participant must not silently
        // take reviewed import work with it, and this is also the second path into the table, which
        // SQL Server would refuse as a second cascade.
        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(e => e.ParticipantId)
            .OnDelete(DeleteBehavior.NoAction);

        // At most one pending import per Participant and Inventory, enforced by the database rather
        // than by two code paths agreeing. The filter is plain unquoted SQL text, exactly like the
        // shipped pending-proposal filter, so it is valid on both SQL Server and SQLite.
        builder.HasIndex(e => new { e.ParticipantId, e.InventoryId })
            .IsUnique()
            .HasFilter($"Status = '{PendingStatus}'");

        // A token can never back two imports.
        builder.HasIndex(e => e.TokenHash).IsUnique();

        // Backs the expiry sweep and the settled-retention sweep.
        builder.HasIndex(e => new { e.Status, e.ExpiresAtTicks });
        builder.HasIndex(e => e.SettledAtTicks);
    }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ImportUploadEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ImportUploadEntityConfiguration : IEntityTypeConfiguration<ImportUploadEntity>
{
    public void Configure(EntityTypeBuilder<ImportUploadEntity> builder)
    {
        builder.ToTable("ImportUploads");

        // Keyed by the proposal, so one proposal can hold at most one upload and the two are found
        // together without a second identity to keep in step.
        builder.HasKey(e => e.ProposalId);

        builder.Property(e => e.Content).IsRequired();

        // The only cascade path into this table, and the only relationship it has: an upload that
        // outlived its proposal would be exactly the retained raw file the specification forbids.
        builder.HasOne<ImportProposalEntity>()
            .WithMany()
            .HasForeignKey(e => e.ProposalId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

Create `src/MultiChannelAgent.Infrastructure/Persistence/Configurations/ImportOperationEntityConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ImportOperationEntityConfiguration : IEntityTypeConfiguration<ImportOperationEntity>
{
    public void Configure(EntityTypeBuilder<ImportOperationEntity> builder)
    {
        builder.ToTable("ImportOperations");

        // The derived operation identity is the key, so applying the same import twice is a primary
        // key violation rather than a second import.
        builder.HasKey(e => e.OperationId);

        builder.Property(e => e.FileDigest).HasMaxLength(64).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(e => e.ActorId)
            .OnDelete(DeleteBehavior.NoAction);

        // Deliberately no relationship to the proposal: settled proposals are swept after a day and
        // the ledger must outlive them.
        builder.HasIndex(e => e.ProposalId).IsUnique();
    }
}
```

- [ ] **Step 5: Register the sets**

In `src/MultiChannelAgent.Infrastructure/Persistence/MultiChannelAgentDbContext.cs`, add beside the shipped `DbSet` properties:

```csharp
    public DbSet<ImportProposalEntity> ImportProposals => Set<ImportProposalEntity>();

    public DbSet<ImportUploadEntity> ImportUploads => Set<ImportUploadEntity>();

    public DbSet<ImportOperationEntity> ImportOperations => Set<ImportOperationEntity>();
```

- [ ] **Step 6: Generate the migration**

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add AddInitialImport \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --output-dir Persistence/Migrations
```

Confirm the generated migration creates `ImportProposals`, `ImportUploads`, and `ImportOperations` with the indexes configured above - including the `Status = 'Pending'` filter on the composite unique index - and nothing else. No backfill is needed: all three tables are new and start empty.

- [ ] **Step 7: Run the model test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ImportRelationalModelTests|FullyQualifiedName~UnitTermRelationalModelTests|FullyQualifiedName~ReferenceRelationalModelTests"`
Expected: PASS. The shipped model tests must still pass unchanged - nothing about the existing schema moved.

Run: `dotnet ef migrations has-pending-model-changes --project src/MultiChannelAgent.Infrastructure --startup-project src/MultiChannelAgent.Infrastructure`
Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 8: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Persistence \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ImportRelationalModelTests.cs
git commit -m "feat(infrastructure): persist the import proposal, its file, and its ledger for #34"
```

---

## Task 11: Store the proposal and discard the file with it

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/ImportProposalMapper.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlImportProposalStore.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockEmptyStateReader.cs`
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryAuditRetentionStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportProposalStoreTests.cs`

Why: the entries must round-trip exactly - what was previewed is what commits - and "one pending per Participant and Inventory" and "the file goes when the proposal goes" have to be true against a real database, not just in a double.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportProposalStoreTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Proves against real SQL Server, under production migrations, the invariants the pending import
/// store exists for: exactly one pending import per Participant and Inventory - enforced by the
/// database, not by agreement between two code paths - atomic replacement that takes the superseded
/// file with it, a guarded settle only one caller can win, a token that is nowhere in the row, and a
/// raw upload that is gone by every path out of Pending.
/// </summary>
public sealed class SqlImportProposalStoreTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private ImportProposal Proposal(string token, string name = "Steel Bolts", ParticipantId? participantId = null) =>
        ImportProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId ?? _participant,
            _inventory,
            FileDigest.Of(RawContent),
            [
                new ImportEntry
                {
                    LineNumber = 2,
                    SourceLineNumbers = [2, 5],
                    Name = name,
                    NormalizedName = NameNormalization.Normalize(name),
                    Quantity = Quantity.Create(10.5m),
                    UnitId = _unit,
                    UnitCanonicalName = "each",
                    LocationId = null,
                    LocationName = null,
                    Note = "Blue box",
                },
            ],
            EmptyStateVersion.Empty,
            Now);

    [SkippableFact]
    public async Task A_stored_import_round_trips_every_exact_entry_it_carries()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());

        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        var read = await store.FindPendingAsync(_participant, _inventory, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(proposal.Id, read!.Id);
        Assert.Equal(proposal.FileDigest, read.FileDigest);
        Assert.Equal(EmptyStateVersion.Empty, read.EmptyStateVersion);
        Assert.Equal(proposal.ExpiresAt, read.ExpiresAt);

        var entry = Assert.Single(read.Entries);
        Assert.Equal("Steel Bolts", entry.Name);
        Assert.Equal("steel bolts", entry.NormalizedName);
        Assert.Equal("10.5", entry.Quantity.ToInvariantText());
        Assert.Equal(_unit, entry.UnitId);
        Assert.Equal("each", entry.UnitCanonicalName);
        Assert.Null(entry.LocationId);
        Assert.Equal("Blue box", entry.Note);
        Assert.Equal([2, 5], entry.SourceLineNumbers);
    }

    [SkippableFact]
    public async Task The_raw_file_is_stored_with_the_proposal_and_gone_the_moment_it_settles()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.Equal(RawContent, (await store.FindRawContentAsync(proposal.Id, CancellationToken.None))!.Value.ToArray());

        Assert.True(await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None));

        Assert.Null(await store.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(await db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());

        // The proposal itself remains, so a late answer can be told truthfully what happened.
        Assert.Equal(ImportProposalStatus.Rejected, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Storing_a_second_import_supersedes_the_first_and_takes_its_file_with_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var first = Proposal(ConfirmationToken.Issue(), "Steel Bolts");
        await store.StoreAsync(first, RawContent, Now, CancellationToken.None);

        var second = Proposal(ConfirmationToken.Issue(), "Brass Rivets");
        Assert.True(await store.StoreAsync(second, RawContent, Now, CancellationToken.None));

        Assert.Equal(ImportProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(first.Id, CancellationToken.None));

        var pending = await store.FindPendingAsync(_participant, _inventory, CancellationToken.None);
        Assert.Equal(second.Id, pending!.Id);
    }

    [SkippableFact]
    public async Task A_second_pending_import_for_one_Participant_and_Inventory_cannot_exist_at_all()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using (var writer = NewContext())
        {
            await new SqlImportProposalStore(writer).StoreAsync(
                Proposal(ConfirmationToken.Issue()), RawContent, Now, CancellationToken.None);
        }

        // Deliberately bypasses the store: the invariant must be the database's, not the code's.
        using var smuggler = NewContext();
        smuggler.ImportProposals.Add(new ImportProposalEntity
        {
            ProposalId = Guid.NewGuid(),
            TokenHash = ConfirmationToken.HashOf(ConfirmationToken.Issue()).Value,
            ParticipantId = _participant.Value,
            InventoryId = _inventory.Value,
            FileDigest = FileDigest.Of(RawContent).Value,
            Status = nameof(ImportProposalStatus.Pending),
            EntriesJson = "{}",
            ExpectedStockEntryCount = 0,
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(10),
            ExpiresAtTicks = Now.AddMinutes(10).UtcTicks,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => smuggler.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Two_Participants_may_each_have_their_own_pending_import()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        var other = await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);

        await store.StoreAsync(Proposal(ConfirmationToken.Issue()), RawContent, Now, CancellationToken.None);
        await store.StoreAsync(
            Proposal(ConfirmationToken.Issue(), participantId: other), RawContent, Now, CancellationToken.None);

        Assert.NotNull(await store.FindPendingAsync(_participant, _inventory, CancellationToken.None));
        Assert.NotNull(await store.FindPendingAsync(other, _inventory, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Only_one_caller_can_win_a_settle()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.True(await store.SettleAsync(proposal.Id, ImportProposalStatus.Confirmed, Now, CancellationToken.None));
        Assert.False(await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Confirmed, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_expired_import_is_swept_out_of_Pending_and_its_file_discarded()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        var swept = await store.ExpirePendingBeforeAsync(
            Now.AddMinutes(ImportProposal.LifetimeMinutes), maxRows: 100, CancellationToken.None);

        Assert.Equal(1, swept);
        Assert.Equal(ImportProposalStatus.Expired, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_settled_import_is_discarded_once_it_is_past_retention()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);

        Assert.Equal(1, await store.DeleteSettledBeforeAsync(Now.AddHours(1), maxRows: 100, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_Inventory_holding_a_zero_quantity_entry_is_not_empty()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed empty-state read.");

        await SeedAsync();
        using var db = NewContext();
        var reader = new SqlStockEmptyStateReader(db);

        Assert.False(await reader.AnyStockAsync(_inventory, CancellationToken.None));

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            UnitId = _unit.Value,
            Name = "Steel Bolts",
            NormalizedName = "steel bolts",
            Quantity = 0m,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        Assert.True(await reader.AnyStockAsync(_inventory, CancellationToken.None));
    }

    /// <summary>Seeds the Participants, Inventory, and Unit every case needs, and returns a second Participant.</summary>
    private async Task<ParticipantId> SeedAsync()
    {
        using var db = NewContext();
        var other = new ParticipantId(Guid.NewGuid());

        foreach (var participantId in (Guid[])[_participant.Value, other.Value])
        {
            db.Participants.Add(new ParticipantEntity
            {
                Id = participantId,
                DisplayName = "Owner Person",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
        }

        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = $"Warehouse {_inventory.Value:N}",
            NormalizedName = $"warehouse {_inventory.Value:N}",
            CreatedByParticipantId = _participant.Value,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unit.Value,
            InventoryId = _inventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync();

        return other;
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlImportProposalStoreTests"`
Expected: FAIL to compile - `SqlImportProposalStore` and `SqlStockEmptyStateReader` do not exist. If Docker is unavailable locally, the compile failure is still the red you need; run the assertions in CI.

- [ ] **Step 3: Write the mapper**

Create `src/MultiChannelAgent.Infrastructure/Inventories/ImportProposalMapper.cs`:

```csharp
using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// Serializes and reads the exact entries a stored import will create.
///
/// Quantity crosses as invariant decimal text and every identity as its Guid text, exactly as the
/// shipped <see cref="ConfirmationProposalMapper"/> does. <see cref="SchemaVersion"/> is written so a
/// later shape change is detected rather than silently mis-read: an import proposal is only ever ten
/// minutes old, so a row this process cannot read is a deployment mistake, not a migration case.
/// </summary>
internal static class ImportProposalMapper
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new();

    private sealed record EntryDto(
        int LineNumber,
        IReadOnlyList<int> SourceLineNumbers,
        string Name,
        string NormalizedName,
        string Quantity,
        Guid UnitId,
        string UnitCanonicalName,
        Guid? LocationId,
        string? LocationName,
        string? Note);

    private sealed record EntriesEnvelope(int Version, IReadOnlyList<EntryDto> Entries);

    public static ImportProposalEntity ToEntity(ImportProposal proposal) => new()
    {
        ProposalId = proposal.Id.Value,
        TokenHash = proposal.TokenHash.Value,
        ParticipantId = proposal.ParticipantId.Value,
        InventoryId = proposal.InventoryId.Value,
        FileDigest = proposal.FileDigest.Value,
        Status = nameof(ImportProposalStatus.Pending),
        EntriesJson = JsonSerializer.Serialize(
            new EntriesEnvelope(SchemaVersion, [.. proposal.Entries.Select(ToDto)]), Options),
        ExpectedStockEntryCount = proposal.EmptyStateVersion.ExpectedStockEntryCount,
        CreatedAt = proposal.CreatedAt,
        ExpiresAt = proposal.ExpiresAt,
        ExpiresAtTicks = proposal.ExpiresAt.UtcTicks,
        SettledAt = null,
        SettledAtTicks = null,
    };

    public static ImportProposal ToDomain(ImportProposalEntity entity)
    {
        var envelope = JsonSerializer.Deserialize<EntriesEnvelope>(entity.EntriesJson, Options)
            ?? throw new InvalidOperationException("A stored import proposal carried no entries.");

        if (envelope.Version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"A stored import proposal uses unsupported schema version {envelope.Version}.");
        }

        if (!FileDigest.TryParse(entity.FileDigest, out var digest))
        {
            throw new InvalidOperationException("A stored import proposal carried an unreadable file digest.");
        }

        return new ImportProposal
        {
            Id = new ImportProposalId(entity.ProposalId),
            TokenHash = new ConfirmationTokenHash(entity.TokenHash),
            ParticipantId = new ParticipantId(entity.ParticipantId),
            InventoryId = new InventoryId(entity.InventoryId),
            FileDigest = digest,
            Entries = [.. envelope.Entries.Select(ToDomain)],
            EmptyStateVersion = new EmptyStateVersion(entity.ExpectedStockEntryCount),
            CreatedAt = entity.CreatedAt,
        };
    }

    private static EntryDto ToDto(ImportEntry entry) => new(
        entry.LineNumber,
        entry.SourceLineNumbers,
        entry.Name,
        entry.NormalizedName,
        entry.Quantity.ToInvariantText(),
        entry.UnitId.Value,
        entry.UnitCanonicalName,
        entry.LocationId?.Value,
        entry.LocationName,
        entry.Note);

    private static ImportEntry ToDomain(EntryDto dto)
    {
        if (!Quantity.TryParseInvariant(dto.Quantity, out var quantity))
        {
            throw new InvalidOperationException("A stored import proposal carried an unreadable Quantity.");
        }

        return new ImportEntry
        {
            LineNumber = dto.LineNumber,
            SourceLineNumbers = dto.SourceLineNumbers,
            Name = dto.Name,
            NormalizedName = dto.NormalizedName,
            Quantity = quantity,
            UnitId = new UnitId(dto.UnitId),
            UnitCanonicalName = dto.UnitCanonicalName,
            LocationId = dto.LocationId is { } locationId ? new LocationId(locationId) : null,
            LocationName = dto.LocationName,
            Note = dto.Note,
        };
    }
}
```

- [ ] **Step 4: Write the stores**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlImportProposalStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IImportProposalStore"/>.
///
/// Storing supersedes any import this Participant already had pending for this Inventory, in one
/// transaction, so the filtered unique index can never be raced into a violation and the superseded
/// file is discarded with it. Every path out of <c>Pending</c> deletes the raw upload, which is what
/// makes "the raw CSV is discarded after completion or expiry" a durable fact rather than a promise.
/// </summary>
public sealed class SqlImportProposalStore(MultiChannelAgentDbContext db) : IImportProposalStore
{
    private static readonly string PendingStatus = nameof(ImportProposalStatus.Pending);

    public async Task<bool> StoreAsync(
        ImportProposal proposal, ReadOnlyMemory<byte> rawContent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // Superseding first means the filtered unique index is free by the time the insert lands.
            var superseded = await SettlePendingAsync(
                db.ImportProposals.Where(p =>
                    p.ParticipantId == proposal.ParticipantId.Value
                    && p.InventoryId == proposal.InventoryId.Value),
                ImportProposalStatus.Superseded,
                now,
                cancellationToken);

            db.ImportProposals.Add(ImportProposalMapper.ToEntity(proposal));
            db.ImportUploads.Add(new ImportUploadEntity
            {
                ProposalId = proposal.Id.Value,
                Content = rawContent.ToArray(),
                CreatedAt = now,
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return superseded > 0;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    public async Task<ImportProposal?> FindPendingAsync(
        ParticipantId participantId, InventoryId inventoryId, CancellationToken cancellationToken)
    {
        var row = await db.ImportProposals
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.ParticipantId == participantId.Value
                    && p.InventoryId == inventoryId.Value
                    && p.Status == PendingStatus,
                cancellationToken);

        return row is null ? null : ImportProposalMapper.ToDomain(row);
    }

    public async Task<ReadOnlyMemory<byte>?> FindRawContentAsync(
        ImportProposalId proposalId, CancellationToken cancellationToken)
    {
        var content = await db.ImportUploads
            .AsNoTracking()
            .Where(u => u.ProposalId == proposalId.Value)
            .Select(u => u.Content)
            .FirstOrDefaultAsync(cancellationToken);

        return content is null ? null : content.AsMemory();
    }

    public async Task<bool> SettleAsync(
        ImportProposalId proposalId, ImportProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var settled = await SettlePendingAsync(
                db.ImportProposals.Where(p => p.ProposalId == proposalId.Value), status, now, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return settled == 1;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    public async Task<ImportProposalStatus?> FindStatusAsync(ImportProposalId proposalId, CancellationToken cancellationToken)
    {
        var status = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.ProposalId == proposalId.Value)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null ? null : Enum.Parse<ImportProposalStatus>(status);
    }

    public async Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var ticks = now.UtcTicks;

        var expiring = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.Status == PendingStatus && p.ExpiresAtTicks <= ticks)
            .OrderBy(p => p.ExpiresAtTicks)
            .Take(maxRows)
            .Select(p => p.ProposalId)
            .ToListAsync(cancellationToken);

        if (expiring.Count == 0)
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var settled = await SettlePendingAsync(
                db.ImportProposals.Where(p => expiring.Contains(p.ProposalId)),
                ImportProposalStatus.Expired,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return settled;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    public async Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var ticks = cutoff.UtcTicks;

        var deletable = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.SettledAtTicks != null && p.SettledAtTicks <= ticks)
            .OrderBy(p => p.SettledAtTicks)
            .Take(maxRows)
            .Select(p => p.ProposalId)
            .ToListAsync(cancellationToken);

        return deletable.Count == 0
            ? 0

            // The upload cascades with it, so a settled proposal never leaves a file behind even if
            // one somehow survived its settle.
            : await db.ImportProposals.Where(p => deletable.Contains(p.ProposalId)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Moves matching rows out of Pending, guarded, and deletes their uploads in the same transaction.
    /// The guard is what makes a settle single-use: two callers racing one proposal are resolved by
    /// the database, and the loser is told it lost rather than guessing.
    /// </summary>
    private async Task<int> SettlePendingAsync(
        IQueryable<ImportProposalEntity> rows,
        ImportProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = rows.Where(p => p.Status == PendingStatus);

        var ids = await pending.AsNoTracking().Select(p => p.ProposalId).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }

        var settled = await pending.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(p => p.Status, status.ToString())
                .SetProperty(p => p.SettledAt, now)
                .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
            cancellationToken);

        // The raw file goes with the settle, not with a later sweep: "discarded after completion or
        // expiry" means at completion, not eventually.
        await db.ImportUploads.Where(u => ids.Contains(u.ProposalId)).ExecuteDeleteAsync(cancellationToken);

        return settled;
    }
}
```

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlStockEmptyStateReader.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockEmptyStateReader"/>.
///
/// Deliberately unfiltered on Quantity: a zero-quantity Stock Entry is a Stock Entry, which is why
/// Forget exists to remove one, and #34 says the gate counts them.
/// </summary>
public sealed class SqlStockEmptyStateReader(MultiChannelAgentDbContext db) : IStockEmptyStateReader
{
    public Task<bool> AnyStockAsync(InventoryId inventoryId, CancellationToken cancellationToken) =>
        db.StockEntries.AsNoTracking().AnyAsync(e => e.InventoryId == inventoryId.Value, cancellationToken);
}
```

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryAuditRetentionStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IInventoryAuditRetentionStore"/>: the bounded delete that finally
/// enforces the ninety days <c>AuditFact.RetentionDays</c> has always declared.
/// </summary>
public sealed class SqlInventoryAuditRetentionStore(MultiChannelAgentDbContext db) : IInventoryAuditRetentionStore
{
    public async Task<int> DeleteOccurredBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var deletable = await db.InventoryAudits
            .AsNoTracking()
            .Where(a => a.OccurredAt < cutoff)
            .OrderBy(a => a.OccurredAt)
            .Take(maxRows)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        return deletable.Count == 0
            ? 0
            : await db.InventoryAudits.Where(a => deletable.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlImportProposalStoreTests"`
Expected: PASS, 9 tests. If Docker is genuinely unavailable, run without the environment variable, confirm they report as skipped rather than failed, and say so plainly in the commit message.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Inventories/ImportProposalMapper.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlImportProposalStore.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlStockEmptyStateReader.cs \
        src/MultiChannelAgent.Infrastructure/Inventories/SqlInventoryAuditRetentionStore.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportProposalStoreTests.cs
git commit -m "feat(infrastructure): store an import proposal and discard its file with it for #34"
```

---

## Task 12: Create every entry atomically, or create none

**Files:**
- Create: `src/MultiChannelAgent.Infrastructure/Inventories/SqlImportExecutionStore.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreConcurrencyTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreChangeTrackerIsolationTests.cs`

Why: this is the transaction the whole ticket rests on. Six things must commit together or not at all - the reference locks, the proposal consumption, the empty-state re-assertion, every Stock Entry, the audit and ledger, and the raw file's deletion - and an import must be provably unable to land in an Inventory that stopped being empty.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreTests.cs`. It reuses `SqlImportProposalStoreTests`' seeding shape - copy `SeedAsync`, `NewContext`, and `Proposal` into this class rather than sharing, exactly as the shipped SQL store test classes each carry their own:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The one transaction Initial Import rests on: create every Stock Entry with its audit, its ledger,
/// its proposal consumption and its raw file's deletion - or change nothing at all. It also proves
/// the claim the whole workflow depends on: an import can never land in an Inventory that stopped
/// being empty while it was being reviewed.
/// </summary>
public sealed class SqlImportExecutionStoreTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _location = new(Guid.NewGuid());

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private ImportEntry Entry(string name, decimal quantity, LocationId? locationId = null, string? note = null) => new()
    {
        LineNumber = 2,
        SourceLineNumbers = [2],
        Name = name,
        NormalizedName = NameNormalization.Normalize(name),
        Quantity = Quantity.Create(quantity),
        UnitId = _unit,
        UnitCanonicalName = "each",
        LocationId = locationId,
        LocationName = locationId is null ? null : "Shelf A",
        Note = note,
    };

    private ImportProposal Proposal(params ImportEntry[] entries) => ImportProposal.Create(
        ConfirmationToken.HashOf(ConfirmationToken.Issue()),
        _participant,
        _inventory,
        FileDigest.Of(RawContent),
        entries.Length == 0 ? [Entry("Steel Bolts", 4m)] : entries,
        EmptyStateVersion.Empty,
        Now);

    private ImportExecutionCommand Command(ImportProposal proposal) => new()
    {
        OperationId = proposal.ExecutionOperationId,
        InventoryId = _inventory,
        ActorId = _participant,
        ConsumesProposalId = proposal.Id,
        FileDigest = proposal.FileDigest,
        Entries = proposal.Entries,
        EmptyStateVersion = proposal.EmptyStateVersion,
        Now = Now,
    };

    private async Task<ImportProposal> StorePendingAsync(MultiChannelAgentDbContext db, params ImportEntry[] entries)
    {
        var proposal = Proposal(entries);
        await new SqlImportProposalStore(db).StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        return proposal;
    }

    private static SqlImportExecutionStore Store(MultiChannelAgentDbContext db) => new(db);

    [SkippableFact]
    public async Task A_confirmed_import_creates_every_entry_with_its_audit_its_ledger_and_no_file_left_behind()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(
            db,
            Entry("Steel Bolts", 10.5m, note: "Blue box"),
            Entry("Brass Rivets", 0m, _location));

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.Recorded!.CreatedEntryCount);
        Assert.Equal(proposal.FileDigest, result.Recorded.FileDigest);

        var entries = await db.StockEntries.AsNoTracking()
            .Where(e => e.InventoryId == _inventory.Value)
            .OrderBy(e => e.NormalizedName)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Brass Rivets", entries[0].Name);
        Assert.Equal(0m, entries[0].Quantity);
        Assert.Equal(_location.Value, entries[0].LocationId);
        Assert.Equal("Steel Bolts", entries[1].Name);
        Assert.Equal(10.5m, entries[1].Quantity);
        Assert.Equal("Blue box", entries[1].Note);
        Assert.Null(entries[1].LocationId);

        // Exactly one fact, carrying nothing about what was imported.
        var audit = Assert.Single(await db.InventoryAudits.AsNoTracking()
            .Where(a => a.InventoryId == _inventory.Value)
            .ToListAsync());
        Assert.Equal(nameof(AuditEventType.StockImported), audit.EventType);
        Assert.Equal("Import:Completed", audit.OutcomeCode);
        Assert.Null(audit.SubjectParticipantId);

        Assert.Single(await db.ImportOperations.AsNoTracking().Where(o => o.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
        Assert.Equal(
            nameof(ImportProposalStatus.Confirmed),
            (await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
    }

    [SkippableFact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_instead_of_importing_twice()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);

        var first = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);
        var replay = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, first.Outcome);
        Assert.Equal(ImportExecutionOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(first.Recorded!.CreatedEntryCount, replay.Recorded!.CreatedEntryCount);
        Assert.Single(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await db.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task An_import_into_an_Inventory_that_stopped_being_empty_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);

        // A zero-quantity entry is still an entry, so this is exactly the case the gate exists for.
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            UnitId = _unit.Value,
            Name = "Existing",
            NormalizedName = "existing",
            Quantity = 0m,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        Assert.Single(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportOperations.AsNoTracking().ToListAsync());

        // Rolled back with everything else, so the caller settles it rather than the store burning it.
        Assert.Equal(
            nameof(ImportProposalStatus.Pending),
            (await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
        Assert.Single(await db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task An_import_whose_proposal_was_already_settled_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);
        await new SqlImportProposalStore(db).SettleAsync(
            proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Empty(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task An_import_naming_a_Unit_retired_since_the_preview_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);

        await db.Units.Where(u => u.Id == _unit.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.RetiredAt, Now));
        db.ChangeTracker.Clear();

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Empty(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportOperations.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task An_import_naming_a_Location_retired_since_the_preview_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db, Entry("Steel Bolts", 4m, _location));

        await db.Locations.Where(l => l.Id == _location.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.RetiredAt, Now));
        db.ChangeTracker.Clear();

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Empty(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task Every_created_entry_is_a_real_Stock_Entry_the_conversation_can_then_read()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db, Entry("Steel Bolts", 4m, _location, "Blue box"));

        await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        var entry = await db.StockEntries.AsNoTracking().SingleAsync(e => e.InventoryId == _inventory.Value);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.NotEqual(Guid.Empty, entry.ConcurrencyStamp);
        Assert.Equal("steel bolts", entry.NormalizedName);
        Assert.Equal(_unit.Value, entry.UnitId);
    }

    private async Task SeedAsync()
    {
        using var db = NewContext();

        db.Participants.Add(new ParticipantEntity
        {
            Id = _participant.Value,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = $"Warehouse {_inventory.Value:N}",
            NormalizedName = $"warehouse {_inventory.Value:N}",
            CreatedByParticipantId = _participant.Value,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unit.Value,
            InventoryId = _inventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        db.Locations.Add(new LocationEntity
        {
            Id = _location.Value,
            InventoryId = _inventory.Value,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 2: Write the concurrency test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreConcurrencyTests.cs`, copying the same seeding helpers:

```csharp
    [SkippableFact]
    public async Task An_import_racing_a_Stock_write_never_leaves_both_the_import_and_the_other_entry()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        await SeedAsync();
        ImportProposal proposal;
        using (var setup = NewContext())
        {
            proposal = await StorePendingAsync(setup);
        }

        async Task<ImportExecutionOutcome> ImportAsync()
        {
            using var db = NewContext();
            var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);
            return result.Outcome;
        }

        async Task WriteStockAsync()
        {
            using var db = NewContext();

            // Exactly the production shape: resolve, then write through the real store.
            var resolved = await new SqlInventoryReferenceStore(db).ResolveUnitAsync(
                _inventory, _unit.Value.ToString(), CancellationToken.None);

            if (resolved is null)
            {
                return;
            }

            await new SqlStockMutationStore(db).ApplyAsync(
                new StockMutationCommand
                {
                    OperationId = StockOperationId.Derive(TurnId.NewId(), "add_stock", 0),
                    InventoryId = _inventory,
                    ActorId = _participant,
                    Kind = StockMutationKind.Add,
                    Amount = Quantity.Create(1m),
                    ResultingQuantity = Quantity.Create(1m),
                    NewEntryName = "Racing Entry",
                    NewEntryUnitId = resolved,
                    NewEntryLocationId = null,
                    NotePreserved = false,
                    Now = Now,
                },
                CancellationToken.None);
        }

        // The import's empty-state assertion is a range query under serializable isolation, so the two
        // serialize; whichever loses says so, and neither faults.
        var importTask = ImportAsync();
        var stockTask = WriteStockAsync();
        var outcome = await importTask;
        await stockTask;

        using var verify = NewContext();
        var entries = await verify.StockEntries.AsNoTracking()
            .Where(e => e.InventoryId == _inventory.Value)
            .Select(e => e.Name)
            .ToListAsync();

        if (outcome == ImportExecutionOutcome.Applied)
        {
            // The import won, so the racing write either lost or landed after it - but the import
            // itself must be exactly what it proposed, never mixed with a half of something else.
            Assert.Contains("Steel Bolts", entries);
        }
        else
        {
            Assert.Equal(ImportExecutionOutcome.Conflict, outcome);
            Assert.DoesNotContain("Steel Bolts", entries);
            Assert.Empty(await verify.ImportOperations.AsNoTracking().ToListAsync());
        }
    }

    [SkippableFact]
    public async Task Only_one_of_two_concurrent_confirmations_of_one_import_can_win()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed concurrency proof.");

        await SeedAsync();
        ImportProposal proposal;
        using (var setup = NewContext())
        {
            proposal = await StorePendingAsync(setup);
        }

        async Task<ImportExecutionOutcome> ConfirmAsync()
        {
            using var db = NewContext();

            // A distinct operation identity per attempt, so this proves the proposal's single use
            // rather than the ledger's - two browser tabs confirming produce two requests, not one.
            var command = Command(proposal) with { OperationId = new ImportOperationId(Guid.NewGuid()) };
            var result = await Store(db).ApplyAsync(command, CancellationToken.None);
            return result.Outcome;
        }

        var outcomes = await Task.WhenAll(ConfirmAsync(), ConfirmAsync());

        Assert.Equal(1, outcomes.Count(outcome => outcome == ImportExecutionOutcome.Applied));
        Assert.Equal(1, outcomes.Count(outcome => outcome == ImportExecutionOutcome.Conflict));

        using var verify = NewContext();
        Assert.Single(await verify.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
    }
```

- [ ] **Step 3: Write the change-tracker isolation test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreChangeTrackerIsolationTests.cs`, mirroring the shipped `SqlReferenceAdministrationStoreChangeTrackerIsolationTests` exactly - the same in-memory SQLite connection, the same seeding shape, this store. It is Docker-free because the invariant is provider-independent:

```csharp
    [Fact]
    public async Task A_failed_import_leaves_nothing_staged_in_the_shared_context()
    {
        SeedExistingStock();
        var proposal = await StorePendingAsync();

        // The Inventory is not empty, so this must refuse - after having staged every Stock Entry,
        // every audit, and the ledger row, which is exactly what must not survive.
        var result = await Store().ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.DoesNotContain(_db.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);
        Assert.Single(await _db.StockEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.ImportOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InventoryAudits.AsNoTracking().ToListAsync());

        // The very next write in this same scope must not flush what the abandoned import staged.
        _db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            Name = "Shelf Z",
            NormalizedName = "shelf z",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        await _db.SaveChangesAsync();

        Assert.Single(await _db.StockEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.ImportOperations.AsNoTracking().ToListAsync());
    }
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlImportExecutionStore"`
Expected: FAIL to compile - `SqlImportExecutionStore` does not exist.

- [ ] **Step 5: Write the store**

Create `src/MultiChannelAgent.Infrastructure/Inventories/SqlImportExecutionStore.cs`:

```csharp
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IImportExecutionStore"/>: the one transaction Initial Import rests
/// on.
///
/// One <see cref="ApplyAsync"/> call locks and verifies every reference the entries name, consumes
/// the proposal, re-asserts that the Inventory still holds no Stock Entry at all, creates every
/// entry, appends one minimal semantic audit fact, writes its ledger row, and discards the raw
/// upload - inside one explicit transaction. Any failure rolls the whole thing back, so a caller that
/// sees <see cref="ImportExecutionOutcome.Conflict"/> can rely on nothing at all having happened,
/// including the proposal still being pending and its file still being there.
///
/// Two things here are deliberate rather than incidental:
///
/// <list type="bullet">
/// <item><b>Serializable, and why it has to be.</b> The empty-state assertion is a question about an
/// <em>absence</em>, and an absence is a range. Under read-committed a Stock Entry could be inserted
/// just after the check and commit just before this transaction does, leaving an "initial" import
/// sitting on top of stock nobody reviewed. Serializable makes the check take a range lock, so the
/// two serialize and one of them plainly loses.</item>
/// <item><b>The shared lock order.</b> References, then proposal, then Stock - the same order
/// <see cref="AssignedReferenceLocks"/> documents and both shipped writers follow, so an import and a
/// Retire contend in one agreed sequence rather than deadlocking halfway through.</item>
/// </list>
/// </summary>
public sealed class SqlImportExecutionStore(MultiChannelAgentDbContext db) : IImportExecutionStore
{
    private static readonly string PendingStatus = nameof(ImportProposalStatus.Pending);

    public async Task<RecordedImport?> FindRecordedAsync(
        InventoryId inventoryId, ImportOperationId operationId, CancellationToken cancellationToken)
    {
        var header = await db.ImportOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        if (header is null || !FileDigest.TryParse(header.FileDigest, out var digest))
        {
            return null;
        }

        return new RecordedImport(
            operationId,
            new ImportProposalId(header.ProposalId),
            new ParticipantId(header.ActorId),
            digest,
            header.CreatedEntryCount);
    }

    public async Task<ImportExecutionResult> ApplyAsync(ImportExecutionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, already);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            // 1. Hold every Unit and Location these entries name, active-only, in the shared order.
            //    A preview may be ten minutes old; a Retire since then must stop it here.
            if (!await AssignedReferenceLocks.TryHoldActiveAsync(
                    db,
                    command.InventoryId,
                    command.Entries.Select(entry => entry.UnitId),
                    command.Entries.Select(entry => entry.LocationId).OfType<LocationId>(),
                    cancellationToken))
            {
                return await RolledBackConflictAsync(transaction);
            }

            // 2. Consume the proposal, guarded, so two confirmations can never both import.
            var consumed = await db.ImportProposals
                .Where(p => p.ProposalId == command.ConsumesProposalId.Value && p.Status == PendingStatus)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.Status, nameof(ImportProposalStatus.Confirmed))
                        .SetProperty(p => p.SettledAt, command.Now)
                        .SetProperty(p => p.SettledAtTicks, command.Now.UtcTicks),
                    cancellationToken);

            if (consumed != 1)
            {
                return await RolledBackConflictAsync(transaction);
            }

            // 3. The authoritative empty-state assertion. The preview said the Inventory held nothing;
            //    this says it still does, and the serializable range lock is what keeps that true until
            //    the entries below have committed.
            if (await db.StockEntries.AnyAsync(e => e.InventoryId == command.InventoryId.Value, cancellationToken))
            {
                return await RolledBackConflictAsync(transaction);
            }

            // 4. Every entry, through the domain factory, so persistence never sees a name or Note the
            //    domain would have refused.
            foreach (var entry in command.Entries)
            {
                var stockEntry = StockEntry.Create(
                    command.InventoryId,
                    entry.UnitId,
                    entry.LocationId,
                    entry.Name,
                    entry.Note,
                    entry.Quantity,
                    command.Now);

                db.StockEntries.Add(new StockEntryEntity
                {
                    Id = stockEntry.Id.Value,
                    InventoryId = stockEntry.InventoryId.Value,
                    UnitId = stockEntry.UnitId.Value,
                    LocationId = stockEntry.LocationId?.Value,
                    Name = stockEntry.Name,
                    NormalizedName = stockEntry.NormalizedName,
                    Note = stockEntry.Note,
                    Quantity = stockEntry.Quantity.Value,
                    CreatedAt = stockEntry.CreatedAt,
                });
            }

            // 5. One ledger row and exactly one minimal semantic audit fact. The fact says an import
            //    happened here, by whom, and when - never what was in it.
            db.ImportOperations.Add(new ImportOperationEntity
            {
                OperationId = command.OperationId.Value,
                InventoryId = command.InventoryId.Value,
                ProposalId = command.ConsumesProposalId.Value,
                ActorId = command.ActorId.Value,
                FileDigest = command.FileDigest.Value,
                CreatedEntryCount = command.Entries.Count,
                AppliedAt = command.Now,
            });

            db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
                AuditEventType.StockImported,
                AuditActorKind.Participant,
                command.ActorId.ToString(),
                command.InventoryId,
                subjectParticipantId: null,
                ImportFacts.CompletedOutcomeCode,
                command.Now)));

            // 6. The raw file goes with the import that used it.
            await db.ImportUploads
                .Where(u => u.ProposalId == command.ConsumesProposalId.Value)
                .ExecuteDeleteAsync(cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ImportExecutionResult(
                ImportExecutionOutcome.Applied,
                new RecordedImport(
                    command.OperationId,
                    command.ConsumesProposalId,
                    command.ActorId,
                    command.FileDigest,
                    command.Entries.Count));
        }
        catch (DbUpdateException)
        {
            await db.AbandonAsync(transaction);

            // A competing writer may have been this very operation, applied by another replica. Its
            // ledger row is the authoritative record, so converge on re-reporting it rather than
            // claiming a conflict against ourselves.
            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, converged);
            }

            // Equivalent Stock is unique in the database, so a competing writer that created one of
            // these entries first makes the insert fail. Classify that as the state having changed only
            // when the Inventory genuinely now holds Stock; anything else is a real fault and keeps
            // propagating rather than being reported as a routine conflict.
            if (await db.StockEntries.AsNoTracking()
                    .AnyAsync(e => e.InventoryId == command.InventoryId.Value, cancellationToken))
            {
                return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
            }

            throw;
        }
        catch
        {
            // Every other fault leaves the same debris, and this DbContext serves a whole batch of
            // requests: the transaction would roll back on dispose, but the ChangeTracker would not.
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    private async Task<ImportExecutionResult> RolledBackConflictAsync(IDbContextTransaction transaction)
    {
        await db.AbandonAsync(transaction);

        return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
    }
}
```

Note that this store takes only the `DbContext`. It consumes the proposal with its own guarded update in step 2 rather than through `IImportProposalStore`, because the consumption has to happen inside this transaction - and taking the store as well would suggest there were two ways to do it.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~SqlImportExecutionStore"`
Expected: PASS - 7 write cases, 2 concurrency cases, and the Docker-free isolation case. If Docker is unavailable locally, the isolation case must still pass; say plainly in the commit message that the rest reported as skipped.

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/MultiChannelAgent.Infrastructure/Inventories/SqlImportExecutionStore.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreConcurrencyTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/SqlImportExecutionStoreChangeTrackerIsolationTests.cs
git commit -m "feat(infrastructure): create every imported entry atomically or create none for #34"
```

---

## Task 13: Sweep expired imports, their files, and old audits

**Files:**
- Create: `src/MultiChannelAgent.Application/Inventories/ImportCleanupCoordinator.cs`
- Create: `src/MultiChannelAgent.Host/Workers/ImportCleanupWorker.cs`
- Modify: `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportCleanupCoordinatorTests.cs`

Why: "Raw CSV is discarded after completion or expiry and only the specified 90-day semantic facts remain" is half a cleanup story that does not exist yet - nothing in the shipped system enforces `AuditFact.RetentionDays` at all.

- [ ] **Step 1: Write the failing test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportCleanupCoordinatorTests.cs`. Mirror the shipped `ConfirmationExpirySqliteTests` shape: a Docker-free SQLite `MultiChannelAgentDbContext`, `FakeTimeProvider`, and the real coordinator over the real stores:

```csharp
    [Fact]
    public async Task An_expired_import_leaves_Pending_and_its_file_is_discarded()
    {
        var proposal = await StorePendingAsync();
        _time.SetUtcNow(Now.AddMinutes(ImportProposal.LifetimeMinutes));

        Assert.Equal(1, await Coordinator().SweepAsync(CancellationToken.None));

        Assert.Equal(
            ImportProposalStatus.Expired,
            await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_still_pending_import_is_left_exactly_where_it_is()
    {
        var proposal = await StorePendingAsync();
        _time.SetUtcNow(Now.AddMinutes(ImportProposal.LifetimeMinutes - 1));

        await Coordinator().SweepAsync(CancellationToken.None);

        Assert.Equal(ImportProposalStatus.Pending, await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.NotNull(await _proposals.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_settled_import_is_discarded_once_it_is_past_retention()
    {
        var proposal = await StorePendingAsync();
        await _proposals.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);
        _time.SetUtcNow(Now + ImportCleanupCoordinator.SettledRetention + TimeSpan.FromMinutes(1));

        await Coordinator().SweepAsync(CancellationToken.None);

        Assert.Null(await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_audit_fact_older_than_ninety_days_is_discarded_and_a_newer_one_is_kept()
    {
        await SeedAuditAsync(Now.AddDays(-AuditFact.RetentionDays).AddMinutes(-1));
        await SeedAuditAsync(Now.AddDays(-AuditFact.RetentionDays).AddMinutes(1));

        await Coordinator().SweepAsync(CancellationToken.None);

        Assert.Single(await _db.InventoryAudits.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_sweep_that_cannot_take_the_lease_does_nothing_at_all()
    {
        var proposal = await StorePendingAsync();
        _time.SetUtcNow(Now.AddMinutes(ImportProposal.LifetimeMinutes));
        _leases.RefuseNext();

        Assert.Equal(0, await Coordinator().SweepAsync(CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Pending, await _proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ImportCleanupCoordinatorTests"`
Expected: FAIL to compile - `ImportCleanupCoordinator` does not exist.

- [ ] **Step 3: Write the coordinator**

Create `src/MultiChannelAgent.Application/Inventories/ImportCleanupCoordinator.cs`:

```csharp
using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Expires pending Initial Imports whose ten minutes have run out - discarding their raw files with
/// them - discards settled ones past retention, and sweeps audit facts past their ninety days.
///
/// The first two mirror <see cref="ConfirmationProposalCleanupCoordinator"/> exactly. The third is
/// new to the whole system: <see cref="AuditFact.RetentionDays"/> has said ninety since audits
/// existed, and nothing enforced it. #34 requires that only the specified ninety-day semantic facts
/// remain, so the sweep lives here and covers every audit fact rather than only the import one -
/// there is no honest way to retain one kind for ninety days and another forever.
///
/// Runs under its own exclusive lease, so several hosted replicas never duplicate the work, and
/// exposes a deterministic one-shot operation so tests can drive it without timing a background loop.
/// </summary>
public sealed class ImportCleanupCoordinator(
    IImportProposalStore proposalStore,
    IInventoryAuditRetentionStore auditStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<ImportCleanupCoordinator> logger)
{
    private const string LeaseName = "import-cleanup";

    /// <summary>Bounds one pass so a large backlog is drained over several passes instead of one long transaction.</summary>
    private const int MaxBatchSize = 500;

    /// <summary>How long a settled import is retained, so a late answer can still be told what happened.</summary>
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
        var audits = await auditStore.DeleteOccurredBeforeAsync(
            now.AddDays(-AuditFact.RetentionDays), MaxBatchSize, cancellationToken);

        if (expired > 0 || deleted > 0 || audits > 0)
        {
            logger.LogInformation(
                "Expired {ExpiredCount} pending imports, discarded {DeletedCount} settled ones, and swept {AuditCount} audit facts past retention.",
                expired,
                deleted,
                audits);
        }

        return expired + deleted + audits;
    }
}
```

- [ ] **Step 4: Write the worker and register everything**

Create `src/MultiChannelAgent.Host/Workers/ImportCleanupWorker.cs`:

```csharp
using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="ImportCleanupCoordinator.SweepAsync"/>, so an expired Initial
/// Import stops occupying its Participant's one pending slot for that Inventory, its raw file is
/// discarded, and audit facts do not outlive their ninety days. An import's lifetime is ten minutes,
/// so five-minute granularity frees that slot promptly without anything having to poll for it.
/// </summary>
public sealed class ImportCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ImportCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<ImportCleanupCoordinator>();
                await coordinator.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "An import cleanup pass failed.");
            }
        }
    }
}
```

In `src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs`, register the new pieces beside the shipped inventory registrations:

```csharp
        services.AddScoped<IImportProposalStore, SqlImportProposalStore>();
        services.AddScoped<IImportExecutionStore, SqlImportExecutionStore>();
        services.AddScoped<IStockEmptyStateReader, SqlStockEmptyStateReader>();
        services.AddScoped<IInventoryAuditRetentionStore, SqlInventoryAuditRetentionStore>();
        services.AddScoped<ImportReferenceResolver>();
        services.AddScoped<InitialImportService>();
        services.AddScoped<ImportConfirmationService>();
        services.AddScoped<ImportCleanupCoordinator>();
```

In `src/MultiChannelAgent.Host/Program.cs`, register the worker beside `ConfirmationProposalCleanupWorker`:

```csharp
builder.Services.AddHostedService<ImportCleanupWorker>();
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ImportCleanupCoordinatorTests"`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/MultiChannelAgent.Application/Inventories/ImportCleanupCoordinator.cs \
        src/MultiChannelAgent.Host/Workers/ImportCleanupWorker.cs \
        src/MultiChannelAgent.Infrastructure/ServiceCollectionExtensions.cs \
        src/MultiChannelAgent.Host/Program.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ImportCleanupCoordinatorTests.cs
git commit -m "feat(inventories): sweep expired imports, their files, and audits past ninety days for #34"
```

---

## Task 14: Expose the four endpoints

**Files:**
- Create: `src/MultiChannelAgent.Host/Endpoints/ImportEndpoints.cs`
- Modify: `src/MultiChannelAgent.Host/Program.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportEndpointsHttpTests.cs`
- Test: `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportUploadLimitsHttpTests.cs`
- Test support: `tests/MultiChannelAgent.IntegrationTests/ServerRequestSizeLimits.cs`
- Test support: `tests/MultiChannelAgent.IntegrationTests/UploadSpillGuard.cs`
- Modify: `tests/MultiChannelAgent.IntegrationTests/SqliteWebApplicationFactory.cs`

Why: this is the only way in. It is also where the upload is bounded before it is buffered, where CSRF is enforced, and where "not a member" and "no such Inventory" must be made indistinguishable.

- [ ] **Step 1: Write the failing HTTP test**

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/ImportEndpointsHttpTests.cs`. It follows the shipped `StockEndpointsHttpTests` exactly: the SQLite factory, a cookie-free `HttpClient` with a `CookieJar`, and the `/api/test/sign-in` plus `/api/session/bootstrap` pair. Copy that class's `InitializeAsync`, `DisposeAsync`, `SignInAndBootstrapAsync`, `SendAsync`, and `CreateInventoryAsync` verbatim, then add:

```csharp
    private const string Header = "Name,Quantity,Unit,Location,Note";

    private static MultipartFormDataContent CsvContent(string csv, string fileName = "stock.csv")
    {
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    private async Task<HttpResponseMessage> ValidateAsync(CookieJar jar, string csrfToken, Guid inventoryId, string csv) =>
        await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = CsvContent(csv),
            },
            csrfToken);

    [Fact]
    public async Task An_Editor_sees_that_import_is_available_for_an_empty_Inventory()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import");
        var response = await SendAsync(jar, request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("eligible").GetBoolean());
    }

    [Fact]
    public async Task A_valid_file_previews_the_exact_entries_and_hands_back_a_one_time_token()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\nSteel Bolts,6,,,\n");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.GetProperty("sourceRowCount").GetInt32());
        var entry = Assert.Single(body.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal("Steel Bolts", entry.GetProperty("name").GetString());
        Assert.Equal("10", entry.GetProperty("quantity").GetString());
        Assert.Equal(64, body.GetProperty("fileDigest").GetString()!.Length);
        Assert.NotEqual(Guid.Empty, body.GetProperty("proposalId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task A_confirmed_import_creates_the_entries_the_preview_showed()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/confirm")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = preview.GetProperty("proposalId").GetGuid(),
                    token = preview.GetProperty("token").GetString(),
                }),
            },
            csrfToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.GetProperty("createdEntryCount").GetInt32());

        // The Inventory is no longer empty, so import is no longer offered.
        var eligibility = await (await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal("inventory_not_empty", eligibility.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_file_with_errors_is_a_validation_problem_naming_every_line()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await ValidateAsync(jar, csrfToken, inventoryId, $"{Header}\n,4,,,\nSteel Bolts,nope,,,\n");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(["missing_name", "invalid_quantity"], errors.Select(e => e.GetProperty("code").GetString()));
        Assert.Equal([2, 3], errors.Select(e => e.GetProperty("lineNumber").GetInt32()));
        Assert.Equal(0, body.GetProperty("omittedErrorCount").GetInt32());
    }

    [Fact]
    public async Task A_file_part_longer_than_the_bound_is_refused_on_its_length_rather_than_read()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var oversized = $"{Header}\n" + new string('a', ImportContract.MaxUploadBytes);

        var response = await ValidateAsync(jar, csrfToken, inventoryId, oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_file_part_names_the_part_it_wanted()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent(),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_multipart_body_that_stops_before_its_terminating_boundary_names_the_part_it_wanted()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        const string boundary = "ImportBoundaryThatNeverCloses";

        // A well-formed part header and the beginning of a file, and then nothing: no closing boundary
        // and no terminator, which is what a connection cut mid-upload leaves behind.
        var truncated = new ByteArrayContent(Encoding.UTF8.GetBytes(
            $"--{boundary}\r\n"
            + "Content-Disposition: form-data; name=\"file\"; filename=\"stock.csv\"\r\n"
            + "Content-Type: text/csv\r\n"
            + "\r\n"
            + $"{Header}\r\nSteel Bolts,4,,,\r\n"));
        truncated.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data")
        {
            Parameters = { new NameValueHeaderValue("boundary", boundary) },
        };

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = truncated,
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("file", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mutating_request_without_the_CSRF_token_is_refused()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
        {
            Content = CsvContent($"{Header}\nSteel Bolts,4,,,\n"),
        };
        var response = await SendAsync(jar, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_Inventory_the_Participant_may_not_see_is_indistinguishable_from_one_that_does_not_exist()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Stranger");

        var eligibility = await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{Guid.NewGuid()}/import"));
        var validate = await ValidateAsync(jar, csrfToken, Guid.NewGuid(), $"{Header}\nSteel Bolts,4,,,\n");

        Assert.Equal(HttpStatusCode.NotFound, eligibility.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, validate.StatusCode);
    }

    [Fact]
    public async Task Cancelling_settles_the_import_and_creates_nothing()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");
        var preview = await (await ValidateAsync(
            jar, csrfToken, inventoryId, $"{Header}\nSteel Bolts,4,,,\n"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/reject")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = preview.GetProperty("proposalId").GetGuid(),
                    token = (string?)null,
                }),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var eligibility = await (await SendAsync(
            jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/import")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(eligibility.GetProperty("eligible").GetBoolean());
    }

    [Fact]
    public async Task Confirming_an_unknown_token_is_a_plain_not_found()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync("Import Owner");
        var inventoryId = await CreateInventoryAsync(jar, csrfToken, "Import Warehouse");

        var response = await SendAsync(
            jar,
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/confirm")
            {
                Content = JsonContent.Create(new
                {
                    proposalId = Guid.NewGuid(),
                    token = ConfirmationToken.Issue(),
                }),
            },
            csrfToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ImportEndpointsHttpTests"`
Expected: FAIL - every route answers 404 because nothing maps them, so the JSON reads fail.

- [ ] **Step 3: Write the endpoints**

Create `src/MultiChannelAgent.Host/Endpoints/ImportEndpoints.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>
/// Maps the signed-in Initial Import workflow: the eligibility read, the bounded multipart
/// validation, and the confirmation and cancellation of the one pending import.
///
/// It is a workflow rather than a projection, so unlike the Stock and reference reads every mutating
/// route carries the shipped <see cref="AntiforgeryEndpointFilter"/>, and unlike a conversational
/// tool it never goes near a Turn. What it shares with both is the non-disclosure rule: whether the
/// Inventory does not exist or simply is not this Participant's, the answer is an identical 404.
/// </summary>
public static class ImportEndpoints
{
    /// <summary>
    /// The file bound plus 64 KiB of transport framing, so an oversized upload is refused before it is
    /// buffered while an intermediary that re-frames a maximum-sized body still gets through. What may
    /// be imported is the file bound, checked separately against the file part's own length.
    /// </summary>
    private const long MaxRequestBodyBytes = ImportContract.MaxUploadBytes + (64 * 1024);

    private sealed record ImportTokenRequest(Guid ProposalId, string? Token);

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventories/{inventoryId:guid}/import", async (
            Guid inventoryId,
            ClaimsPrincipal user,
            InitialImportService importService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await importService.ReadEligibilityAsync(
                user.GetParticipantId(), new InventoryId(inventoryId), timeProvider.GetUtcNow(), cancellationToken);

            return result.Kind switch
            {
                ImportResultKind.Completed => Results.Ok(result.View),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        endpoints.MapPost("/api/inventories/{inventoryId:guid}/import/validate", async (
            Guid inventoryId,
            HttpRequest request,
            ClaimsPrincipal user,
            InitialImportService importService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return MissingFile();
            }

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(cancellationToken);
            }

            // Not readable multipart - no boundary, or a body that ends before the boundary it
            // declared - is malformed input, answered exactly like no file at all. Truncation arrives
            // as an IOException rather than an InvalidDataException, so both are caught; the server's
            // own refusal is not, because BadHttpRequestException is an IOException too and a body
            // over this route's bound has to stay the 413 the server made it.
            catch (Exception exception) when (
                exception is InvalidDataException || (exception is IOException and not BadHttpRequestException))
            {
                return MissingFile();
            }

            var file = form.Files.GetFile("file");

            if (file is null || file.Length == 0)
            {
                return MissingFile();
            }

            if (file.Length > ImportContract.MaxUploadBytes)
            {
                return TooLarge();
            }

            // Bounded before it is read, and read once: the whole file has to be in hand to digest it
            // and to validate it, and it is never written anywhere but the proposal's own row.
            using var buffer = new MemoryStream(capacity: (int)file.Length);
            await using (var stream = file.OpenReadStream())
            {
                await stream.CopyToAsync(buffer, cancellationToken);
            }

            var result = await importService.ValidateAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                buffer.ToArray(),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Kind switch
            {
                ImportResultKind.Completed => Results.Ok(result.View),

                // Whether the Inventory does not exist or simply is not authorized for this
                // Participant, the response must be identical: a plain 404, never a distinct signal.
                ImportResultKind.NotFound or ImportResultKind.Forbidden => Results.NotFound(),
                ImportResultKind.NotEmpty => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "This Inventory already holds Stock, so there is nothing initial to import.",
                    extensions: new Dictionary<string, object?> { ["code"] = "inventory_not_empty" }),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "That file could not be imported.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "invalid_file",
                        ["errors"] = result.Errors,
                        ["omittedErrorCount"] = result.OmittedErrorCount,
                    }),
            };
        })
        .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember)
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithMetadata(new RequestSizeLimitMetadata(MaxRequestBodyBytes))
        .WithFormOptions(
            memoryBufferThreshold: (int)MaxRequestBodyBytes,
            multipartBodyLengthLimit: MaxRequestBodyBytes);

        endpoints.MapPost("/api/inventories/{inventoryId:guid}/import/confirm", async (
            Guid inventoryId,
            ImportTokenRequest body,
            ClaimsPrincipal user,
            ImportConfirmationService confirmationService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await confirmationService.ConfirmAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                new ImportProposalId(body.ProposalId),
                body.Token,
                timeProvider.GetUtcNow(),
                cancellationToken);

            return ToResult(result);
        })
        .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember)
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        endpoints.MapPost("/api/inventories/{inventoryId:guid}/import/reject", async (
            Guid inventoryId,
            ImportTokenRequest body,
            ClaimsPrincipal user,
            ImportConfirmationService confirmationService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await confirmationService.RejectAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                new ImportProposalId(body.ProposalId),
                body.Token,
                timeProvider.GetUtcNow(),
                cancellationToken);

            return ToResult(result);
        })
        .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember)
        .AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }

    private static IResult ToResult(ImportConfirmationResult result) => result.Kind switch
    {
        ImportConfirmationResultKind.Completed => Results.Ok(result.View),
        ImportConfirmationResultKind.Rejected => Results.Ok(new { rejected = true }),

        // A refusal and an absence look the same on purpose.
        ImportConfirmationResultKind.NotFound or ImportConfirmationResultKind.Forbidden => Results.NotFound(),
        ImportConfirmationResultKind.Conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That import can no longer be applied.",
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        _ => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "That import could not be confirmed.",
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
    };

    private static IResult MissingFile() => Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["file"] = ["A single non-empty CSV file part named 'file' is required."],
        });

    private static IResult TooLarge() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: $"An import file must not exceed {ImportContract.MaxUploadBytes} bytes.",
        extensions: new Dictionary<string, object?> { ["code"] = "file_too_large" });

    /// <summary>
    /// Applies the per-route body limit. Kestrel's global limit stays where it is; only this route
    /// needs to accept a two-mebibyte body, and only up to that.
    /// </summary>
    private sealed class RequestSizeLimitMetadata(long maxBytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => maxBytes;

        public bool? RequestSizeLimitExceeded => null;
    }
}
```

In `src/MultiChannelAgent.Host/Program.cs`, map it beside the shipped endpoints, immediately after `app.MapReferenceEndpoints();`:

```csharp
app.MapImportEndpoints();
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~ImportEndpointsHttpTests|FullyQualifiedName~ImportUploadLimitsHttpTests"`
Expected: PASS, 26 tests.

- [ ] **Step 5: Commit**

```bash
git add src/MultiChannelAgent.Host/Endpoints/ImportEndpoints.cs \
        src/MultiChannelAgent.Host/Program.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ImportEndpointsHttpTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/ImportUploadLimitsHttpTests.cs \
        tests/MultiChannelAgent.IntegrationTests/ServerRequestSizeLimits.cs \
        tests/MultiChannelAgent.IntegrationTests/UploadSpillGuard.cs \
        tests/MultiChannelAgent.IntegrationTests/SqliteWebApplicationFactory.cs
git commit -m "feat(host): expose the signed-in Initial Import workflow for #34"
```

---

## Task 15: Give the workflow a face

**Files:**
- Create: `src/web/src/importApi.ts`
- Create: `src/web/src/InitialImport.tsx`
- Modify: `src/web/src/App.tsx`

Why: an import that cannot be previewed is not the workflow the ticket describes. The preview is the whole safety story - #26 asks for "a normalized preview and explicit confirmation before import, so that the exact initial state is visible".

- [ ] **Step 1: Write the typed client**

Create `src/web/src/importApi.ts`:

```ts
/** Whether Initial Import is available here, and when it is not, the one machine code saying why. */
export interface ImportEligibility {
  eligible: boolean;
  reason: string | null;
}

/** One Stock Entry the import would create, exactly as it will be created. */
export interface ImportPreviewRow {
  name: string;
  quantity: string;
  unitCanonicalName: string;
  locationName: string | null;
  note: string | null;
  sourceLineNumbers: number[];
}

export interface ImportPreview {
  token: string;
  proposalId: string;
  fileDigest: string;
  sourceRowCount: number;
  entries: ImportPreviewRow[];
  supersededPrevious: boolean;
  expiresAt: string;
}

/** One reported problem: its machine code, where it is, and any bounded suggestions. */
export interface ImportError {
  code: string;
  lineNumber: number;
  columnIndex: number | null;
  suggestions: string[];
}

export interface ImportErrorReport {
  errors: ImportError[];
  omittedErrorCount: number;
}

export interface ImportCompleted {
  proposalId: string;
  createdEntryCount: number;
  fileDigest: string;
}

/** Exactly one of these is present, so a caller cannot forget to handle a case. */
export type ImportValidation =
  | { kind: 'preview'; preview: ImportPreview }
  | { kind: 'errors'; report: ImportErrorReport }
  | { kind: 'not-empty' }
  | { kind: 'too-large' }
  | { kind: 'unavailable' };

const jsonHeaders = (csrfToken: string) => ({
  'Content-Type': 'application/json',
  'X-CSRF-TOKEN': csrfToken,
});

export async function fetchEligibility(inventoryId: string): Promise<ImportEligibility | null> {
  const response = await fetch(`/api/inventories/${inventoryId}/import`, { credentials: 'same-origin' });

  return response.ok ? ((await response.json()) as ImportEligibility) : null;
}

export async function validateImport(
  inventoryId: string,
  csrfToken: string,
  file: File,
): Promise<ImportValidation> {
  const body = new FormData();
  body.append('file', file, file.name);

  const response = await fetch(`/api/inventories/${inventoryId}/import/validate`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'X-CSRF-TOKEN': csrfToken },
    body,
  });

  if (response.ok) {
    return { kind: 'preview', preview: (await response.json()) as ImportPreview };
  }

  if (response.status === 413) {
    return { kind: 'too-large' };
  }

  if (response.status === 409) {
    return { kind: 'not-empty' };
  }

  if (response.status === 400) {
    const problem = (await response.json()) as Partial<ImportErrorReport>;

    return {
      kind: 'errors',
      report: { errors: problem.errors ?? [], omittedErrorCount: problem.omittedErrorCount ?? 0 },
    };
  }

  return { kind: 'unavailable' };
}

export async function confirmImport(
  inventoryId: string,
  csrfToken: string,
  proposalId: string,
  token: string,
): Promise<ImportCompleted | null> {
  const response = await fetch(`/api/inventories/${inventoryId}/import/confirm`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: jsonHeaders(csrfToken),
    body: JSON.stringify({ proposalId, token }),
  });

  return response.ok ? ((await response.json()) as ImportCompleted) : null;
}

export async function rejectImport(
  inventoryId: string,
  csrfToken: string,
  proposalId: string,
  token: string | null,
): Promise<boolean> {
  const response = await fetch(`/api/inventories/${inventoryId}/import/reject`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: jsonHeaders(csrfToken),
    body: JSON.stringify({ proposalId, token }),
  });

  return response.ok;
}
```

- [ ] **Step 2: Write the workflow**

Create `src/web/src/InitialImport.tsx`:

```tsx
import { useCallback, useEffect, useState } from 'react';
import {
  confirmImport,
  fetchEligibility,
  rejectImport,
  validateImport,
  type ImportError,
  type ImportPreview,
  type ImportValidation,
} from './importApi';

interface InitialImportProps {
  inventoryId: string;
  csrfToken: string;
  refetchToken: number;
  onImported: () => void;
}

/** The five columns, so an error naming a column index can name the column a person sees. */
const COLUMNS = ['Name', 'Quantity', 'Unit', 'Location', 'Note'];

/** One readable sentence per machine code. The server sends codes; prose belongs here. */
const MESSAGES: Record<string, string> = {
  unknown_column: 'That column is not one of the five this import accepts.',
  duplicate_column: 'That column appears more than once.',
  wrong_column_count: 'The file must have exactly five columns: Name, Quantity, Unit, Location, Note.',
  invalid_encoding: 'The file is not valid UTF-8 text.',
  unterminated_quote: 'A quoted value is never closed.',
  malformed_quote: 'A quoted value is followed by unexpected text.',
  too_few_fields: 'This line has fewer than five values.',
  too_many_fields: 'This line has more than five values.',
  missing_name: 'Name is required.',
  missing_quantity: 'Quantity is required.',
  invalid_quantity: 'Quantity must be a plain non-negative number, for example 10 or 2.5.',
  quantity_overflow: 'The quantities on these equivalent lines add up to more than can be stored.',
  name_too_long: 'That name is too long.',
  note_too_long: 'That note is too long.',
  unit_too_long: 'That unit is too long.',
  location_too_long: 'That location is too long.',
  unknown_unit: 'No active Unit here answers to that name.',
  unknown_location: 'No active Location here carries that name.',
  conflicting_notes: 'Equivalent lines carry different notes, so they cannot be merged.',
  file_too_large: 'That file is larger than 2 MiB.',
  too_many_rows: 'That file has more than 5,000 rows.',
  too_many_entries: 'That file would create more than 5,000 stock entries.',
  empty_file: 'That file has no rows to import.',
};

function ErrorRows({ errors }: { errors: ImportError[] }) {
  return (
    <table>
      <thead>
        <tr>
          <th>Line</th>
          <th>Column</th>
          <th>Problem</th>
        </tr>
      </thead>
      <tbody>
        {errors.map((error, index) => (
          <tr key={`${error.lineNumber}-${error.code}-${index}`}>
            <td>{error.lineNumber === 0 ? '—' : error.lineNumber}</td>
            <td>{error.columnIndex === null ? '—' : COLUMNS[error.columnIndex]}</td>
            <td>
              {MESSAGES[error.code] ?? error.code}
              {error.suggestions.length > 0 && ` Did you mean: ${error.suggestions.join(', ')}?`}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function PreviewRows({ preview }: { preview: ImportPreview }) {
  return (
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Quantity</th>
          <th>Unit</th>
          <th>Location</th>
          <th>Note</th>
          <th>From lines</th>
        </tr>
      </thead>
      <tbody>
        {preview.entries.map((entry) => (
          <tr key={`${entry.name}-${entry.unitCanonicalName}-${entry.locationName ?? ''}`}>
            <td>{entry.name}</td>
            <td>{entry.quantity}</td>
            <td>{entry.unitCanonicalName}</td>
            <td>{entry.locationName ?? 'Unlocated'}</td>
            <td>{entry.note ?? '—'}</td>
            <td>{entry.sourceLineNumbers.join(', ')}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

/**
 * The signed-in Initial Import workflow: offered only while the Inventory is empty, it validates a
 * chosen file, shows either every actionable error or the exact normalized entries it would create,
 * and creates them only on an explicit confirmation.
 */
function InitialImport({ inventoryId, csrfToken, refetchToken, onImported }: InitialImportProps) {
  const [eligible, setEligible] = useState<boolean | null>(null);
  const [validation, setValidation] = useState<ImportValidation | null>(null);
  const [busy, setBusy] = useState(false);
  const [completed, setCompleted] = useState<number | null>(null);

  const loadEligibility = useCallback(async () => {
    const result = await fetchEligibility(inventoryId);
    setEligible(result?.eligible ?? false);
  }, [inventoryId]);

  useEffect(() => {
    void loadEligibility();
  }, [loadEligibility, refetchToken]);

  async function handleFile(event: React.ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }

    setBusy(true);
    setCompleted(null);
    setValidation(await validateImport(inventoryId, csrfToken, file));
    setBusy(false);
  }

  async function handleConfirm(proposalId: string, token: string) {
    setBusy(true);
    const result = await confirmImport(inventoryId, csrfToken, proposalId, token);
    setBusy(false);

    if (result) {
      setValidation(null);
      setCompleted(result.createdEntryCount);
      onImported();
      return;
    }

    // The import could not be applied - most often because the Inventory stopped being empty. Ask
    // the server what is true now rather than guessing here.
    setValidation(null);
    await loadEligibility();
  }

  async function handleCancel(proposalId: string, token: string) {
    setBusy(true);
    await rejectImport(inventoryId, csrfToken, proposalId, token);
    setBusy(false);
    setValidation(null);
  }

  if (eligible === null) {
    return null;
  }

  return (
    <section aria-label="Initial import">
      <h2>Initial import</h2>

      {completed !== null && <p>Imported {completed} stock entries.</p>}

      {!eligible && (
        <p>
          Initial import is available only while an inventory has no stock entries. Use the conversation to add
          stock instead.
        </p>
      )}

      {eligible && (
        <>
          <p>
            Upload a UTF-8 CSV with exactly the columns {COLUMNS.join(', ')}. Blank Unit means each, blank Location
            means unlocated. Up to 2 MiB and 5,000 rows.
          </p>
          <input type="file" accept=".csv,text/csv" onChange={handleFile} disabled={busy} />
        </>
      )}

      {validation?.kind === 'too-large' && <p>That file is larger than 2 MiB.</p>}
      {validation?.kind === 'not-empty' && <p>This inventory already holds stock, so there is nothing to import.</p>}
      {validation?.kind === 'unavailable' && <p>Import is not available right now. Try again in a moment.</p>}

      {validation?.kind === 'errors' && (
        <>
          <h3>Fix these, then upload the file again</h3>
          <ErrorRows errors={validation.report.errors} />
          {validation.report.omittedErrorCount > 0 && (
            <p>And {validation.report.omittedErrorCount} more problems not shown.</p>
          )}
        </>
      )}

      {validation?.kind === 'preview' && (
        <>
          <h3>
            {validation.preview.entries.length} stock entries from {validation.preview.sourceRowCount} rows
          </h3>
          <PreviewRows preview={validation.preview} />
          <button
            type="button"
            onClick={() => handleConfirm(validation.preview.proposalId, validation.preview.token)}
            disabled={busy}
          >
            Import these entries
          </button>
          <button
            type="button"
            onClick={() => handleCancel(validation.preview.proposalId, validation.preview.token)}
            disabled={busy}
          >
            Cancel
          </button>
        </>
      )}
    </section>
  );
}

export default InitialImport;
```

- [ ] **Step 3: Mount it**

In `src/web/src/App.tsx`, import it beside the other workspaces:

```tsx
import InitialImport from './InitialImport';
```

and mount it inside the existing `bootstrap.activeInventoryId` block, after `ReferenceWorkspace`:

```tsx
      {bootstrap.activeInventoryId && (
        <InitialImport
          inventoryId={bootstrap.activeInventoryId}
          csrfToken={session.csrfToken}
          refetchToken={stockRefetchToken}
          onImported={() => setStockRefetchToken((token) => token + 1)}
        />
      )}
```

- [ ] **Step 4: Verify**

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed. The build type-checks this client's own use of the payload shapes - the
declarations in `importApi.ts` are an assumption about the routes, not a runtime check of what comes
back, so it is Task 16 that proves the routes actually answer in those shapes.

- [ ] **Step 5: Commit**

```bash
git add src/web/src/importApi.ts src/web/src/InitialImport.tsx src/web/src/App.tsx
git commit -m "feat(web): preview and confirm the exact Initial Import for #34"
```

### What the implementation changed, and why

The shape above is what shipped; these are the places writing it against the routes Task 14 actually
serves forced a different answer, recorded so the plan and the code do not disagree.

- **Two different 400s arrive at `validate`.** The bounded error report carries `errors` as a list of
  coded problems, but the answer to a missing or empty file part is a validation problem whose
  `errors` is a *map keyed by part name* - and a rejected CSRF token is a 400 with no `errors` at all.
  Rendering `problem.errors ?? []` into the error table would have thrown on a zero-byte file, which a
  file picker will happily hand over. `validateImport` therefore only reports a list as a report, and
  answers `'unreadable-upload'` otherwise.
- **`confirm` and `reject` return a discriminated result, not `ImportCompleted | null` and `boolean`.**
  The four refusals mean four different things to a Participant: a `404` says the import is settled
  and its preview is stale, a `409` says the proposal was expired or overtaken and names which, a
  `400` carrying `proposal_token_mismatch` deliberately leaves the proposal pending - so the reviewed
  preview is worth keeping - and anything else is transient. Collapsing them would have made the
  workflow either clear reviewed work it did not need to, or claim an import that never happened.
- **The closed code sets are spelled in the client.** `ImportErrorCode` (the 23 the domain defines)
  and `ImportConflictCode` key `Record`-typed prose maps, so a code the client knows about without a
  sentence to render for it fails the build, and every outcome union is closed with an `assertNever`.
  With no test runner in `src/web`, that compiler check is the only executable statement this task can
  make about its own completeness.
- **Cancelling clears only after the server settles the proposal.** Until the rejection is answered,
  the preview *is* the pending import; a failed cancellation keeps it and its token so it can simply
  be tried again. A failed confirmation keeps them too, because confirming the same proposal twice
  re-reports what it did rather than importing twice - the retry is safe and still shows the count.
- **Eligibility is re-read deliberately rather than guessed.** The component re-reads it whenever
  `refetchToken` changes, and after any refusal that may have ended the workflow; a read that answers
  after the effect was replaced is ignored, and a preview that is no longer confirmable is dropped as
  soon as the server says the Inventory holds Stock - whatever put it there. The component is mounted
  keyed by the Active Inventory, as `InventoryGovernance` already is, so switching Inventories starts
  the workflow over instead of carrying one Inventory's preview into another.
- **`credentials: 'include'`, matching every other client in `src/web`,** rather than `same-origin`:
  identical behavior for these same-origin relative URLs, and one convention in the directory.
- **Every async handler is `try`/`finally`.** A network failure or an unreadable body must not leave
  the file control and both buttons disabled for the rest of the session, and each failure says what
  is true about the Stock that was or was not created.

---

## Task 16: Prove Initial Import end to end

**Files:**
- Create: `tests/MultiChannelAgent.IntegrationTests/InitialImportScenario.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/InitialImportSqliteTests.cs`
- Create: `tests/MultiChannelAgent.IntegrationTests/Inventories/InitialImportSqlScenarioTests.cs`

Why: the highest required correctness seam in this repository is one SQL-backed application-boundary suite. Every acceptance criterion of #34 has to be observable from outside: upload a file, read the answer, confirm, and check durable state.

- [ ] **Step 1: Write the scenario**

Create `tests/MultiChannelAgent.IntegrationTests/InitialImportScenario.cs`, following `ReferenceAdministrationScenario.cs` exactly in structure and helper style. It drives the real HTTP boundary through `ConversationTestClient` and asserts durable state through the factory's own `DbContext`. The full sequence:

```csharp
    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var owner = await ConversationTestClient.SignInAsync(httpClient, "Importing Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Import Warehouse");

        // 1. A brand new Inventory is empty, so import is offered.
        Assert.True((await EligibilityAsync(owner, inventoryId)).GetProperty("eligible").GetBoolean());

        // 2. Reference data has to exist first: import resolves, it never creates.
        await CompleteAsync(factory, owner, "imp-unit-1", "create unit Cardboard Box aliases boxes, bx");
        await CompleteAsync(factory, owner, "imp-loc-1", "create location Shelf A");

        // 3. A file with every kind of problem reports all of them at once, and stores nothing.
        var broken = await ValidateAsync(owner, inventoryId, string.Join('\n',
        [
            Header,
            ",4,,,",
            "Steel Bolts,nope,,,",
            "Brass Rivets,1,crate,,",
            "Zinc Screws,1,,Bay 9,",
            "Copper Nails,1,,,Blue box",
            "Copper Nails,1,,,Red box",
            string.Empty,
        ]));

        Assert.Equal(HttpStatusCode.BadRequest, broken.Status);
        Assert.Equal(
            ["missing_name", "invalid_quantity", "unknown_unit", "unknown_location"],
            ErrorCodes(broken.Body));
        Assert.Equal(0, await CountPendingImportsAsync(factory, inventoryId));

        // Row-level problems are answered before equivalence is even considered, so the conflicting
        // Notes on lines 6 and 7 are not piled on top of four unreadable rows.

        // 4. A file whose only problem is conflicting Notes says exactly that.
        var conflicting = await ValidateAsync(owner, inventoryId, string.Join('\n',
            [Header, "Copper Nails,1,,,Blue box", "Copper Nails,1,,,Red box", string.Empty]));

        Assert.Equal(["conflicting_notes"], ErrorCodes(conflicting.Body));

        // 5. A valid file previews the exact normalized entries, merging equivalent rows.
        var csv = string.Join('\n',
        [
            Header,
            "Steel Bolts,4,bx,Shelf A,Blue box",
            "Brass Rivets,2.5,,,",
            "STEEL   bolts,6,boxes,shelf a,",
            "Zinc Screws,0,,,",
            string.Empty,
        ]);

        var preview = await ValidateAsync(owner, inventoryId, csv);
        Assert.Equal(HttpStatusCode.OK, preview.Status);
        Assert.Equal(4, preview.Body.GetProperty("sourceRowCount").GetInt32());

        var entries = preview.Body.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);
        Assert.Equal("Steel Bolts", entries[0].GetProperty("name").GetString());
        Assert.Equal("10", entries[0].GetProperty("quantity").GetString());
        Assert.Equal("Cardboard Box", entries[0].GetProperty("unitCanonicalName").GetString());
        Assert.Equal("Shelf A", entries[0].GetProperty("locationName").GetString());
        Assert.Equal("Blue box", entries[0].GetProperty("note").GetString());
        Assert.Equal([2, 4], entries[0].GetProperty("sourceLineNumbers").EnumerateArray().Select(n => n.GetInt32()));

        // 6. Nothing has happened yet: no Stock, and the file is held for exactly this proposal.
        Assert.Equal(0, await CountStockAsync(factory, inventoryId));
        Assert.Equal(1, await CountPendingImportsAsync(factory, inventoryId));
        Assert.Equal(1, await CountRawUploadsAsync(factory));

        // 7. Validating again replaces this Participant's own pending import and its file.
        var replaced = await ValidateAsync(owner, inventoryId, csv);
        Assert.True(replaced.Body.GetProperty("supersededPrevious").GetBoolean());
        Assert.Equal(1, await CountPendingImportsAsync(factory, inventoryId));
        Assert.Equal(1, await CountRawUploadsAsync(factory));

        // 8. Cancelling changes nothing at all, and discards the file.
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(
            owner, inventoryId, ProposalId(replaced.Body), Token(replaced.Body))).Status);
        Assert.Equal(0, await CountStockAsync(factory, inventoryId));
        Assert.Equal(0, await CountRawUploadsAsync(factory));
        Assert.True((await EligibilityAsync(owner, inventoryId)).GetProperty("eligible").GetBoolean());

        // 9. A confirmed import creates exactly the entries that were previewed, and one audit fact.
        var confirmed = await ValidateAsync(owner, inventoryId, csv);
        var applied = await ConfirmAsync(
            owner, inventoryId, ProposalId(confirmed.Body), Token(confirmed.Body));

        Assert.Equal(HttpStatusCode.OK, applied.Status);
        Assert.Equal(3, applied.Body.GetProperty("createdEntryCount").GetInt32());
        Assert.Equal(3, await CountStockAsync(factory, inventoryId));
        Assert.Equal(1, await CountAuditsAsync(factory, inventoryId, nameof(AuditEventType.StockImported)));

        // 10. The raw CSV is gone, and only the digest remains in the ledger.
        Assert.Equal(0, await CountRawUploadsAsync(factory));
        Assert.Equal(confirmed.Body.GetProperty("fileDigest").GetString(), await LedgerDigestAsync(factory, inventoryId));

        // 11. The imported Stock is exactly what the preview promised, readable through the ordinary
        //     authorized projection - including the zero-quantity entry, which is on hand nowhere but
        //     is a Stock Entry all the same.
        await AssertStockAsync(owner, inventoryId, "Steel Bolts", "10", "Cardboard Box", "Shelf A");
        await AssertStockAsync(owner, inventoryId, "Brass Rivets", "2.5", "each", null);
        await AssertStockAsync(owner, inventoryId, "Zinc Screws", "0", "each", null, includeZero: true);

        // 12. A lost-response retry re-reports the recorded result without importing twice, and
        //     import is no longer offered at all.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(
            owner, inventoryId, ProposalId(confirmed.Body), Token(confirmed.Body))).Status);
        var afterwards = await EligibilityAsync(owner, inventoryId);
        Assert.False(afterwards.GetProperty("eligible").GetBoolean());
        Assert.Equal("inventory_not_empty", afterwards.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await ValidateAsync(owner, inventoryId, csv)).Status);

        // 13. Retired reference data is unknown to import too.
        var second = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(factory), "Second Owner");
        var emptyInventoryId = await second.CreateAndSelectInventoryAsync("Second Warehouse");
        await CompleteAsync(factory, second, "imp-unit-2", "create unit Pallet");
        var retire = await OutcomeAsync(factory, second, "imp-retire-1", "retire unit Pallet");
        await CompleteAsync(factory, second, "imp-confirm-1", $"confirm {TokenOf(retire)}");

        var retired = await ValidateAsync(second, emptyInventoryId, string.Join('\n',
            [Header, "Steel Bolts,1,Pallet,,", string.Empty]));
        Assert.Equal(["unknown_unit"], ErrorCodes(retired.Body));

        // 14. A Viewer may not import at all, and is told nothing that distinguishes refusal from absence.
        var viewer = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(factory), "Third Viewer");
        await second.GrantMembershipAsync(emptyInventoryId, viewer.ParticipantIdentifier, "Viewer");

        Assert.Equal(HttpStatusCode.NotFound, (await EligibilityAsync(viewer, emptyInventoryId)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await ValidateAsync(viewer, emptyInventoryId, csv)).Status);

        // 15. A stranger sees exactly the same 404 for an Inventory they are not a member of.
        var stranger = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(factory), "Fourth Stranger");
        Assert.Equal(HttpStatusCode.NotFound, (await EligibilityAsync(stranger, inventoryId)).Status);
    }
```

Add the helpers this uses in the same file, following the shipped scenario's style: `Header` (the five column names joined by commas), `EligibilityAsync`, `ValidateAsync` (multipart), `ConfirmAsync`, `RejectAsync` (all returning a `(HttpStatusCode Status, JsonElement Body)` tuple), `ProposalId`, `Token`, `ErrorCodes`, `CountStockAsync`, `CountPendingImportsAsync`, `CountRawUploadsAsync`, `CountAuditsAsync`, `LedgerDigestAsync`, `AssertStockAsync`, plus `CompleteAsync`, `OutcomeAsync`, `TokenOf`, and `ProcessPendingAsync` copied from `ReferenceAdministrationScenario.cs` for the conversational steps that create and retire reference data.

- [ ] **Step 2: Write both runners**

Create `tests/MultiChannelAgent.IntegrationTests/InitialImportSqliteTests.cs`:

```csharp
namespace MultiChannelAgent.IntegrationTests;

/// <summary>The Docker-free twin of the SQL-backed Initial Import scenario, so the protocol is proven on every machine.</summary>
public sealed class InitialImportSqliteTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory? _factory;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _factory!.DisposeAsync().AsTask();

    [Fact]
    public Task Initial_import_works_end_to_end() => InitialImportScenario.RunAsync(_factory!);
}
```

Create `tests/MultiChannelAgent.IntegrationTests/Inventories/InitialImportSqlScenarioTests.cs`:

```csharp
namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The whole Initial Import protocol against real SQL Server under production migrations - the
/// highest required correctness seam in this repository.
/// </summary>
public sealed class InitialImportSqlScenarioTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Initial_import_works_end_to_end()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import scenario.");

        await InitialImportScenario.RunAsync(Factory!);
    }
}
```

- [ ] **Step 3: Run the Docker-free twin to verify it fails, then passes**

Run: `dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~InitialImportSqliteTests"`
Expected: FAIL first, on whichever assertion the wiring has not satisfied yet; then PASS once every task above is in place. Fix the wiring, never the assertion - each one is an acceptance criterion.

- [ ] **Step 4: Run the SQL-backed run**

Run: `REQUIRE_DOCKER_TESTS=true dotnet test tests/MultiChannelAgent.IntegrationTests/MultiChannelAgent.IntegrationTests.csproj --filter "FullyQualifiedName~InitialImportSqlScenarioTests"`
Expected: PASS. If Docker is unavailable locally, confirm it reports as skipped rather than failed and say so plainly in the commit message; CI runs it with `REQUIRE_DOCKER_TESTS=true`.

- [ ] **Step 5: Commit**

```bash
git add tests/MultiChannelAgent.IntegrationTests/InitialImportScenario.cs \
        tests/MultiChannelAgent.IntegrationTests/InitialImportSqliteTests.cs \
        tests/MultiChannelAgent.IntegrationTests/Inventories/InitialImportSqlScenarioTests.cs
git commit -m "test(integration): prove Initial Import end to end for #34"
```

---

## Task 17: Whole-suite verification

**Files:** none created; this task fixes whatever it finds.

- [ ] **Step 1: Build exactly as CI does**

Run: `dotnet build --configuration Release`
Expected: Build succeeded, 0 warnings. `TreatWarningsAsErrors` is on, so a switch that is no longer exhaustive over the widened `AuditEventType` surfaces here.

- [ ] **Step 2: Run every backend test**

Run: `dotnet test --configuration Release`
Expected: PASS across Domain, Application, Architecture, and Integration. SQL-backed scenarios skip when Docker is unavailable; every SQLite twin must pass regardless.

- [ ] **Step 3: Confirm the migration is complete and generates**

Run:
```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations has-pending-model-changes \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure
dotnet ef migrations script \
  --project src/MultiChannelAgent.Infrastructure \
  --startup-project src/MultiChannelAgent.Infrastructure \
  --idempotent \
  --output ./migrations-check.sql
grep -c "ImportProposals\|ImportUploads\|ImportOperations" ./migrations-check.sql
rm ./migrations-check.sql
```
Expected: "No changes have been made to the model since the last migration."; the script generates; the grep finds the three new tables.

- [ ] **Step 4: Build and lint the web client**

Run: `npm --prefix src/web run build && npm --prefix src/web run lint`
Expected: both succeed.

- [ ] **Step 5: Confirm the architecture boundaries still hold**

Run: `dotnet test tests/MultiChannelAgent.ArchitectureTests/MultiChannelAgent.ArchitectureTests.csproj`
Expected: PASS. `ImportContract`, `CsvImportDocument`, `ImportRow`, `ImportMergePlan`, `ImportProposal`, `ImportOperationId`, and `FileDigest` live in Domain and reference only Domain; `InitialImportService`, `ImportConfirmationService`, `ImportReferenceResolver`, `ImportCleanupCoordinator`, and the four new store seams live in Application and reference nothing from Infrastructure.

- [ ] **Step 6: Confirm Initial Import is a workflow, not a tool**

Run:
```bash
grep -rn "import" src/MultiChannelAgent.Application/Inventories/InventoryToolRouter.cs \
                  src/MultiChannelAgent.Application/Inventories/StockToolDispatcher.cs \
                  src/MultiChannelAgent.Application/Inventories/ReferenceToolDispatcher.cs \
                  src/MultiChannelAgent.Application/Turns/ScriptedModelBoundary.cs \
  --include=*.cs -i | grep -v "^.*using " || echo "clean"
```
Expected: `clean`. #26 lists ten Unit and Location tools and nine stock tools, and Initial Import is none of them - a tool named here would be a nineteenth-and-a-half nobody specified.

- [ ] **Step 7: Confirm the raw file has exactly one home**

Run:
```bash
grep -rln "ImportUploads" src --include=*.cs
```
Expected: exactly three files - `MultiChannelAgentDbContext.cs`, `ImportUploadEntityConfiguration.cs`, `SqlImportProposalStore.cs`, plus `SqlImportExecutionStore.cs` for the delete. Any other reader would be a second place raw uploaded data can escape from.

- [ ] **Step 8: Confirm no budget or spend policy crept in**

Run:
```bash
git diff origin/main...HEAD -- src tests | grep -niE "budget|spend threshold|chargeback|cost ceiling|quota purchase" || echo "clean"
```
Expected: `clean`. The parent spec puts every one of these out of scope; a "safety" limit added here would be behavior nobody asked for.

- [ ] **Step 9: Scan the diff for anything unfinished**

Run:
```bash
git diff --stat origin/main...HEAD
git diff origin/main...HEAD | grep -nE "TODO|FIXME|XXX|NotImplementedException|placeholder" || echo "clean"
git diff --check
```
Expected: `clean` for both greps, and no whitespace errors.

Replace `origin/main` with whatever base branch this work actually targets if it differs.

- [ ] **Step 10: Commit any fixes**

```bash
git add -A
git commit -m "fix(inventories): settle the whole suite for Initial Import for #34"
```

If nothing needed fixing, skip this commit rather than creating an empty one.

---

## Acceptance criteria coverage

| Acceptance criterion | Where it is implemented | Where it is proven |
| --- | --- | --- |
| Owner and Editor can open Initial Import | Task 8 (`InitialImportService` authorizes `MembershipRole.Editor`, which Owner satisfies) | `InitialImportServiceTests.An_Editor_and_an_Owner_may_both_import`, `...A_Viewer_may_not_import_and_the_denial_is_audited`, scenario step 14 |
| Only when the Inventory has no Stock Entries, including zero-quantity | Task 6 (`IStockEmptyStateReader`), Task 8 (gate before the file is read), Task 12 (authoritative re-assertion) | `InitialImportServiceTests.Import_is_offered_only_while_the_Inventory_holds_no_Stock_at_all`, `SqlImportProposalStoreTests.An_Inventory_holding_a_zero_quantity_entry_is_not_empty`, `SqlImportExecutionStoreTests.An_import_into_an_Inventory_that_stopped_being_empty_changes_nothing`, scenario steps 1 and 12 |
| The specified UTF-8 RFC 4180-style five-column contract | Task 1 (`ImportContract.Headers`), Task 2 (`CsvImportDocument`) | `ImportContractTests.The_file_contract_is_exactly_five_columns_in_one_fixed_order`, the whole of `CsvImportDocumentTests` |
| Rejects unknown, duplicate, oversized, or invalid input | Task 2 (headers, encoding, quoting, bounds), Task 3 (field bounds), Task 14 (file bound, an unreadable body, and 413 before buffering) | `CsvImportDocumentTests` header/encoding/quote/bound cases, `ImportRowTests`, `ImportEndpointsHttpTests.A_file_part_longer_than_the_bound_is_refused_on_its_length_rather_than_read`, `...A_multipart_body_that_stops_before_its_terminating_boundary_names_the_part_it_wanted`, `ImportUploadLimitsHttpTests.A_body_over_the_route_bound_is_refused_by_the_server_before_the_endpoint_reads_any_of_it`, `...A_file_part_past_the_file_bound_is_refused_at_the_file_bound_however_much_framing_the_body_is_allowed` |
| Resolves active Unit names or aliases and active Location names | Task 7 (`ImportReferenceResolver` over active-only `IInventoryReferenceStore`) | `ImportReferenceResolverTests.A_Unit_resolves_by_its_canonical_name_or_by_any_active_alias`, `...A_retired_reference_is_exactly_as_unknown_as_one_that_never_existed`, scenario step 13 |
| Without creating references | Task 7 (nothing in the resolver writes), Task 17 step 6 | `ImportReferenceResolverTests.An_unknown_Unit_is_reported_at_its_own_column_and_never_created`, scenario step 3 |
| Equivalent rows merge by summing Quantity only when Notes are compatible | Task 4 (`ImportMergePlan`) | `ImportMergePlanTests` (every compatibility and conflict case), `InitialImportServiceTests.Conflicting_Notes_on_equivalent_rows_are_reported_as_errors`, scenario steps 4 and 5 |
| All actionable row/column errors are returned together | Task 3 (per-row collection), Task 8 (phase ordering and the bounded report) | `ImportRowTests.Every_thing_wrong_with_one_row_is_reported_together`, `InitialImportServiceTests.Every_actionable_error_comes_back_together_and_nothing_is_stored`, `...An_answer_never_carries_more_than_the_bounded_number_of_errors_and_says_how_many_it_omitted`, scenario step 3 |
| Exact normalized preview | Task 4 (merged entries), Task 8 (`ImportPreviewView`), Task 15 (the table) | `InitialImportServiceTests.A_valid_file_previews_the_exact_normalized_entries_it_would_create`, `ImportEndpointsHttpTests.A_valid_file_previews_the_exact_entries_and_hands_back_a_one_time_token`, scenario step 5 |
| Ten-minute proposal bound to actor, Inventory, digest, rows, and empty-state version | Task 5 (`ImportProposal`), Task 10 (the schema), Task 11 (round-trip) | `ImportProposalTests` (binding, lifetime, entries), `InitialImportServiceTests.A_successful_validation_stores_a_pending_proposal_bound_to_everything_that_decided_it`, `SqlImportProposalStoreTests.A_stored_import_round_trips_every_exact_entry_it_carries` |
| Confirmation creates all entries atomically or none | Task 12 (one `Serializable` transaction, `AbandonAsync` on every exit) | `SqlImportExecutionStoreTests` conflict cases, `SqlImportExecutionStoreChangeTrackerIsolationTests`, `SqlImportExecutionStoreConcurrencyTests` |
| Idempotently | Task 5 (`ImportOperationId.DeriveForProposal`), Task 10 (unique proposal index), Task 12 (replay first) | `SqlImportExecutionStoreTests.Applying_the_same_operation_identity_again_re_reports_it_instead_of_importing_twice`, `ImportConfirmationServiceTests.A_replayed_confirmation_re_reports_what_it_did_instead_of_importing_twice`, scenario step 12 |
| Without reparsing | Task 9 (stored rows handed to the writer; the upload is never read) | `ImportConfirmationServiceTests.Confirmation_applies_the_stored_rows_and_never_reads_the_file`, Task 17 step 7 |
| Raw CSV discarded after completion or expiry | Task 11 (`SettlePendingAsync` deletes with every settle), Task 12 (deleted in the import transaction), Task 13 (expiry sweep) | `SqlImportProposalStoreTests.The_raw_file_is_stored_with_the_proposal_and_gone_the_moment_it_settles`, `...An_expired_import_is_swept_out_of_Pending_and_its_file_discarded`, `ImportCleanupCoordinatorTests`, scenario steps 8 and 10 |
| Only the specified 90-day semantic facts remain | Task 1 (`StockImported` / `Import:Completed`), Task 12 (exactly one fact), Task 13 (the audit sweep that did not exist) | `SqlImportExecutionStoreTests` audit assertions, `ImportCleanupCoordinatorTests.An_audit_fact_older_than_ninety_days_is_discarded_and_a_newer_one_is_kept`, scenario step 9 |
| Denials produce the specified semantic audit facts | Decision 11 (shipped `AccessDenied`), Tasks 8 and 9 (both authorize through `InventoryAuthorizationService`) | `InitialImportServiceTests.A_non_member_is_told_nothing...`, `...A_Viewer_may_not_import...`, `ImportConfirmationServiceTests.A_Viewer_may_not_confirm_and_the_denial_is_audited` |
| One signed-in web workflow, never a tool or a channel | Task 14 (four HTTP routes), Task 17 step 6 | `ImportEndpointsHttpTests`, Task 17 step 6's grep |
| Non-disclosure preserved | Tasks 8, 9, and 14 (`NotFound` and `Forbidden` collapse to one 404) | `ImportEndpointsHttpTests.An_Inventory_the_Participant_may_not_see_is_indistinguishable_from_one_that_does_not_exist`, scenario steps 14 and 15 |
| CSRF and authorization on every mutating route | Task 14 (`AntiforgeryEndpointFilter`, `ActiveTenantMember`) | `ImportEndpointsHttpTests.A_mutating_request_without_the_CSRF_token_is_refused` |
| Concurrency and races handled | Task 10 (filtered unique index), Task 12 (`Serializable`, reference locks, guarded consume) | `SqlImportProposalStoreTests.A_second_pending_import_..._cannot_exist_at_all`, `SqlImportExecutionStoreConcurrencyTests` |
| Migrations, indexes, and cascade paths | Task 10 (one migration, single cascade path per table) | `ImportRelationalModelTests`, `InitialImportSqlScenarioTests` (real migrations on a fresh database), Task 17 step 3 |
| Web preview, error report, and confirmation | Task 15 | `npm run build` type-checking this client's use of every payload shape, scenario steps 3, 5, and 9 through the same routes the client calls |
| No monetary budgets | No task adds one; Task 17 step 8 enforces it | Task 17 step 8 |

---

## Deliberate design decisions worth knowing

- **Initial Import is a workflow, not a tool.** #26 lists nineteen tools and Initial Import is none of them. It has no `TurnExecutionContext`, no dispatcher arm, and no scripted grammar, and Task 17 step 6 greps to keep it that way.
- **Its proposal is its own aggregate.** `ConfirmationProposal` is bounded to 25 changes, keyed one-pending-per-ChannelConversation, and carries expected versions of existing Stock Entries. An import carries up to 5,000 entries, belongs to a browser session, and touches no existing entry by definition. Sharing the type would mean relaxing all three rules for every conversational confirmation.
- **The empty-state version is an absence, so it is enforced with a range lock.** There is no row to version when the assertion is "there are no rows". Serializable isolation plus a re-assertion inside the execution transaction is what makes a ten-minute-old preview safe, and it is the same shape #33 uses to stop a Retire racing a Stock write.
- **The parser knows only bytes.** `CsvImportDocument` understands encoding, quoting, newlines, and the five headers, and nothing about Inventories. Every other rule - required fields, bounds, references, equivalence - lives somewhere that can be tested without a CSV in sight.
- **Envelope errors stop; row errors accumulate.** A file with a wrong header produces exactly one error, because rows read against misaligned columns would be noise. A file with five hundred bad rows produces five hundred, because that is the report a Participant fixes the file from.
- **The error report is bounded, and says so exactly.** Five hundred errors plus an exact omitted count, rather than five thousand items or a vague "and more".
- **Notes are compared case-sensitively.** A Note is free text somebody wrote to record a distinction, so folding `Blue box` into `blue box` would quietly erase one. Refusing and asking is the direction a Participant can act on.
- **Blank means default, not missing.** Blank Unit is `each`, blank Location is unlocated, blank Note is no Note - decided in `ImportRow` so nothing downstream has to remember it.
- **References are resolved once per distinct term.** A five-thousand-row file with three Units performs three lookups, and the cache never outlives the request so it can never serve a reference retired since.
- **The raw file lives in SQL for ten minutes and nowhere else.** One durable store, one cleanup path, one place to prove it is gone - and no blob dependency introduced for a two-mebibyte bound.
- **Confirmation never reads the file.** The stored rows are handed to the writer exactly as previewed. That is what "without reparsing" means, and it is why the file can be discarded at all.
- **Three ledgers, disjoint by construction.** `ImportOperationId` hashes material shaped so it can never equal a `StockOperationId` or a `ReferenceOperationId`.
- **The audit sweep is new, and covers everything.** `AuditFact.RetentionDays` has said ninety since audits existed and nothing enforced it. #34 forced the question, and there is no honest way to retain one kind of fact for ninety days and another forever.
- **No budgets.** Nothing here adds a cost ceiling, spend threshold, or quota check; the parent spec puts all of them out of scope, and a "safety" limit added here would be a behavior nobody asked for.
